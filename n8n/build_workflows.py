#!/usr/bin/env python3
"""Generate the WiseSphere n8n workflows (n8n/workflows/*.json).

Why generated: the decision logic lives in n8n/src/wslib.js, which is unit
tested outside n8n. n8n Code nodes cannot import files, so this script inlines
the library into each Code node and wires the HTTP Request nodes around it.

    python3 n8n/build_workflows.py          # write n8n/workflows/*.json
    python3 n8n/build_workflows.py --check  # fail if the committed JSON is stale (CI)

No secrets are written: workflows reference n8n credentials by fixed id
(created by ops/n8n-bootstrap.sh from the hub .env) and read non-secret
settings from WS_* environment variables.
"""
from __future__ import annotations

import argparse
import json
import sys
import uuid
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
LIB = (ROOT / "n8n/src/wslib.js").read_text()
OUT = ROOT / "n8n/workflows"
SLA = yaml.safe_load((ROOT / "config/sla.yaml").read_text())

# Fixed ids so imports are idempotent and credentials bind on import.
CRED_GLPI = {"httpCustomAuth": {"id": "wsGlpiApiCred001", "name": "WS GLPI API"}}
CRED_HOOK = {"httpHeaderAuth": {"id": "wsWebhookSecret1", "name": "WS Webhook Secret"}}
CRED_SMS = {"httpHeaderAuth": {"id": "wsSmsProvider001", "name": "WS SMS Provider"}}
WF_IDS = {
    "WF-01": "wsWF01ZbxToGlpi0",
    "WF-02": "wsWF02WzhToGlpi0",
    "WF-03": "wsWF03CsatReview",
    "WF-04": "wsWF04SlaWatch00",
    "WF-99": "wsWF99ErrorAlert",
}
GLPI = "={{ $env.WS_GLPI_API_URL }}"
SESSION = "={{ $('GLPI initSession').first().json.session_token }}"


def nid(workflow: str, name: str) -> str:
    return str(uuid.uuid5(uuid.NAMESPACE_URL, f"wisesphere/{workflow}/{name}"))


