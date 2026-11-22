# Proof of Concept (Nov 2026) – archived

The PoC proved the core loop: a monitored device goes down → a GLPI ticket opens with no human action.
Files here are kept as they were delivered, for traceability. The platform in the rest of this repo replaces them.

| File | What it is |
|---|---|
| `docker-compose.yml` | PoC stack: GLPI 10.0.18, Zabbix 7.0 (MySQL), n8n `latest`, 7 containers. Passwords are test defaults – never reuse them. |
| `n8n-zabbix-to-glpi.json` | PoC workflow (4 nodes). Token placeholders only. |
| `docs/` | PoC summary and setup guide (PDF) |
| `screenshots/` | Compose start, Zabbix host available, n8n run, GLPI tickets, Zabbix action log |

The demo video (`WiseSphere.mp4`, 17 MB) is not stored in Git – keep it on the shared drive.

## What the screenshots show, and what the platform changes

| Seen in the PoC | Why it matters | Platform (lab-tested) |
|---|---|---|
| Every ticket is priority **High** (`04-glpi-tickets.png`), even "Load average is too high", which Zabbix rates **Average** (`05-zabbix-action-log.png`) | Wrong priorities break SLAs and the NOC queue | Severity → urgency/impact (INT-01). Same "agent not available" problem now opens as **Medium** with the client's SLA due date |
| **Time to resolve** column empty | No SLA tracking | Tier SLA attached by GLPI rules per client (ITSM-02); WF-04 sends SMS before breach |
| Two open "agent is not available" tickets (#2, #5) that never close | Backlog fills with stale alerts | Recovery solves the ticket automatically (TC-02): **56 s** after the agent came back in the lab |
| All tickets in the root entity, title `[Zabbix] …` | No client separation, no dedupe key | Ticket in the client entity (`CL001`), title `[ZBX:<event id>] host: problem`, repeats become follow-ups |
| Webhook open to anyone; tokens pasted into the workflow | Anyone on the network can create tickets; secrets in a file | `X-WS-Secret` header (403 otherwise); tokens live in n8n credentials only |
| "Zabbix server has been restarted", "swap usage" tickets | Noise (alert fatigue, risk K-03) | Action skips suppressed (maintenance) problems and severities below Warning; storms grouped into one ticket |
| Manual set-up (~1 hour, setup guide steps 2–6) | Not repeatable per client | `make lab-bootstrap` – about 1 minute, idempotent |

## The PoC test on the platform

```bash
make lab-poc-test
```

Result (22 Nov 2026, lab): agent stopped → ticket `[ZBX:34] CL001 KWI srv01 (agent): Linux: Zabbix agent is not available (for 3m)`
in entity CL001, priority Medium, SLA attached, after **297 s** (Zabbix's 3-minute availability wait, same as the PoC);
agent started → ticket solved automatically after **56 s**.

## Note on the PoC compose comment
`glpi/glpi:10.0.18` was pinned because "GLPI 11 uses a different API path". GLPI 11 adds a new API (`/api.php/v2`)
but still serves the legacy `/apirest.php` used by the PoC – the platform runs on GLPI 11.0.13 with it.
Two GLPI 11 differences did need changes: API client and user tokens are stored encrypted (so the n8n account
authenticates with login + app token instead of a copied user token), and GLPI 10 is end-of-life (PRD §2.1).
