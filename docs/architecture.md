# WiseSphere – architecture and build status

Source of truth: **WS-PRD-001 v2.0** (PRD & implementation plan) and the PoC (Zabbix 7 → n8n → GLPI 10, Nov 2026).
This repository is the platform as code (NFR-OPS-04): Compose stacks, workflows, provisioning, dashboards.
Per PRD R-06 there is **no custom UI in Release 1** – GLPI is the ticketing tool and client portal, Grafana the dashboards.

## Data flow

```
 client site (spoke VM, outbound only)                     central hub
 ┌──────────────────────────────┐   WireGuard UDP 51820   ┌──────────────────────────────────────────────┐
 │ Zabbix active proxy (PSK,72h)├────────────────────────►│ Zabbix 8.0 server ──action──► media type      │
 │ GLPI Agent  --tag=CLnnn      │   TCP 10051 in tunnel   │                         ws.event.v1 + secret │
 │ Wazuh relay (optional)       │                         │                              ▼               │
 └──────────────▲───────────────┘                         │  n8n 2.x (internal) WF-01..04, WF-99         │
   Wazuh agents on client servers ──1514/1515──► relay ──►│  Wazuh 4.14 ──custom-n8n──► ws.secevent.v1   │
                                                          │        ▼ GLPI REST API (app token + svc acct) │
                                                          │  GLPI 11: entity per client, SLA rules, CSAT  │
                                                          │  Grafana 13: NOC wall, client orgs            │
                                                          │  SMS provider (D-04) ◄── on-call alerts       │
                                                          └──────────────────────────────────────────────┘
```

## The client code is the key (PRD §4.7)
`config/tenants.yaml` → `CLnnn` drives every tool: GLPI entity tag, GLPI Agent tag, Zabbix host groups
`CLnnn/SITE/Network|Servers` and host tag `client=CLnnn`, Wazuh labels, WireGuard subnet `10.250.nnn.0/24`.

## Workflows (n8n) – logic in `n8n/src/wslib.js`, unit tested, inlined by `n8n/build_workflows.py`
| Flow | Trigger | Behaviour |
|---|---|---|
| WF-01 Zabbix→GLPI (INT-01) | `POST /webhook/zbx-event` | create ticket `[ZBX:<event>]` in client entity with asset link; repeat → follow-up; recovery → solution (auto-solve); >10 problems / 5 min → one `[STORM:CLnnn]` parent + SMS; disaster → SMS; unknown client → root entity, flagged |
| WF-02 Wazuh→GLPI (INT-02) | `POST /webhook/wazuh-alert` | level ≥10 → SOC ticket `[WZH:agent:rule]`, dedupe while open; ≥12 → SMS. No automatic blocking (C-02) |
| WF-03 CSAT (INT-03) | daily 08:10 | survey score ≤2 → private service-recovery task for WS-SDM |
| WF-04 SLA watch (INT-04) | every 5 min | TTO/TTR at T-15 and at breach → SMS; in business hours to the desk, after hours only priorities the tier covers |
| WF-99 failures (INT-07) | any workflow error | SMS to the integration engineer; failed executions kept for retry |

All webhooks require the `X-WS-Secret` header (INT-05). Zabbix delivers one alert at a time
(`maxsessions=1`) so dedupe and storm grouping never race; measured ~1 s per alert in the lab.

## Status against the plan

| WBS | Item | Status in this repo |
|---|---|---|
| 1.1 | Hub stack, TLS, hardening | `hub/docker-compose.yml`, `hub/traefik/` – T-01, T-03…T-06 fixed. OS hardening (CIS L1) is a host task |
| 1.2/1.3 | Tenancy, GLPI entities, SLAs, calendars, request sources, survey | `glpi/provision_glpi.py` ✅ tested |
| 1.4 | WireGuard gateway | `ops/wg-hub-setup.sh`, `scripts/new_spoke.py` |
| 1.5 | Backups + off-site | `ops/backup-hub.sh` (restic, incl. Wazuh T-07), `ops/restore-drill.md` |
| 2.1 | Spoke template | `spoke/` – T-02, T-08 fixed; proxy PSK link ✅ tested in lab |
| 2.2/2.3 | Zabbix server, media type, action, groups | `zabbix/provision_zabbix.py` ✅ tested. Standard template library (SNMP, UPS, web/cert) – next |
| 3.1/3.2 | n8n + WF-01 | ✅ TC-01..TC-04 pass end to end |
| 3.3 | WF-04 SLA + SMS | ✅ TC-06 passes with the SMS mock; real provider = decision D-04 |
| 3.4 | Platform self-monitoring | n8n `/metrics` + WF-99; Zabbix checks for proxy last-seen / certs – next |
| 4.x | Wazuh layer | integration script + WF-02 ✅ (TC-07); Wazuh deployment, DLS roles, approved-block sub-flow – Phase 4 |
| 5.1 | Grafana NOC wall | `grafana/` – DSH-01/02/03 + CSAT/goal tiles; GLPI queries ✅ verified |
| 5.3 | Monthly reports | not started |
| – | Branding | `branding/` – placeholder tokens, GLPI CSS applied ✅; waiting for final design + logo |

## Version pins (NFR-OPS-03) – verified on Docker Hub, Nov 2026
GLPI 11.0.13 · MariaDB 11.8.9 · Zabbix 8.0.0 (alpine) · PostgreSQL 17.12 · TimescaleDB 2.32.1-pg17 ·
n8n 2.49.3 · Grafana 13.2.6 · Traefik v3.7.19 · nginx 1.30.7 · restic 0.19.1. GLPI Agent 1.18 (installer from GitHub releases).
