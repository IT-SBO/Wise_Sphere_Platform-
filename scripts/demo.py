#!/usr/bin/env python3
"""WiseSphere live demo scenarios against the running lab (used by `./wisesphere demo ...`).

  down      a client device goes down  -> ticket in the client's queue in seconds
  up        the device comes back      -> ticket solved automatically
  security  Wazuh brute-force alerts   -> SOC tickets (level 10), SMS for level 12, repeat de-duplicated
  storm     15 devices down at once    -> ONE storm ticket + SMS instead of 15 tickets
  sla       ticket 10 min from breach  -> "SLA AT RISK" SMS to the on-call engineer
  clear     all lab devices back up, open demo tickets solved
  all       clear, down, up, security, sla, storm one after the other

Everything goes through the real path (Zabbix trigger -> action -> webhook -> C# service -> GLPI),
exactly like the end-to-end tests. Lab only.
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

LAB = Path(__file__).resolve().parent.parent / "lab"
ENV = dict(l.split("=", 1) for l in (LAB / ".env").read_text().splitlines() if "=" in l and not l.startswith("#"))
SERVICE = f"http://localhost:{ENV.get('INTEGRATION_PORT', '8088')}"
SMS = f"http://localhost:{ENV.get('SMS_PORT', '8099')}"
PORTAL = f"http://localhost:{ENV.get('PORTAL_PORT', '8089')}"
GLPI = f"http://localhost:{ENV.get('GLPI_PORT', '8090')}"
ORANGE, GREEN, RED, DIM, END = "\033[38;5;166m", "\033[32m", "\033[31m", "\033[2m", "\033[0m"


def sh(*cmd: str) -> str:
    return subprocess.run(cmd, check=True, capture_output=True, text=True).stdout


def sql(query: str) -> list[list[str]]:
    out = sh("docker", "exec", "ws-glpi-db", "mariadb", "-uroot", f"-p{ENV['GLPI_DB_ROOT_PASSWORD']}", "glpi", "-N", "-B", "-e", query)
    return [line.split("\t") for line in out.splitlines() if line]


def sender(n: int, value: int):
    sh("docker", "exec", "ws-zabbix-server", "zabbix_sender", "-z", "127.0.0.1", "-s", "cl001-kwi-lab", "-k", f"ws.test.{n}", "-o", str(value))


def post(path: str, body: dict | None = None) -> tuple[int, dict]:
    req = urllib.request.Request(SERVICE + path, data=json.dumps(body or {}).encode(), method="POST",
                                 headers={"Content-Type": "application/json", "X-WS-Secret": ENV["WS_WEBHOOK_SECRET"]})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw.strip().startswith(b"{") else {})
    except urllib.error.HTTPError as e:
        return e.code, {}


def sms() -> list[dict]:
    with urllib.request.urlopen(SMS + "/messages", timeout=10) as r:
        return json.loads(r.read())


def wait(fn, timeout=120):
    t0 = time.time()
    while time.time() - t0 < timeout:
        v = fn()
        if v:
            return v, time.time() - t0
        time.sleep(1)
    return None, time.time() - t0


def step(text: str):
    print(f"\n{ORANGE}▶ {text}{END}", flush=True)


def ok(text: str):
    print(f"  {GREEN}✔{END} {text}", flush=True)


def fail(text: str):
    print(f"  {RED}✘{END} {text}", flush=True)


def ticket_link(tid) -> str:
    return f"portal {PORTAL}/tickets/{tid}  ·  GLPI {GLPI}/front/ticket.form.php?id={tid}"


def max_ticket() -> int:
    return int(sql("SELECT COALESCE(MAX(id),0) FROM glpi_tickets")[0][0])


def device_ticket(n: int, open_only=True, after: int = 0):
    status = "AND status < 5" if open_only else ""
    rows = sql(f"SELECT id, status, name FROM glpi_tickets WHERE name LIKE '[ZBX:%Lab device {n:02d} is down' AND is_deleted=0 "
               f"{status} AND id > {after} ORDER BY id DESC LIMIT 1")
    return rows[0] if rows else None


def demo_down():
    step("Device down: 'Lab device 01' at client CL001 stops answering (Zabbix trigger fires)")
    sender(1, 0)
    time.sleep(2)
    first = max_ticket()
    sender(1, 1)
    t, secs = wait(lambda: device_ticket(1, after=first))
    if t:
        ok(f"Ticket #{t[0]} created in {secs:.1f} s, in the CL001 queue with SLA, NOC group, source 'Monitoring'")
        print(f"    {DIM}{t[2]}{END}\n    {DIM}{ticket_link(t[0])}{END}")
    else:
        fail(f"no ticket after {secs:.0f} s – check `./wisesphere logs integration`")
    return t


def demo_up():
    step("Device back up: 'Lab device 01' answers again (Zabbix recovery)")
    before = device_ticket(1)
    sender(1, 0)
    if not before:
        ok("no open ticket for device 01 – nothing to solve (run `demo down` first)")
        return
    t, secs = wait(lambda: (lambda r: r if r and r[0][0] in ("5", "6") else None)(sql(f"SELECT status FROM glpi_tickets WHERE id={before[0]}")))
    ok(f"Ticket #{before[0]} solved automatically in {secs:.1f} s") if t else fail(f"ticket #{before[0]} not solved yet")


def demo_security():
    step("Security: Wazuh sees an SSH brute-force attack on cl001-dc01, then a successful login")
    stamp = int(time.time())
    base = {"schema": "ws.secevent.v1", "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "srcip": "198.51.100.77",
            "agent": {"id": f"{stamp % 1000:03d}", "name": "cl001-dc01", "ip": "10.1.1.10", "labels": {"client": "CL001", "site": "KWI"}}}
    c1, b1 = post("/webhook/wazuh-alert", {**base, "alert_id": f"{stamp}.1", "rule": {"id": "5712", "level": 10, "description": "sshd: brute force"}})
    c2, b2 = post("/webhook/wazuh-alert", {**base, "alert_id": f"{stamp}.2", "rule": {"id": "5763", "level": 12, "description": "sshd: brute force success"}})
    c3, b3 = post("/webhook/wazuh-alert", {**base, "alert_id": f"{stamp}.3", "rule": {"id": "5712", "level": 10, "description": "sshd: brute force"}})
    if b1.get("ticket_id"):
        ok(f"Level 10 -> SOC ticket #{b1['ticket_id']}  ({ticket_link(b1['ticket_id'])})")
    else:
        fail(f"level 10 -> HTTP {c1} {b1}")
    if b2.get("ticket_id"):
        ok(f"Level 12 -> SOC ticket #{b2['ticket_id']} + SMS page to the SOC on-call")
    ok(f"Same attack again -> {b3.get('action', '?')} on the existing ticket (no duplicate)")
    print(f"    {DIM}Sign in to the portal as soc.engineer, open the ticket: 'Containment (SOC approval)' blocks 198.51.100.77 on the firewall{END}")


def demo_storm():
    step("Alert storm: 15 devices at CL001 go down at the same time (e.g. a switch failure)")
    for n in range(1, 16):
        sender(n, 0)
    time.sleep(2)
    first = max_ticket()
    for n in range(1, 16):
        sender(n, 1)
    s, secs = wait(lambda: sql(f"SELECT id FROM glpi_tickets WHERE name LIKE '[STORM:CL001]%' AND status < 5 AND id > {first} ORDER BY id DESC LIMIT 1"), timeout=180)
    if s:
        ok(f"ONE storm ticket #{s[0][0]} in {secs:.1f} s instead of 15 tickets; the other alerts are follow-ups on it")
        print(f"    {DIM}{ticket_link(s[0][0])}{END}")
        m, _ = wait(lambda: [x for x in sms() if "STORM" in x.get("message", "")], timeout=60)
        ok("SMS sent to the NOC on-call: " + m[-1]["message"][:90]) if m else fail("no storm SMS")
    else:
        fail("no storm ticket")


def demo_sla():
    step("SLA watch: a P2 ticket is 10 minutes from breaching its resolve time")
    stamp = int(time.time())
    _, body = post("/webhook/zbx-event", {"event_id": str(800000 + stamp % 100000), "event_value": "1", "severity": "4",
                                         "event_name": f"Core router high CPU (demo {stamp})", "host": "cl001-kwi-lab", "client_code": "CL001"})
    tid = body.get("ticket_id")
    if not tid:
        fail(f"no ticket: {body}")
        return
    sql(f"UPDATE glpi_tickets SET time_to_resolve = DATE_ADD(NOW(), INTERVAL 10 MINUTE) WHERE id={tid}")
    post("/jobs/sla-watch/run")
    m, _ = wait(lambda: [x for x in sms() if f"#{tid}" in x.get("message", "") and "AT RISK" in x["message"]], timeout=60)
    ok(f"Ticket #{tid}: 'AT RISK' SMS sent – {m[-1]['message'][:80]}") if m else fail(f"no SLA SMS for #{tid}")


def demo_clear(quiet: bool = False):
    if not quiet:
        step("Clear: all lab devices back up, open demo tickets solved")
    for n in range(1, 16):
        sender(n, 0)
    sql("UPDATE glpi_tickets SET status=5 WHERE status<5 AND (name LIKE '[STORM:%' OR name LIKE '%(demo %' OR name LIKE '[ZBX:%Lab device %')")
    # storm grouping looks back 5 minutes: age the solved demo tickets so the next run starts clean
    sql("UPDATE glpi_tickets SET date = DATE_SUB(date, INTERVAL 1 HOUR) WHERE status>=5 AND (name LIKE '[STORM:%' OR name LIKE '[ZBX:%Lab device %')")
    if not quiet:
        ok("done")


SCENARIOS = {"down": demo_down, "up": demo_up, "security": demo_security, "storm": demo_storm, "sla": demo_sla, "clear": demo_clear}


def main(argv: list[str]) -> int:
    name = argv[1] if len(argv) > 1 else "all"
    if name in ("-h", "--help", "help"):
        print(__doc__)
        return 0
    if name == "all":
        demo_clear(quiet=True)
        demo_down(); time.sleep(3); demo_up(); demo_security(); demo_sla(); demo_storm()
        print(f"\n  When finished: ./wisesphere demo clear")
    elif name in SCENARIOS:
        SCENARIOS[name]()
    else:
        print(__doc__)
        return 2
    print(f"\n{DIM}SMS sent so far: {SMS}/messages   ·   firewall blocks: {SMS}/fw/blocks{END}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
