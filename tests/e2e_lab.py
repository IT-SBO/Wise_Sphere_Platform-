#!/usr/bin/env python3
"""End-to-end tests against the running lab (PRD §6.5 test plan, lab subset).

  python3 tests/e2e_lab.py            # after: make lab-up lab-bootstrap

Real path: Zabbix trigger (fired with zabbix_sender) -> Zabbix action -> media type ->
WiseSphere integration service (C#) -> GLPI ticket. Results are read from the GLPI and Zabbix databases.

  TC-01  problem -> ticket in client entity <= 60 s (NFR-PERF-01)
  TC-02  recovery -> ticket solved automatically
  TC-03  repeat notification -> follow-up, no duplicate
  TC-04  15 devices down -> STORM parent ticket + SMS, remaining events as follow-ups
  TC-06  ticket near SLA breach -> "AT RISK" SMS from WF-04
  TC-07  Wazuh level 10 -> SOC ticket; level 12 -> SMS (WF-02)
  SEC    wrong webhook secret -> 403; invalid payload -> 400 (INT-05)
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

LAB = Path(__file__).resolve().parent.parent / "lab"
ENV = dict(l.split("=", 1) for l in (LAB / ".env").read_text().splitlines() if "=" in l and not l.startswith("#"))
SERVICE = f"http://localhost:{ENV['INTEGRATION_PORT']}"
SMS = f"http://localhost:{ENV['SMS_PORT']}"
RESULTS: list[tuple[str, bool, str]] = []


def sh(*cmd: str) -> str:
    return subprocess.run(cmd, check=True, capture_output=True, text=True).stdout


def sql(query: str) -> list[list[str]]:
    out = sh("docker", "exec", "ws-glpi-db", "mariadb", "-uroot", f"-p{ENV['GLPI_DB_ROOT_PASSWORD']}", "glpi", "-N", "-B", "-e", query)
    return [line.split("\t") for line in out.splitlines() if line]


def zsql(query: str) -> list[list[str]]:
    out = sh("docker", "exec", "ws-zabbix-db", "psql", "-U", "zabbix", "-d", "zabbix", "-At", "-F", "\t", "-c", query)
    return [line.split("\t") for line in out.splitlines() if line]


def sender(item: str, value: int):
    sh("docker", "exec", "ws-zabbix-server", "zabbix_sender", "-z", "127.0.0.1", "-s", "cl001-kwi-lab", "-k", item, "-o", str(value))


def post(path: str, body: dict, secret: str | None = None) -> tuple[int, dict]:
    req = urllib.request.Request(SERVICE + path, data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", "X-WS-Secret": secret or ENV["WS_WEBHOOK_SECRET"]})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw or b"{}")
        except json.JSONDecodeError:
            return e.code, {"raw": raw.decode(errors="replace")}


def run_job(name: str):
    req = urllib.request.Request(f"{SERVICE}/jobs/{name}/run", data=b"", method="POST", headers={"X-WS-Secret": ENV["WS_WEBHOOK_SECRET"]})
    urllib.request.urlopen(req, timeout=60).read()


def sms_messages() -> list[dict]:
    with urllib.request.urlopen(SMS + "/messages", timeout=10) as r:
        return json.loads(r.read())


def sms_clear():
    urllib.request.urlopen(urllib.request.Request(SMS + "/messages", method="DELETE"), timeout=10)


def wait(desc: str, fn, timeout=90, every=1.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = fn()
        if v:
            return v, time.time() - t0
        time.sleep(every)
    return None, time.time() - t0


def check(name: str, ok: bool, detail: str = ""):
    RESULTS.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)


def ticket_by_trigger(desc: str):
    rows = sql(f"SELECT id, entities_id, status, name FROM glpi_tickets WHERE name LIKE '[ZBX:%{desc}' AND is_deleted=0 ORDER BY id DESC LIMIT 1")
    return rows[0] if rows else None


def reset_lab_triggers():
    for n in range(1, 16):
        sender(f"ws.test.{n}", 0)


def main() -> int:
    cl001 = sql("SELECT id FROM glpi_entities WHERE tag='CL001'")[0][0]
    soc_group = sql("SELECT id FROM glpi_groups WHERE name='WS-SOC'")[0][0]
    reset_lab_triggers()
    time.sleep(5)
    # Lab isolation: close and age earlier test tickets so a re-run is not seen as part of an alert storm
    sql("UPDATE glpi_tickets SET status=6, date=DATE_SUB(date, INTERVAL 1 DAY) "
        "WHERE name LIKE '[ZBX:%' OR name LIKE '[STORM:%' OR name LIKE '[WZH:%'")
    sms_clear()
    stamp = int(time.time())
    zbx_start = int(zsql("SELECT COALESCE(MAX(alertid),0) FROM alerts")[0][0])

    # ---- SEC
    code, _ = post("/webhook/zbx-event", {"event_id": "1"}, secret="wrong")
    check("SEC wrong secret rejected", code == 403, f"HTTP {code}")
    code, body = post("/webhook/zbx-event", {"event_id": "x", "status": "PROBLEM"})
    check("SEC invalid payload rejected", code == 400 and body.get("ok") is False, f"HTTP {code} {body.get('errors')}")

    # ---- TC-01
    desc = "Lab device 01 is down"
    before = ticket_by_trigger(desc)
    sender("ws.test.1", 1)
    t, secs = wait("ticket", lambda: (lambda r: r if r and r != before else None)(ticket_by_trigger(desc)), timeout=120)
    check("TC-01 problem -> GLPI ticket in client entity", bool(t) and t[1] == cl001, f"{secs:.1f}s ticket={t and t[0]} entity={t and t[1]}")
    check("TC-01 latency <= 60 s (NFR-PERF-01)", bool(t) and secs <= 60, f"{secs:.1f}s")
    if not t:
        return summary()
    ticket_id = t[0]
    sla = sql(f"SELECT slas_id_ttr, slas_id_tto, requesttypes_id FROM glpi_tickets WHERE id={ticket_id}")[0]
    check("ITSM-02 tier SLA attached by rule", sla[0] != "0" and sla[1] != "0", f"ttr={sla[0]} tto={sla[1]}")
    check("ITSM-03 request source = Monitoring", sql(f"SELECT name FROM glpi_requesttypes WHERE id={sla[2]}")[0][0] == "Monitoring")
    grp = sql(f"SELECT g.name FROM glpi_groups_tickets gt JOIN glpi_groups g ON g.id=gt.groups_id WHERE gt.tickets_id={ticket_id} AND gt.type=2")
    check("Ticket assigned to WS-NOC", ["WS-NOC"] in grp, str(grp))

    # ---- TC-03 (replay the same Zabbix event id directly, as a Zabbix retry would)
    event_id = t[3].split("]")[0].split(":")[1]
    code, body = post("/webhook/zbx-event", {"event_id": event_id, "event_value": "1", "severity": "4", "event_name": desc,
                                             "host": "cl001-kwi-lab", "client_code": "CL001"})
    count = sql(f"SELECT COUNT(*) FROM glpi_tickets WHERE name LIKE '[ZBX:{event_id}]%'")[0][0]
    fu = sql(f"SELECT COUNT(*) FROM glpi_itilfollowups WHERE itemtype='Ticket' AND items_id={ticket_id}")[0][0]
    check("TC-03 repeat -> follow-up, no duplicate", body.get("action") == "followup" and count == "1" and int(fu) >= 1, f"tickets={count} followups={fu}")

    # ---- TC-02
    sender("ws.test.1", 0)
    st, secs = wait("solved", lambda: sql(f"SELECT status FROM glpi_tickets WHERE id={ticket_id}")[0][0] == "5", timeout=120)
    check("TC-02 recovery -> ticket solved automatically", bool(st), f"{secs:.1f}s")

    # ---- TC-06 SLA watch: P2 Gold ticket whose TTR is 10 min away
    sms_clear()
    code, body = post("/webhook/zbx-event", {"event_id": str(900000 + stamp % 100000), "event_value": "1", "severity": "4",
                                             "event_name": f"SLA test {stamp}", "host": "cl001-kwi-lab", "client_code": "CL001"})
    tid = body.get("ticket_id")
    sql(f"UPDATE glpi_tickets SET time_to_resolve = DATE_ADD(NOW(), INTERVAL 10 MINUTE) WHERE id={tid}")
    run_job("sla-watch")
    msgs, _ = wait("sla sms", lambda: [m for m in sms_messages() if f"#{tid}" in m.get("message", "") and "AT RISK" in m["message"]], timeout=60)
    check("TC-06 SLA at risk -> SMS", bool(msgs), msgs[0]["message"][:90] if msgs else f"ticket {tid}")

    # ---- TC-07 Wazuh
    sms_clear()
    base = {"schema": "ws.secevent.v1", "timestamp": "2026-11-22T10:00:00Z", "srcip": "203.0.113.5",
            "agent": {"id": f"{stamp % 1000:03d}", "name": "cl001-dc01", "ip": "10.1.1.10", "labels": {"client": "CL001", "site": "KWI"}}}
    c10, b10 = post("/webhook/wazuh-alert", {**base, "alert_id": f"{stamp}.1", "rule": {"id": "5712", "level": 10, "description": "sshd: brute force"}})
    c12, b12 = post("/webhook/wazuh-alert", {**base, "alert_id": f"{stamp}.2", "rule": {"id": "5763", "level": 12, "description": "sshd: brute force success"}})
    c10b, b10b = post("/webhook/wazuh-alert", {**base, "alert_id": f"{stamp}.3", "rule": {"id": "5712", "level": 10, "description": "sshd: brute force"}})
    ok = b10.get("action") == "create" and b12.get("action") == "create" and b10b.get("action") == "followup"
    check("TC-07 Wazuh -> SOC tickets, repeat deduped", ok, f"{b10.get('action')}/{b12.get('action')}/{b10b.get('action')}")
    if b10.get("ticket_id"):
        g = sql(f"SELECT groups_id FROM glpi_groups_tickets WHERE tickets_id={b10['ticket_id']} AND type=2")
        check("TC-07 assigned to WS-SOC", [soc_group] in g, str(g))
    msgs, _ = wait("soc sms", lambda: [m for m in sms_messages() if "SEC L12" in m.get("message", "")], timeout=30)
    check("TC-07 level 12 -> SMS to SOC, level 10 no SMS", bool(msgs) and not any("SEC L10" in m["message"] for m in sms_messages()),
          msgs[0]["message"][:80] if msgs else "")

    # ---- TC-04 storm: 15 devices down at once (threshold 10 / 5 min)
    sms_clear()
    for n in range(2, 16):
        sender(f"ws.test.{n}", 1)
    storm, secs = wait("storm", lambda: sql("SELECT id, status FROM glpi_tickets WHERE name LIKE '[STORM:CL001]%' AND status < 5 ORDER BY id DESC LIMIT 1"), timeout=180)
    check("TC-04 storm -> one STORM parent ticket", bool(storm), f"{secs:.1f}s id={storm and storm[0][0]}")
    if storm:
        sid = storm[0][0]
        def grouped():
            n = int(sql(f"SELECT COUNT(*) FROM glpi_itilfollowups WHERE itemtype='Ticket' AND items_id={sid}")[0][0])
            created = int(sql("SELECT COUNT(*) FROM glpi_tickets WHERE name LIKE '[ZBX:%Lab device%' AND status < 5")[0][0])
            return (n, created) if n + created + 1 >= 14 else None
        g, _ = wait("grouping", grouped, timeout=120)
        check("TC-04 later events grouped as follow-ups", bool(g), f"followups={g and g[0]} individual_open={g and g[1]}")
        storm_sms, _ = wait("sms", lambda: [m for m in sms_messages() if "STORM" in m.get("message", "")], timeout=60)
        check("TC-04 SMS to on-call", bool(storm_sms), storm_sms[0]["message"][:80] if storm_sms else "")
        dup = sql("SELECT COUNT(*) FROM glpi_tickets WHERE name LIKE '[STORM:CL001]%' AND status < 5")[0][0]
        check("TC-04 no duplicate STORM tickets", dup == "1", f"open storm tickets={dup}")
    reset_lab_triggers()
    if storm:
        sql(f"UPDATE glpi_tickets SET status=6 WHERE id={storm[0][0]}")  # close so later runs start clean

    # ---- TC-10 portal: client isolation, CSRF, login (WiseSphere portal backend)
    portal_checks()

    # ---- Zabbix action log
    failed = zsql(f"SELECT COUNT(*) FROM alerts WHERE status=2 AND alertid > {zbx_start}")[0][0]
    check("Zabbix action log has no failed alerts", failed == "0", f"failed={failed}")
    return summary()


def portal_checks():
    import http.cookiejar
    base = f"http://localhost:{ENV['PORTAL_PORT']}"

    def session(user, password):
        jar = http.cookiejar.CookieJar()
        op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
        req = urllib.request.Request(base + "/api/auth/login", data=json.dumps({"username": user, "password": password}).encode(),
                                     method="POST", headers={"Content-Type": "application/json", "X-WS-Portal": "1"})
        try:
            return op, json.loads(op.open(req, timeout=30).read())
        except urllib.error.HTTPError as e:
            return None, {"status": e.code}

    def get(op, path):
        try:
            with op.open(base + path, timeout=30) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, None

    _, bad = session("client.cl001", "wrong-password")
    check("TC-10 portal rejects a wrong password", bad.get("status") == 401, str(bad))
    noc, me_noc = session("noc.operator", ENV["LAB_USERS_PASSWORD"])
    cli, me_cli = session("client.cl001", ENV["LAB_USERS_PASSWORD"])
    check("TC-10 staff vs client accounts", bool(noc and cli) and me_noc["isStaff"] and not me_cli["isStaff"],
          f"noc staff={me_noc.get('isStaff')} client staff={me_cli.get('isStaff')}")
    _, rows = get(cli, "/api/tickets?scope=all")
    clients = sorted({r["client"] for r in rows or []})
    check("TC-10 client sees only its own tickets", clients == ["CL001"], str(clients))
    other = sql("SELECT t.id FROM glpi_tickets t JOIN glpi_entities e ON e.id=t.entities_id WHERE e.tag='CL000' LIMIT 1")
    if other:
        code_cli, _ = get(cli, f"/api/tickets/{other[0][0]}")
        code_noc, _ = get(noc, f"/api/tickets/{other[0][0]}")
        check("TC-10 client cannot open another client's ticket; staff can", code_cli == 404 and code_noc == 200, f"client={code_cli} staff={code_noc}")
    req = urllib.request.Request(base + "/api/tickets/1/followups", data=b'{"text":"x"}', method="POST", headers={"Content-Type": "application/json"})
    try:
        code = noc.open(req, timeout=30).status
    except urllib.error.HTTPError as e:
        code = e.code
    check("Portal refuses writes without the anti-CSRF header", code == 400, f"HTTP {code}")

    # ---- RPT-01 monthly report
    month = time.strftime("%Y-%m")
    code_r, rep = get(cli, f"/api/reports/monthly?client=CL001&month={month}")
    code_x, _ = get(cli, f"/api/reports/monthly?client=CL000&month={month}")
    check("RPT-01 monthly report for own client; other client refused", code_r == 200 and rep["ticketsOpened"] > 0 and code_x == 404,
          f"own={code_r} opened={rep and rep['ticketsOpened']} other={code_x}")

    # ---- SOC-04 approved containment
    soc, me_soc = session("soc.engineer", ENV["LAB_USERS_PASSWORD"])
    stamp = int(time.time())
    _, alert = post("/webhook/wazuh-alert", {"alert_id": f"{stamp}.7", "rule": {"id": "5712", "level": 10, "description": "sshd: brute force"},
                                            "agent": {"id": f"8{stamp % 100:02d}", "name": "cl001-dc03", "labels": {"client": "CL001"}}, "srcip": "198.51.100.88"})
    tid = alert.get("ticket_id")

    def approve(op, ip, hours=4):
        req = urllib.request.Request(f"{base}/api/tickets/{tid}/containment", method="POST",
                                     data=json.dumps({"ip": ip, "hours": hours, "reason": "e2e brute force test"}).encode(),
                                     headers={"Content-Type": "application/json", "X-WS-Portal": "1"})
        try:
            with op.open(req, timeout=30) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read() or b"{}")

    c_noc, _ = approve(noc, "198.51.100.88")
    c_int, b_int = approve(soc, "10.1.1.5")
    c_long, _ = approve(soc, "198.51.100.88", hours=48)
    c_ok, b_ok = approve(soc, "198.51.100.88")
    with urllib.request.urlopen(f"{SMS}/fw/blocks", timeout=10) as r:
        fw = json.loads(r.read())
    check("SOC-04 only the SOC group can approve", me_soc.get("isSoc") and not me_noc.get("isSoc") and c_noc == 403, f"noc={c_noc}")
    check("SOC-04 internal address and >24 h refused", c_int == 422 and c_long == 422, f"internal={c_int} 48h={c_long}")
    check("SOC-04 approved block reaches the firewall", c_ok == 200 and any(b["ip"] == "198.51.100.88" for b in fw), f"HTTP {c_ok}")
    note = sql(f"SELECT COUNT(*) FROM glpi_itilfollowups WHERE itemtype='Ticket' AND items_id={tid} AND content LIKE '%Containment APPROVED by soc.engineer%'")[0][0]
    check("SOC-04 approval recorded in the GLPI ticket", note == "1", f"followups={note}")
    if c_ok == 200:
        req = urllib.request.Request(f"{base}/api/containment/{b_ok['block']['id']}", method="DELETE", headers={"X-WS-Portal": "1"})
        try:
            code_d = soc.open(req, timeout=30).status
        except urllib.error.HTTPError as e:
            code_d = e.code
        with urllib.request.urlopen(f"{SMS}/fw/blocks", timeout=10) as r:
            fw = json.loads(r.read())
        check("SOC-04 block removed from the firewall on request", code_d == 204 and not any(b["ip"] == "198.51.100.88" for b in fw), f"HTTP {code_d}")


def summary() -> int:
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    print(f"\n{passed}/{len(RESULTS)} checks passed")
    out = Path(os.environ.get("WS_E2E_REPORT", LAB / "e2e-report.json"))
    out.write_text(json.dumps([{"check": n, "pass": ok, "detail": d} for n, ok, d in RESULTS], indent=2))
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
