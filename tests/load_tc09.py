#!/usr/bin/env python3
"""TC-09 load test (PRD §6.5): 500 alerts in 10 minutes without loss, latency p95 <= 60 s.

  python3 tests/load_tc09.py [--events 500] [--concurrency 10]

Posts synthetic Zabbix problem events for two clients and three sites straight to the webhook
(what the Zabbix alerter does with maxsessions=10), then checks every event is in GLPI as a ticket
or as a follow-up of the client's STORM ticket. Lab only.
"""
import argparse
import concurrent.futures as cf
import json
import statistics
import subprocess
import time
import urllib.request
from pathlib import Path

LAB = Path(__file__).resolve().parent.parent / "lab"
ENV = dict(l.split("=", 1) for l in (LAB / ".env").read_text().splitlines() if "=" in l and not l.startswith("#"))
URL = f"http://localhost:{ENV['INTEGRATION_PORT']}/webhook/zbx-event"


def post(i: int, base: int):
    client = ["CL000", "CL001"][i % 2]
    body = {"event_id": str(base + i), "event_value": "1", "severity": str(2 + i % 4), "event_name": f"Load test {base} problem {i}",
            "host": f"{client.lower()}-load-{i % 30:02d}", "client_code": client, "site_code": ["HQ", "KWI", "KWI"][i % 3]}
    req = urllib.request.Request(URL, data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", "X-WS-Secret": ENV["WS_WEBHOOK_SECRET"]})
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=120) as r:
        res = json.loads(r.read())
    return time.perf_counter() - t0, res["action"]


def sql(q):
    return subprocess.run(["docker", "exec", "ws-glpi-db", "mariadb", "-uroot", f"-p{ENV['GLPI_DB_ROOT_PASSWORD']}", "glpi", "-N", "-B", "-e", q],
                          check=True, capture_output=True, text=True).stdout.strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--events", type=int, default=500)
    ap.add_argument("--concurrency", type=int, default=10)
    a = ap.parse_args()
    base = 5_000_000 + int(time.time()) % 1_000_000 * 10
    t0 = time.perf_counter()
    with cf.ThreadPoolExecutor(a.concurrency) as ex:
        results = list(ex.map(lambda i: post(i, base), range(a.events)))
    total = time.perf_counter() - t0
    lat = sorted(r[0] for r in results)
    actions = {k: sum(1 for r in results if r[1] == k) for k in {r[1] for r in results}}
    tickets = int(sql(f"SELECT COUNT(*) FROM glpi_tickets WHERE name LIKE '[ZBX:%' AND CAST(SUBSTRING_INDEX(SUBSTRING_INDEX(name,']',1),':',-1) AS UNSIGNED) >= {base}"))
    followups = int(sql(f"SELECT COUNT(*) FROM glpi_itilfollowups f JOIN glpi_tickets t ON t.id=f.items_id AND f.itemtype='Ticket' "
                        f"WHERE t.name LIKE '[STORM:%' AND f.content LIKE '%Load test {base} %'"))
    storms = int(sql(f"SELECT COUNT(*) FROM glpi_tickets WHERE name LIKE '[STORM:%' AND content LIKE '%Load test {base} %'"))
    p95 = lat[int(len(lat) * 0.95) - 1]
    print(f"{a.events} events, concurrency {a.concurrency}: {total:.1f} s total ({a.events / total:.1f} events/s)")
    print(f"latency median {statistics.median(lat) * 1000:.0f} ms, p95 {p95 * 1000:.0f} ms, max {lat[-1] * 1000:.0f} ms")
    found = tickets + storms + followups
    print(f"actions {actions}; in GLPI: {tickets} tickets + {storms} storm tickets + {followups} storm follow-ups = {found}")
    ok = found >= a.events and p95 <= 60 and total <= 600
    print("TC-09", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
