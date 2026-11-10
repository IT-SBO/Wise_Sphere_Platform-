# WiseSphere – Execution Plan & Proposed Timeline

**Status:** Proposal for discussion · **Date:** 10 November 2026
**Built from:** WS-PRD-001 v2.0, Board presentation (Nov 2026), WS-SOP-001 v2.0, WS-HR-001 v2.0, Technical Kit v1.1

---

## 1. What we are building (one paragraph)

WiseSphere turns WISE Group's network-solutions company into a managed services provider (MSP/MSSP).
One central **hub** (GLPI 11 for tickets and assets, Zabbix 8.0 LTS for monitoring, Wazuh 4.14 for security,
n8n 2.x for internal automation, Grafana 13 for dashboards) serves many clients. Each client site gets a small
**spoke** appliance that connects outbound only, buffers data for 72 h and discovers devices automatically.
Clients get a ticket within 60 seconds of an outage, a response within their SLA, their own portal and a monthly report.
It is sold as a monthly subscription in three packages: Silver (monitoring), Gold (monitoring + patching), Platinum (full MSSP).

## 2. Review verdict

The plan in the documents is **sound and achievable**, and the numbers reconcile:

| Check | Result |
|---|---|
| Build budget | 41,283 + 60,728 = **102,011 USD** (incl. 15% contingency ≈ 13,306) ✔ |
| Hybrid vs in-house year-1 people cost | 165,315 vs 309,720 USD → **47% lower** ✔ |
| Break-even clients | 20,270 ÷ 1,625 ≈ **13** (Phase A) · 26,170 ÷ 1,625 ≈ **17** (24/7 desk) ✔ |
| Blended revenue / client | 50% × 1,200 + 35% × 1,920 + 15% × 3,420 = **1,785 USD/month** ✔ |

It needs **five adjustments** before it is safe to start:

1. **Week 0 is too short.** The selection process in the Job Descriptions alone takes 8–10 days (sourcing → paid test → panel → contract), and hosting has to be ordered and provisioned. Make mobilisation a 4–5 week phase in which only the Solutions Architect is on full cost.
2. **The calendar ignores Ramadan 2027** (expected ≈ 8 Feb – 9 Mar, subject to moon sighting), Kuwait National & Liberation Days (25–26 Feb) and Eid al-Fitr. These fall exactly on the security, UAT and operator-training weeks. Keep engineering effort the same but stretch the calendar for client-dependent work.
3. **The Technical Kit has defects** that will block Week 1–2 (see §7). Fix them in the lab during mobilisation.
4. **Small document errors** must be fixed before anything is shared: the PRD executive summary contains raw template code (`` ` + fm(M.release_now) + ` ``), and the front matter says "34 questions" while 28 are listed.
5. **Commercial and legal prerequisites** must be closed before the first paid contract: client contract + data-processing clause, liability cap, professional-indemnity / cyber insurance, SMS provider, and a decision on the patch tool before Gold is sold.

## 3. Proposed timeline (recommended – "Realistic start")

Work week is Sunday–Thursday. Weeks are labelled W1… from the start of the build.

| Phase | Dates | Duration | Outcome | Gate (date) |
|---|---|---|---|---|
| **A. Decision & real quotes** | 10 – 26 Nov 2026 | 3 weeks | Real quotes (hosting, 2–3 per role, SMS, insurance); Architect confirmed; pilot client shortlisted; docs corrected | **Board decision – Thu 26 Nov 2026** |
| **0. Mobilisation** | 29 Nov – 31 Dec 2026 | 5 weeks (incl. year-end week) | Specialists selected & contracted; hosting live; decisions D-01/02/03/06; pilot data pack signed; kit fixed and hub running in a lab with simulated devices | **G0 – Thu 31 Dec 2026** |
| 1. Hub core & tenancy | 3 – 7 Jan 2027 | W1 | Hub hardened, GLPI tenants, WireGuard gateway, backups | – |
| 2. Spoke & monitoring | 10 – 21 Jan | W2–W3 | Spoke template, Zabbix + pilot proxy, discovery, 2 h tunnel-cut test | – |
| 3. Automation (MVP) | 17 – 28 Jan | W3–W4 | WF-01 + WF-04 live on the pilot | **G1 MVP – Thu 28 Jan 2027** (releases tranche 2) |
| 4. Security layer | 31 Jan – 18 Feb | W5–W7 | Wazuh, agents, tuning, WF-02, approved block | **M2 SOC live – Thu 18 Feb 2027** |
| 5. Dashboards, tests & go-live | 21 Feb – 18 Mar | W8–W11 (Ramadan, National Day, Eid) | NOC wall, client views, CSAT, stress test, DR drill, SOPs, operator training | **G2 Go-live – Thu 18 Mar 2027** |
| 6. Hypercare | 21 Mar – 15 Apr | W12–W15 | 3+ clients onboarded, tuning, first monthly reports, handover to L1/L2 | **M4 Steady state – Thu 15 Apr 2027** |