class WF:
    def __init__(self, key: str, name: str, description: str):
        self.key, self.name, self.description = key, name, description
        self.nodes: list[dict] = []
        self.conn: dict[str, dict] = {}

    def add(self, name, type_, version, params, pos, **extra):
        node = {"parameters": params, "id": nid(self.key, name), "name": name, "type": type_,
                "typeVersion": version, "position": list(pos)}
        node.update(extra)
        self.nodes.append(node)
        return name

    def link(self, src, dst, out=0):
        outs = self.conn.setdefault(src, {"main": []})["main"]
        while len(outs) <= out:
            outs.append([])
        outs[out].append({"node": dst, "type": "main", "index": 0})

    def chain(self, *names):
        for a, b in zip(names, names[1:]):
            self.link(a, b)

    def to_json(self, error_workflow=True):
        settings = {"executionOrder": "v1", "saveDataErrorExecution": "all", "saveDataSuccessExecution": "all",
                    "saveManualExecutions": True, "callerPolicy": "workflowsFromSameOwner"}
        if error_workflow:
            settings["errorWorkflow"] = WF_IDS["WF-99"]
        return {
            "id": WF_IDS[self.key],
            "name": f"{self.key} {self.name}",
            "nodes": self.nodes,
            "connections": self.conn,
            "active": False,
            "settings": settings,
            "pinData": {},
            "meta": {"templateCredsSetupCompleted": True, "description": self.description,
                     "generatedBy": "n8n/build_workflows.py - do not edit in the n8n UI, change the source and rebuild"},
            "tags": [],
        }

    # ------------------------------------------------------------ node helpers
    def run_now(self, pos, target):
        """Lets operators, the CLI (`n8n execute --id`) and other workflows run a scheduled flow on demand."""
        self.add("Run Now", "n8n-nodes-base.executeWorkflowTrigger", 1.1, {"inputSource": "passthrough"}, pos)
        self.link("Run Now", target)

    def code(self, name, body, pos, mode="runOnceForAllItems"):
        js = LIB + "\n// ---- node logic ----\n" + body.strip() + "\n"
        params = {"jsCode": js}
        if mode != "runOnceForAllItems":
            params["mode"] = mode
        return self.add(name, "n8n-nodes-base.code", 2, params, pos)

    def glpi(self, name, method, path, pos, query=None, body=None, session=True, full=True, extra_opts=None):
        headers = [{"name": "Content-Type", "value": "application/json"}]
        if session:
            headers.append({"name": "Session-Token", "value": SESSION})
        params = {
            "method": method,
            "url": GLPI + path if not path.startswith("={{") else path,
            "authentication": "genericCredentialType",
            "genericAuthType": "httpCustomAuth",
            "sendHeaders": True,
            "headerParameters": {"parameters": headers},
            "options": {"timeout": 20000},
        }
        if query:
            params["sendQuery"] = True
            params["queryParameters"] = {"parameters": [{"name": k, "value": v} for k, v in query]}
        if body is not None:
            params["sendBody"] = True
            params["specifyBody"] = "json"
            params["jsonBody"] = body
        if full:
            # one item with {statusCode, body}; never split arrays, never throw (the Code node checks)
            params["options"]["response"] = {"response": {"fullResponse": True, "neverError": True, "responseFormat": "json"}}
        if extra_opts:
            params["options"].update(extra_opts)
        return self.add(name, "n8n-nodes-base.httpRequest", 4.2, params, pos, credentials=CRED_GLPI)

    def if_(self, name, expr, pos):
        return self.add(name, "n8n-nodes-base.if", 2.2, {
            "conditions": {
                "options": {"caseSensitive": True, "leftValue": "", "typeValidation": "loose", "version": 2},
                "conditions": [{"id": nid(self.key, name + "/c"), "leftValue": expr, "rightValue": "",
                                "operator": {"type": "boolean", "operation": "true", "singleValue": True}}],
                "combinator": "and",
            },
            "looseTypeValidation": True,
            "options": {},
        }, pos)

    def sms(self, name, pos):
        body = ("={{ JSON.stringify({ to: ({ noc: $env.WS_ONCALL_NOC, soc: $env.WS_ONCALL_SOC, desk: $env.WS_DESK_PHONE, "
                "integration: $env.WS_ONCALL_INTEGRATION })[$json.notify.channel] || $env.WS_ONCALL_NOC, "
                "message: $json.notify.message, source: 'wisesphere' }) }}")
        return self.add(name, "n8n-nodes-base.httpRequest", 4.2, {
            "method": "POST", "url": "={{ $env.WS_SMS_URL }}",
            "authentication": "genericCredentialType", "genericAuthType": "httpHeaderAuth",
            "sendBody": True, "specifyBody": "json", "jsonBody": body,
            "options": {"timeout": 10000},
        }, pos, credentials=CRED_SMS, onError="continueRegularOutput", retryOnFail=True, maxTries=3, waitBetweenTries=2000)

    def webhook(self, name, path, pos):
        return self.add(name, "n8n-nodes-base.webhook", 2, {
            "httpMethod": "POST", "path": path, "authentication": "headerAuth",
            "responseMode": "responseNode", "options": {},
        }, pos, credentials=CRED_HOOK, webhookId=nid(self.key, "webhook/" + path))

    def respond(self, name, body_expr, code, pos):
        return self.add(name, "n8n-nodes-base.respondToWebhook", 1.1, {
            "respondWith": "json", "responseBody": body_expr, "options": {"responseCode": code},
        }, pos)

    def session_open(self, x, y):
        a = self.glpi("GLPI initSession", "GET", "/initSession", (x, y), session=False, full=False)
        b = self.glpi("GLPI Root Scope", "POST", "/changeActiveEntities", (x + 220, y),
                      body='{"entities_id": 0, "is_recursive": true}', full=False)
        self.link(a, b)
        return a, b

    def session_close(self, x, y):
        # executeOnce: several branches may reach this node; never fail the run on logout
        return self.add(
            "GLPI killSession", "n8n-nodes-base.httpRequest", 4.2, {
                "method": "GET", "url": GLPI + "/killSession", "authentication": "genericCredentialType",
                "genericAuthType": "httpCustomAuth", "sendHeaders": True,
                "headerParameters": {"parameters": [{"name": "Session-Token", "value": SESSION}]},
                "options": {"timeout": 10000},
            }, (x, y), credentials=CRED_GLPI, onError="continueRegularOutput", executeOnce=True)


