#!/usr/bin/env python3
"""Add or update monitored client devices in Zabbix (SOP-01 step "baseline monitoring").

One device:
  add_hosts.py --url ... --password ... --client CL001 --site KWI --role switch --name core-sw01 --ip 192.168.10.2
Many devices from a CSV (columns: client,site,role,name,ip[,glpi_asset][,snmp_community]):
  add_hosts.py --url ... --password ... --csv devices.csv

Each device gets: the "WS Role <role>" template (config/templates.yaml), host group CLnnn/SITE/Network|Servers,
tags client/site/role (+ glpi_asset when known, ITAM-05), and is monitored by the site proxy CLnnn-SITE-PRX01.
SNMP devices use SNMPv2c with the community in macro {$SNMP_COMMUNITY} (set per client from the vault);
SNMPv3 can be configured on the host afterwards (preferred, R-12).
"""
from __future__ import annotations

import argparse
import csv
import os
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from provision_zabbix import Zbx  # noqa: E402

SERVER_ROLES = ("server-linux", "server-windows")


def add_host(z: Zbx, roles: dict, d: dict) -> str:
    role = roles.get(d["role"])
    if role is None:
        raise SystemExit(f"unknown role {d['role']!r}; roles: {', '.join(roles)}")
    client, site = d["client"].upper(), d["site"].upper()
    kind = "Servers" if d["role"] in SERVER_ROLES else "Network"
    group = z.call("hostgroup.get", {"filter": {"name": [f"{client}/{site}/{kind}"]}, "output": ["groupid"]})
    if not group:
        raise SystemExit(f"host group {client}/{site}/{kind} missing - add the site to config/tenants.yaml and run provision_zabbix.py")
    tmpl = z.call("template.get", {"filter": {"host": [f"WS Role {role['name']}"]}, "output": ["templateid"]})
    if not tmpl:
        raise SystemExit("role templates missing - run provision_zabbix.py first")
    proxy = z.call("proxy.get", {"filter": {"name": [f"{client}-{site}-PRX01"]}, "output": ["proxyid"]})

    technical = f"{client.lower()}-{site.lower()}-{d['name'].lower()}"
    tags = [{"tag": "client", "value": client}, {"tag": "site", "value": site}, {"tag": "role", "value": d["role"]}]
    if d.get("glpi_asset"):
        tags.append({"tag": "glpi_asset", "value": d["glpi_asset"]})
    body = {"host": technical, "name": f"{client} {site} {d['name']}", "groups": [{"groupid": group[0]["groupid"]}],
            "templates": [{"templateid": tmpl[0]["templateid"]}], "tags": tags,
            "monitored_by": 1 if proxy else 0}
    if proxy:
        body["proxyid"] = proxy[0]["proxyid"]
    if d.get("snmp_community"):
        body["macros"] = [{"macro": "{$SNMP_COMMUNITY}", "value": d["snmp_community"], "type": 1}]

    iface = {"snmp": {"type": 2, "port": "161", "details": {"version": 2, "bulk": 1, "community": "{$SNMP_COMMUNITY}"}},
             "agent": {"type": 1, "port": "10050"}, "none": {"type": 1, "port": "10050"}}[role["interface"]]
    interfaces = [{"main": 1, "useip": 1, "ip": d["ip"], "dns": "", **iface}]

    found = z.call("host.get", {"filter": {"host": [technical]}, "output": ["hostid"]})
    if found:
        z.call("host.update", {"hostid": found[0]["hostid"], **body})
        return found[0]["hostid"]
    return z.call("host.create", {**body, "interfaces": interfaces})["hostids"][0]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", required=True)
    ap.add_argument("--user", default=os.environ.get("ZABBIX_ADMIN_USER", "Admin"))
    ap.add_argument("--password", default=os.environ.get("ZABBIX_ADMIN_PASSWORD"))
    ap.add_argument("--templates", default=str(ROOT / "config/templates.yaml"))
    ap.add_argument("--csv")
    for f in ("client", "site", "role", "name", "ip", "glpi_asset", "snmp_community"):
        ap.add_argument(f"--{f.replace('_', '-')}")
    a = ap.parse_args()
    roles = yaml.safe_load(Path(a.templates).read_text())["roles"]
    z = Zbx(a.url, a.user, a.password)
    rows = list(csv.DictReader(open(a.csv, newline=""))) if a.csv else [
        {k: getattr(a, k) for k in ("client", "site", "role", "name", "ip", "glpi_asset", "snmp_community")}]
    for d in rows:
        if not all(d.get(k) for k in ("client", "site", "role", "name", "ip")):
            raise SystemExit(f"client, site, role, name and ip are required: {d}")
        print(f"{d['client']}/{d['site']} {d['name']} ({d['role']}) -> hostid {add_host(z, roles, d)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
