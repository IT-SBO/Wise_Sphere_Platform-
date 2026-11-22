#!/var/ossec/framework/python/bin/python3
"""Wazuh -> n8n integration (INT-02). Installed as /var/ossec/integrations/custom-n8n

Wazuh calls:  custom-n8n <alert_file> <api_key> <hook_url>
  api_key  = the shared webhook secret (sent as X-WS-Secret, INT-05)
  hook_url = https://<n8n>/webhook/wazuh-alert
Only alerts at or above <level> in ossec.conf reach this script (10 -> tickets, WF-02 pages at 12).
Converts the Wazuh alert to ws.secevent.v1. Exit code != 0 is logged in integrations.log.
"""
import json
import sys
import urllib.request

MAX_LOG = 2000


def to_secevent(a: dict) -> dict:
    rule = a.get("rule", {})
    agent = a.get("agent", {})
    data = a.get("data", {}) or {}
    return {
        "schema": "ws.secevent.v1",
        "alert_id": str(a.get("id", "")),
        "timestamp": a.get("timestamp", ""),
        "rule": {
            "id": str(rule.get("id", "")),
            "level": int(rule.get("level", 0)),
            "description": rule.get("description", ""),
            "groups": rule.get("groups", []),
            "mitre": rule.get("mitre", {}),
        },
        "agent": {
            "id": str(agent.get("id", "")),
            "name": agent.get("name", ""),
            "ip": agent.get("ip", ""),
            "labels": agent.get("labels", {}),   # client=CLnnn, site=XXX set per agent group (agent.conf)
        },
        "srcip": data.get("srcip", ""),
        "full_log": str(a.get("full_log", ""))[:MAX_LOG],
    }


def main(argv) -> int:
    if len(argv) < 4:
        print("usage: custom-n8n <alert_file> <api_key> <hook_url>", file=sys.stderr)
        return 2
    alert_file, secret, url = argv[1], argv[2], argv[3]
    with open(alert_file, encoding="utf-8") as f:
        alert = json.load(f)
    body = json.dumps(to_secevent(alert)).encode()
    req = urllib.request.Request(url, data=body, method="POST",
                                 headers={"Content-Type": "application/json", "X-WS-Secret": secret})
    with urllib.request.urlopen(req, timeout=30) as r:
        if r.status >= 300:
            print(f"n8n HTTP {r.status}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