CHECK_HTTP = r"""
function body(nodeName, { allowEmpty = true } = {}) {
  const r = $(nodeName).first().json;
  const code = Number(r.statusCode || 200);
  const b = r.body !== undefined ? r.body : r;
  // GLPI answers 400 ERROR_RANGE_EXCEED_TOTAL when a search has no rows.
  const empty = code === 400 && JSON.stringify(b).includes('ERROR_RANGE_EXCEED_TOTAL');
  if (empty && allowEmpty) return [];
  if (code < 200 || code >= 300) throw new Error(`${nodeName} failed: HTTP ${code} ${JSON.stringify(b).slice(0, 300)}`);
  return b;
}
const envInt = (name, dflt) => { const v = $env[name]; return v === undefined || v === '' ? dflt : Number(v); };
"""


# ---------------------------------------------------------------- WF-01
def wf01() -> WF:
    w = WF("WF-01", "Zabbix to GLPI", "INT-01: Zabbix problem/recovery -> GLPI ticket with dedupe, storm grouping, auto-solve and asset link.")
    w.webhook("Zabbix Event", "zbx-event", (0, 300))
    w.code("Normalize", "return [{ json: WS.normalizeZabbixEvent($input.first().json.body) }];", (220, 300))
    w.if_("Valid?", "={{ $json.valid }}", (440, 300))
    w.respond("Respond 400", "={{ JSON.stringify({ ok: false, errors: $json.errors }) }}", 400, (660, 520))
    a, b = w.session_open(660, 300)
    code_q = "={{ $('Normalize').first().json.event.client_code ? '^' + $('Normalize').first().json.event.client_code + '$' : '^__unmapped__$' }}"
    w.glpi("Find Entity", "GET", "/Entity", (1100, 300), query=[("searchText[tag]", code_q), ("range", "0-1")])
    w.glpi("Find Event Ticket", "GET", "/Ticket", (1320, 300), query=[
        ("searchText[name]", "={{ '[ZBX:' + $('Normalize').first().json.event.event_id + ']' }}"), ("range", "0-20")])
    w.glpi("Find Storm Ticket", "GET", "/Ticket", (1540, 300), query=[
        ("searchText[name]", "={{ '[STORM:' + ($('Normalize').first().json.event.client_code || 'UNMAPPED') + ']' }}"),
        ("range", "0-20"), ("order", "DESC")])
    ent = "={{ (Array.isArray($('Find Entity').first().json.body) && $('Find Entity').first().json.body[0]) ? $('Find Entity').first().json.body[0].id : -1 }}"
    w.glpi("Count Recent", "GET", "/search/Ticket", (1760, 300), query=[
        ("criteria[0][field]", "1"), ("criteria[0][searchtype]", "contains"), ("criteria[0][value]", "[ZBX:"),
        ("criteria[1][link]", "AND"), ("criteria[1][field]", "15"), ("criteria[1][searchtype]", "morethan"),
        ("criteria[1][value]", "={{ '-' + ($env.WS_STORM_WINDOW_MIN || 5) + 'MINUTE' }}"),
        ("criteria[2][link]", "AND"), ("criteria[2][field]", "80"), ("criteria[2][searchtype]", "equals"), ("criteria[2][value]", ent),
        ("forcedisplay[0]", "2"), ("range", "0-0")])
    w.code("Decide", CHECK_HTTP + r"""
const event = $('Normalize').first().json.event;
const entities = body('Find Entity');
const entityId = Array.isArray(entities) && entities[0] ? Number(entities[0].id) : null;
const recent = body('Count Recent');
const d = WS.decideZabbix({
  event, entityId,
  eventTickets: body('Find Event Ticket'),
  stormTickets: body('Find Storm Ticket'),
  recentCount: Array.isArray(recent) ? 0 : Number(recent.totalcount || 0),
  cfg: {
    defaultEntityId: envInt('WS_GLPI_DEFAULT_ENTITY', 0),
    stormThreshold: envInt('WS_STORM_THRESHOLD', 10),
    requestTypeId: envInt('WS_GLPI_REQTYPE_MONITORING', 0),
    nocGroupId: envInt('WS_GLPI_GROUP_NOC', 0),
    solutionTypeId: envInt('WS_GLPI_SOLUTIONTYPE', 0),
  },
});
if (!d.ops.length) return [{ json: { noop: true, summary: d.summary, notify: d.notify } }];
return d.ops.map((op) => ({ json: { noop: false, path: op.path, body: op.body, summary: d.summary, notify: d.notify } }));
""", (1980, 300))
    w.if_("Has Writes?", "={{ $json.noop !== true }}", (2200, 300))
    w.glpi("GLPI Write", "POST", "={{ $env.WS_GLPI_API_URL + $json.path }}", (2420, 200),
           body="={{ JSON.stringify($json.body) }}", full=False)
    w.code("Summarize", r"""
const first = $('Decide').first().json;
const results = $input.all().map((i) => i.json);
const created = results.find((r) => r && r.id && !Array.isArray(r));
return [{ json: { ...first.summary, ticket_id: first.summary.ticket_id || (created ? created.id : undefined), results, notify: first.notify } }];
""", (2640, 200))
    w.if_("Notify?", "={{ !!$json.notify }}", (2860, 200))
    w.sms("Send SMS", (3080, 100))
    w.session_close(3300, 300)
    w.respond("Respond OK", "={{ JSON.stringify({ ok: true, ...( $('Summarize').isExecuted ? $('Summarize').first().json : $('Decide').first().json.summary ) }) }}", 200, (3520, 300))
    w.chain("Zabbix Event", "Normalize", "Valid?")
    w.link("Valid?", a, 0)
    w.link("Valid?", "Respond 400", 1)
    w.chain(b, "Find Entity", "Find Event Ticket", "Find Storm Ticket", "Count Recent", "Decide", "Has Writes?")
    w.link("Has Writes?", "GLPI Write", 0)
    w.link("Has Writes?", "GLPI killSession", 1)
    w.chain("GLPI Write", "Summarize", "Notify?")
    w.link("Notify?", "Send SMS", 0)
    w.link("Notify?", "GLPI killSession", 1)
    w.link("Send SMS", "GLPI killSession")
    w.link("GLPI killSession", "Respond OK")
    return w


