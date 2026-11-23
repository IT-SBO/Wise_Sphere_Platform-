// Builds docs/WiseSphere_Development_Document.docx
const fs = require('fs');
const path = require('path');
const {
  Document, Packer, Paragraph, TextRun, HeadingLevel, Table, TableRow, TableCell, WidthType, ShadingType,
  AlignmentType, ImageRun, LineRuleType, PageBreak, TableOfContents, Header, Footer, PageNumber, LevelFormat, BorderStyle,
} = require('docx');

const REPO = require('path').resolve(__dirname, '../..');
const OUT = path.join(REPO, 'docs/WiseSphere_Development_Document.docx');
const W = 9638; // A4 content width in DXA (margins 1134)
const FONT = 'Arial';
const NAVY = '1D273B', BLUE = '1F4FB3', GREY = '66728A', HEAD_FILL = 'E8EEF8', ZEBRA = 'F6F8FB';

// ---------------------------------------------------------------- helpers
const runs = (text, opts = {}) => {
  // **bold** and `code` inline markup
  const parts = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`)/g;
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) parts.push(new TextRun({ text: text.slice(last, m.index), ...opts }));
    const t = m[0];
    if (t.startsWith('**')) parts.push(new TextRun({ text: t.slice(2, -2), bold: true, ...opts }));
    else parts.push(new TextRun({ text: t.slice(1, -1), font: 'Consolas', size: (opts.size || 21) - 1, color: '233A6E', ...opts }));
    last = m.index + t.length;
  }
  if (last < text.length) parts.push(new TextRun({ text: text.slice(last), ...opts }));
  return parts;
};
const P = (text, opts = {}) => brk(new Paragraph({ children: runs(text, opts.run), spacing: { after: 120 }, ...opts.para }));
const H1 = (t) => brk(new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)], pageBreakBefore: true }));
const H2 = (t) => brk(new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(t)] }));
const H3 = (t) => brk(new Paragraph({ heading: HeadingLevel.HEADING_3, children: [new TextRun(t)] }));
const B = (text, level = 0) => brk(new Paragraph({ children: runs(text), numbering: { reference: 'bullets', level }, spacing: { after: 60 } }));
// Each run of consecutive N() steps restarts at 1: any other block in between starts a new list instance.
let listInstance = 0, newList = true;
const N = (text) => { if (newList) { listInstance++; newList = false; }
  return new Paragraph({ children: runs(text), numbering: { reference: 'steps', level: 0, instance: listInstance }, spacing: { after: 60 } }); };
const brk = (x) => { newList = true; return x; };
const CODE = (lines) => lines.map((l, i) => new Paragraph({
  children: [new TextRun({ text: l || ' ', font: 'Consolas', size: 18, color: '1D273B' })],
  shading: { type: ShadingType.CLEAR, fill: 'F2F4F8', color: 'auto' },
  spacing: { after: i === lines.length - 1 ? 160 : 0, line: 260 },
  indent: { left: 120, right: 120 },
}));
const NOTE = (text) => brk(new Paragraph({
  children: runs(text, { size: 20 }),
  shading: { type: ShadingType.CLEAR, fill: 'FFF7E0', color: 'auto' },
  border: { left: { style: BorderStyle.SINGLE, size: 18, color: 'F59F00', space: 8 } },
  spacing: { before: 80, after: 160 }, indent: { left: 160 },
}));

function table(header, rows, widths) {
  newList = true;
  const total = widths.reduce((a, b) => a + b, 0);
  const scaled = widths.map((w) => Math.round((w / total) * W));
  scaled[scaled.length - 1] += W - scaled.reduce((a, b) => a + b, 0);
  const cell = (text, i, head, zebra) => new TableCell({
    width: { size: scaled[i], type: WidthType.DXA },
    shading: head ? { type: ShadingType.CLEAR, fill: HEAD_FILL, color: 'auto' } : zebra ? { type: ShadingType.CLEAR, fill: ZEBRA, color: 'auto' } : undefined,
    margins: { top: 60, bottom: 60, left: 100, right: 100 },
    children: String(text).split('\n').map((line) => new Paragraph({ children: runs(line, { size: 18, bold: head || undefined, color: head ? NAVY : undefined }), spacing: { after: 20 } })),
  });
  return new Table({
    width: { size: W, type: WidthType.DXA },
    columnWidths: scaled,
    rows: [
      new TableRow({ tableHeader: true, children: header.map((h, i) => cell(h, i, true)) }),
      ...rows.map((r, ri) => new TableRow({ cantSplit: true, children: r.map((c, i) => cell(c, i, false, ri % 2 === 1)) })),
    ],
  });
}
const gap = () => new Paragraph({ children: [], spacing: { after: 120 } });
function img(file, widthPx, caption) {
  newList = true;
  const buf = fs.readFileSync(path.join(REPO, 'docs/screenshots', file));
  const w = buf.readUInt32BE(16), h = buf.readUInt32BE(20);
  const width = widthPx, height = Math.round((h / w) * widthPx);
  return [
    new Paragraph({ alignment: AlignmentType.CENTER, spacing: { before: 120, after: 60, line: 240, lineRule: LineRuleType.AUTO }, keepNext: true,
      children: [new ImageRun({ type: 'png', data: buf, transformation: { width, height } })] }),
    new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 200 }, children: [new TextRun({ text: caption, italics: true, size: 18, color: GREY })] }),
  ];
}

// ---------------------------------------------------------------- content
const content = [];
const add = (...xs) => xs.flat().forEach((x) => content.push(x));

// Cover
add(
  new Paragraph({ spacing: { before: 2600, after: 120 }, children: [new TextRun({ text: 'WiseSphere', size: 64, bold: true, color: NAVY })] }),
  new Paragraph({ spacing: { after: 120 }, children: [new TextRun({ text: 'Managed Services (MSP / MSSP) Platform', size: 32, color: BLUE })] }),
  new Paragraph({ spacing: { after: 600 }, children: [new TextRun({ text: 'Development Document – Release 1', size: 40, bold: true, color: NAVY })] }),
  table(['Item', 'Detail'], [
    ['Document ID', 'WS-DEV-001'],
    ['Version / status', 'v1.1 – Release 1 feature-complete in the lab; open decisions listed in section 15'],
    ['Date', '22 November 2026'],
    ['Audience', 'In-house C# / Angular development team, Solutions Architect, DevOps / integration engineer, QA'],
    ['Based on', 'WS-PRD-001 v2.0 (PRD & Implementation Plan), WiseSphere Execution Plan, PoC summary and setup guide (Nov 2026)'],
    ['Source code', 'Git repository `IT-SBO/Wise_Sphere_Platform-`, branch `claude/trusting-knuth-hpcbuz`'],
    ['Technology decision', 'C# / .NET 10 (back end) and Angular 22 (front end), maintained by the in-house team'],
  ], [28, 72]),
  gap(),
  NOTE('**Design and logo:** the visual design is not final. All colours, fonts and the logo come from one file (`branding/tokens.json`). When the final design arrives it is applied without code changes (section 8.6).'),
);

// TOC
add(
  new Paragraph({ pageBreakBefore: true, children: [new TextRun({ text: 'Contents', bold: true, size: 32, color: NAVY })], spacing: { after: 200 } }),
  new TableOfContents('Contents', { hyperlink: true, headingStyleRange: '1-2' }),
  P('Right-click the table and choose **Update Field** if page numbers are missing.', { run: { size: 18, color: GREY, italics: true } }),
);

// 1
add(H1('1. Purpose and scope'),
  P('This document explains how the WiseSphere platform is built, so that the in-house team can run it, change it and extend it without the original authors. It covers the architecture, the code, the data model, configuration, security, testing, deployment and day-to-day development.'),
  H2('1.1 What WiseSphere does'),
  P('WiseSphere turns WISE Group’s network-solutions company into a managed services provider. One central **hub** watches, protects and supports many client networks. Each client site gets a small **spoke** appliance. Every monitoring alert and security event becomes a ticket in the right client’s queue automatically, measured against the client’s contractual SLA.'),
  H2('1.2 Scope of this release'),
  table(['In this document (built and tested)', 'Not yet built (planned, see section 15)'], [
    ['Integration service (C#): Zabbix → GLPI, Wazuh → GLPI, SLA watch, CSAT review, on-call SMS', 'Wazuh deployment itself and per-client index security (Phase 4, needs the Wazuh VM)'],
    ['Approved firewall block with auto-expiry (SOC-04): FortiGate and generic HTTP adapters', 'Executive report (RPT-02) and ERP billing link (Release 2)'],
    ['WiseSphere portal (Angular + ASP.NET Core): dashboard, tickets, monitoring, requests, monthly report (RPT-01), EN/AR', 'Patch tool (D-05), credential vault (D-06)'],
    ['Zabbix device-role templates (NOC-02) and platform self-monitoring (NFR-OPS-01)', 'Hub HA / DR site (Release 2)'],
    ['Hub and spoke stacks, provisioning scripts, Grafana NOC wall, backups and restore drill', ''],
    ['Lab with automated end-to-end, load and PoC tests; Windows run guide', ''],
  ], [55, 45]),
  H2('1.3 How to read it'),
  B('Developers new to the project: sections 2, 3, 5 and 13.'),
  B('DevOps / operations: sections 9, 10 and 12.'),
  B('QA: section 11. Security reviewers: section 10.'),
  B('Management: sections 2, 11 and 15.'),
);

// 2
add(H1('2. Solution overview and key decisions'),
  P('WiseSphere is not written from scratch. Mature open-source products do the heavy lifting; custom code connects them and adds the WiseSphere experience. This keeps licence costs at zero per device and focuses our code on what makes the service different.'),
  ...img('architecture.png', 640, 'Figure 1 – Components and data flow. Blue boxes are custom code in this repository.'),
  H2('2.1 Decisions'),
  table(['#', 'Decision', 'Reason'], [
    ['DD-01', 'Custom code in **C# / .NET 10** (LTS) and **Angular 22**', 'In-house team skills; strong typing and tests; long-term support; high performance'],
    ['DD-02', 'Replace n8n (used in the PoC) with a C# **integration service**', 'n8n handled ~1 alert per second once alerts had to be processed one at a time to avoid duplicates; the C# service processes clients in parallel (500 alerts in ~20 s). It also removes the n8n licence limit (PRD C-01).'],
    ['DD-03', 'Keep GLPI, Zabbix, Wazuh and Grafana as products', 'Years of work to rebuild; the PRD concluded the same (Q9)'],
    ['DD-04', 'Portal users sign in with their **GLPI account**; the portal calls GLPI with the user’s own session', 'GLPI already enforces what each user may see per client; no second permission system to keep in sync (client isolation, TC-10)'],
    ['DD-05', 'One **client code** `CLnnn` links every tool', 'PRD §4.7; one key for GLPI entity, Zabbix groups, Wazuh labels, WireGuard subnet'],
    ['DD-06', 'Business rules are pure C# with no I/O (`WiseSphere.Core`)', 'Fast, deterministic unit tests; rules can be changed safely'],
    ['DD-07', 'Everything as code in Git (Compose, provisioning, dashboards, brand tokens)', 'NFR-OPS-04; repeatable environments; change control (SOP-06)'],
    ['DD-08', 'A custom portal in Release 1', '**Scope change to PRD R-06** – needs management sign-off (section 15)'],
  ], [9, 41, 50]),
  H2('2.2 Main flows'),
  N('A monitored device fails → Zabbix raises a problem → the Zabbix action calls the integration service webhook → the service finds the client’s GLPI entity and opens a ticket (or adds a follow-up, or groups it into an alert-storm ticket) → disaster-level problems page the NOC by SMS.'),
  N('The device recovers → Zabbix sends the recovery → the service adds a solution and GLPI marks the ticket solved.'),
  N('Wazuh detects an attack (level ≥ 10) → a SOC ticket is opened; level ≥ 12 also pages the SOC. Containment is never automatic (C-02).'),
  N('Every 5 minutes the service checks SLA deadlines and sends “AT RISK” (15 min before) and “BREACHED” SMS, respecting each client’s package and business hours.'),
  N('Staff and client IT managers use the portal to see their dashboard, tickets, history and current problems, raise requests and print the monthly report.'),
  N('For an opted-in client, a SOC engineer approves blocking an attacker address on the security ticket in the portal; the block is applied to the client firewall and removed automatically when it expires (max 24 h).'),
  N('Zabbix also watches the platform itself: if the integration service, the portal, GLPI or a client-site proxy stops answering, a ticket is opened like any other alert.'),
);

// 3
add(H1('3. Architecture'),
  H2('3.1 Components'),
  table(['Component', 'Technology', 'Role', 'Runs on'], [
    ['Integration service', 'C# / ASP.NET Core 10', 'Webhooks from Zabbix and Wazuh, SLA watch, CSAT review, SMS, failure paging', 'HUB-APP-01 (container `ws-integration`)'],
    ['Portal', 'Angular 22 + ASP.NET Core 10', 'Branded web app for NOC/SOC staff and client IT managers', 'HUB-APP-01 (container `ws-portal`)'],
    ['GLPI 11', 'PHP + MariaDB 11.8', 'Tickets, CMDB, SLAs, satisfaction survey, entities per client', 'HUB-APP-01'],
    ['Zabbix 8.0 LTS', 'C + PHP, PostgreSQL/TimescaleDB', 'Monitoring; active proxies at client sites', 'HUB-ZBX-01'],
    ['Wazuh 4.14', 'Official single-node stack', 'SIEM / XDR for Platinum clients', 'HUB-WZH-01'],
    ['Grafana 13 OSS', 'Go + React', 'NOC wall (read-only data)', 'HUB-APP-01'],
    ['Traefik v3.7', 'Go', 'TLS edge, IP allow-lists, security headers', 'HUB-APP-01'],
    ['Spoke appliance', 'Docker Compose on Ubuntu 24.04', 'Zabbix proxy, GLPI Agent, optional Wazuh relay, WireGuard', 'One VM per client site'],
  ], [20, 22, 38, 20]),
  H2('3.2 Network and ports'),
  table(['Flow', 'From → To', 'Port', 'Protection'], [
    ['Tunnel', 'Spoke → hub', 'UDP 51820', 'WireGuard, started by the spoke (no inbound port at the client)'],
    ['Monitoring', 'Zabbix proxy → server', 'TCP 10051 in tunnel', 'PSK, unique per site'],
    ['Alerts', 'Zabbix / Wazuh → integration service', 'HTTPS 443 (or internal 8080)', 'Shared secret header + IP allow-list'],
    ['GLPI API', 'Integration service / portal → GLPI', 'HTTP 80, internal network only', 'App token + IP range + service or user account'],
    ['Users', 'Browser → portal / GLPI / Grafana', 'HTTPS 443', 'TLS, HttpOnly session cookie, MFA in GLPI'],
    ['Admin UIs', 'Staff → Zabbix, jobs, metrics', 'HTTPS 443 via staff VPN', 'Staff-only IP allow-list'],
  ], [14, 28, 22, 36]),
  H2('3.3 Concurrency and performance design'),
  B('**One lock per client** (`KeyedLock`): events of the same client are processed in order, so duplicate detection and alert-storm counting are always correct. Different clients run in parallel.'),
  B('**GLPI session pool** (`GlpiClient`): PHP locks a session per request, so the service keeps several GLPI sessions (default 4) to work in parallel.'),
  B('**Storm counting in GLPI**: the number of recent tickets is computed by GLPI with a relative time (`-5MINUTE`), so server clocks and time zones never disagree.'),
  B('**Caching**: client entity ids are cached for 5 minutes; GLPI item ids (request types, groups) are loaded once at start-up.'),
  B('Measured in the lab: alert → ticket in **1.2 s**; **500 alerts in about 20 s** with none lost, 95th-percentile latency **0.49 s** (PRD target: 500 alerts in 10 min, p95 ≤ 60 s).'),
);

// 4
add(H1('4. Technology stack and versions'),
  P('Every version is pinned (NFR-OPS-03). Upgrades go through the staging server first (SOP-13). Versions were checked against the official registries in November 2026.'),
  table(['Item', 'Version', 'Notes'], [
    ['.NET SDK / ASP.NET Core runtime', '10.0.404 / 10.0.15', 'LTS. Images `mcr.microsoft.com/dotnet/sdk`, `aspnet`'],
    ['Angular', '22.3', 'Standalone components, signals, zoneless change detection, Vite/esbuild build'],
    ['Node.js (build only)', '22.23.5', 'Not needed at run time – the portal is static files served by ASP.NET Core'],
    ['TypeScript / Vitest', '6.0 / 5.0', 'Angular CLI defaults'],
    ['GLPI', '11.0.13', 'Legacy REST API `apirest.php` (still supported in 11). GLPI 12 after its first patch releases (D-01)'],
    ['MariaDB', '11.8.9', 'GLPI database'],
    ['Zabbix', '8.0.0 (alpine images)', 'Server, web, proxy (SQLite), agent 2. Proxies must match the server major version'],
    ['PostgreSQL / TimescaleDB', '17.12 / 2.32.1-pg17', 'Zabbix database'],
    ['Wazuh', '4.14.x', '5.0 is beta – lab only'],
    ['Grafana OSS', '13.2.6', 'Zabbix plugin `alexanderzobnin-zabbix-app`'],
    ['Traefik', 'v3.7.19', 'TLS 1.2+, Let’s Encrypt or company certificates'],
    ['YamlDotNet', '17.1.0', 'Reads `config/*.yaml` in the integration service'],
    ['xUnit', '2.9.3', 'Unit tests'],
    ['Python (tooling)', '3.11+', 'Provisioning scripts and end-to-end tests: requests, PyYAML, cryptography, pytest'],
  ], [30, 22, 48]),
);

// 5
add(H1('5. Repository structure'),
  table(['Path', 'Contents'], [
    ['`WiseSphere.slnx`, `Directory.Build.props`', '.NET solution and shared build settings (warnings as errors, analyzers on)'],
    ['`src/WiseSphere.Core`', 'Event contracts and business rules (pure C#, no I/O): parsers and deciders for Zabbix, Wazuh, SLA, CSAT'],
    ['`src/WiseSphere.Glpi`', 'Typed GLPI 11 REST client: session pool, searches, writes, per-user sessions for the portal'],
    ['`src/WiseSphere.Integration`', 'ASP.NET Core service: webhook endpoints, scheduled jobs, SMS, metrics, Dockerfile'],
    ['`src/WiseSphere.Portal`', 'ASP.NET Core back end of the portal: sign-in, ticket/dashboard/monitoring API, serves the Angular app'],
    ['`web/portal`', 'Angular 22 front end'],
    ['`tests/WiseSphere.Core.Tests`', 'xUnit tests of the business rules (50 tests)'],
    ['`tests/e2e_lab.py`, `load_tc09.py`, `poc_agent_down.sh`', 'End-to-end, load and PoC tests against the lab'],
    ['`tests/unit`', 'Tests of the Python tooling, schemas and compose files'],
    ['`config/tenants.yaml`, `config/sla.yaml`', 'Clients, sites, packages and SLA matrix – the single source of truth'],
    ['`schemas/`', 'JSON schemas of the event contracts `ws.event.v1` and `ws.secevent.v1`'],
    ['`glpi/`, `zabbix/`, `wazuh/`, `grafana/`', 'Provisioning scripts and product configuration'],
    ['`hub/`, `spoke/`, `lab/`', 'Docker Compose stacks for production hub, client sites and the lab'],
    ['`ops/`', 'Backup, WireGuard hub set-up, restore drill'],
    ['`scripts/`', 'Lab bootstrap, new client site (`new_spoke.py`), .NET SDK in a container (`dotnet.sh`)'],
    ['`branding/`', 'Brand tokens and the script that applies them to the portal and GLPI'],
    ['`docs/`, `poc/`', 'Documents, screenshots, the archived PoC'],
  ], [36, 64]),
);

// 6
add(H1('6. Data model and multi-tenancy'),
  H2('6.1 The client code'),
  P('A client code `CLnnn` (for example `CL001`) is created once in `config/tenants.yaml` and reused by every tool:'),
  table(['Tool', 'Object', 'Example'], [
    ['GLPI', 'Sub-entity under the root entity “WiseSphere”, with tag', '`CL001 - Pilot Client`, tag `CL001`'],
    ['GLPI Agent', 'Agent tag (assets go to the client entity)', '`--tag=CL001`'],
    ['Zabbix', 'Host groups, proxy name, host tags', '`CL001/KWI/Network`, `CL001-KWI-PRX01`, tag `client=CL001`'],
    ['Wazuh', 'Agent group and labels', 'group `CL001-servers`, labels `client=CL001`, `site=KWI`'],
    ['WireGuard', 'Peer and subnet', '`cl001-kwi`, `10.250.1.0/24` (hub = `10.250.0.0/24`)'],
    ['Portal', 'What a user sees', 'Entities the user holds in GLPI (staff: root entity, recursive)'],
  ], [16, 44, 40]),
  H2('6.2 Configuration files'),
  P('`config/tenants.yaml` – one entry per client:'),
  ...CODE(['tenants:', '  - code: CL001', '    name: Pilot Client', '    tier: gold            # silver | gold | platinum', '    country: KW           # business-hours calendar', '    language: ar_SA', '    sites:', '      - code: KWI', '        wg_subnet: 10.250.1.0/24', '        spoke_size: small']),
  P('`config/sla.yaml` – business-hours calendars per country, SLA targets per package and priority (time to own / time to resolve, in minutes), which priorities page after hours, and the “at risk” warning time (15 min). The GLPI provisioning script turns this into GLPI calendars, SLMs, SLAs and ticket rules; the integration service reads it for SLA paging.'),
  H2('6.3 Ticket naming conventions (used for de-duplication)'),
  table(['Prefix', 'Meaning', 'Created by'], [
    ['`[ZBX:<event id>]`', 'One Zabbix problem', 'INT-01'],
    ['`[STORM:CLnnn]`', 'Alert storm parent for a client (>10 problems in 5 min)', 'INT-01'],
    ['`[WZH:<agent id>:<rule id>]`', 'One Wazuh rule on one agent (repeats become follow-ups while open)', 'INT-02'],
  ], [28, 52, 20]),
  NOTE('Do not rename these prefixes in GLPI by hand – the service finds existing tickets by them.'),
  H2('6.4 Event contracts'),
  table(['Field (ws.event.v1)', 'Zabbix macro', 'Notes'], [
    ['`event_id`', '`{EVENT.ID}`', 'Problem event id – also sent on recovery'],
    ['`event_value` / `event_update`', '`{EVENT.VALUE}` / `{EVENT.UPDATE.STATUS}`', '1 problem, 0 recovery; update = acknowledgement/comment'],
    ['`severity`', '`{EVENT.NSEVERITY}`', '0–5. Mapping: 5→U5/I5, 4→U4/I4, 3→U3/I3, ≤2→U2/I2'],
    ['`event_name`, `host`, `host_name`, `host_ip`', '`{EVENT.NAME}`, `{HOST.HOST}`, `{HOST.NAME}`, `{HOST.IP}`', ''],
    ['`tags`', '`{EVENT.TAGSJSON}`', 'Supplies `client`, `site`, `glpi_asset` (itemtype:id) from host tags'],
    ['`event_url`, `opdata`, `event_time`', 'deep link, `{EVENT.OPDATA}`, date/time', 'Written into the ticket'],
  ], [30, 36, 34]),
  P('Wazuh alerts use `ws.secevent.v1` (rule id, level, description, groups, MITRE, agent id/name/ip, agent labels `client`/`site`, source IP, log). Level mapping: ≥13→U5/I5, 12→U4/I4, 10–11→U4/I3. Full schemas are in `schemas/`.'),
);

// 7
add(H1('7. Integration service (C#)'),
  H2('7.1 Projects and classes'),
  table(['Class', 'Project', 'Responsibility'], [
    ['`ZabbixEventParser`, `SecurityAlertParser`', 'Core', 'Validate webhook bodies, drop unresolved Zabbix macros, accept the PoC payload'],
    ['`ZabbixDecider`', 'Core', 'INT-01 rules: create / follow-up / solve / storm create / storm follow-up / no-op'],
    ['`SecurityDecider`', 'Core', 'INT-02 rules: create or follow-up, SOC paging level'],
    ['`SlaDecider`, `CsatDecider`', 'Core', 'INT-04 and INT-03 rules, de-duplication, business hours, package tiers'],
    ['`GlpiClient`', 'Glpi', 'All GLPI API calls; session pool; per-user sessions for the portal'],
    ['`IntakeService`', 'Integration', 'Gathers GLPI context, calls the decider, writes, pages'],
    ['`SlaWatchJob`, `CsatReviewJob`, `SchedulerWorker`', 'Integration', 'Scheduled jobs (every 5 min / daily 08:10 local)'],
    ['`SmsSender`, `FailureNotifier`', 'Integration', 'Provider-neutral SMS with retries; pages the integration engineer on errors (max one per 10 min per error)'],
    ['`ContainmentPolicy`', 'Core', 'SOC-04 rules: opt-in, public address only, allow-list, max 24 h, no duplicates, expiry'],
    ['`ContainmentService`, `FortiGateAdapter`, `GenericHttpAdapter`, `LogOnlyAdapter`', 'Integration', 'Executes approved blocks on the client firewall, records them in GLPI, removes them on expiry'],
    ['`KeyedLock`, `GlpiCatalog`, `StateStore`, `Metrics`', 'Integration', 'Per-client lock, GLPI ids, small persistent state, Prometheus counters'],
  ], [34, 14, 52]),
  H2('7.2 HTTP endpoints'),
  table(['Method and path', 'Caller', 'Behaviour'], [
    ['`POST /webhook/zbx-event`', 'Zabbix media type “WiseSphere”', 'Body `ws.event.v1`. 200 with the action taken; 400 invalid; 403 wrong secret; 502 if GLPI fails (Zabbix retries 3×)'],
    ['`POST /webhook/wazuh-alert`', 'Wazuh `custom-wisesphere`', 'Body `ws.secevent.v1`. Same status codes'],
    ['`POST /jobs/sla-watch/run`', 'Operator / tests', 'Runs the SLA check now'],
    ['`POST /jobs/csat-review/run`', 'Operator / tests', 'Runs the CSAT review now'],
    ['`GET /healthz`, `GET /readyz`', 'Docker / monitoring', 'Alive; ready = GLPI reachable'],
    ['`GET /metrics`', 'Zabbix / Prometheus', 'Counters per action, SMS sent/failed, processing time'],
    ['`POST /containment/blocks`', 'Portal (after SOC approval)', 'Body: ticketId, clientCode, ip, hours, approvedBy, reason. 200 with the block; 422 with the reasons when refused'],
    ['`GET /containment/blocks?ticket=`, `DELETE /containment/blocks/{id}`', 'Portal', 'List / remove blocks'],
    ['`POST /jobs/containment-expiry/run`', 'Scheduler (every 5 min) / operator', 'Removes expired blocks'],
  ], [30, 24, 46]),
  P('All webhooks and jobs require the header `X-WS-Secret`, compared in constant time. Example response:'),
  ...CODE(['{ "ok": true, "action": "create", "client_code": "CL001", "entity_id": 2,', '  "mapped": true, "ticket_id": 88, "paged": false, "reason": null }']),
  H2('7.3 Decision rules (INT-01)'),
  table(['Situation', 'Result in GLPI'], [
    ['New problem, client known', 'Ticket in the client entity, urgency/impact from severity, request source “Monitoring”, assigned to WS-NOC, asset linked when `glpi_asset` tag exists; SLA attached by GLPI rule; disaster → SMS to NOC'],
    ['Same event received again', 'Follow-up on the existing ticket – never a duplicate'],
    ['≥ 10 problems for the client in 5 min', 'One `[STORM:CLnnn]` parent ticket + SMS; further problems become follow-ups on it'],
    ['Recovery', 'Solution added → ticket solved; if the problem was grouped in a storm, a follow-up on the storm ticket'],
    ['Acknowledgement / comment in Zabbix', 'Follow-up with the user and message'],
    ['Unknown client (no `client` tag)', 'Ticket in the root entity, marked UNMAPPED – fix the host tags'],
  ], [32, 68]),
  H2('7.4 Configuration (environment variables)'),
  P('Settings live in `appsettings.json` and are overridden with `WS__Section__Key` environment variables. Secrets are passed only through the environment (vault), never committed.'),
  table(['Setting', 'Default', 'Purpose'], [
    ['`WS__Integration__WebhookSecret`', '–', 'Required, ≥ 16 characters'],
    ['`WS__Integration__StormThreshold` / `StormWindowMinutes`', '10 / 5', 'Alert storm grouping (INT-06)'],
    ['`WS__Integration__WazuhTicketLevel` / `WazuhPageLevel`', '10 / 12', 'SOC-02'],
    ['`WS__Integration__SlaWatchIntervalMinutes`, `CsatReviewTime`', '5 / 08:10', 'Scheduled jobs'],
    ['`WS__Integration__Ids__*`', '0', 'GLPI ids of request types, groups and solution type (written by the provisioning script)'],
    ['`WS__Glpi__ApiUrl`, `AppToken`, `User`, `Password`, `SessionPoolSize`', 'internal URL, –, svc-integration, –, 4', 'GLPI service account (least-privilege Technician profile)'],
    ['`WS__Sms__Url`, `KeyHeader`, `Key`, `Numbers__noc|soc|desk|integration`', '–', 'SMS provider (D-04) and on-call numbers (SOP-05)'],
    ['`WS__Firewalls__CLnnn__ApiKey`', '–', 'Firewall API key per opted-in client (vault)'],
  ], [44, 20, 36]),
  H2('7.5 Approved containment (SOC-04)'),
  P('Blocking is never automatic (C-02). It is offered only for clients that opted in by contract, configured in `config/tenants.yaml`:'),
  ...CODE(['    containment:', '      enabled: true', '      firewall_type: fortigate        # fortigate | generic-http | log-only', '      firewall_url: https://fw.client.example', '      address_group: WS-Blocklist      # used by a deny rule created once by the client', '      allow_list: ["203.0.113.0/28"]  # never blocked', '      max_hours: 24']),
  table(['Step', 'What happens'], [
    ['1. Request', 'A member of the GLPI group WS-SOC opens the `[WZH:…]` ticket in the portal; the attacker address is pre-filled from the alert; they choose 1–24 h and write a reason'],
    ['2. Checks', 'Portal: user is in WS-SOC, ticket is a security ticket the user can see. Service: client opted in, address is a valid public IP, not on the allow-list, not already blocked, duration ≤ 24 h'],
    ['3. Block', 'The adapter adds the address to the firewall (FortiGate: address object + member of the address group; generic: `POST /block`); log-only mode records the decision without touching the firewall'],
    ['4. Record', 'Private follow-up on the ticket: who approved, address, firewall, expiry time, reason'],
    ['5. Expiry', 'Every 5 minutes expired blocks are removed and a follow-up is written; SOC can also remove a block earlier from the ticket'],
  ], [16, 84]),
  NOTE('Use `log-only` for a client until its firewall integration has been tested together with the client’s firewall team. Verify the FortiGate adapter on the pilot’s FortiOS version.'),
  ...img('portal-10-containment.png', 520, 'Figure 5 – Security ticket with the containment panel (SOC engineer view).'),
);

// 8
add(H1('8. WiseSphere portal (Angular + ASP.NET Core)'),
  P('One container serves the Angular app as static files and the API under `/api`. Users sign in with their GLPI login. The back end opens a GLPI session for that user and keeps it on the server; the browser only receives an HttpOnly cookie with a random session id.'),
  ...img('portal-02-dashboard-noc.png', 560, 'Figure 2 – Staff dashboard: KPIs, client health, tickets nearest to SLA breach (WISE Group brand).'),
  H2('8.1 Users and what they see'),
  table(['User', 'GLPI set-up', 'Portal view'], [
    ['NOC / SOC staff', 'Technician (or stronger) profile on the root entity, recursive', 'All clients, client health matrix, all problems, “Open in GLPI” link'],
    ['Client IT manager', 'Observer (or a client-manager profile) on the client entity only', 'Own tickets, own problems, raise requests and replies'],
  ], [22, 40, 38]),
  H2('8.2 API'),
  table(['Method and path', 'Purpose'], [
    ['`POST /api/auth/login`, `POST /api/auth/logout`', 'Sign in / out (login is rate-limited: 10 per minute per IP)'],
    ['`GET /api/me`', 'Name, staff flag, entities'],
    ['`GET /api/dashboard`', 'Counters, client health, nearest SLA breaches, recently updated'],
    ['`GET /api/tickets?scope=open|solved|all&page=n`', 'Ticket list, 25 per page, newest first'],
    ['`GET /api/tickets/{id}`', 'Ticket, description and history (follow-ups, tasks, solutions)'],
    ['`POST /api/tickets/{id}/followups`', 'Add a reply'],
    ['`POST /api/tickets`', 'New request in one of the user’s client entities'],
    ['`GET /api/monitoring/problems`', 'Current Zabbix problems for the user’s clients (read-only Zabbix token)'],
    ['`GET /api/reports/monthly?client=CLnnn&month=YYYY-MM`', 'Monthly service report (RPT-01) – only for the user’s own clients'],
    ['`GET|POST /api/tickets/{id}/containment`, `DELETE /api/containment/{id}`', 'SOC-04 – WS-SOC group only'],
  ], [44, 56]),
  H2('8.3 Front-end structure'),
  table(['File', 'Purpose'], [
    ['`app.ts`, `app.routes.ts`, `app.config.ts`', 'Shell (top bar, navigation), lazy-loaded routes, HTTP interceptor'],
    ['`core/api.ts`', 'Typed API client; adds the anti-CSRF header; sends the user to the login page on 401'],
    ['`core/auth.ts`', 'Signed-in user (signal) and route guard'],
    ['`core/i18n.ts`', 'English / Arabic dictionaries, right-to-left switch, date formatting, `t` pipe'],
    ['`core/models.ts`', 'TypeScript types matching the C# API records'],
    ['`pages/*.ts`', 'Login, dashboard, tickets, ticket detail, new request, monitoring, shared ticket table'],
    ['`styles.css`, `tokens.css`', 'Layout and components; brand tokens (generated)'],
  ], [36, 64]),
  H2('8.4 Arabic and right-to-left'),
  P('Every visible text comes from the dictionaries in `core/i18n.ts`. The language toggle sets `<html lang dir>`; the stylesheet uses logical properties (`margin-inline`, `border-inline-start`), so the whole layout mirrors correctly. Dates use the user’s language. Add every new text key to both dictionaries.'),
  ...img('portal-07-dashboard-arabic.png', 520, 'Figure 3 – Client user dashboard in Arabic (right-to-left).'),
  H2('8.5 Ticket detail and monitoring'),
  ...img('portal-04-ticket-detail.png', 520, 'Figure 4 – Ticket with history and reply box. Ticket text is HTML from GLPI and is sanitised by Angular.'),
  H2('8.6 Monthly service report (RPT-01)'),
  P('Reports → choose client and month → **Print / Save as PDF**. The page has a print layout (no navigation, A4). It shows tickets opened / solved / still open, solved within SLA, average time to resolve, satisfaction, tickets by priority and by source, monitoring problems and total problem time, alert storms, security incidents, the devices with most incidents and the list of P1 incidents. Client users can produce the report for their own company only. Figures come from GLPI with the user’s own rights and from Zabbix (read-only).'),
  ...img('portal-09-monthly-report.png', 460, 'Figure 6 – Monthly service report (lab data).'),
  H2('8.7 Applying the final design and logo'),
  N('Put the logo files in `branding/assets/` (`logo.svg`, `logo-dark.svg`, `favicon.png`).'),
  N('Edit `branding/tokens.json` – colours, Latin and Arabic fonts, brand name.'),
  N('Run `make brand`. This regenerates `web/portal/src/tokens.css` and the GLPI brand CSS.'),
  N('If the design changes the layout (not only colours), edit `web/portal/src/styles.css` and the page templates. Component logic does not change.'),
  N('Rebuild the image (`make image`) and deploy. Apply the GLPI CSS with `python3 branding/apply_branding.py --glpi-url …`.'),
  NOTE('Grafana OSS allows only the application title to be changed; a custom logo in Grafana needs Grafana Enterprise.'),
);

// 9
add(H1('9. Environments, set-up and deployment'),
  H2('9.1 Environments'),
  table(['Environment', 'Purpose', 'How'], [
    ['Lab (developer laptop / CI)', 'Development, end-to-end and load tests', '`lab/docker-compose.yml`: GLPI, Zabbix, integration service, portal, simulated devices, SMS mock'],
    ['Staging', 'Upgrade and release testing, restore drill (TC-11)', '`hub/` stack, both profiles on one VM'],
    ['Production hub', 'Live service', '`hub/` stack: profile `app` on HUB-APP-01, `zabbix` on HUB-ZBX-01; Wazuh on HUB-WZH-01'],
    ['Client site', 'Monitoring collection', '`spoke/` stack installed by `spoke/install-spoke.sh`'],
  ], [22, 30, 48]),
  H2('9.2 Lab quick start'),
  P('Needs Docker with Compose, Python 3.11+ and about 6 GB RAM (8 GB for Docker recommended). The .NET SDK and Node are optional: `scripts/dotnet.sh` runs the SDK in a container and the images build the Angular app themselves. **On Windows** use Docker Desktop with WSL 2 – step by step in `docs/RUN_ON_WINDOWS.md`.'),
  ...CODE(['pip install -r requirements.txt', 'make lab-up          # build images, start containers', 'make lab-bootstrap   # provision GLPI + Zabbix, start integration service and portal', 'make lab-test        # 29 end-to-end checks', 'make lab-load-test   # TC-09: 500 alerts', 'make lab-poc-test    # stop the agent -> ticket; start it -> solved']),
  table(['Service', 'Lab URL', 'Login'], [
    ['Portal', 'http://localhost:8089', '`noc.operator` (staff), `soc.engineer` (staff + SOC), `client.cl001` (client) – password `LAB_USERS_PASSWORD` in `lab/.env`'],
    ['GLPI', 'http://localhost:8090', 'glpi / glpi (lab only)'],
    ['Zabbix', 'http://localhost:8081', 'Admin / zabbix (lab only)'],
    ['Integration service', 'http://localhost:8088/readyz', 'Webhooks need `X-WS-Secret`'],
    ['SMS + firewall mock', 'http://localhost:8099/messages, /fw/blocks', 'Every SMS sent and every address blocked'],
  ], [22, 32, 46]),
  H2('9.3 Production deployment (hub)'),
  N('Provision the VMs (PRD §5.2) and harden the OS (CIS level 1). On HUB-APP-01 run `ops/wg-hub-setup.sh` (WireGuard runs on the host).'),
  N('Copy `hub/.env.example` to `hub/.env` (mode 600) and fill host names and secrets from the vault.'),
  N('Build and push the images from CI: `wisesphere/integration` and `wisesphere/portal` with a version tag; set `INTEGRATION_IMAGE` and `PORTAL_IMAGE`.'),
  N('Start GLPI: `docker compose --profile app up -d glpi-db glpi`; enable the API (`bin/console config:set enable_api 1`, `enable_api_login_credentials 1`) and the time zone (see `scripts/bootstrap_lab.sh`).'),
  N('Run `glpi/provision_glpi.py` with the integration and portal app tokens and the backend subnet; then `scripts/glpi_ids_env.py hub/glpi-ids.json hub/.env`.'),
  N('Start Zabbix (`--profile zabbix`) and run `zabbix/provision_zabbix.py --webhook-url https://<integration-host>/webhook/zbx-event --readonly-token-out …`; put the token in `ZABBIX_PORTAL_TOKEN`.'),
  N('Start the rest: `docker compose --profile app up -d`. Check `/readyz` of the integration service and sign in to the portal.'),
  N('Install `ops/backup-hub.sh` as a nightly timer and run the restore drill (`ops/restore-drill.md`) before go-live.'),
  H2('9.4 Adding a client (SOP-01)'),
  N('Add the client and its sites to `config/tenants.yaml`; commit through a pull request.'),
  N('Re-run `glpi/provision_glpi.py` and `zabbix/provision_zabbix.py` (both are safe to run again).'),
  N('Register the client’s devices: `zabbix/add_hosts.py --csv devices.csv` (columns client, site, role, name, ip, optional glpi_asset). Each device gets its role template (`config/templates.yaml`), host group, tags and the site proxy.'),
  N('If the contract includes containment: add the `containment` block to the client and its firewall API key to the vault.'),
  N('Restart the integration service so it reloads the tenants file.'),
  N('Run `scripts/new_spoke.py CLnnn SITE …` – creates WireGuard keys, the Zabbix proxy with its PSK and a bundle for the site; store the bundle in the vault.'),
  N('On the client VM run `spoke/install-spoke.sh <bundle>` and record the time (target ≤ 60 minutes).'),
  N('Create the client’s GLPI users (client-manager profile on the client entity) – they can sign in to the portal at once.'),
);

// 10
add(H1('10. Security'),
  table(['Control', 'Implementation', 'Requirement'], [
    ['No inbound port at client sites', 'Spoke starts the WireGuard tunnel; Zabbix proxy is active', 'NFR-SEC-01'],
    ['Webhook authentication', '`X-WS-Secret` with constant-time comparison + IP allow-list in Traefik', 'INT-05'],
    ['Least-privilege GLPI accounts', 'Service account with Technician profile; portal users act with their own rights; API clients limited by IP and app token', 'INT-05'],
    ['Client isolation', 'GLPI entity rights per user; Zabbix groups filtered by client code plus a second check on event tags; tested (TC-10)', 'ITSM-01, C-05'],
    ['Portal session', 'Random 256-bit id in an HttpOnly, Secure, SameSite=Strict cookie; GLPI token stays on the server; 8-hour lifetime', 'NFR-SEC-02'],
    ['CSRF', 'All portal writes need the `X-WS-Portal` header (cannot be sent cross-site without CORS, which is off)', 'OWASP'],
    ['Brute force', 'Login rate limit 10/min per IP; GLPI account lockout policy', 'OWASP'],
    ['Browser hardening', 'Strict Content-Security-Policy (no inline scripts), nosniff, frame-deny, referrer policy; HTML from GLPI sanitised by Angular', 'OWASP'],
    ['Secrets', 'Only in environment variables / vault; never in Git; `.gitignore` covers env files and bundles', 'NFR-SEC-04'],
    ['Read-only monitoring access', 'Dedicated Zabbix role allowing only read methods; write calls refused (verified)', 'NFR-SEC-02'],
    ['No automatic containment', 'Security tickets require SOC approval before any block (SOP-03)', 'C-02, SOC-04'],
    ['Admin UIs', 'Zabbix, jobs and metrics reachable only from the staff VPN range', 'NFR-SEC-02, T-03/T-04'],
    ['Encryption in transit', 'TLS 1.2+ at Traefik; PSK between proxy and server; WireGuard tunnel', 'NFR-SEC-03'],
  ], [24, 56, 20]),
  NOTE('Before go-live: enable MFA for all staff in GLPI, change every default password, and store all tokens in the credential vault (decision D-06).'),
);

// 11
add(H1('11. Testing and quality'),
  H2('11.1 Test levels'),
  table(['Level', 'What', 'Command', 'Result (22 Nov 2026)'], [
    ['Unit (C#)', 'Business rules: parsing, severity mapping, dedupe, storm, recovery, SLA tiers and business hours, CSAT, containment', '`make unit`', '50 / 50 pass'],
    ['Unit (Python)', 'Config files, Wazuh script output vs schema, brand CSS, compose secrets', '`make unit`', '6 / 6 pass'],
    ['End-to-end', 'Real Zabbix → service → GLPI path, SLA SMS, Wazuh, portal isolation, monthly report, containment', '`make lab-test`', '29 / 29 pass'],
    ['Self-monitoring', 'Portal container stopped → “portal is down” ticket; restarted → solved', 'manual', 'Ticket after ~5 min, solved on recovery'],
    ['Load (TC-09)', '500 alerts, 10 in parallel, none lost', '`make lab-load-test`', '~20 s, 500/500, p95 0.49 s'],
    ['PoC scenario', 'Stop the agent container → ticket; start → solved', '`make lab-poc-test`', 'Ticket after 297 s (Zabbix 3-min wait), solved 56 s after recovery'],
    ['CI', 'Build, unit tests, images, Angular build, compose validation', '`.github/workflows/ci.yml`', 'Runs on every push'],
  ], [16, 40, 20, 24]),
  H2('11.2 End-to-end checks against the PRD test plan'),
  table(['PRD test', 'Check', 'Lab result'], [
    ['TC-01', 'Problem → ticket in client entity, SLA attached, source Monitoring, NOC group', '1.2 s (target ≤ 60 s)'],
    ['TC-02', 'Recovery → ticket solved automatically', 'Pass'],
    ['TC-03', 'Repeat notification → follow-up, no duplicate', 'Pass'],
    ['TC-04', '15 devices down → one storm ticket, follow-ups, SMS, no duplicate storm', 'Pass'],
    ['TC-06', 'Ticket 10 min from breach → “AT RISK” SMS', 'Pass'],
    ['TC-07', 'Wazuh level 10 → SOC ticket; 12 → SMS; repeat deduped', 'Pass'],
    ['TC-09', '500 alerts without loss', 'Pass'],
    ['TC-10', 'Client sees only own tickets/problems; cannot open another client’s ticket', 'Pass'],
    ['Security', 'Wrong secret 403, bad payload 400, wrong password 401, write without CSRF header 400', 'Pass'],
    ['TC-08', 'SOC approves block → firewall rule with expiry, logged in ticket; non-SOC, internal IP and > 24 h refused', 'Pass (lab mock firewall)'],
    ['RPT-01', 'Monthly report for own client; other client refused', 'Pass'],
    ['TC-05, TC-11', 'Tunnel cut, restore drill', 'Need the real hub and pilot site – not yet run'],
  ], [16, 60, 24]),
  H2('11.3 Quality rules'),
  B('C# builds with **warnings as errors** and the .NET analyzers at “latest-recommended”.'),
  B('Every business-rule change comes with a unit test; every bug fix starts with a failing test.'),
  B('Run `make check` before every pull request; run `make lab-test` for changes that touch the integration path or the portal.'),
);

// 12
add(H1('12. Operations'),
  table(['Topic', 'How'], [
    ['Health', '`/healthz` and `/readyz` on the integration service; `/healthz` on the portal; Docker restart policy'],
    ['Self-monitoring', 'Zabbix host `wisesphere-platform`: HTTP checks of the integration service, portal and GLPI every minute (ticket in CL000 after 3 min without HTTP 200); one “site proxy not seen for 3 minutes” P1 per client site, ticketed in the client’s own queue (PRD §5.4)'],
    ['Device roles', '`config/templates.yaml`: switch, router, firewall (FortiGate / generic), Linux / Windows server, APC UPS, printer, ping-only → Zabbix templates “WS Role …” built from the Zabbix 8.0 vendor templates'],
    ['Metrics', '`/metrics` (Prometheus text): events per action, SMS sent/failed, processing time – scrape from Zabbix or Prometheus (NFR-OPS-01)'],
    ['Logs', 'JSON lines to stdout (Docker json-file, rotated 5 × 20 MB). One line per event: client, action, ticket, duration'],
    ['Failures', 'Processing errors page the integration engineer by SMS (INT-07); Zabbix retries failed webhooks 3 times; the action log shows the reason'],
    ['Backups', '`ops/backup-hub.sh` nightly: GLPI DB + files (incl. its encryption key), Zabbix DB, Grafana, service state, configs → encrypted off-site restic repository, 30 daily / 12 monthly; Wazuh keys/rules with `--wazuh`'],
    ['Restore', '`ops/restore-drill.md` – target ≤ 8 h, data ≤ 24 h old (TC-11), quarterly'],
    ['Upgrades', 'Change the pinned version, deploy to staging, run `make lab-test` against it, then production (SOP-13)'],
    ['Data retention', 'Zabbix history 30 d / trends 13 months; Wazuh 90 d hot + 12 months archive; tickets permanent (NFR-SEC-08)'],
  ], [18, 82]),
);

// 13
add(H1('13. Development guide'),
  H2('13.1 Tooling'),
  B('Visual Studio 2022+, Rider or VS Code with the C# Dev Kit; Node 22 and the Angular CLI for front-end work.'),
  B('Without a local SDK: `scripts/dotnet.sh build|test` runs the .NET 10 SDK in Docker.'),
  B('Front-end live reload: start the lab, then `make web-dev` (Angular on :4200, `/api` proxied to the lab portal on :8089).'),
  H2('13.2 Branching and change control'),
  B('Work on a feature branch; open a pull request to `main`; CI must be green; one reviewer approves (SOP-06).'),
  B('Never edit GLPI or Zabbix configuration by hand where a provisioning script owns it – change the script or `config/*.yaml` and re-run it.'),
  B('Tag releases (`v1.0.0`); images are tagged with the same version.'),
  H2('13.3 Coding standards'),
  table(['Area', 'Standard'], [
    ['C#', 'Nullable enabled, records for data, `sealed` classes, async all the way with `CancellationToken`, no business rules in endpoints – they belong in `WiseSphere.Core`'],
    ['Logging', 'Structured messages with named properties (`{Client}`, `{Action}`); never log secrets or full payloads with personal data'],
    ['HTTP to GLPI', 'Only through `GlpiClient`; add a typed method instead of raw calls in services'],
    ['Angular', 'Standalone components, `inject()`, signals for state, `@if/@for` control flow, lazy routes, no business rules in templates'],
    ['Texts', 'Every visible string through `t` with keys in both English and Arabic'],
    ['Styling', 'Use tokens (`var(--ws-…)`) and logical properties; never hard-code brand colours'],
    ['Security', 'New API endpoints go in the signed-in group; validate input lengths; writes need the CSRF header (automatic via the interceptor)'],
  ], [16, 84]),
  H2('13.4 Common changes – where to edit'),
  table(['Change', 'Edit', 'Test'], [
    ['Change the alert-storm threshold', '`WS__Integration__StormThreshold`', '–'],
    ['Change how severity maps to priority', '`ZabbixDecider.MapSeverity`', '`Severity_mapping_INT01`'],
    ['Change SLA times or business hours', '`config/sla.yaml`, re-run GLPI provisioning', '`SlaTests`, TC-06'],
    ['New alert source (e.g. a firewall)', 'New parser + decider in Core, endpoint in `Program.cs`, schema in `schemas/`', 'New unit tests + e2e case'],
    ['New SMS provider', '`SmsSender` (body/headers)', 'Lab SMS mock or provider sandbox'],
    ['New portal page', '`web/portal/src/app/pages/`, route in `app.routes.ts`, API in `WiseSphere.Portal`', 'Manual + e2e check'],
    ['New text in the portal', '`core/i18n.ts` (EN and AR)', 'Switch language and check RTL'],
  ], [30, 44, 26]),
);

// 14
add(H1('14. Requirements traceability'),
  table(['Requirement', 'Implemented in', 'Verified by'], [
    ['ITSM-01 entity per client', '`glpi/provision_glpi.py`', 'TC-01, TC-10'],
    ['ITSM-02 SLA per package and calendar', '`config/sla.yaml`, GLPI SLM/SLA/rules', 'TC-01 (SLA attached), TC-06'],
    ['ITSM-03 request sources', 'Request types Monitoring / Security', 'TC-01'],
    ['ITSM-05 satisfaction survey', 'Entity survey settings; CSAT review job', 'Unit tests'],
    ['NOC-01/03 proxy per site, 72 h buffer', '`spoke/`, `scripts/new_spoke.py`', 'Lab proxy online with PSK; TC-05 open'],
    ['NOC-02 standard templates', '`config/templates.yaml`, role templates, `zabbix/add_hosts.py`', 'Provisioning (9 roles)'],
    ['NOC-04 maintenance suppresses tickets', 'Zabbix action condition “not suppressed”', 'Provisioning'],
    ['SOC-04 approved containment', '`ContainmentPolicy`, `ContainmentService`, portal panel', 'Unit tests, TC-08'],
    ['RPT-01 monthly client report', '`ReportService`, portal Reports page', 'e2e, Figure 6'],
    ['NFR-OPS-01 platform health', 'Zabbix host `wisesphere-platform`', 'Portal-down test'],
    ['SOC-02 level ≥10 ticket, ≥12 page', '`SecurityDecider`', 'TC-07'],
    ['INT-01 Zabbix → GLPI', '`ZabbixDecider`, `IntakeService`', 'TC-01…TC-04'],
    ['INT-02 Wazuh → GLPI', '`SecurityDecider`, `wazuh/custom-wisesphere.py`', 'TC-07'],
    ['INT-03 CSAT escalation', '`CsatDecider`, `CsatReviewJob`', 'Unit tests'],
    ['INT-04 SLA SMS', '`SlaDecider`, `SlaWatchJob`', 'TC-06'],
    ['INT-05 authenticated webhooks, least privilege', 'Secret header, API clients, service account', 'Security checks'],
    ['INT-06 storm grouping', '`ZabbixDecider` + `KeyedLock`', 'TC-04, TC-09'],
    ['INT-07 failure alerting', '`FailureNotifier`, Zabbix retries', 'Manual'],
    ['DSH-01/02/03 dashboards', 'Portal dashboard + monitoring; Grafana NOC wall', 'Screenshots, TC-10'],
    ['NFR-PERF-01 ≤ 60 s', 'Parallel design', '1.2 s; TC-09 p95 0.49 s'],
    ['NFR-LNG-01 Arabic / English', 'Portal i18n with RTL', 'Figure 3'],
    ['NFR-OPS-03/04 pinned versions, config as code', 'Compose, `.env.example`, Git', 'CI'],
  ], [34, 40, 26]),
);

// 15
add(H1('15. Open decisions, known limitations and next steps'),
  H2('15.1 Decisions needed'),
  table(['#', 'Decision', 'Needed for', 'Owner'], [
    ['DD-08', 'Approve the custom portal as a change to PRD R-06', 'Go-live of the portal', 'Executive Management'],
    ['D-04', 'SMS / voice provider', 'Real paging (lab uses a mock)', 'Solutions Architect'],
    ['D-02', 'Hub hosting (in-region)', 'Production deployment', 'Executive Management'],
    ['D-06', 'Credential vault', 'Storing tokens, spoke bundles', 'SOC Engineer'],
    ['D-05', 'Patch / remote-management tool', 'Selling the Gold package', 'Executive Management'],
    ['–', 'Final design and logo', 'Branded portal and GLPI', 'Marketing / Design'],
    ['–', 'Least-privilege GLPI profiles: confirm Technician for the service account; create a “Client manager” profile', 'Production accounts', 'Solutions Architect'],
    ['–', 'Containment per client: written opt-in, firewall type, allow-list; test with the client firewall team', 'Enabling SOC-04 for a client', 'SOC Engineer + client'],
  ], [10, 46, 26, 18]),
  H2('15.2 Known limitations'),
  B('Portal sessions are kept in memory: a restart signs users out, and a second portal instance would need a shared store (e.g. Redis). One instance is enough for Release 1 (25 clients).'),
  B('The SLA and CSAT de-duplication state is a small JSON file in the service’s data volume; with more than one integration-service instance it must move to a shared store.'),
  B('The Grafana Zabbix plugin downloads at first start; hubs without internet access need it pre-installed.'),
  B('Active containment blocks are kept in the integration service’s state file; with more than one service instance this must move to a shared store.'),
  B('The FortiGate adapter follows the FortiOS REST API and has been tested only against the lab mock; verify it with the pilot’s firewall. Other vendors (Palo Alto, MikroTik) need an adapter or the generic-HTTP middleware.'),
  B('The monthly report is produced with the browser’s “Save as PDF”; automatic e-mailing of reports and the executive report (RPT-02) are not built.'),
  B('The Wazuh deployment itself and per-client index security are not built (Phase 4, needs the Wazuh VM).'),
  B('Tests TC-05 (tunnel cut) and TC-11 (restore drill) need the real hub and pilot site.'),
  H2('15.3 Next steps'),
  N('Management sign-off on the portal scope; receive the design and logo; apply them (section 8.6).'),
  N('Choose the SMS provider and adapt `SmsSender` if its API differs.'),
  N('Phase 4: Wazuh deployment and per-client index security; enable containment for the pilot (log-only first, then FortiGate).'),
  N('Executive report (RPT-02) and monthly report e-mailing.'),
  N('Deploy to staging, run the full test plan including TC-05 and TC-11, then the pilot client.'),
);

// Appendix
add(H1('Appendix A – Portal screens'),
  ...img('portal-01-login.png', 520, 'Figure 7 – Sign-in page.'),
  ...img('portal-05-monitoring.png', 520, 'Figure 8 – Monitoring: current problems for the user’s clients.'),
  ...img('portal-08-new-request-arabic.png', 520, 'Figure 9 – New request form in Arabic.'),
);

// ---------------------------------------------------------------- document
const doc = new Document({
  creator: 'WiseSphere',
  features: { updateFields: true },
  title: 'WiseSphere Development Document',
  description: 'WS-DEV-001 – Release 1',
  styles: {
    default: { document: { run: { font: FONT, size: 21, color: '1D273B' }, paragraph: { spacing: { line: 288, lineRule: LineRuleType.AUTO } } } },
    paragraphStyles: [
      { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { font: FONT, size: 34, bold: true, color: NAVY }, paragraph: { spacing: { before: 0, after: 200 }, keepNext: true, outlineLevel: 0 } },
      { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { font: FONT, size: 26, bold: true, color: BLUE }, paragraph: { spacing: { before: 280, after: 120 }, keepNext: true, outlineLevel: 1 } },
      { id: 'Heading3', name: 'Heading 3', basedOn: 'Normal', next: 'Normal', quickFormat: true,
        run: { font: FONT, size: 22, bold: true, color: NAVY }, paragraph: { spacing: { before: 200, after: 80 }, keepNext: true, outlineLevel: 2 } },
    ],
  },
  numbering: {
    config: [
      { reference: 'bullets', levels: [
        { level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 400, hanging: 260 } } } },
        { level: 1, format: LevelFormat.BULLET, text: '–', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 800, hanging: 260 } } } } ] },
      { reference: 'steps', levels: [
        { level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 420, hanging: 300 } } } } ] },
    ],
  },
  sections: [{
    properties: { page: { size: { width: 11906, height: 16838 }, margin: { top: 1134, bottom: 1134, left: 1134, right: 1134 } } },
    headers: { default: new Header({ children: [new Paragraph({ alignment: AlignmentType.RIGHT,
      children: [new TextRun({ text: 'WiseSphere – Development Document (WS-DEV-001)', size: 16, color: GREY })] })] }) },
    footers: { default: new Footer({ children: [new Paragraph({ alignment: AlignmentType.CENTER,
      children: [new TextRun({ text: 'Page ', size: 16, color: GREY }), new TextRun({ children: [PageNumber.CURRENT], size: 16, color: GREY }),
        new TextRun({ text: ' of ', size: 16, color: GREY }), new TextRun({ children: [PageNumber.TOTAL_PAGES], size: 16, color: GREY })] })] }) },
    children: content,
  }],
});

Packer.toBuffer(doc).then((buf) => { fs.writeFileSync(OUT, buf); console.log('wrote', OUT, buf.length); });
