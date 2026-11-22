#!/usr/bin/env bash
# Create lab/.env with random secrets (once) and an initial lab/n8n.env.
set -euo pipefail
cd "$(dirname "$0")/../lab"
rnd() { head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-32}"; }
if [ ! -f .env ]; then
  cp .env.example .env
  for k in GLPI_DB_ROOT_PASSWORD GLPI_DB_PASSWORD GLPI_DB_RO_PASSWORD ZABBIX_DB_PASSWORD N8N_DB_PASSWORD \
           N8N_ENCRYPTION_KEY GRAFANA_ADMIN_PASSWORD SMS_KEY WS_WEBHOOK_SECRET; do
    sed -i "s|^$k=change-me$|$k=$(rnd 32)|" .env
  done
  echo "lab/.env created"
fi
[ -f n8n.env ] || { cp n8n.env.base n8n.env; echo "lab/n8n.env created"; }
