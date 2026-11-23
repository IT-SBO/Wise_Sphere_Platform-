# WiseSphere – architecture and build status

Source of truth: **WS-PRD-001 v2.0** (PRD & implementation plan) and the PoC (Zabbix 7 → n8n → GLPI 10, Nov 2026).
This repository is the platform as code (NFR-OPS-04): Compose stacks, workflows, provisioning, dashboards.
Decision (Nov 2026): custom code is **C# / .NET 10** (integration service) and **Angular** (WiseSphere portal, pending management sign-off as a change to PRD R-06), maintained by the in-house team. n8n from the PoC is retired.

## Data flow

```
 client site (spoke VM, outbound only)                     central hub
 ┌──────────────────────────────┐   WireGuard UDP 51820   ┌──────────────────────────────────────────────┐
 │ Zabbix active proxy (PSK,72h)├────────────────────────►│ Zabbix 8.0 server ──action──► media type      │
 │ GLPI Agent  --tag=CLnnn      │   TCP 10051 in tunnel   │                         ws.event.v1 + secret │
 │ Wazuh relay (optional)       │                         │                              ▼               │
 └──────────────▲───────────────┘                         │  WiseSphere integration service (C# .NET 10) │
   Wazuh agents on client servers ──1514/1515──► relay ──►│  Wazuh 4.14 ──custom-wisesphere──► ws.secevent.v1   │
                                                          │        ▼ GLPI REST API (app token + svc acct) │
                                                          │  GLPI 11: entity per client, SLA rules, CSAT  │
                                                          │  Grafana 13: NOC wall, client orgs            │
                                                          │  SMS provider (D-04) ◄── on-call alerts       │
                                                          └──────────────────────────────────────────────┘
```

## The client code is the key (PRD §4.7)
`config/tenants.yaml` → `CLnnn` drives every tool: GLPI entity tag, GLPI Agent tag, Zabbix host groups
`CLnnn/SITE/Network|Servers` and host tag `client=CLnnn`, Wazuh labels, WireGuard subnet `10.250.nnn.0/24`.

## Integration service (C# .NET 10) – `src/`
| Function | Trigger | Behaviour |
|---|---|---|
| INT-01 Zabbix→GLPI | `POST /webhook/zbx-event` | ticket `[ZBX:<event>]` in client entity with asset link; repeat → follow-up; recovery → solution (auto-solve); >10 problems / 5 min → one `[STORM:CLnnn]` parent + SMS; disaster → SMS; unknown client → root entity, flagged |
| INT-02 Wazuh→GLPI | `POST /webhook/wazuh-alert` | level ≥10 → SOC ticket `[WZH:agent:rule]`, dedupe while open; ≥12 → SMS. No automatic blocking (C-02) |
| INT-03 CSAT review | daily 08:10 local | survey score ≤2 → private service-recovery task for WS-SDM |
| INT-04 SLA watch | every 5 min | TTO/TTR at T-15 and at breach → SMS; desk in business hours, after hours only the priorities the tier covers |
| INT-07 failures | any processing error | SMS to the integration engineer (max 1 per 10 min per error kind); Zabbix retries the webhook |

Concurrency: one lock per client – a client's events are processed in order (dedupe and storm grouping stay
correct) while different clients run in parallel; GLPI sessions are pooled. Measured: 500 alerts in ~20 s, p95 0.49 s.

## Status against the plan

| WBS | Item | Status in this repo |
|---|---|---|
| 1.1 | Hub stack, TLS, hardening | `hub/docker-compose.yml`, `hub/traefik/` – T-01, T-03…T-06 fixed. OS hardening (CIS L1) is a host task |
| 1.2/1.3 | Tenancy, GLPI entities, SLAs, calendars, request sources, survey | `glpi/provision_glpi.py` ✅ tested |
| 1.4 | WireGuard gateway | `ops/wg-hub-setup.sh`, `scripts/new_spoke.py` |
| 1.5 | Backups + off-site | `ops/backup-hub.sh` (restic, incl. Wazuh T-07), `ops/restore-drill.md` |
| 2.1 | Spoke template | `spoke/` – T-02, T-08 fixed; proxy PSK link ✅ tested in lab |
| 2.2/2.3 | Zabbix server, media type, action, groups | `zabbix/provision_zabbix.py` ✅ tested. Standard template library (SNMP, UPS, web/cert) – next |
| 3.1/3.2 | Integration service + INT-01 | ✅ TC-01..TC-04, TC-09 pass end to end (C#) |
| 3.3 | WF-04 SLA + SMS | ✅ TC-06 passes with the SMS mock; real provider = decision D-04 |
| 3.4 | Platform self-monitoring | service `/metrics`, `/readyz` + failure SMS; Zabbix checks for proxy last-seen / certs – next |
| 4.x | Wazuh layer | integration script + INT-02 ✅ (TC-07); Wazuh deployment, DLS roles, approved-block sub-flow – Phase 4 |
| 5.1 | Grafana NOC wall | `grafana/` – DSH-01/02/03 + CSAT/goal tiles; GLPI queries ✅ verified |
| 5.3 | Monthly reports | not started |
| – | Branding | `branding/` – WISE Group brand (wisegroup.trade) on portal and GLPI ✅ |

## Version pins (NFR-OPS-03) – verified on Docker Hub, Nov 2026
GLPI 11.0.13 · MariaDB 11.8.9 · Zabbix 8.0.0 (alpine) · PostgreSQL 17.12 · TimescaleDB 2.32.1-pg17 ·
Integration service: .NET SDK 10.0.404 / ASP.NET Core runtime 10.0.15 · Grafana 13.2.6 · Traefik v3.7.19 · nginx 1.30.7 · restic 0.19.1. GLPI Agent 1.18 (installer from GitHub releases).
