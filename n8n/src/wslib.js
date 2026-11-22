/*
 * WiseSphere integration library (shared by the n8n Code nodes).
 *
 * n8n Code nodes cannot `require` project files, so n8n/build/build-workflows.mjs
 * inlines this file at the top of every Code node. Keep it dependency-free,
 * synchronous and side-effect free: every function takes data in and returns
 * a decision out. All HTTP calls are done by n8n HTTP Request nodes.
 *
 * Unit tests: n8n/test/*.test.mjs  (node --test)
 */

const WS = (() => {
  const TICKET_STATUS = { NEW: 1, ASSIGNED: 2, PLANNED: 3, WAITING: 4, SOLVED: 5, CLOSED: 6 };
  const ZBX_SEVERITY_LABEL = ['Not classified', 'Information', 'Warning', 'Average', 'High', 'Disaster'];

  /** Trim a value and drop unresolved Zabbix macros such as "{EVENT.TAGS.client}" or "*UNKNOWN*". */
  function clean(v) {
    if (v === undefined || v === null) return '';
    const s = String(v).trim();
    if (s === '*UNKNOWN*') return '';
    if (/^\{[A-Z0-9_.$#]+(\.[^}]*)?\}$/.test(s)) return '';
    return s;
  }

  /** Parse Zabbix {EVENT.TAGSJSON} ([{tag, value}]) into a plain object; tolerant of bad input. */
  function parseTags(raw) {
    const out = {};
    if (!raw) return out;
    let arr = raw;
    if (typeof raw === 'string') {
      const s = clean(raw);
      if (!s) return out;
      try { arr = JSON.parse(s); } catch (e) { return out; }
    }
    if (!Array.isArray(arr)) return out;
    for (const t of arr) {
      if (t && typeof t.tag === 'string' && !(t.tag in out)) out[t.tag] = String(t.value ?? '');
    }
    return out;
  }

  function escapeHtml(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  /** GLPI rich-text content from [label, value] rows. */
  function htmlRows(title, rows, footer) {
    const body = rows
      .filter(([, v]) => v !== undefined && v !== null && String(v) !== '')
      .map(([k, v]) => `<b>${escapeHtml(k)}:</b> ${escapeHtml(v)}`)
      .join('<br>');
    return `<p>${escapeHtml(title)}</p><p>${body}</p>` + (footer ? `<p><i>${escapeHtml(footer)}</i></p>` : '');
  }

  function truncate(s, n) {
    s = String(s ?? '');
    return s.length > n ? s.slice(0, n - 1) + '…' : s;
  }

  /** INT-01 mapping: Zabbix severity -> GLPI urgency/impact (priority is computed by GLPI's matrix). */
  function zbxSeverityToGlpi(sev) {
    const n = Number(sev);
    if (n >= 5) return { urgency: 5, impact: 5 };
    if (n === 4) return { urgency: 4, impact: 4 };
    if (n === 3) return { urgency: 3, impact: 3 };
    return { urgency: 2, impact: 2 };
  }

  /** INT-02 mapping: Wazuh rule level -> GLPI urgency/impact. */
  function wazuhLevelToGlpi(level) {
    const n = Number(level);
    if (n >= 13) return { urgency: 5, impact: 5 };
    if (n === 12) return { urgency: 4, impact: 4 };
    if (n >= 10) return { urgency: 4, impact: 3 };
    return { urgency: 3, impact: 2 };
  }

  /** Same matrix as GLPI's default priority matrix (used for SMS wording and tier rules). */
  function glpiPriority(urgency, impact) {
    const m = {
      5: [0, 3, 4, 4, 5, 5], 4: [0, 2, 3, 4, 4, 5], 3: [0, 2, 3, 3, 4, 4],
      2: [0, 1, 2, 2, 3, 3], 1: [0, 1, 1, 2, 2, 2],
    };
    return (m[urgency] || m[3])[impact] || 3;
  }

  const zbxKey = (eventId) => `[ZBX:${eventId}]`;
  const stormKey = (client) => `[STORM:${client || 'UNMAPPED'}]`;
  const wzhKey = (agentId, ruleId) => `[WZH:${agentId}:${ruleId}]`;

  // ---------------------------------------------------------------- Zabbix

  /** Validate and normalise a ws.event.v1 webhook body. */
  function normalizeZabbixEvent(body) {
    const errors = [];
    if (!body || typeof body !== 'object') return { valid: false, errors: ['body is not a JSON object'], event: null };
    const tags = parseTags(body.tags);

    let status = clean(body.status).toUpperCase();
    if (!status && clean(body.event_value) !== '') status = clean(body.event_value) === '0' ? 'RESOLVED' : 'PROBLEM';
    if (status === 'OK' || status === 'RECOVERY') status = 'RESOLVED';
    if (clean(body.event_update) === '1' && status === 'PROBLEM') status = 'UPDATE';

    const event = {
      schema: 'ws.event.v1',
      event_id: clean(body.event_id ?? body.eventid),
      status,
      severity: clean(body.severity),
      event_name: clean(body.event_name ?? body.subject),
      host: clean(body.host),
      host_name: clean(body.host_name) || clean(body.host),
      host_ip: clean(body.host_ip),
      client_code: (clean(body.client_code) || clean(tags.client)).toUpperCase(),
      site_code: (clean(body.site_code) || clean(tags.site)).toUpperCase(),
      glpi_asset: clean(body.glpi_asset) || clean(tags.glpi_asset),
      event_time: clean(body.event_time),
      opdata: clean(body.opdata),
      trigger_id: clean(body.trigger_id),
      event_url: clean(body.event_url),
      update_message: clean(body.update_message),
      update_user: clean(body.update_user),
    };

    // Severity may arrive as a label from older media types.
    if (!/^[0-5]$/.test(event.severity)) {
      const idx = ZBX_SEVERITY_LABEL.findIndex((l) => l.toLowerCase() === event.severity.toLowerCase());
      event.severity = idx >= 0 ? String(idx) : '';
    }

    if (!/^[0-9]+$/.test(event.event_id)) errors.push('event_id must be numeric');
    if (!['PROBLEM', 'RESOLVED', 'UPDATE'].includes(event.status)) errors.push('status must be PROBLEM, RESOLVED or UPDATE');
    if (event.status === 'PROBLEM' && event.severity === '') errors.push('severity must be 0-5');
    if (!event.event_name) errors.push('event_name is required');
    if (!event.host) errors.push('host is required');
    if (event.client_code && !/^CL[0-9]{3}$/.test(event.client_code)) errors.push('client_code must look like CL001');
    if (event.glpi_asset && !/^[A-Za-z]+:[0-9]+$/.test(event.glpi_asset)) event.glpi_asset = '';

    return { valid: errors.length === 0, errors, event };
  }

  function zbxTicketName(ev) {
    return truncate(`${zbxKey(ev.event_id)} ${ev.host_name}: ${ev.event_name}`, 250);
  }

  function zbxDetails(ev) {
    return [
      ['Client', ev.client_code || 'UNMAPPED'],
      ['Site', ev.site_code],
      ['Host', ev.host_name !== ev.host ? `${ev.host_name} (${ev.host})` : ev.host],
      ['IP', ev.host_ip],
      ['Problem', ev.event_name],
      ['Severity', ZBX_SEVERITY_LABEL[Number(ev.severity)] || ev.severity],
      ['Operational data', ev.opdata],
      ['Event time', ev.event_time],
      ['Zabbix event ID', ev.event_id],
      ['Zabbix link', ev.event_url],
    ];
  }

  /** Pick the ticket whose name carries exactly this key (GLPI LIKE search can over-match). */
  function findByKey(tickets, key, { openOnly = false } = {}) {
    const list = (Array.isArray(tickets) ? tickets : [])
      .filter((t) => t && typeof t.name === 'string' && t.name.startsWith(key))
      .filter((t) => !openOnly || Number(t.status) < TICKET_STATUS.SOLVED)
      .sort((a, b) => Number(b.id) - Number(a.id));
    return list[0] || null;
  }

  /**
   * Decide what WF-01 must write to GLPI for one Zabbix event.
   *
   * ctx = {
   *   event,                 // normalised ws.event.v1
   *   entityId,              // GLPI entity of the client (null if unknown)
   *   eventTickets,          // GLPI tickets whose name contains [ZBX:<id>]
   *   stormTickets,          // GLPI tickets whose name contains [STORM:<client>]
   *   recentCount,           // monitoring tickets opened for this entity in the storm window
   *   cfg: { defaultEntityId, stormThreshold, requestTypeId, nocGroupId, solutionTypeId }
   * }
   * Returns { action, ops: [{ path, body }], notify: null | { channel, message }, summary }
   */
  function decideZabbix(ctx) {
    const { event: ev, cfg = {} } = ctx;
    const threshold = Number(cfg.stormThreshold ?? 10);
    const mapped = ctx.entityId !== null && ctx.entityId !== undefined;
    const entityId = mapped ? Number(ctx.entityId) : Number(cfg.defaultEntityId ?? 0);
    const existing = findByKey(ctx.eventTickets, zbxKey(ev.event_id));
    const openStorm = findByKey(ctx.stormTickets, stormKey(ev.client_code), { openOnly: true });
    const footer = 'Opened automatically by WiseSphere (Zabbix → n8n → GLPI).';
    const summary = { event_id: ev.event_id, status: ev.status, client_code: ev.client_code || null, entity_id: entityId, mapped };

    const followup = (ticketId, title, extraRows = []) => ({
      path: '/ITILFollowup',
      body: { input: { itemtype: 'Ticket', items_id: Number(ticketId), is_private: 0, requesttypes_id: cfg.requestTypeId || undefined,
        content: htmlRows(title, [...zbxDetails(ev), ...extraRows]) } },
    });

    if (ev.status === 'PROBLEM') {
      if (existing) {
        return { action: 'followup', ops: [followup(existing.id, 'Repeat notification from Zabbix for the same event.')], notify: null,
          summary: { ...summary, action: 'followup', ticket_id: Number(existing.id) } };
      }
      const recent = Number(ctx.recentCount || 0);
      if (mapped && recent >= threshold) {
        if (openStorm) {
          return { action: 'storm_followup', ops: [followup(openStorm.id, `${zbxKey(ev.event_id)} Additional problem during alert storm.`)], notify: null,
            summary: { ...summary, action: 'storm_followup', ticket_id: Number(openStorm.id) } };
        }
        const name = truncate(`${stormKey(ev.client_code)} Alert storm – ${ev.client_code}: more than ${threshold} problems in 5 minutes`, 250);
        const input = {
          name, entities_id: entityId, type: 1, urgency: 5, impact: 4, status: TICKET_STATUS.NEW,
          requesttypes_id: cfg.requestTypeId || undefined,
          _groups_id_assign: cfg.nocGroupId || undefined,
          content: htmlRows(`Alert storm detected for ${ev.client_code}. Further problems are added to this ticket as follow-ups.`,
            [['First grouped event', zbxKey(ev.event_id)], ...zbxDetails(ev)], footer),
        };
        return {
          action: 'storm_create', ops: [{ path: '/Ticket', body: { input: clean_(input) } }],
          notify: { channel: 'noc', message: `WiseSphere STORM ${ev.client_code}: >${threshold} problems in 5 min. Latest: ${truncate(ev.host_name + ' ' + ev.event_name, 80)}` },
          summary: { ...summary, action: 'storm_create' },
        };
      }
      const sev = zbxSeverityToGlpi(ev.severity);
      const input = {
        name: zbxTicketName(ev), entities_id: entityId, type: 1, status: TICKET_STATUS.NEW, ...sev,
        requesttypes_id: cfg.requestTypeId || undefined,
        _groups_id_assign: cfg.nocGroupId || undefined,
        content: htmlRows(mapped ? 'Monitoring alert raised by Zabbix.' : 'Monitoring alert raised by Zabbix for an UNMAPPED host (no client tag). Fix the host tags.',
          zbxDetails(ev), footer),
      };
      if (ev.glpi_asset) {
        const [itemtype, id] = ev.glpi_asset.split(':');
        input.items_id = { [itemtype]: [Number(id)] };
      }
      const notify = Number(ev.severity) >= 5
        ? { channel: 'noc', message: `WiseSphere P1 ${ev.client_code || 'UNMAPPED'}: ${truncate(ev.host_name + ' – ' + ev.event_name, 110)}` }
        : null;
      return { action: 'create', ops: [{ path: '/Ticket', body: { input: clean_(input) } }], notify, summary: { ...summary, action: 'create' } };
    }

    if (ev.status === 'RESOLVED') {
      if (existing && Number(existing.status) < TICKET_STATUS.SOLVED) {
        return {
          action: 'solve',
          ops: [{ path: '/ITILSolution', body: { input: clean_({ itemtype: 'Ticket', items_id: Number(existing.id), solutiontypes_id: cfg.solutionTypeId || undefined,
            content: htmlRows('Problem resolved – Zabbix reported recovery. Engineer to confirm closure.', zbxDetails(ev)) }) } }],
          notify: null, summary: { ...summary, action: 'solve', ticket_id: Number(existing.id) },
        };
      }
      if (!existing && openStorm) {
        return { action: 'storm_followup', ops: [followup(openStorm.id, `${zbxKey(ev.event_id)} Recovered.`)], notify: null,
          summary: { ...summary, action: 'storm_followup', ticket_id: Number(openStorm.id) } };
      }
      return { action: 'noop', ops: [], notify: null, summary: { ...summary, action: 'noop', reason: existing ? 'ticket already solved/closed' : 'no ticket for event' } };
    }

    // UPDATE: acknowledgement / comment made in Zabbix
    if (existing) {
      const rows = [['Updated by', ev.update_user], ['Message', ev.update_message]];
      return { action: 'followup', ops: [followup(existing.id, 'Event updated in Zabbix.', rows)], notify: null,
        summary: { ...summary, action: 'followup', ticket_id: Number(existing.id) } };
    }
    return { action: 'noop', ops: [], notify: null, summary: { ...summary, action: 'noop', reason: 'no ticket for event' } };
  }

  // ---------------------------------------------------------------- Wazuh

  function normalizeWazuhAlert(body) {
    const errors = [];
    if (!body || typeof body !== 'object') return { valid: false, errors: ['body is not a JSON object'], alert: null };
    const rule = body.rule || {};
    const agent = body.agent || {};
    const labels = agent.labels || {};
    const alert = {
      schema: 'ws.secevent.v1',
      alert_id: clean(body.alert_id ?? body.id),
      timestamp: clean(body.timestamp),
      rule_id: clean(rule.id),
      level: Number(rule.level),
      description: clean(rule.description),
      groups: Array.isArray(rule.groups) ? rule.groups.map(String) : [],
      mitre: rule.mitre && typeof rule.mitre === 'object' ? rule.mitre : {},
      agent_id: clean(agent.id),
      agent_name: clean(agent.name),
      agent_ip: clean(agent.ip),
      client_code: clean(labels.client).toUpperCase(),
      site_code: clean(labels.site).toUpperCase(),
      srcip: clean(body.srcip ?? (body.data && body.data.srcip)),
      full_log: truncate(clean(body.full_log), 2000),
    };
    if (!alert.rule_id) errors.push('rule.id is required');
    if (!Number.isFinite(alert.level)) errors.push('rule.level must be a number');
    if (!alert.agent_id) errors.push('agent.id is required');
    if (!alert.description) errors.push('rule.description is required');
    if (alert.client_code && !/^CL[0-9]{3}$/.test(alert.client_code)) errors.push('agent.labels.client must look like CL001');
    return { valid: errors.length === 0, errors, alert };
  }

  /**
   * WF-02 decision. ctx = { alert, entityId, alertTickets, cfg: { defaultEntityId, requestTypeId, socGroupId, ticketLevel, pageLevel } }
   * Open ticket with the same [WZH:agent:rule] key -> follow-up (dedupe); else create. Level >= pageLevel -> SMS SOC.
   */
  function decideWazuh(ctx) {
    const { alert: a, cfg = {} } = ctx;
    const ticketLevel = Number(cfg.ticketLevel ?? 10);
    const pageLevel = Number(cfg.pageLevel ?? 12);
    const mapped = ctx.entityId !== null && ctx.entityId !== undefined;
    const entityId = mapped ? Number(ctx.entityId) : Number(cfg.defaultEntityId ?? 0);
    const key = wzhKey(a.agent_id, a.rule_id);
    const summary = { alert_id: a.alert_id, rule_id: a.rule_id, level: a.level, client_code: a.client_code || null, entity_id: entityId, mapped };
    const mitre = a.mitre || {};
    const rows = [
      ['Client', a.client_code || 'UNMAPPED'], ['Site', a.site_code], ['Agent', `${a.agent_name} (#${a.agent_id})`], ['Agent IP', a.agent_ip],
      ['Rule', `${a.rule_id} – ${a.description}`], ['Level', a.level], ['Groups', a.groups.join(', ')],
      ['MITRE', [].concat(mitre.id || []).concat(mitre.tactic || []).join(', ')], ['Source IP', a.srcip],
      ['Time', a.timestamp], ['Wazuh alert ID', a.alert_id], ['Log', a.full_log],
    ];

    if (a.level < ticketLevel) return { action: 'noop', ops: [], notify: null, summary: { ...summary, action: 'noop', reason: `level below ${ticketLevel}` } };

    const existing = findByKey(ctx.alertTickets, key, { openOnly: true });
    if (existing) {
      return {
        action: 'followup',
        ops: [{ path: '/ITILFollowup', body: { input: clean_({ itemtype: 'Ticket', items_id: Number(existing.id), is_private: 1, requesttypes_id: cfg.requestTypeId || undefined,
          content: htmlRows('Repeat security alert (same agent and rule).', rows) }) } }],
        notify: null, summary: { ...summary, action: 'followup', ticket_id: Number(existing.id) },
      };
    }
    const input = {
      name: truncate(`${key} ${a.agent_name}: ${a.description}`, 250), entities_id: entityId, type: 1, status: TICKET_STATUS.NEW,
      ...wazuhLevelToGlpi(a.level), requesttypes_id: cfg.requestTypeId || undefined, _groups_id_assign: cfg.socGroupId || undefined,
      content: htmlRows('Security alert raised by Wazuh. Containment needs SOC approval in this ticket (SOP-03).', rows,
        'Opened automatically by WiseSphere (Wazuh → n8n → GLPI).'),
    };
    const notify = a.level >= pageLevel
      ? { channel: 'soc', message: `WiseSphere SEC L${a.level} ${a.client_code || 'UNMAPPED'}: ${truncate(a.agent_name + ' – ' + a.description, 110)}` }
      : null;
    return { action: 'create', ops: [{ path: '/Ticket', body: { input: clean_(input) } }], notify, summary: { ...summary, action: 'create' } };
  }

  // ---------------------------------------------------------------- SLA watch (WF-04)

  /** "WiseSphere > CL001 - Pilot Client" -> "CL001" */
  function clientFromEntityName(name) {
    const m = String(name ?? '').match(/\b(CL[0-9]{3})\b/);
    return m ? m[1] : '';
  }

  /** Local wall-clock parts for an IANA zone. */
  function localParts(date, timeZone) {
    const f = new Intl.DateTimeFormat('en-GB', { timeZone, weekday: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
    const parts = Object.fromEntries(f.formatToParts(date).map((p) => [p.type, p.value]));
    const dow = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].indexOf(parts.weekday);
    return { dow, minutes: Number(parts.hour) * 60 + Number(parts.minute) };
  }

  function isBusinessHours(date, calendar) {
    if (!calendar) return true;
    const { dow, minutes } = localParts(date, calendar.timezone || 'UTC');
    const toMin = (hhmm) => { const [h, m] = String(hhmm).split(':').map(Number); return h * 60 + m; };
    return calendar.days.includes(dow) && minutes >= toMin(calendar.start) && minutes < toMin(calendar.end);
  }

  /**
   * Decide which SLA SMS to send. Rows come from GLPI search, already split by stage:
   *   rows = [{ id, name, priority, entity, stage: 'AT RISK'|'BREACHED', kind: 'TTO'|'TTR' }]
   * policy = { tenants: { CL001: { tier, country } }, tiers: {...from config/sla.yaml}, calendars: {...} }
   * sent   = { "<id>:<kind>:<stage>": epochMs } (workflow static data, used for dedupe)
   */
  function decideSlaAlerts(rows, policy, sent, now) {
    const out = [];
    const nowMs = now.getTime();
    const seen = new Set();
    for (const r of rows || []) {
      const key = `${r.id}:${r.kind}:${r.stage}`;
      if (seen.has(key) || (sent && sent[key])) continue;
      seen.add(key);
      const client = clientFromEntityName(r.entity);
      const tenant = (policy.tenants || {})[client] || {};
      const tierName = tenant.tier || 'silver';
      const tier = (policy.tiers || {})[tierName] || { after_hours_priorities: [5, 6] };
      const cal = (policy.calendars || {})[tenant.country || 'KW'];
      const inHours = isBusinessHours(now, cal);
      const prio = Number(r.priority);
      if (!inHours && !(tier.after_hours_priorities || []).includes(prio)) continue;
      out.push({
        key, ticket_id: Number(r.id), channel: inHours ? 'desk' : 'noc',
        message: `WiseSphere SLA ${r.stage} ${client || '?'} ${tierName.toUpperCase()} P${priorityLabel(prio)} ${r.kind} #${r.id}: ${truncate(r.name, 80)}`,
      });
    }
    // prune dedupe memory older than 3 days
    const pruned = {};
    for (const [k, t] of Object.entries(sent || {})) if (nowMs - Number(t) < 3 * 86400000) pruned[k] = t;
    for (const o of out) pruned[o.key] = nowMs;
    return { alerts: out, sent: pruned };
  }

  /** GLPI priority 6/5 -> P1, 4 -> P2, 3 -> P3, else P4 */
  function priorityLabel(p) {
    const n = Number(p);
    return n >= 5 ? 1 : n === 4 ? 2 : n === 3 ? 3 : 4;
  }

  // ---------------------------------------------------------------- CSAT (WF-03)

  /** rows: GLPI TicketSatisfaction items. Returns new low scores (<= maxScore) not yet processed. */
  function decideCsat(rows, processed, maxScore = 2) {
    const done = new Set((processed || []).map(String));
    const low = (rows || []).filter((r) => r && r.satisfaction !== null && r.satisfaction !== undefined && r.satisfaction !== ''
      && Number(r.satisfaction) <= maxScore && !done.has(String(r.id)));
    const ids = [...done, ...low.map((r) => String(r.id))].slice(-2000);
    return {
      tasks: low.map((r) => ({
        ticket_id: Number(r.tickets_id),
        score: Number(r.satisfaction),
        content: htmlRows('Low satisfaction score – service-recovery call needed within 1 business day (SOP-05).',
          [['Score', `${r.satisfaction} / 5`], ['Client comment', r.comment]]),
      })),
      processed: ids,
    };
  }

  /** Drop undefined keys so GLPI does not receive "undefined"/null for optional fields. */
  function clean_(obj) {
    const o = {};
    for (const [k, v] of Object.entries(obj)) if (v !== undefined && v !== null && v !== '') o[k] = v;
    return o;
  }

  return {
    TICKET_STATUS, clean, parseTags, escapeHtml, htmlRows, truncate,
    zbxSeverityToGlpi, wazuhLevelToGlpi, glpiPriority, priorityLabel,
    zbxKey, stormKey, wzhKey, findByKey,
    normalizeZabbixEvent, zbxTicketName, decideZabbix,
    normalizeWazuhAlert, decideWazuh,
    clientFromEntityName, isBusinessHours, decideSlaAlerts,
    decideCsat, compact: clean_,
  };
})();

if (typeof module !== 'undefined' && module.exports) module.exports = WS;
