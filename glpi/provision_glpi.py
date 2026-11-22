#!/usr/bin/env python3
"""Provision GLPI 11 for WiseSphere through the REST API (idempotent).

Creates or updates, from config/tenants.yaml and config/sla.yaml:
  * the root entity name and one sub-entity per client (name "CLnnn - Name", tag CLnnn)  ITSM-01, §4.7
  * native satisfaction survey per client entity                                       ITSM-05
  * request sources "Monitoring" and "Security"                                        ITSM-03
  * groups WS-NOC, WS-SOC, WS-SDM, WS-Integration                                      §4.3
  * solution type "Auto-recovered (monitoring)"
  * business-hours calendars per country and one SLM per tier+country with TTO/TTR
    SLAs per priority, plus ticket rules that attach them per client entity          ITSM-02
  * the integration service account and its API client (app token, IP range)                   INT-05

Usage (run on the hub host, or let scripts/bootstrap_lab.sh run it):
  provision_glpi.py --url http://localhost:8090/apirest.php --admin-user glpi --admin-password ... \
      --svc-password ... --app-token ... --api-cidr 172.30.0.0/24 --out lab/glpi-ids.json

Secrets come from arguments / environment only and are never written to the output file.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import os
import sys
from pathlib import Path

import requests
import yaml

ROOT = Path(__file__).resolve().parent.parent
GLPI_PRIORITY_NAME = {5: "P1", 4: "P2", 3: "P3", 2: "P4"}
SLA_TTR, SLA_TTO = 0, 1
RULE_ON_ADD = 1


class Glpi:
    def __init__(self, url: str, user: str, password: str, app_token: str | None = None):
        self.url = url.rstrip("/")
        self.s = requests.Session()
        self.s.headers["Content-Type"] = "application/json"
        if app_token:
            self.s.headers["App-Token"] = app_token
        r = self.s.get(f"{self.url}/initSession", auth=(user, password), timeout=30)
        if r.status_code != 200:
            sys.exit(f"GLPI login failed: HTTP {r.status_code} {r.text[:300]}")
        self.s.headers["Session-Token"] = r.json()["session_token"]
        self.call("POST", "/changeActiveEntities", {"entities_id": 0, "is_recursive": True})

    def call(self, method: str, path: str, body=None, params=None, ok_empty=True):
        r = self.s.request(method, f"{self.url}{path}", json=body, params=params, timeout=30)
        if r.status_code == 400 and ok_empty and "ERROR_RANGE_EXCEED_TOTAL" in r.text:
            return []
        if r.status_code >= 300 and r.status_code != 206:
            raise RuntimeError(f"{method} {path} -> HTTP {r.status_code}: {r.text[:400]}")
        return r.json() if r.text else None

    def find(self, itemtype: str, **exact):
        params = {f"searchText[{k}]": f"^{v}$" for k, v in exact.items()}
        params["range"] = "0-50"
        rows = self.call("GET", f"/{itemtype}", params=params) or []
        # searchText is a LIKE with anchors; re-check exact equality
        return [r for r in rows if all(str(r.get(k)) == str(v) for k, v in exact.items())]

    def ensure(self, itemtype: str, match: dict, values: dict | None = None) -> int:
        values = {**match, **(values or {})}
        found = self.find(itemtype, **match)
        if found:
            item_id = int(found[0]["id"])
            diff = {k: v for k, v in values.items() if str(found[0].get(k)) != str(v)}
            if diff:
                self.call("PUT", f"/{itemtype}/{item_id}", {"input": diff})
            return item_id
        res = self.call("POST", f"/{itemtype}", {"input": values})
        return int(res["id"])

    def close(self):
        try:
            self.call("GET", "/killSession")
        except Exception:
            pass


def hm(t: str) -> str:
    return t if t.count(":") == 2 else f"{t}:00"


def provision(g: Glpi, tenants: dict, sla: dict, args) -> dict:
    ids: dict = {"entities": {}, "slm": {}, "sla": {}, "calendars": {}}

    # Root entity name (§4.7 "Sub-entity under WiseSphere")
    try:
        g.call("PUT", "/Entity/0", {"input": {"name": tenants.get("root_entity", "WiseSphere")}})
    except RuntimeError as e:
        print(f"warn: could not rename root entity: {e}", file=sys.stderr)

    ids["requesttypes"] = {
        "monitoring": g.ensure("RequestType", {"name": "Monitoring"}, {"is_active": 1, "is_ticketheader": 1, "comment": "Zabbix via WiseSphere integration (INT-01)"}),
        "security": g.ensure("RequestType", {"name": "Security"}, {"is_active": 1, "is_ticketheader": 1, "comment": "Wazuh via WiseSphere integration (INT-02)"}),
    }
    ids["groups"] = {
        key: g.ensure("Group", {"name": name}, {"entities_id": 0, "is_recursive": 1, "is_assign": 1, "is_requester": 1, "is_notify": 1})
        for key, name in {"noc": "WS-NOC", "soc": "WS-SOC", "sdm": "WS-SDM", "integration": "WS-Integration"}.items()
    }
    ids["solutiontype"] = g.ensure("SolutionType", {"name": "Auto-recovered (monitoring)"}, {"entities_id": 0, "is_recursive": 1})

    # Business-hours calendars (ITSM-02)
    for country, cal in sla["calendars"].items():
        cal_id = g.ensure("Calendar", {"name": cal["name"]}, {"entities_id": 0, "is_recursive": 1})
        existing = g.call("GET", f"/Calendar/{cal_id}/CalendarSegment", params={"range": "0-50"}) or []
        have = {(int(s["day"]), s["begin"], s["end"]) for s in existing}
        for day in cal["days"]:
            seg = (day, hm(cal["start"]), hm(cal["end"]))
            if seg not in have:
                g.call("POST", "/CalendarSegment", {"input": {"calendars_id": cal_id, "day": day, "begin": seg[1], "end": seg[2],
                                                               "entities_id": 0, "is_recursive": 1}})
        ids["calendars"][country] = cal_id

    # One SLM per tier+country used by at least one tenant; SLAs per priority
    used = {(t["tier"], t["country"]) for t in tenants["tenants"]}
    for tier, country in sorted(used):
        spec = sla["tiers"][tier]
        cal_id = ids["calendars"][country]
        slm_name = f"{spec['label']} ({country})"
        slm_id = g.ensure("SLM", {"name": slm_name}, {"entities_id": 0, "is_recursive": 1, "calendars_id": 0})
        ids["slm"][f"{tier}/{country}"] = slm_id
        for prio, p in spec["priorities"].items():
            for kind, key in ((SLA_TTO, "tto"), (SLA_TTR, "ttr")):
                name = f"{tier.capitalize()} {country} {GLPI_PRIORITY_NAME[int(prio)]} {key.upper()}"
                sla_id = g.ensure("SLA", {"name": name}, {
                    "slms_id": slm_id, "type": kind, "number_time": int(p[key]), "definition_time": "minute",
                    "entities_id": 0, "is_recursive": 1, "use_ticket_calendar": 0,
                    "calendars_id": cal_id if p["business_hours"] else 0, "end_of_working_day": 0,
                })
                ids["sla"][f"{tier}/{country}/{prio}/{key}"] = sla_id

    # Client entities + survey + SLA rules
    for t in tenants["tenants"]:
        name = f"{t['code']} - {t['name']}"
        found = g.find("Entity", tag=t["code"])
        values = {"name": name, "entities_id": 0, "tag": t["code"],
                  "inquest_config": 1, "inquest_rate": 100, "inquest_delay": 0,   # native survey on closure, every ticket
                  "calendars_id": ids["calendars"][t["country"]]}
        if found:
            eid = int(found[0]["id"])
            diff = {k: v for k, v in values.items() if str(found[0].get(k)) != str(v)}
            if diff:
                g.call("PUT", f"/Entity/{eid}", {"input": diff})
        else:
            eid = int(g.call("POST", "/Entity", {"input": values})["id"])
        ids["entities"][t["code"]] = eid

        for prio in sla["tiers"][t["tier"]]["priorities"]:
            rule_name = f"WS SLA {t['code']} {GLPI_PRIORITY_NAME[int(prio)]}"
            ensure_sla_rule(g, rule_name, eid, int(prio),
                            ids["sla"][f"{t['tier']}/{t['country']}/{prio}/tto"],
                            ids["sla"][f"{t['tier']}/{t['country']}/{prio}/ttr"])
        # P1 rule also covers GLPI priority 6 (major)
        ensure_sla_rule(g, f"WS SLA {t['code']} P1 major", eid, 6,
                        ids["sla"][f"{t['tier']}/{t['country']}/5/tto"], ids["sla"][f"{t['tier']}/{t['country']}/5/ttr"])

    ids["service_user"] = ensure_service_user(g, args) if args.svc_password else None
    ids["api_client"] = ensure_api_client(g, args) if args.app_token else None
    return ids


def ensure_sla_rule(g: Glpi, name: str, entity_id: int, priority: int, tto_id: int, ttr_id: int):
    found = g.find("RuleTicket", name=name)
    if found:
        return int(found[0]["id"])
    rid = int(g.call("POST", "/RuleTicket", {"input": {
        "name": name, "sub_type": "RuleTicket", "match": "AND", "is_active": 1, "condition": RULE_ON_ADD,
        "entities_id": 0, "is_recursive": 1, "description": "Generated by glpi/provision_glpi.py - attach tier SLA"}})["id"])
    for crit, pattern in (("entities_id", entity_id), ("priority", priority)):
        g.call("POST", "/RuleCriteria", {"input": {"rules_id": rid, "criteria": crit, "condition": 0, "pattern": str(pattern)}})
    for field, value in (("slas_id_tto", tto_id), ("slas_id_ttr", ttr_id)):
        g.call("POST", "/RuleAction", {"input": {"rules_id": rid, "action_type": "assign", "field": field, "value": str(value)}})
    return rid


def ensure_service_user(g: Glpi, args) -> dict:
    users = g.find("User", name=args.svc_user)
    values = {"name": args.svc_user, "realname": "WiseSphere integration service", "is_active": 1,
              "password": args.svc_password, "password2": args.svc_password}
    if users:
        uid = int(users[0]["id"])
        upd = {"is_active": 1}
        if args.rotate_svc_password:  # GLPI refuses re-setting a recently used password
            upd.update(password=args.svc_password, password2=args.svc_password)
        g.call("PUT", f"/User/{uid}", {"input": upd})
    else:
        uid = int(g.call("POST", "/User", {"input": values})["id"])
    profiles = g.find("Profile", name=args.svc_profile)
    if not profiles:
        raise RuntimeError(f"profile {args.svc_profile!r} not found")
    pid = int(profiles[0]["id"])
    links = g.call("GET", f"/User/{uid}/Profile_User", params={"range": "0-50"}) or []
    if not any(int(l["profiles_id"]) == pid and int(l["entities_id"]) == 0 for l in links):
        g.call("POST", "/Profile_User", {"input": {"users_id": uid, "profiles_id": pid, "entities_id": 0, "is_recursive": 1, "is_dynamic": 0}})
    g.call("PUT", f"/User/{uid}", {"input": {"profiles_id": pid, "entities_id": 0}})
    return {"id": uid, "name": args.svc_user, "profile": args.svc_profile}


def ensure_api_client(g: Glpi, args) -> dict:
    net = ipaddress.ip_network(args.api_cidr, strict=False)
    values = {"name": "wisesphere-integration", "is_active": 1, "ipv4_range_start": str(net.network_address),
              "ipv4_range_end": str(net.broadcast_address), "app_token": args.app_token, "dolog_method": 0,
              "entities_id": 0, "is_recursive": 1, "comment": "WiseSphere integration service (INT-05). Rotate via provision_glpi.py."}
    found = g.find("APIClient", name="wisesphere-integration")
    if found:
        cid = int(found[0]["id"])
        g.call("PUT", f"/APIClient/{cid}", {"input": values})
    else:
        cid = int(g.call("POST", "/APIClient", {"input": values})["id"])
    return {"id": cid, "cidr": str(net)}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", required=True, help="GLPI legacy API base, e.g. http://localhost:8090/apirest.php")
    ap.add_argument("--admin-user", default=os.environ.get("GLPI_ADMIN_USER", "glpi"))
    ap.add_argument("--admin-password", default=os.environ.get("GLPI_ADMIN_PASSWORD"))
    ap.add_argument("--admin-app-token", default=os.environ.get("GLPI_ADMIN_APP_TOKEN"), help="if the bootstrap API client needs one")
    ap.add_argument("--tenants", default=str(ROOT / "config/tenants.yaml"))
    ap.add_argument("--sla", default=str(ROOT / "config/sla.yaml"))
    ap.add_argument("--svc-user", default="svc-integration")
    ap.add_argument("--svc-password", default=os.environ.get("WS_GLPI_SVC_PASSWORD"))
    ap.add_argument("--rotate-svc-password", action="store_true", help="also reset the password of an existing service account")
    ap.add_argument("--svc-profile", default=os.environ.get("WS_GLPI_SVC_PROFILE", "Technician"),
                    help="GLPI profile for the integration service account (least privilege, INT-05)")
    ap.add_argument("--app-token", default=os.environ.get("WS_GLPI_APP_TOKEN"), help="app token for the integration service API client")
    ap.add_argument("--api-cidr", default=os.environ.get("WS_GLPI_API_CIDR", "127.0.0.1/32"), help="source range allowed for the integration service API client")
    ap.add_argument("--out", help="write resulting ids (no secrets) to this JSON file")
    args = ap.parse_args()
    if not args.admin_password:
        ap.error("--admin-password or GLPI_ADMIN_PASSWORD is required")

    tenants = yaml.safe_load(Path(args.tenants).read_text())
    sla = yaml.safe_load(Path(args.sla).read_text())
    g = Glpi(args.url, args.admin_user, args.admin_password, args.admin_app_token)
    try:
        ids = provision(g, tenants, sla, args)
    finally:
        g.close()
    text = json.dumps(ids, indent=2)
    if args.out:
        Path(args.out).write_text(text + "\n")
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