# ---------------------------------------------------------------- WF-02
def wf02() -> WF:
    w = WF("WF-02", "Wazuh to GLPI", "INT-02: Wazuh alert level>=10 -> SOC ticket (dedupe by agent+rule), SMS for level>=12. Containment only after approval (SOP-03).")
    w.webhook("Wazuh Alert", "wazuh-alert", (0, 300))
    w.code("Normalize", "return [{ json: WS.normalizeWazuhAlert($input.first().json.body) }];", (220, 300))
    w.if_("Valid?", "={{ $json.valid }}", (440, 300))
    w.respond("Respond 400", "={{ JSON.stringify({ ok: false, errors: $json.errors }) }}", 400, (660, 520))
    a, b = w.session_open(660, 300)
    code_q = "={{ $('Normalize').first().json.alert.client_code ? '^' + $('Normalize').first().json.alert.client_code + '$' : '^__unmapped__$' }}"
    w.glpi("Find Entity", "GET", "/Entity", (1100, 300), query=[("searchText[tag]", code_q), ("range", "0-1")])
    w.glpi("Find Alert Ticket", "GET", "/Ticket", (1320, 300), query=[
        ("searchText[name]", "={{ '[WZH:' + $('Normalize').first().json.alert.agent_id + ':' + $('Normalize').first().json.alert.rule_id + ']' }}"),
        ("range", "0-20"), ("order", "DESC")])
    w.code("Decide", CHECK_HTTP + r"""
const alert = $('Normalize').first().json.alert;
const entities = body('Find Entity');
const d = WS.decideWazuh({
  alert,
  entityId: Array.isArray(entities) && entities[0] ? Number(entities[0].id) : null,
  alertTickets: body('Find Alert Ticket'),
  cfg: {
    defaultEntityId: envInt('WS_GLPI_DEFAULT_ENTITY', 0),
    requestTypeId: envInt('WS_GLPI_REQTYPE_SECURITY', 0),
    socGroupId: envInt('WS_GLPI_GROUP_SOC', 0),
    ticketLevel: envInt('WS_WAZUH_TICKET_LEVEL', 10),
    pageLevel: envInt('WS_WAZUH_PAGE_LEVEL', 12),
  },
});
if (!d.ops.length) return [{ json: { noop: true, summary: d.summary, notify: d.notify } }];
return d.ops.map((op) => ({ json: { noop: false, path: op.path, body: op.body, summary: d.summary, notify: d.notify } }));
""", (1540, 300))
    w.if_("Has Writes?", "={{ $json.noop !== true }}", (1760, 300))
    w.glpi("GLPI Write", "POST", "={{ $env.WS_GLPI_API_URL + $json.path }}", (1980, 200),
           body="={{ JSON.stringify($json.body) }}", full=False)
    w.code("Summarize", r"""
const first = $('Decide').first().json;
const results = $input.all().map((i) => i.json);
const created = results.find((r) => r && r.id && !Array.isArray(r));
return [{ json: { ...first.summary, ticket_id: first.summary.ticket_id || (created ? created.id : undefined), results, notify: first.notify } }];
""", (2200, 200))
    w.if_("Notify?", "={{ !!$json.notify }}", (2420, 200))
    w.sms("Send SMS", (2640, 100))
    w.session_close(2860, 300)
    w.respond("Respond OK", "={{ JSON.stringify({ ok: true, ...( $('Summarize').isExecuted ? $('Summarize').first().json : $('Decide').first().json.summary ) }) }}", 200, (3080, 300))
    w.chain("Wazuh Alert", "Normalize", "Valid?")
    w.link("Valid?", a, 0)
    w.link("Valid?", "Respond 400", 1)
    w.chain(b, "Find Entity", "Find Alert Ticket", "Decide", "Has Writes?")
    w.link("Has Writes?", "GLPI Write", 0)
    w.link("Has Writes?", "GLPI killSession", 1)
    w.chain("GLPI Write", "Summarize", "Notify?")
    w.link("Notify?", "Send SMS", 0)
    w.link("Notify?", "GLPI killSession", 1)
    w.link("Send SMS", "GLPI killSession")
    w.link("GLPI killSession", "Respond OK")
    return w


