# WiseSphere Platform

Managed services (MSP / MSSP) platform for WISE Group – built from GLPI 11, Zabbix 8.0 LTS, Wazuh 4.14 and
Grafana 13 in a hub-and-spoke design, connected by a WiseSphere integration service written in **C# / .NET 10**, with a WiseSphere portal in **Angular 22 + ASP.NET Core**, as specified in **WS-PRD-001 v2.0**.

The PoC proved the core loop (device down → ticket, no human). This repository turns that into the
Release-1 platform: multi-client, deduplicated, storm-safe, SLA-aware, secured, and repeatable from Git.

> **Development document:** [`docs/WiseSphere_Development_Document.docx`](docs/WiseSphere_Development_Document.docx) – architecture, code, APIs, set-up, testing, security and the development guide.
>
> Design and logo: everything visual lives in [`branding/`](branding/README.md). Current colours are placeholders;
> drop in the final files and run one command.

## What is in the box

| Folder | Contents |
|---|---|
| `config/` | `tenants.yaml` (client codes CLnnn, tier, country, sites) and `sla.yaml` (SLA matrix per package) – the single source of truth |
| `src/` | C# / .NET 10: `WiseSphere.Core` (event contracts + decision rules), `WiseSphere.Glpi` (GLPI API client), `WiseSphere.Integration` (webhooks, SLA watch, CSAT review, SMS), `WiseSphere.Portal` (portal back end) |
| `web/portal/` | Angular 22 portal front end (English / Arabic, brand tokens) |
| `tests/` | xUnit tests (`WiseSphere.Core.Tests`), Python tooling tests, end-to-end lab suite, TC-09 load test |
| `glpi/` | `provision_glpi.py` – entities, SLAs, calendars, rules, survey, groups, service account, API client |
| `zabbix/` | `provision_zabbix.py` – media type, action, host groups, notify user; webhook script |
| `wazuh/` | Integration script and config for the security layer (Phase 4) |
| `grafana/` | Datasources + NOC Global Overview dashboard (DSH-01/02/03, CSAT, PRD goals) |
| `hub/` | Production hub stack (Traefik, GLPI, integration service, Grafana, Zabbix) with the Technical Kit fixes |
| `spoke/` | Client-site appliance: Zabbix active proxy (PSK, 72 h buffer), Wazuh relay, installer |
| `lab/` | Whole platform on one machine with simulated devices and an SMS mock |
| `ops/` | Backups (restic, off-site), WireGuard hub setup, restore drill |
| `scripts/` | Lab bootstrap, `new_spoke.py` (onboard a site), `dotnet.sh` (.NET SDK in a container) |
| `docs/` | [Architecture & build status](docs/architecture.md), PRD, execution plan, launch plan |
| `poc/` | The original PoC (compose, workflow, PDFs, screenshots) and [what the platform changes](poc/README.md) |

## Try it: the lab (about 5 minutes)

Needs Docker with Compose, Python 3.11+, ~6 GB RAM. The .NET SDK is optional (`scripts/dotnet.sh` runs it in a container).

```bash
pip install -r requirements.txt
```

```bash
make lab-up
```

```bash
make lab-bootstrap
```

```bash
make lab-test
```

| Service | URL | Login |
|---|---|---|
| GLPI | http://localhost:8090 | glpi / glpi (lab only) |
| Zabbix | http://localhost:8081 | Admin / zabbix (lab only) |
| **WiseSphere portal** | http://localhost:8089 | `noc.operator` (staff) or `client.cl001` (client); password `LAB_USERS_PASSWORD` in `lab/.env` |
| Integration service | http://localhost:8088/readyz, `/metrics` | – (webhooks need the `X-WS-Secret` header) |
| SMS mock | http://localhost:8099/messages | – |
| Grafana | http://localhost:3000 | `docker compose --profile grafana up -d grafana` in `lab/`, admin / see `lab/.env` |

`make lab-test` drives real Zabbix problems (via `zabbix_sender`) through the C# integration service into GLPI and checks:

| Test | Result in the lab (C# service) |
|---|---|
| TC-01 problem → ticket in client entity with tier SLA, NOC group, source "Monitoring" | ✅ ~1.2 s (target ≤ 60 s) |
| TC-02 recovery → ticket solved automatically | ✅ |
| TC-03 repeated event → follow-up, no duplicate | ✅ |
| TC-04 15 devices down → one STORM parent ticket + SMS | ✅ |
| TC-06 ticket 10 min from breach → "AT RISK" SMS | ✅ |
| TC-07 Wazuh level 10 → SOC ticket, level 12 → SMS, repeat deduped | ✅ |
| Wrong webhook secret → 403, bad payload → 400 | ✅ |
| TC-10 portal: client sees only its own tickets, CSRF and login checks | ✅ |
| TC-09 500 alerts (`make lab-load-test`) | ✅ 20 s, none lost, p95 0.49 s (target: 10 min, p95 ≤ 60 s) |

The PoC test also runs on the platform: `make lab-poc-test` stops the agent container (ticket after ~5 min, priority from Zabbix severity, client SLA) and starts it again (ticket solved automatically in ~1 min).

## Differences from the PoC

| PoC | Platform |
|---|---|
| GLPI 10.0.18, Zabbix 7.0, n8n | GLPI 11.0.13, Zabbix 8.0 LTS (PRD §2.1); n8n replaced by a C# .NET 10 service |
| Tokens pasted into the workflow JSON | Secrets only in environment / vault; nothing secret in Git (NFR-SEC-04) |
| Open webhook | Shared-secret header, 403 otherwise (INT-05) |
| Every alert = new ticket in the root entity | Client entity by tag, dedupe, storm grouping, asset link, auto-solve on recovery |
| Manual GLPI / Zabbix set-up | Idempotent provisioning scripts |
| Time zone fixed by hand | Done by the bootstrap |

## Going to production (hub)

1. Hosts per PRD §5.2; on HUB-APP-01 run `ops/wg-hub-setup.sh` (WireGuard on the host).
2. `cp hub/.env.example hub/.env`, fill names and secrets from the vault; `chmod 600 hub/.env`.
3. `docker compose --profile app up -d` (and `--profile zabbix` on HUB-ZBX-01).
4. GLPI: enable the API, then `glpi/provision_glpi.py --api-cidr <BACKEND_SUBNET> …` (see `scripts/bootstrap_lab.sh` for the exact sequence).
5. `python3 scripts/glpi_ids_env.py hub/glpi-ids.json hub/.env`, then `docker compose --profile app up -d ws-integration`.
6. Zabbix: `zabbix/provision_zabbix.py --webhook-url https://<integration-host>/webhook/zbx-event …`.
7. Per client site: add it to `config/tenants.yaml`, re-run the provisioning, `scripts/new_spoke.py CLnnn SITE …`,
   then `spoke/install-spoke.sh <bundle>` on the client VM (SOP-01, timed ≤ 60 min).
8. Install `ops/backup-hub.sh` as a nightly timer and run the restore drill before go-live (TC-11).

## Development

```bash
make check
```

Business rules live in `src/WiseSphere.Core` (pure C#, no I/O) with xUnit tests in `tests/WiseSphere.Core.Tests`.
Change a rule → add/adjust a test → `make unit` → `make image` → `make lab-test`. Change control through Git (SOP-06).

## Open decisions that affect the build (PRD §13.3)
D-04 SMS provider (the service sends `POST {to, message, source}` with a key header – adapt `SmsSender` if the provider differs) ·
D-05 patch tool (Gold package) · D-06 credential vault · D-07 Grafana client access model.
