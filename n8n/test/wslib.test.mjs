import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const WS = require('../src/wslib.js');

const cfg = { defaultEntityId: 0, stormThreshold: 10, requestTypeId: 7, nocGroupId: 3, solutionTypeId: 0 };
const zbx = (over = {}) => WS.normalizeZabbixEvent({
  schema: 'ws.event.v1', event_id: '1001', status: 'PROBLEM', severity: '4',
  event_name: 'Interface Gi0/1 down', host: 'cl001-kwi-sw01', host_name: 'Core switch',
  tags: JSON.stringify([{ tag: 'client', value: 'cl001' }, { tag: 'site', value: 'kwi' }, { tag: 'glpi_asset', value: 'NetworkEquipment:12' }]),
  ...over,
}).event;

test('clean drops unresolved Zabbix macros', () => {
  assert.equal(WS.clean('{EVENT.TAGS.client}'), '');
  assert.equal(WS.clean('*UNKNOWN*'), '');
  assert.equal(WS.clean('  CL001 '), 'CL001');
  assert.equal(WS.clean(null), '');
});

test('normalize reads client/site/asset from TAGSJSON and upper-cases codes', () => {
  const e = zbx();
  assert.equal(e.client_code, 'CL001');
  assert.equal(e.site_code, 'KWI');
  assert.equal(e.glpi_asset, 'NetworkEquipment:12');
});

test('normalize accepts PoC payload (subject/eventid, severity label)', () => {
  const r = WS.normalizeZabbixEvent({ subject: 'Zabbix agent is not available', host: 'Zabbix server', severity: 'High', eventid: '42', event_value: '1' });
  assert.equal(r.valid, true, r.errors.join());
  assert.equal(r.event.severity, '4');
  assert.equal(r.event.status, 'PROBLEM');
  assert.equal(r.event.client_code, '');
});

test('normalize rejects bad payloads', () => {
  assert.equal(WS.normalizeZabbixEvent(null).valid, false);
  const r = WS.normalizeZabbixEvent({ event_id: 'abc', status: 'X', host: '', event_name: '' });
  assert.equal(r.valid, false);
  assert.ok(r.errors.length >= 3);
  assert.equal(WS.normalizeZabbixEvent({ event_id: '1', status: 'PROBLEM', severity: '3', event_name: 'x', host: 'h', client_code: 'ACME' }).valid, false);
});

test('severity mapping INT-01', () => {
  assert.deepEqual(WS.zbxSeverityToGlpi('5'), { urgency: 5, impact: 5 });
  assert.deepEqual(WS.zbxSeverityToGlpi('4'), { urgency: 4, impact: 4 });
  assert.deepEqual(WS.zbxSeverityToGlpi('3'), { urgency: 3, impact: 3 });
  assert.deepEqual(WS.zbxSeverityToGlpi('1'), { urgency: 2, impact: 2 });
});

test('TC-01 new problem creates a ticket in the client entity with asset link', () => {
  const d = WS.decideZabbix({ event: zbx(), entityId: 5, eventTickets: [], stormTickets: [], recentCount: 0, cfg });
  assert.equal(d.action, 'create');
  const input = d.ops[0].body.input;
  assert.equal(d.ops[0].path, '/Ticket');
  assert.equal(input.entities_id, 5);
  assert.ok(input.name.startsWith('[ZBX:1001] Core switch: Interface Gi0/1 down'));
  assert.deepEqual(input.items_id, { NetworkEquipment: [12] });
  assert.equal(input.urgency, 4);
  assert.equal(input.requesttypes_id, 7);
  assert.equal(input._groups_id_assign, 3);
  assert.equal(d.notify, null);
});

test('disaster severity pages NOC', () => {
  const d = WS.decideZabbix({ event: zbx({ severity: '5' }), entityId: 5, eventTickets: [], stormTickets: [], recentCount: 0, cfg });
  assert.equal(d.notify.channel, 'noc');
});

test('unmapped host goes to default entity and is flagged', () => {
  const e = zbx({ tags: '' });
  const d = WS.decideZabbix({ event: e, entityId: null, eventTickets: [], stormTickets: [], recentCount: 50, cfg });
  assert.equal(d.action, 'create', 'storm logic only for mapped clients');
  assert.equal(d.ops[0].body.input.entities_id, 0);
  assert.match(d.ops[0].body.input.content, /UNMAPPED/);
});