**Why the calendar is longer than "8 weeks" but the cost is not:** engineering effort stays as budgeted (remote specialists are paid per milestone/day, not per calendar week). Only the Solutions Architect's time grows: about +4 weeks in mobilisation and +3 weeks around Ramadan ≈ **+10,500 USD**, which fits inside the 13,306 USD contingency but uses most of it. We recommend asking the Board to approve this as a small separate mobilisation line so the contingency stays available for build risks.

### Alternative – "Fast track" (not recommended)

Board decision 26 Nov → 1-week W0 from 29 Nov → G1 Thu 31 Dec 2026 → G2 Thu 28 Jan 2027 → hypercare during Ramadan.
It finishes before Ramadan but needs four contracts signed in one week, puts the MVP gate on New Year's Eve, and leaves zero slack. Choose it only if the Architect and at least two specialists are already identified today.

## 4. After go-live – business roadmap

Month 1 = January 2027 (start of build), as in the financial model.

| When | Milestone |
|---|---|
| Apr 2027 (M4) | Silver package on sale; pilot becomes the reference client |
| Apr – Jun 2027 | Free 30-day "network health check" campaign to existing infrastructure customers (target 10 checks → 4 contracts) |
| Apr – May 2027 (R1.5) | Patch & remote-management tool (D-05) live → **Gold on sale from June 2027** |
| Jun – Jul 2027 (R2) | Hub HA/DR site, ERP billing integration |
| From Jul 2027 | Platinum push to regulated sectors (1 per quarter) |
| ≈ Nov 2027 (8 clients) | Move from business hours + on-call to full 24/7 desk (6 operators) |
| ≈ Jun 2028 (month 18) | Monthly break-even – base case, ~1 new client per month |
| ≈ Jan 2029 / Sep 2029 | Full payback – incremental view (month 25) / full-cost view (month 33) |

The single biggest driver is **sales pace**. At half the planned growth the platform does not pay back within 36 months, so the sales plan must be owned by a named person from day one.

## 5. Ready-to-start checklist (all must be "yes" before W1)

| # | Item | Owner | Due |
|---|---|---|---|
| 1 | Board approval + tranche 1 (41,283 USD) released | Exec Mgmt | 26 Nov |
| 2 | Solutions Architect named and signed | Exec Mgmt | 26 Nov |
| 3 | Real quotes replace assumptions (hosting, 2–3 per role, SMS, insurance) | Architect | 26 Nov |
| 4 | Pilot client signed (1 site, 50–300 devices, cooperative IT) + named contact | Sales / Architect | 10 Dec |
| 5 | DevOps, NOC and SOC specialists contracted (NDA + IP, MFA accounts) | Architect | 17 Dec |
| 6 | Hub hosting provisioned in-region (3 VMs + staging + off-site backup) | DevOps | 24 Dec |
| 7 | Technical Kit defects fixed; hub running in lab with simulated devices | DevOps / NOC | 31 Dec |
| 8 | Decisions D-01 (GLPI 11), D-02 (hosting), D-03 (pilot), D-06 (vault) taken | Exec / Architect | 31 Dec |
| 9 | Pilot data pack signed (diagram, IP ranges, SNMP access, critical servers) | Pilot client | 31 Dec |
| 10 | 3 existing support staff named for operator training from W6 | Ops Manager | 31 Dec |
| 11 | Client contract template + data-processing clause reviewed by legal | Legal | before first paid contract |
| 12 | Professional-indemnity / cyber insurance quoted | Finance | before first paid contract |

