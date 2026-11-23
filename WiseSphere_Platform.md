# WiseSphere Platform – Project Reference

One page with everything needed to install, run, test and continue the WiseSphere platform.
Keep it next to the code: `WiseSphere_Platform.md` in the repository root.

| Item | Value |
|---|---|
| Project | WiseSphere – Managed Services (MSP / MSSP) Platform |
| Repository | https://github.com/IT-SBO/Wise_Sphere_Platform- (branch **`main`**) |
| Local folder (Windows) | `D:\mnt\WiseSphere Platform` |
| Same folder in Ubuntu (WSL) | `/mnt/d/mnt/WiseSphere Platform` |
| Requirements | WS-PRD-001 v2.0 (`docs/WiseSphere_PRD_and_Implementation_Plan.docx`) |
| Development document | `docs/WiseSphere_Development_Document.docx` |
| Windows guide (detail) | `docs/RUN_ON_WINDOWS.md` |

---

## 1. What the platform is

| Part | Technology | Role |
|---|---|---|
| Integration service | C# / .NET 10 (`src/WiseSphere.Integration`) | Zabbix and Wazuh alerts → GLPI tickets, SLA SMS, CSAT review, approved firewall blocks |
| Portal | Angular 22 + ASP.NET Core (`web/portal`, `src/WiseSphere.Portal`) | Dashboard, tickets, monitoring, monthly report, English / Arabic |
| GLPI 11 | Product | Tickets, assets, SLAs, one entity per client |
| Zabbix 8.0 LTS | Product | Monitoring, proxies at client sites, platform self-monitoring |
| Wazuh 4.14 | Product | Security events (Phase 4) |
| Grafana 13 | Product | NOC wall |

Every client has one code **CLnnn** (e.g. `CL001`) defined in `config/tenants.yaml`.

---

## 2. One-time installation on Windows (everything large on D:)

Run in **PowerShell as Administrator** unless it says Ubuntu.

> Only the WSL engine itself must live on C: (about 2 GB). Keep at least 3 GB free on C:.

### 2.1 Folders and temporary files on D:
```powershell
mkdir D:\WSL, D:\Docker, D:\Temp -Force
```
```powershell
$env:TEMP="D:\Temp"; $env:TMP="D:\Temp"
```

### 2.2 WSL engine
```powershell
wsl --update --web-download
```
Restart the PC if asked.

### 2.3 Ubuntu installed on D:
```powershell
wsl --install --web-download -d Ubuntu --location D:\WSL\Ubuntu
```
Create the Linux user and password when asked (used for `sudo`). Then `exit`.

### 2.4 Docker Desktop installed on D:
```powershell
cd D:\Temp
```
```powershell
curl.exe -L -o "Docker Desktop Installer.exe" "https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe"
```
```powershell
Start-Process -Wait ".\Docker Desktop Installer.exe" -ArgumentList 'install','--accept-license','--installation-dir=D:\Docker\DockerDesktop','--wsl-default-data-root=D:\Docker\wsl'
```
Restart the PC. Start **Docker Desktop**, wait for **Engine running**, then in **Settings**:
- **VM resources** → Disk image location = `D:\Docker\wsl`; memory **8 GB** or more
- **WSL integration** → switch on **Ubuntu** → Apply

### 2.5 Tools inside Ubuntu
Open Ubuntu (type `wsl` in PowerShell):
```bash
sudo apt update && sudo apt install -y git make python3-pip python3-venv curl
```
```bash
docker version
```
Both **Client** and **Server** must be shown.

### 2.6 Get the code (once)
```bash
cd "/mnt/d/mnt/WiseSphere Platform"
```
```bash
git clone -b main https://github.com/IT-SBO/Wise_Sphere_Platform-.git .
```
```bash
python3 -m venv .venv && source .venv/bin/activate && pip install -r requirements.txt
```

---

## 3. Every time you work on it

1. Start **Docker Desktop** and wait for **Engine running**.
2. In PowerShell type `wsl`, then:
```bash
cd "/mnt/d/mnt/WiseSphere Platform"
```
```bash
git pull origin main
```
```bash
source .venv/bin/activate
```
```bash
make lab-up
```
```bash
make lab-bootstrap
```
```bash
make lab-test
```
Expected: `make lab-bootstrap` ends with `== lab ready`, and `make lab-test` ends with **29/29 checks passed**.

### Stop (data kept)
```bash
make lab-down
```

### Start again from zero (deletes all lab data)
```bash
make lab-reset
```

---

## 4. Addresses and logins (lab)

| What | Address | Login |
|---|---|---|
| **WiseSphere portal** | http://localhost:8089 | `noc.operator` (NOC), `soc.engineer` (SOC), `client.cl001` (client) |
| GLPI | http://localhost:8090 | `glpi` / `glpi` |
| Zabbix | http://localhost:8081 | `Admin` / `zabbix` |
| Integration service | http://localhost:8088/readyz | – |
| SMS + firewall mock | http://localhost:8099/messages · http://localhost:8099/fw/blocks | – |

