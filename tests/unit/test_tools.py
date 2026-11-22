"""Unit tests for the Python tooling (no services needed)."""
import importlib.util
import json
import sys
from pathlib import Path

import jsonschema
import yaml

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def test_tenants_follow_naming_convention():
    t = yaml.safe_load((ROOT / "config/tenants.yaml").read_text())
    sla = yaml.safe_load((ROOT / "config/sla.yaml").read_text())
    codes, subnets = set(), set()
    for tenant in t["tenants"]:
        assert tenant["code"].startswith("CL") and len(tenant["code"]) == 5 and tenant["code"][2:].isdigit()
        assert tenant["tier"] in sla["tiers"]
        assert tenant["country"] in sla["calendars"]
        assert tenant["code"] not in codes
        codes.add(tenant["code"])
        for site in tenant["sites"]:
            assert site["wg_subnet"] != "10.250.0.0/24", "reserved for the hub"
            assert site["wg_subnet"] not in subnets
            subnets.add(site["wg_subnet"])


def test_sla_tiers_cover_all_priorities():
    sla = yaml.safe_load((ROOT / "config/sla.yaml").read_text())
    for name, tier in sla["tiers"].items():
        assert set(tier["priorities"]) == {2, 3, 4, 5}, name
        for p in tier["priorities"].values():
            assert p["tto"] < p["ttr"]
    assert sla["tiers"]["gold"]["priorities"][5]["tto"] == 15  # PRD §8: P1 response 15 min


def test_wazuh_integration_produces_valid_secevent():
    mod = load("custom_wisesphere", "wazuh/custom-wisesphere.py")
    schema = json.loads((ROOT / "schemas/ws.secevent.v1.json").read_text())
    alert = {"id": "1.2", "timestamp": "2026-11-22T10:00:00Z",
             "rule": {"id": "5712", "level": 10, "description": "sshd brute force", "groups": ["sshd"], "mitre": {"id": ["T1110"]}},
             "agent": {"id": "003", "name": "dc01", "ip": "10.1.1.10", "labels": {"client": "CL001", "site": "KWI"}},
             "data": {"srcip": "203.0.113.5"}, "full_log": "x" * 5000}
    ev = mod.to_secevent(alert)
    jsonschema.validate(ev, schema)
    assert len(ev["full_log"]) == 2000


def test_brand_css_renders_tokens():
    mod = load("apply_branding", "branding/apply_branding.py")
    tokens = json.loads((ROOT / "branding/tokens.json").read_text())
    css = mod.render(tokens)
    assert tokens["colors"]["primary"] in css and "{{" not in css


def test_compose_files_pass_secrets_only_through_env():
    for p in ("hub/docker-compose.yml", "lab/docker-compose.yml"):
        text = (ROOT / p).read_text()
        for secret in ("WebhookSecret", "Glpi__Password", "Glpi__AppToken", "Sms__Key"):
            for line in text.splitlines():
                if secret + ":" in line:
                    assert "${" in line, (p, line.strip())


def test_zabbix_media_params_match_event_schema():
    sys.path.insert(0, str(ROOT / "zabbix"))
    mod = load("provision_zabbix", "zabbix/provision_zabbix.py")
    schema = json.loads((ROOT / "schemas/ws.event.v1.json").read_text())
    sent = {k for k, _ in mod.PARAMS} - {"url", "secret", "HTTPProxy"}
    assert sent <= set(schema["properties"]), sent - set(schema["properties"])
