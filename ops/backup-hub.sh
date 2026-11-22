#!/usr/bin/env bash
# Nightly hub backup (C-06, NFR-AVL-01: RPO 24 h). Run from a systemd timer / cron on each hub VM:
#   15 1 * * *  /opt/wisesphere/ops/backup-hub.sh >> /var/log/wisesphere-backup.log 2>&1
#
# Dumps databases, archives volumes and configs, and ships them to an encrypted, off-site restic
# repository (retention 30 daily / 12 monthly). Restore drill: ops/restore-drill.md (TC-11, WBS 5.5).
# Covers: GLPI (MariaDB + files), integration service state, Zabbix (PostgreSQL), Grafana, hub configs.
# Wazuh (T-07): run with --wazuh on HUB-WZH-01 to add keys, rules, decoders and an indexer snapshot.
set -euo pipefail
HUB_DIR="${HUB_DIR:-/opt/wisesphere/hub}"
set -a; source "$HUB_DIR/.env"; set +a
: "${RESTIC_REPOSITORY:?}" "${RESTIC_PASSWORD:?}"
RESTIC_IMAGE="${RESTIC_IMAGE:-restic/restic:0.19.1}"
STAGE="$(mktemp -d /var/tmp/ws-backup.XXXXXX)"; trap 'rm -rf "$STAGE"' EXIT
STAMP="$(date +%Y%m%d-%H%M%S)"
log() { echo "[$(date -Is)] $*"; }
running() { docker ps --format '{{.Names}}' | grep -qx "$1"; }

if running ws-glpi-db; then
  log "GLPI database"
  docker exec ws-glpi-db mariadb-dump -uroot -p"$GLPI_DB_ROOT_PASSWORD" --single-transaction --routines --triggers glpi | gzip > "$STAGE/glpi-$STAMP.sql.gz"
  log "GLPI files (documents, config incl. GLPI encryption key)"
  docker run --rm --volumes-from ws-glpi -v "$STAGE":/out alpine:3.24.4 tar czf "/out/glpi-files-$STAMP.tgz" -C /var/glpi .
fi
if running ws-integration; then
  log "integration service state (SMS dedupe, processed surveys - small, rebuildable)"
  docker run --rm --volumes-from ws-integration -v "$STAGE":/out alpine:3.24.4 tar czf "/out/integration-$STAMP.tgz" -C /data .
fi
if running ws-zabbix-db; then
  log "Zabbix database"
  docker exec ws-zabbix-db pg_dump -U zabbix -Fc zabbix > "$STAGE/zabbix-$STAMP.dump"
fi
if running ws-grafana; then
  docker run --rm --volumes-from ws-grafana -v "$STAGE":/out alpine:3.24.4 tar czf "/out/grafana-$STAMP.tgz" -C /var/lib/grafana .
fi
log "hub configuration"
tar czf "$STAGE/hub-config-$STAMP.tgz" -C "$HUB_DIR" . -C /etc wireguard 2>/dev/null || tar czf "$STAGE/hub-config-$STAMP.tgz" -C "$HUB_DIR" .

if [ "${1:-}" = "--wazuh" ]; then
  log "Wazuh manager keys, rules, decoders, shared agent groups (T-07)"
  M="${WAZUH_MANAGER_CONTAINER:-single-node-wazuh.manager-1}"
  docker exec "$M" tar czf - -C /var/ossec etc/client.keys etc/ossec.conf etc/rules etc/decoders etc/lists etc/shared integrations \
    > "$STAGE/wazuh-manager-$STAMP.tgz"
  if [ -n "${WAZUH_INDEXER_URL:-}" ]; then
    log "Wazuh indexer snapshot (repository 'ws-backup' must be registered once)"
    curl -fsS -k -u "$WAZUH_INDEXER_USER:$WAZUH_INDEXER_PASSWORD" -X PUT \
      "$WAZUH_INDEXER_URL/_snapshot/ws-backup/snap-$STAMP?wait_for_completion=false" >/dev/null
  fi
fi

log "upload to $RESTIC_REPOSITORY"
R=(docker run --rm -e RESTIC_REPOSITORY -e RESTIC_PASSWORD -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY -v "$STAGE":/data:ro "$RESTIC_IMAGE")
"${R[@]}" snapshots >/dev/null 2>&1 || "${R[@]}" init
"${R[@]}" backup /data --tag wisesphere --host "$(hostname)"
"${R[@]}" forget --tag wisesphere --keep-daily 30 --keep-monthly 12 --prune
log "backup finished"
