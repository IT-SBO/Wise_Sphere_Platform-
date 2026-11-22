#!/usr/bin/env bash
# WiseSphere spoke installer - SOP-01 (timed: target <= 60 min from VM hand-over, NFR-OPS-02).
#
#   sudo ./install-spoke.sh /path/to/bundle      # bundle produced on the hub by scripts/new_spoke.py
#
# Bundle contents: spoke.env, wg0.conf, proxy.psk.  Supported OS: Ubuntu 24.04 LTS / Debian 12.
# Fixes from the Technical Kit review: SSH limited to ADMIN_CIDR (T-08), Docker from the signed apt
# repository instead of `curl | sh` (T-08), Wazuh relay ports open to CLIENT_LAN only (T-02).
set -euo pipefail
START=$(date +%s)
BUNDLE="${1:?usage: install-spoke.sh <bundle-dir>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 1; }
for f in spoke.env wg0.conf proxy.psk; do [ -f "$BUNDLE/$f" ] || { echo "missing $BUNDLE/$f"; exit 1; }; done
set -a; source "$BUNDLE/spoke.env"; set +a
step() { echo; echo "[$(( $(date +%s) - START ))s] == $*"; }

step "packages (signed repositories only)"
. /etc/os-release
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg wireguard ufw jq >/dev/null
install -m 0755 -d /etc/apt/keyrings
curl -fsSL "https://download.docker.com/linux/${ID}/gpg" -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list
apt-get update -qq
apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin >/dev/null

step "firewall: no inbound from the internet; SSH from ${ADMIN_CIDR} only"
ufw --force reset >/dev/null
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw allow from "$ADMIN_CIDR" to any port 22 proto tcp >/dev/null
ufw allow from "$CLIENT_LAN" to any port 1514 proto tcp >/dev/null
ufw allow from "$CLIENT_LAN" to any port 1515 proto tcp >/dev/null
ufw --force enable >/dev/null

step "WireGuard tunnel to the hub (outbound UDP 51820)"
install -m 600 "$BUNDLE/wg0.conf" /etc/wireguard/wg0.conf
systemctl enable --now wg-quick@wg0
for i in $(seq 1 30); do ping -c1 -W1 "$WG_HUB_IP" >/dev/null 2>&1 && break; sleep 2; done
ping -c1 -W2 "$WG_HUB_IP" >/dev/null || { echo "hub $WG_HUB_IP not reachable through the tunnel - check outbound UDP 51820"; exit 1; }

step "Zabbix active proxy ${PROXY_NAME} (+ optional Wazuh relay)"
install -d -m 700 /opt/wisesphere/spoke/secrets
cp "$HERE/docker-compose.yml" "$HERE/nginx.conf" "$HERE/wazuh-relay.conf.template" /opt/wisesphere/spoke/
install -m 600 "$BUNDLE/spoke.env" /opt/wisesphere/spoke/.env
install -m 644 "$BUNDLE/proxy.psk" /opt/wisesphere/spoke/secrets/proxy.psk   # read by uid 1997 in the container
cd /opt/wisesphere/spoke
PROFILES=()
[ "${ENABLE_WAZUH_RELAY:-false}" = "true" ] && PROFILES=(--profile wazuh-relay)
docker compose "${PROFILES[@]}" up -d

step "GLPI Agent ${GLPI_AGENT_VERSION} (tag ${CLIENT_CODE} routes assets to the client entity)"
AG="glpi-agent-${GLPI_AGENT_VERSION}-linux-installer.pl"
curl -fsSL -o "/tmp/$AG" "https://github.com/glpi-project/glpi-agent/releases/download/${GLPI_AGENT_VERSION}/$AG"
perl "/tmp/$AG" --install --type=all --server="${GLPI_SERVER_URL%/}/front/inventory.php" \
  --tag="$CLIENT_CODE" --runnow --service --no-question >/dev/null
# Network discovery / SNMP inventory tasks are defined centrally in GLPI per entity (SOP-01 step 6)

step "checks"
docker compose ps
wg show wg0 latest-handshakes
ELAPSED=$(( $(date +%s) - START ))
echo
echo "Spoke ${CLIENT_CODE}/${SITE_CODE} installed in $((ELAPSED/60)) min $((ELAPSED%60)) s."
echo "Next (SOP-01): confirm proxy ${PROXY_NAME} 'online' in Zabbix and start the first discovery run; record the time."