# ---------------------------------------------------------------- WF-03
def wf03() -> WF:
    w = WF("WF-03", "CSAT Review", "INT-03: daily - GLPI satisfaction scores <=2 -> private service-recovery task for the Service Delivery Manager group.")
    w.add("Daily 08:10", "n8n-nodes-base.scheduleTrigger", 1.2,
          {"rule": {"interval": [{"field": "cronExpression", "expression": "10 8 * * *"}]}}, (0, 300))
    a, b = w.session_open(220, 300)
    w.glpi("Get Surveys", "GET", "/TicketSatisfaction", (660, 300), query=[("range", "0-500"), ("sort", "id"), ("order", "DESC")])
    w.code("Decide", CHECK_HTTP + r"""
const store = $getWorkflowStaticData('global');
const rows = body('Get Surveys');
const r = WS.decideCsat(Array.isArray(rows) ? rows : [], store.csatProcessed || [], envInt('WS_CSAT_LOW_SCORE', 2));
store.csatProcessed = r.processed;
const group = envInt('WS_GLPI_GROUP_SDM', 0);
if (!r.tasks.length) return [{ json: { noop: true } }];
return r.tasks.map((t) => ({ json: { noop: false, path: '/TicketTask', body: { input: WS.compact({ tickets_id: t.ticket_id, content: t.content, is_private: 1, state: 1, groups_id_tech: group || undefined }) } } }));
""", (880, 300))
    w.if_("Has Writes?", "={{ $json.noop !== true }}", (1100, 300))
    w.glpi("GLPI Write", "POST", "={{ $env.WS_GLPI_API_URL + $json.path }}", (1320, 200),
           body="={{ JSON.stringify($json.body) }}", full=False)
    w.session_close(1540, 300)
    w.chain("Daily 08:10", a)
    w.run_now((0, 500), a)
    w.chain(b, "Get Surveys", "Decide", "Has Writes?")
    w.link("Has Writes?", "GLPI Write", 0)
    w.link("Has Writes?", "GLPI killSession", 1)
    w.link("GLPI Write", "GLPI killSession")
    return w


