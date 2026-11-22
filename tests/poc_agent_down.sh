#!/usr/bin/env bash
# The PoC test, on the platform lab: stop the monitored agent -> ticket opens; start it -> ticket solved.
# Slow on purpose: Zabbix waits 3 min before declaring the agent unavailable (avoids false alarms).
set -euo pipefail
cd "$(dirname "$0")/../lab"; set -a; source .env; set +a
q() { docker exec ws-glpi-db mariadb -uroot -p"$GLPI_DB_ROOT_PASSWORD" glpi -N -e "$1"; }
avail() { docker exec ws-zabbix-db psql -U zabbix -d zabbix -At -c "select i.available from interface i join hosts h using(hostid) where h.host='cl001-kwi-srv01'"; }
echo "waiting for the agent to be available in Zabbix..."; until [ "$(avail)" = 1 ]; do sleep 5; done
T0=$(date +%s); docker stop ws-lab-device-01 >/dev/null; echo "agent stopped"
until id=$(q "select id from glpi_tickets where name like '%srv01%not available%' and status<5 order by id desc limit 1") && [ -n "$id" ]; do
  sleep 5; [ $(( $(date +%s) - T0 )) -gt 480 ] && { echo "FAIL: no ticket after 8 min"; docker start ws-lab-device-01 >/dev/null; exit 1; }
done
echo "PASS ticket #$id opened after $(( $(date +%s) - T0 ))s: $(q "select name from glpi_tickets where id=$id")"
T1=$(date +%s); docker start ws-lab-device-01 >/dev/null; echo "agent started"
until [ "$(q "select status from glpi_tickets where id=$id")" = 5 ]; do
  sleep 5; [ $(( $(date +%s) - T1 )) -gt 300 ] && { echo "FAIL: ticket not solved after 5 min"; exit 1; }
done
echo "PASS ticket #$id solved automatically after $(( $(date +%s) - T1 ))s"
