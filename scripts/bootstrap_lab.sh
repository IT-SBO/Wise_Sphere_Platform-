#!/usr/bin/env bash
# One-shot lab bootstrap: GLPI (API, time zone, tenants, SLAs, service account),
# Zabbix (media type, action, groups, lab hosts) and n8n (credentials, workflows).
# Idempotent - safe to run again. Requires: docker compose stack from lab/ running, python3 + requests + pyyaml.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/lab"
set -a; source .env; set +a
GLPI_URL="http://localhost:${GLPI_PORT}/apirest.php"
ZBX_URL="http://localhost:${ZABBIX_PORT}/api_jsonrpc.php"
SQL() { docker exec ws-glpi-db mariadb -uroot -p"$GLPI_DB_ROOT_PASSWORD" "$@"; }
wait_for() { # wait_for <seconds> <description> <command...>
  local t="$1" what="$2"; shift 2
  for _ in $(seq 1 "$t"); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
  echo "timeout waiting for $what" >&2; return 1
}
rnd() { head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-40}"; }

# Persist generated lab secrets in lab/.env once
grep -q '^WS_GLPI_APP_TOKEN=' .env || echo "WS_GLPI_APP_TOKEN=$(rnd 40)" >> .env
grep -q '^WS_GLPI_SVC_PASSWORD=' .env || echo "WS_GLPI_SVC_PASSWORD=Ws-$(rnd 24)-9" >> .env
set -a; source .env; set +a

echo "== waiting for GLPI install"
wait_for 600 "GLPI install" sh -c 'docker logs ws-glpi 2>&1 | grep -q "Welcome to"'
wait_for 120 "GLPI web" curl -sf -o /dev/null "http://localhost:${GLPI_PORT}/"

echo "== GLPI: API + time zone (PoC fix: ticket times were 2 h off)"
docker exec ws-glpi bin/console config:set enable_api 1 >/dev/null
docker exec ws-glpi bin/console config:set enable_api_login_credentials 1 >/dev/null
docker exec ws-glpi-db sh -c "mariadb-tzinfo-to-sql /usr/share/zoneinfo 2>/dev/null | mariadb -uroot -p\"$GLPI_DB_ROOT_PASSWORD\" mysql"
SQL -e "GRANT SELECT ON mysql.time_zone_name TO 'glpi'@'%'; FLUSH PRIVILEGES;"
docker exec ws-glpi bin/console database:enable_timezones --no-interaction >/dev/null 2>&1 || true
docker exec ws-glpi bin/console config:set timezone "$TZ" >/dev/null
# Read-only DB user for Grafana (DSH-03/04)
SQL -e "CREATE USER IF NOT EXISTS 'grafana_ro'@'%' IDENTIFIED BY '$GLPI_DB_RO_PASSWORD'; GRANT SELECT ON glpi.* TO 'grafana_ro'@'%'; FLUSH PRIVILEGES;"

echo "== GLPI: provisioning through the API (bootstrap client opened to the docker gateway only while it runs)"
GW="$(docker network inspect wisesphere-lab_lab -f '{{(index .IPAM.Config 0).Gateway}}')"
SQL glpi -e "UPDATE glpi_apiclients SET ipv4_range_start=INET_ATON('$GW'), ipv4_range_end=INET_ATON('$GW') WHERE id=1"
restore() { SQL glpi -e "UPDATE glpi_apiclients SET ipv4_range_start=INET_ATON('127.0.0.1'), ipv4_range_end=INET_ATON('127.0.0.1') WHERE id=1"; }
trap restore EXIT
python3 "$ROOT/glpi/provision_glpi.py" --url "$GLPI_URL" --admin-user glpi --admin-password "${GLPI_ADMIN_PASSWORD:-glpi}" \
  --svc-password "$WS_GLPI_SVC_PASSWORD" --app-token "$WS_GLPI_APP_TOKEN" --api-cidr 172.30.0.0/24 --out glpi-ids.json >/dev/null
restore; trap - EXIT

echo "== Zabbix: provisioning"
wait_for 300 "Zabbix web" curl -sf -o /dev/null "http://localhost:${ZABBIX_PORT}/"
python3 "$ROOT/zabbix/provision_zabbix.py" --url "$ZBX_URL" --user Admin --password "${ZABBIX_ADMIN_PASSWORD:-zabbix}" \
  --webhook-url http://n8n:5678/webhook/zbx-event --webhook-secret "$WS_WEBHOOK_SECRET" \
  --zabbix-public-url "http://localhost:${ZABBIX_PORT}" --lab >/dev/null

echo "== n8n: settings, credentials, workflows"
python3 "$ROOT/scripts/render_n8n_env.py" --ids glpi-ids.json --base n8n.env.base --out n8n.env
docker compose up -d --force-recreate n8n >/dev/null
wait_for 180 "n8n" docker exec ws-n8n wget -qO- http://localhost:5678/healthz
"$ROOT/scripts/n8n_bootstrap.sh" ws-n8n
docker restart ws-n8n >/dev/null
wait_for 180 "n8n" docker exec ws-n8n wget -qO- http://localhost:5678/healthz
echo "== lab ready: GLPI http://localhost:${GLPI_PORT}  Zabbix http://localhost:${ZABBIX_PORT}  n8n http://localhost:${N8N_PORT}"