# ---------------------------------------------------------------- WF-04
def wf04() -> WF:
    policy = {"tiers": SLA["tiers"], "calendars": SLA["calendars"]}
    risk = int(SLA.get("at_risk_minutes", 15))
    w = WF("WF-04", "SLA Watch", f"INT-04: every 5 min - SMS at T-{risk} min and at breach of time-to-own / time-to-resolve, tier and business-hours aware.")
    w.add("Every 5 min", "n8n-nodes-base.scheduleTrigger", 1.2,
          {"rule": {"interval": [{"field": "minutes", "minutesInterval": 5}]}}, (0, 300))
    a, b = w.session_open(220, 300)

    def search(name, field, status_value, op, value, pos):
        return w.glpi(name, "GET", "/search/Ticket", pos, query=[
            ("criteria[0][field]", "12"), ("criteria[0][searchtype]", "equals"), ("criteria[0][value]", status_value),
            ("criteria[1][link]", "AND"), ("criteria[1][field]", field), ("criteria[1][searchtype]", op), ("criteria[1][value]", value),
            *( [("criteria[2][link]", "AND"), ("criteria[2][field]", field), ("criteria[2][searchtype]", "morethan"), ("criteria[2][value]", "NOW")]
               if op == "lessthan" and value != "NOW" else []),
            ("forcedisplay[0]", "1"), ("forcedisplay[1]", "2"), ("forcedisplay[2]", "3"), ("forcedisplay[3]", "80"),
            ("range", "0-200")])

    # 18 = time to resolve, 155 = time to own; status "notold" = not solved/closed, 1 = new
    search("TTR At Risk", "18", "notold", "lessthan", f"{risk}MINUTE", (660, 300))
    search("TTR Breached", "18", "notold", "lessthan", "NOW", (880, 300))
    search("TTO At Risk", "155", "1", "lessthan", f"{risk}MINUTE", (1100, 300))
    search("TTO Breached", "155", "1", "lessthan", "NOW", (1320, 300))
    w.code("Decide", CHECK_HTTP + f"const POLICY = {json.dumps(policy)};\n" + r"""
const PRIO = { 'very high': 5, 'major': 6, 'high': 4, 'medium': 3, 'low': 2, 'very low': 1 };
const prio = (v) => (/^[0-9]+$/.test(String(v)) ? Number(v) : PRIO[String(v).toLowerCase()] || 3);
const strip = (s) => String(s ?? '').replace(/<[^>]*>/g, '').trim();
const rows = [];
for (const [node, kind, stage] of [['TTR Breached', 'TTR', 'BREACHED'], ['TTR At Risk', 'TTR', 'AT RISK'], ['TTO Breached', 'TTO', 'BREACHED'], ['TTO At Risk', 'TTO', 'AT RISK']]) {
  const b = body(node);
  for (const r of (b && Array.isArray(b.data) ? b.data : [])) rows.push({ id: r['2'], name: strip(r['1']), priority: prio(r['3']), entity: strip(r['80']), kind, stage });
}
let tenants = {};
try { tenants = JSON.parse($env.WS_TENANTS_JSON || '{}'); } catch (e) { tenants = {}; }
const store = $getWorkflowStaticData('global');
const r = WS.decideSlaAlerts(rows, { ...POLICY, tenants }, store.slaSent || {}, new Date());
store.slaSent = r.sent;
if (!r.alerts.length) return [{ json: { noop: true } }];
return r.alerts.map((a) => ({ json: { noop: false, notify: { channel: a.channel, message: a.message }, ticket_id: a.ticket_id } }));
""", (1540, 300))
    w.if_("Has Alerts?", "={{ $json.noop !== true }}", (1760, 300))
    w.sms("Send SMS", (1980, 200))
    w.session_close(2200, 300)
    w.chain("Every 5 min", a)
    w.run_now((0, 500), a)
    w.chain(b, "TTR At Risk", "TTR Breached", "TTO At Risk", "TTO Breached", "Decide", "Has Alerts?")
    w.link("Has Alerts?", "Send SMS", 0)
    w.link("Has Alerts?", "GLPI killSession", 1)
    w.link("Send SMS", "GLPI killSession")
    return w