Portal password (generated per machine, same for the three users):
```bash
grep LAB_USERS_PASSWORD lab/.env
```

---

## 5. Useful commands

| Purpose | Command |
|---|---|
| All containers running? | `docker ps --format '{{.Names}}  {{.Status}}'` |
| Integration service log | `docker logs ws-integration --tail 50` |
| Portal log | `docker logs ws-portal --tail 50` |
| GLPI log | `docker logs ws-glpi --tail 50` |
| PoC test (device down → ticket → solved, ~6 min) | `make lab-poc-test` |
| Load test (500 alerts) | `make lab-load-test` |
| Instant test alert | `docker exec ws-zabbix-server zabbix_sender -z 127.0.0.1 -s cl001-kwi-lab -k ws.test.1 -o 1` (recovery: `-o 0`) |
| Unit tests | `make unit` |
| Apply brand colours / logo | edit `branding/tokens.json`, put logos in `branding/assets/`, run `make brand` |

---

## 6. Problems already met and their fixes

| Symptom | Cause | Fix |
|---|---|---|
| `cd "/mnt/d/..."` → *Cannot find path 'C:\mnt\...'* | Typed in **PowerShell** | Type `wsl` first; Linux paths work only in Ubuntu. In PowerShell the folder is `D:\mnt\WiseSphere Platform` |
| `Invalid distribution name: 'Ubuntu-24.04'` | Old WSL | `wsl --update`, then `wsl --list --online` and use the shown name (`Ubuntu`) |
| `Error: 0x8024001e` / `0x80246008` | Windows Store / Update download blocked | Use `--web-download`; start services `wuauserv` and `BITS` |
| `Out of disk space -- Volume: 'C:'` / *WSL installation appears to be corrupted* | C: almost full | Free at least 3 GB on C: (Disk Cleanup), install Ubuntu and Docker on D: (section 2) |
| `Start-Process … cannot find the file` | Installer not downloaded to `D:\Temp` | Download with `curl.exe` (section 2.4) |
| `make lab-test` → `IndexError` on `glpi_entities … CL001` | Setup (bootstrap) did not finish | Run `make lab-bootstrap` until it prints `== lab ready` |
| Portal: *Something went wrong* / *GLPI is not answering* | GLPI slow on first API call | Open http://localhost:8090 once (glpi/glpi), then `make lab-bootstrap` again |
| `Read timed out … port=8090 (read timeout=30)` | Old script version | `git pull origin main` – new version waits up to 180 s and retries. Check: `grep -c "repairing incomplete rule" glpi/provision_glpi.py` prints `1` |
| Everything very slow | Project on `/mnt/d` (Windows drive shared into Linux) or Docker memory too low | Give Docker 8 GB; optionally keep the project inside Ubuntu: `cd ~ && git clone -b main https://github.com/IT-SBO/Wise_Sphere_Platform-.git wisesphere` (Ubuntu itself is on D:). Run `make lab-down` in the old folder first |
| `port is already allocated` | Port used by another program | Change the port in `lab/.env` and `make lab-up` |
| `/bin/bash^M: bad interpreter` | Windows line endings | `git config core.autocrlf false`, then `git rm --cached -r . -q && git reset --hard` |

---

## 7. Status

**Done and tested in the lab (29/29 end-to-end, 50/50 unit tests):**
Zabbix → GLPI tickets (dedupe, storm grouping, auto-solve), Wazuh → SOC tickets, SLA SMS, CSAT review,
portal (dashboard, tickets, monitoring, requests, monthly report, EN/AR), approved firewall block with expiry,
device-role templates, platform self-monitoring, hub/spoke stacks, backups, development document.

**Open – decisions or real infrastructure needed:**
| Item | Owner |
|---|---|
| Management sign-off on the portal (change to PRD R-06) | Executive Management |
| Final design and logo → `branding/` | Design |
| SMS provider (D-04), credential vault (D-06), hosting (D-02) | Solutions Architect / Management |
| Wazuh server deployment (Phase 4) | SOC Engineer |
| Tunnel-cut test TC-05 and restore drill TC-11 on the real hub | NOC / DevOps |
| Containment per client: written opt-in, firewall type, allow-list | SOC + client |

**Next step on this PC:** finish section 3 until `make lab-test` shows 29/29, then sign in to the portal.

---

## 8. Where things are in the repository

| Path | Contents |
|---|---|
| `config/` | Clients (`tenants.yaml`), SLA matrix (`sla.yaml`), device roles (`templates.yaml`) |
| `src/` | C# code: Core rules, GLPI client, integration service, portal back end |
| `web/portal/` | Angular front end |
| `lab/` | Local lab (Docker Compose) |
| `hub/`, `spoke/` | Production hub and client-site stacks |
| `glpi/`, `zabbix/`, `wazuh/`, `grafana/` | Provisioning and product configuration |
| `ops/` | Backups, WireGuard, restore drill |
| `tests/` | Unit, end-to-end, load and PoC tests |
| `docs/` | PRD, development document, Windows guide, screenshots |
| `poc/` | The original proof of concept |
