#!/usr/bin/env bash
# Create lab/.env with random secrets (once).
set -euo pipefail
cd "$(dirname "$0")/../lab"
rnd() { head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-32}"; }
if [ ! -f .env ]; then
  cp .env.example .env
  for k in GLPI_DB_ROOT_PASSWORD GLPI_DB_PASSWORD GLPI_DB_RO_PASSWORD ZABBIX_DB_PASSWORD \
           GRAFANA_ADMIN_PASSWORD SMS_KEY FW_KEY WS_WEBHOOK_SECRET LAB_USERS_PASSWORD; do
    sed -i "s|^$k=change-me$|$k=$(rnd 32)|" .env
  done
  echo "lab/.env created"
fi
