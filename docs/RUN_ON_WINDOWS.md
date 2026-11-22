# Running WiseSphere on Windows

The platform runs in Linux containers. On Windows you use **Docker Desktop with WSL 2** and run the
commands in an **Ubuntu (WSL) terminal**. Everything is then opened in your normal Windows browser.

Time: about 20 minutes the first time (downloads ~4 GB), then 2 minutes to start.

## 1. One-time set-up

1. Install **Docker Desktop** (https://www.docker.com/products/docker-desktop) and keep the default **WSL 2 based engine**.
2. Open **PowerShell as Administrator** and install Ubuntu:
   ```powershell
   wsl --install -d Ubuntu-24.04
   ```
   Restart if asked, open "Ubuntu" from the Start menu and create a Linux user.
3. In Docker Desktop: **Settings → Resources → WSL integration** → enable **Ubuntu-24.04**.
   **Settings → Resources → Advanced**: give Docker at least **8 GB memory**.
4. In the Ubuntu terminal install the tools:
   ```bash
   sudo apt update && sudo apt install -y git make python3-pip python3-venv curl
   ```

## 2. Get the code

Your folder `D:\WiseSphere Platform` is `/mnt/d/WiseSphere Platform` inside Ubuntu:

```bash
cd "/mnt/d/WiseSphere Platform"
git clone https://github.com/IT-SBO/Wise_Sphere_Platform-.git .
git checkout claude/trusting-knuth-hpcbuz     # or main, once the work is merged
```

> Faster alternative: clone into the Linux home (`cd ~ && git clone …`). Builds on `/mnt/d` work but are
> slower because Windows drives are shared into Linux. The code is still visible in Windows Explorer at
> `\\wsl$\Ubuntu-24.04\home\<user>\`.

Python tools in a virtual environment (once):

```bash
python3 -m venv .venv && source .venv/bin/activate && pip install -r requirements.txt
```

## 3. Start the lab

```bash
source .venv/bin/activate
make lab-up          # builds the C# service and the portal, starts all containers (first time: ~10 min)
make lab-bootstrap   # configures GLPI and Zabbix, creates lab users (about 1 minute)
make lab-test        # 29 automatic checks - all should say PASS
```

Open in your Windows browser:

| What | Address | Login |
|---|---|---|
| **WiseSphere portal** | http://localhost:8089 | `noc.operator`, `soc.engineer` or `client.cl001`; password = `LAB_USERS_PASSWORD` in `lab/.env` |
| GLPI | http://localhost:8090 | glpi / glpi |
| Zabbix | http://localhost:8081 | Admin / zabbix |
| Integration service | http://localhost:8088/readyz | – |
| SMS + firewall mock | http://localhost:8099/messages, http://localhost:8099/fw/blocks | – |

Show the passwords: `grep -E 'LAB_USERS_PASSWORD' lab/.env`

## 4. Try it

- **The PoC test:** `make lab-poc-test` stops a monitored device; after ~5 minutes a ticket appears in the portal,
  and it is solved automatically when the device comes back.
- **An instant alert:**
  ```bash
  docker exec ws-zabbix-server zabbix_sender -z 127.0.0.1 -s cl001-kwi-lab -k ws.test.1 -o 1   # problem
  docker exec ws-zabbix-server zabbix_sender -z 127.0.0.1 -s cl001-kwi-lab -k ws.test.1 -o 0   # recovery
  ```
- **Load test:** `make lab-load-test` (500 alerts).
- **Monthly report:** portal → Reports → choose client and month → *Print / Save as PDF*.
- **Containment:** sign in as `soc.engineer`, open a `[WZH:…]` security ticket, approve a block, then check http://localhost:8099/fw/blocks.

## 5. Stop, start, reset

```bash
make lab-down        # stop (data kept)
make lab-up          # start again
make lab-reset       # delete everything and start from zero next time
```

## 6. Development on Windows

- **C#:** open `WiseSphere.slnx` in Visual Studio 2022 (17.13 or later) or Rider; install the .NET 10 SDK.
  Tests: Test Explorer, or `dotnet test` in the repository folder.
- **Angular:** install Node.js 22, then in `web/portal`: `npm ci` and `npx ng serve` → http://localhost:4200
  (API calls go to the lab portal on port 8089).
- After changing C# or Angular code, rebuild the containers: `make lab-up` (it builds the images).

## Troubleshooting

| Problem | Fix |
|---|---|
| `Cannot connect to the Docker daemon` | Start Docker Desktop and wait for "Engine running"; check WSL integration for Ubuntu is on |
| `port is already allocated` | Another program uses the port (e.g. 8080 was busy in the PoC). Change the port in `lab/.env` (`GLPI_PORT`, `PORTAL_PORT`, …) and run `make lab-up` again |
| `/bin/bash^M: bad interpreter` | Files were checked out with Windows line endings. Run `git config core.autocrlf false`, then `git rm --cached -r . -q && git reset --hard` |
| Containers stop or are very slow | Give Docker more memory (Settings → Resources), close other heavy programs |
| `make lab-test` fails right after bootstrap | Wait one minute (GLPI and Zabbix finish starting) and run it again |
| Portal says "Monitoring view is not configured" | Run `make lab-bootstrap` again (it creates the read-only Zabbix token) |
