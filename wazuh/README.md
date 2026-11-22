# Wazuh 4.14 security layer (Phase 4, WBS 4.1–4.5)

Wazuh runs on its own VM (HUB-WZH-01, 8 vCPU / 32 GB / 1 TB) using the official
`wazuh-docker` **single-node** deployment pinned to `4.14.x` (5.0 is beta and not
upgradeable in place – lab only). Cluster in Release 2.

| File | Purpose |
|---|---|
| `custom-n8n.py` | Integration script → `/var/ossec/integrations/custom-n8n` (owner `root:wazuh`, mode 750). Converts alerts to `ws.secevent.v1` and posts them to n8n WF-02 with the shared secret. |
| `ossec-integration.xml` | `<integration>` block for the manager: level ≥ 10 only (SOC-02). |
| `agent-group-template.conf` | Per-client agent group `CLnnn-servers` with labels `client` / `site` (§4.7) and FIM/SCA policy (SOC-01). |

Agents reach the manager directly (cloud servers) or through the spoke **Wazuh relay**
(`spoke/docker-compose.yml`, profile `wazuh-relay`) so client servers never need internet egress (C-03).

## Isolation (C-05)
Create one indexer role per client with document-level security on `agent.labels.client: CLnnn`
and map client users to it; SOC staff use the full role. Tested in TC-10.

## Containment (C-02, SOC-04)
There is **no automatic blocking**. WF-02 opens a ticket and pages the SOC. A block is executed only
after the SOC engineer approves it in the GLPI ticket, with a 24 h expiry and an allow-list of critical
IPs (approved-block sub-flow, WBS 4.4 – to be built in Phase 4 against the pilot client's firewall API).

## Test
```bash
# from the Wazuh manager, with a sample alert
/var/ossec/integrations/custom-n8n /tmp/sample-alert.json "$WS_WEBHOOK_SECRET" https://n8n.internal.example.com/webhook/wazuh-alert
```