test('TC-02 recovery solves the open ticket', () => {
  const d = WS.decideZabbix({ event: zbx({ status: 'RESOLVED' }), entityId: 5,
    eventTickets: [{ id: 77, name: '[ZBX:1001] Core switch: Interface Gi0/1 down', status: 2 }], stormTickets: [], recentCount: 0, cfg });
  assert.equal(d.action, 'solve');
  assert.equal(d.ops[0].path, '/ITILSolution');
  assert.equal(d.ops[0].body.input.items_id, 77);
});

test('recovery for already solved ticket is a no-op', () => {
  const d = WS.decideZabbix({ event: zbx({ status: 'RESOLVED' }), entityId: 5,
    eventTickets: [{ id: 77, name: '[ZBX:1001] x', status: 5 }], stormTickets: [], recentCount: 0, cfg });
  assert.equal(d.action, 'noop');
});

test('TC-03 repeat notification adds follow-up, no duplicate', () => {
  const d = WS.decideZabbix({ event: zbx(), entityId: 5,
    eventTickets: [{ id: 77, name: '[ZBX:1001] Core switch', status: 1 }], stormTickets: [], recentCount: 3, cfg });
  assert.equal(d.action, 'followup');
  assert.equal(d.ops[0].path, '/ITILFollowup');
});

test('key match is exact: [ZBX:100] does not match [ZBX:1001]', () => {
  assert.equal(WS.findByKey([{ id: 1, name: '[ZBX:1001] a', status: 1 }], '[ZBX:100]'), null);
});

test('TC-04 storm: threshold reached creates STORM parent and pages', () => {
  const d = WS.decideZabbix({ event: zbx(), entityId: 5, eventTickets: [], stormTickets: [], recentCount: 10, cfg });
  assert.equal(d.action, 'storm_create');
  assert.ok(d.ops[0].body.input.name.startsWith('[STORM:CL001]'));
  assert.equal(d.notify.channel, 'noc');
});

test('TC-04 storm: open parent receives follow-ups; solved parent is ignored', () => {
  const open = WS.decideZabbix({ event: zbx(), entityId: 5, eventTickets: [], stormTickets: [{ id: 90, name: '[STORM:CL001] Alert storm', status: 1 }], recentCount: 12, cfg });
  assert.equal(open.action, 'storm_followup');
  assert.equal(open.ops[0].body.input.items_id, 90);
  const solved = WS.decideZabbix({ event: zbx(), entityId: 5, eventTickets: [], stormTickets: [{ id: 90, name: '[STORM:CL001] Alert storm', status: 5 }], recentCount: 12, cfg });
  assert.equal(solved.action, 'storm_create');
});

test('recovery of an event grouped into a storm adds follow-up to the storm', () => {
  const d = WS.decideZabbix({ event: zbx({ status: 'RESOLVED' }), entityId: 5, eventTickets: [],
    stormTickets: [{ id: 90, name: '[STORM:CL001] x', status: 2 }], recentCount: 0, cfg });
  assert.equal(d.action, 'storm_followup');
});

test('content is HTML-escaped', () => {
  const d = WS.decideZabbix({ event: zbx({ event_name: '<script>alert(1)</script>' }), entityId: 5, eventTickets: [], stormTickets: [], recentCount: 0, cfg });
  assert.ok(!d.ops[0].body.input.content.includes('<script>'));
});

// ---------------------------------------------------------------- Wazuh
const wz = (over = {}) => WS.normalizeWazuhAlert({
  schema: 'ws.secevent.v1', alert_id: '1700000000.123', timestamp: '2026-11-22T10:00:00Z',
  rule: { id: '5712', level: 10, description: 'sshd: brute force trying to get access', groups: ['authentication_failures'], mitre: { id: ['T1110'], tactic: ['Credential Access'] } },
  agent: { id: '003', name: 'cl001-dc01', ip: '10.1.1.10', labels: { client: 'CL001', site: 'KWI' } },
  srcip: '203.0.113.5', ...over,
}).alert;

test('wazuh level mapping INT-02', () => {
  assert.deepEqual(WS.wazuhLevelToGlpi(13), { urgency: 5, impact: 5 });
  assert.deepEqual(WS.wazuhLevelToGlpi(12), { urgency: 4, impact: 4 });
  assert.deepEqual(WS.wazuhLevelToGlpi(10), { urgency: 4, impact: 3 });
});

