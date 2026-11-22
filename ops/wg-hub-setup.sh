#!/usr/bin/env bash
# Hub WireGuard gateway on the HOST (T-01): wg0 = 10.250.0.1/16, UDP 51820.
# Spokes are added by appending bundles/<CL>-<SITE>/hub-peer.conf and running `wg syncconf`.
# Spoke-to-spoke traffic is dropped: a client site can only reach the hub services it needs.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 1; }
WG_HUB_IP="${WG_HUB_IP:-10.250.0.1}"; WG_PORT="${WG_PORT:-51820}"
apt-get install -y -qq wireguard >/dev/null
umask 077
[ -f /etc/wireguard/hub.key ] || wg genkey > /etc/wireguard/hub.key
[ -f /etc/wireguard/wg0.conf ] || cat > /etc/wireguard/wg0.conf <<CONF
[Interface]
Address = ${WG_HUB_IP}/16
ListenPort = ${WG_PORT}
PrivateKey = $(cat /etc/wireguard/hub.key)
# no forwarding between spokes; only Zabbix (10051) and the Wazuh relay targets are reachable
PostUp = iptables -I FORWARD -i wg0 -o wg0 -j DROP
PostDown = iptables -D FORWARD -i wg0 -o wg0 -j DROP
CONF
systemctl enable --now wg-quick@wg0
ufw allow "${WG_PORT}"/udp >/dev/null 2>&1 || true
ufw allow in on wg0 to "${WG_HUB_IP}" port 10051 proto tcp >/dev/null 2>&1 || true
echo "hub public key (give to scripts/new_spoke.py --hub-pubkey): $(wg pubkey < /etc/wireguard/hub.key)"
