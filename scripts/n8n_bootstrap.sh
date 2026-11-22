#!/usr/bin/env bash
# Load WiseSphere credentials + workflows into a running n8n container and publish them.
#
#   scripts/n8n_bootstrap.sh <n8n-container>
#
# Needs in the environment: WS_GLPI_APP_TOKEN, WS_GLPI_SVC_USER, WS_GLPI_SVC_PASSWORD,
# WS_WEBHOOK_SECRET, SMS_KEY (sent as X-SMS-Key; adapt SMS_HEADER for the chosen provider, D-04).
# Credentials are written to a temp file inside the container and deleted right after import:
# they never touch Git or the workflow JSON (NFR-SEC-04).
set -euo pipefail
C="${1:?usage: n8n_bootstrap.sh <n8n-container>}"
: "${WS_GLPI_APP_TOKEN:?}" "${WS_GLPI_SVC_PASSWORD:?}" "${WS_WEBHOOK_SECRET:?}" "${SMS_KEY:?}"
SVC_USER="${WS_GLPI_SVC_USER:-svc-n8n}"
SMS_HEADER="${SMS_HEADER:-X-SMS-Key}"
DIR="$(cd "$(dirname "$0")/.." && pwd)"

TMP="$(mktemp)"; trap 'rm -f "$TMP"' EXIT
python3 - "$TMP" <<'PY'
import base64, json, os, sys
basic = base64.b64encode(f"{os.environ.get('WS_GLPI_SVC_USER','svc-n8n')}:{os.environ['WS_GLPI_SVC_PASSWORD']}".encode()).decode()
creds = [
  {"id": "wsGlpiApiCred001", "name": "WS GLPI API", "type": "httpCustomAuth",
   "data": {"json": json.dumps({"headers": {"App-Token": os.environ["WS_GLPI_APP_TOKEN"], "Authorization": f"Basic {basic}"}})}},
  {"id": "wsWebhookSecret1", "name": "WS Webhook Secret", "type": "httpHeaderAuth",
   "data": {"name": "X-WS-Secret", "value": os.environ["WS_WEBHOOK_SECRET"]}},
  {"id": "wsSmsProvider001", "name": "WS SMS Provider", "type": "httpHeaderAuth",
   "data": {"name": os.environ.get("SMS_HEADER", "X-SMS-Key"), "value": os.environ["SMS_KEY"]}},
]
json.dump(creds, open(sys.argv[1], "w"))
PY
docker cp "$TMP" "$C":/tmp/ws-creds.json
docker exec -u root "$C" chown node /tmp/ws-creds.json
docker exec "$C" sh -c 'n8n import:credentials --input=/tmp/ws-creds.json; rc=$?; rm -f /tmp/ws-creds.json; exit $rc'

# Workflows: mounted read-only at /opt/wisesphere/workflows, or copied in
if ! docker exec "$C" test -d /opt/wisesphere/workflows; then
  docker exec -u root "$C" mkdir -p /opt/wisesphere/workflows
  docker cp "$DIR/n8n/workflows/." "$C":/opt/wisesphere/workflows/
fi
docker exec "$C" n8n import:workflow --separate --input=/opt/wisesphere/workflows
for id in wsWF99ErrorAlert wsWF01ZbxToGlpi0 wsWF02WzhToGlpi0 wsWF03CsatReview wsWF04SlaWatch00; do
  docker exec "$C" n8n publish:workflow --id="$id"
done
echo "n8n: credentials imported, workflows published. Restart n8n to (re)activate: docker restart $C"