test('TC-07 level 10 creates SOC ticket without SMS; level 12 pages SOC', () => {
  const c = { defaultEntityId: 0, socGroupId: 4, requestTypeId: 8 };
  const d10 = WS.decideWazuh({ alert: wz(), entityId: 5, alertTickets: [], cfg: c });
  assert.equal(d10.action, 'create');
  assert.ok(d10.ops[0].body.input.name.startsWith('[WZH:003:5712]'));
  assert.equal(d10.ops[0].body.input._groups_id_assign, 4);
  assert.equal(d10.notify, null);
  const d12 = WS.decideWazuh({ alert: wz({ rule: { id: '5712', level: 12, description: 'x' } }), entityId: 5, alertTickets: [], cfg: c });
  assert.equal(d12.notify.channel, 'soc');
});

test('wazuh dedupe on open ticket; below level ignored', () => {
  const d = WS.decideWazuh({ alert: wz(), entityId: 5, alertTickets: [{ id: 3, name: '[WZH:003:5712] x', status: 2 }], cfg: {} });
  assert.equal(d.action, 'followup');
  const low = WS.decideWazuh({ alert: wz({ rule: { id: '1', level: 5, description: 'x' } }), entityId: 5, alertTickets: [], cfg: {} });
  assert.equal(low.action, 'noop');
});

// ---------------------------------------------------------------- SLA
const policy = {
  tenants: { CL001: { tier: 'gold', country: 'KW' }, CL002: { tier: 'silver', country: 'KW' } },
  tiers: { silver: { after_hours_priorities: [5, 6] }, gold: { after_hours_priorities: [4, 5, 6] } },
  calendars: { KW: { timezone: 'Asia/Kuwait', days: [0, 1, 2, 3, 4], start: '08:00', end: '17:00' } },
};

test('business hours in Kuwait', () => {
  assert.equal(WS.isBusinessHours(new Date('2026-11-22T07:00:00Z'), policy.calendars.KW), true);  // Sun 10:00 KW
  assert.equal(WS.isBusinessHours(new Date('2026-11-22T20:00:00Z'), policy.calendars.KW), false); // Sun 23:00 KW
  assert.equal(WS.isBusinessHours(new Date('2026-11-27T07:00:00Z'), policy.calendars.KW), false); // Friday
});

test('TC-06 SLA alerts: tier-aware after hours, deduped', () => {
  const night = new Date('2026-11-22T20:00:00Z');
  const rows = [
    { id: 1, name: 'Gold P2', priority: 4, entity: 'WiseSphere > CL001 - Pilot', stage: 'AT RISK', kind: 'TTR' },
    { id: 2, name: 'Silver P2', priority: 4, entity: 'WiseSphere > CL002 - Other', stage: 'AT RISK', kind: 'TTR' },
    { id: 3, name: 'Silver P1', priority: 5, entity: 'WiseSphere > CL002 - Other', stage: 'BREACHED', kind: 'TTO' },
  ];
  const r1 = WS.decideSlaAlerts(rows, policy, {}, night);
  assert.deepEqual(r1.alerts.map((a) => a.ticket_id), [1, 3]);
  assert.match(r1.alerts[0].message, /AT RISK CL001 GOLD P2 TTR #1/);
  const r2 = WS.decideSlaAlerts(rows, policy, r1.sent, night);
  assert.equal(r2.alerts.length, 0, 'same stage is not sent twice');
  const r3 = WS.decideSlaAlerts([{ ...rows[0], stage: 'BREACHED' }], policy, r1.sent, night);
  assert.equal(r3.alerts.length, 1, 'breach after at-risk is sent');
});

test('SLA dedupe memory is pruned after 3 days', () => {
  const now = new Date('2026-11-22T07:00:00Z');
  const r = WS.decideSlaAlerts([], policy, { 'old:TTR:AT RISK': now.getTime() - 4 * 86400000, 'new:TTR:AT RISK': now.getTime() }, now);
  assert.deepEqual(Object.keys(r.sent), ['new:TTR:AT RISK']);
});

// ---------------------------------------------------------------- CSAT
test('CSAT: only new scores <= 2 create recovery tasks', () => {
  const rows = [{ id: 1, tickets_id: 10, satisfaction: 1, comment: 'slow' }, { id: 2, tickets_id: 11, satisfaction: 5 }, { id: 3, tickets_id: 12, satisfaction: null }];
  const r = WS.decideCsat(rows, []);
  assert.deepEqual(r.tasks.map((t) => t.ticket_id), [10]);
  assert.equal(WS.decideCsat(rows, r.processed).tasks.length, 0);
});