## 6. Gates – how we will know it works

| Gate | Pass criteria (all measured) | If it fails |
|---|---|---|
| **G0** Mobilised | All 4 engineers contracted with MFA; pilot data pack signed; hub VMs live | Delay W1; no contractor spend yet |
| **G1** MVP (Thu 28 Jan) | Spoke online ≤ 60 min; ≥ 95% devices discovered; outage → GLPI ticket ≤ 60 s; recovery auto-solves; storm grouped; SLA SMS received; no open P1 defect | Extend 2 weeks (~8k from contingency), reduce scope, or stop with spend capped at 41,283 USD |
| **M2** SOC live (Thu 18 Feb) | Agents on all pilot critical servers; brute-force → ticket + SMS ≤ 2 min; block only after approval | Fix in W8, keep G2 date |
| **G2** Go-live (Thu 18 Mar) | NOC wall + client org live; CSAT recorded; 500 alerts in 10 min without loss; DR restore ≤ 8 h; SOPs signed; L1/L2 trained | Hold sales; fix and re-test |
| **M4** Steady state (Thu 15 Apr) | ≥ 3 more clients live; ≥ 90% automated intake; false positives ≤ 20%; first monthly reports sent | Extend hypercare; retainers continue |

## 7. Technical Kit – fixes needed before W1

| # | File | Issue | Fix |
|---|---|---|---|
| T-01 | `hub/docker-compose.yml` | Zabbix port bound to `${WG_HUB_IP}:10051`, but the WireGuard address lives inside the `wireguard` container, not on the host – the bind fails and spoke traffic to 10.250.0.1 ends at the WG container | Run WireGuard on the host (or `network_mode: host`) and route the WG subnet to the Zabbix server; or DNAT 10051 inside the WG container |
| T-02 | `spoke/docker-compose.yml` | `wazuh-relay` shares the WG container's network namespace and publishes no port, so client servers on the LAN cannot reach 1514/1515 | Publish 1514/1515 on the `wg` service (LAN side) and open them in ufw for the client LAN only |
| T-03 | `hub/.env.example` | `STAFF_CIDRS` includes `10.250.0.0/16` (all client tunnels) → client sites could reach the n8n and Zabbix UIs | Use a separate staff VPN subnet; never include spoke subnets |
| T-04 | `hub/docker-compose.yml` | n8n is behind the `staff-only` allow-list, but Zabbix and the Wazuh VM must call its webhooks | Separate router for `/webhook/*` allowing only the Zabbix server and Wazuh manager addresses |
| T-05 | `hub/docker-compose.yml` | `--entrypoints.websecure.http.tls.options=modern@file` references a file provider that is not configured → Traefik error | Add a file provider with the TLS options, or remove the flag |
| T-06 | compose + `.env` | Unpinned images (`timescaledb:latest-pg16`, `wireguard:latest`, `alpine-8.0-latest`, `N8N_TAG=2`) break NFR-OPS-03 | Pin exact patch versions / digests |
| T-07 | `ops/backup-hub.sh` | Wazuh (keys, rules, indexer snapshots) is not backed up although SOP-07 requires it | Add a Wazuh backup job on the Wazuh VM |
| T-08 | `spoke/install-spoke.sh` | SSH allowed from all of `10.0.0.0/8`; installer pipes `get.docker.com` into `sh` | Restrict SSH to the client admin subnet; install Docker from the signed apt repo |

## 8. Decisions we need from management now

1. **Start option** – Realistic start (recommended) or Fast track.
2. **Board date** – confirm Thursday 26 November 2026.
3. **Solutions Architect** – existing person or new hire? (critical path for everything else)
4. **Pilot client** – which existing customer?
5. **Mobilisation line** – approve ~10,500 USD separately, or accept it from contingency.

## 9. Presenting to the community

For a wider audience, present §1, §3 (timeline), §6 (gates) and the after-launch roadmap. Keep compensation bands, day rates and client pricing internal; show the investment only as the headline (staged release, Week-4 gate).
