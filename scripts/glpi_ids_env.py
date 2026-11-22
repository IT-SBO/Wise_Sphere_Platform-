#!/usr/bin/env python3
"""Write the GLPI ids produced by glpi/provision_glpi.py (--out file) into an env file as WS_GLPI_ID_* lines.

  glpi_ids_env.py lab/glpi-ids.json lab/.env      # replaces existing WS_GLPI_ID_* lines
The compose files pass them to the integration service as WS__Integration__Ids__*.
"""
import json
import sys
from pathlib import Path

ids = json.loads(Path(sys.argv[1]).read_text())
env = Path(sys.argv[2])
values = {
    "WS_GLPI_ID_REQTYPE_MONITORING": ids["requesttypes"]["monitoring"],
    "WS_GLPI_ID_REQTYPE_SECURITY": ids["requesttypes"]["security"],
    "WS_GLPI_ID_GROUP_NOC": ids["groups"]["noc"],
    "WS_GLPI_ID_GROUP_SOC": ids["groups"]["soc"],
    "WS_GLPI_ID_GROUP_SDM": ids["groups"]["sdm"],
    "WS_GLPI_ID_SOLUTIONTYPE": ids["solutiontype"],
}
lines = [l for l in env.read_text().splitlines() if not l.startswith("WS_GLPI_ID_")]
env.write_text("\n".join(lines + [f"{k}={v}" for k, v in values.items()]) + "\n")
print(f"wrote {len(values)} GLPI ids to {env}")