# ---------------------------------------------------------------- WF-99
def wf99() -> WF:
    w = WF("WF-99", "Workflow Failure Alert", "INT-07: any failed WiseSphere workflow execution -> SMS to the integration engineer. Retry from n8n Executions (dead-letter = failed executions, kept 'all').")
    w.add("On Workflow Error", "n8n-nodes-base.errorTrigger", 1, {}, (0, 300))
    w.code("Format", r"""
const e = $input.first().json;
const wf = (e.workflow && e.workflow.name) || 'unknown workflow';
const ex = e.execution || {};
const msg = (ex.error && ex.error.message) || 'unknown error';
return [{ json: { notify: { channel: 'integration', message: WS.truncate(`WiseSphere n8n FAILED ${wf} exec ${ex.id || '?'} at ${ex.lastNodeExecuted || '?'}: ${msg}`, 300) }, execution_url: ex.url || '' } }];
""", (220, 300))
    w.sms("Send SMS", (440, 300))
    w.chain("On Workflow Error", "Format", "Send SMS")
    return w


BUILDERS = {"WF-01": wf01, "WF-02": wf02, "WF-03": wf03, "WF-04": wf04, "WF-99": wf99}
FILES = {"WF-01": "WF-01_zabbix-to-glpi.json", "WF-02": "WF-02_wazuh-to-glpi.json", "WF-03": "WF-03_csat-review.json",
         "WF-04": "WF-04_sla-watch.json", "WF-99": "WF-99_error-alert.json"}


def render() -> dict[str, str]:
    out = {}
    for key, build in BUILDERS.items():
        out[FILES[key]] = json.dumps(build().to_json(error_workflow=key != "WF-99"), indent=2, ensure_ascii=False) + "\n"
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="exit 1 if n8n/workflows is out of date")
    args = ap.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    stale = []
    for fname, text in render().items():
        p = OUT / fname
        if args.check:
            if not p.exists() or p.read_text() != text:
                stale.append(fname)
        else:
            p.write_text(text)
            print(f"wrote {p.relative_to(ROOT)}")
    if stale:
        print("stale workflow JSON (run: python3 n8n/build_workflows.py):", ", ".join(stale), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
