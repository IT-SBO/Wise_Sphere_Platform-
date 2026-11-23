# Branding – waiting for the final design and logo

Everything brand-related lives in this folder so the final design can be dropped in
without touching the platform code.

## What to provide
| File | Use |
|---|---|
| `assets/logo.svg` | Main logo (light backgrounds) – GLPI portal header/login, reports |
| `assets/logo-dark.svg` | Logo for dark backgrounds (NOC wall, dark theme) |
| `assets/favicon.png` | 64×64 browser icon |
| `tokens.json` | Colours, fonts (Latin + Arabic), brand name, tagline |

The values in `tokens.json` follow the **WISE Group** brand (https://wisegroup.trade): orange `#dc552d`, dark grey `#424242` navigation, `#252223` text, Cairo font, square corners, and the WiSE GROUP logo in `assets/logo.svg`. `--render` also copies the logo and favicon to `web/portal/public/brand/` and writes `glpi/branding/` (logo, white-text logo for the dark GLPI menu, favicon, Cairo font files).

## Apply
```bash
python3 branding/apply_branding.py --render          # preview: branding/build/glpi-custom.css
python3 branding/apply_branding.py --glpi-url http://localhost:8090/apirest.php --admin-password <glpi admin> --app-token <WS_GLPI_APP_TOKEN>
```
In the lab, `make lab-bootstrap` does this automatically and `make brand-glpi` repeats it.

GLPI reads the fonts and logos from `/wisesphere/`: `glpi/branding/` is mounted into the GLPI container
(`/var/www/glpi/public/wisesphere`, read-only) by `lab/docker-compose.yml` and `hub/docker-compose.yml`, and
`favicon.png` replaces GLPI's browser icon. Users who choose a dark GLPI palette keep it (only the logo and font change).

After changing tokens or logos: `make brand`, then rebuild the images (`make image`, and in `lab/`:
`docker compose build zabbix-web` / `docker compose --profile grafana build grafana`) and run `make brand-glpi`.

## Where the brand appears
| Surface | Mechanism | Status |
|---|---|---|
| GLPI sign-in, staff UI, client helpdesk | Custom CSS on the root entity, inherited by every client entity, + `glpi/branding/` files | Done (WISE Group style) |
| Zabbix web UI | `wisesphere/zabbix-web` image (`zabbix/web/Dockerfile`): blue theme recoloured (`theme.sed`), `wisesphere.css`, logos + footer (`brand.conf.php`), Cairo font, favicon | Done (WISE Group style) |
| Grafana | `wisesphere/grafana` image (`grafana/Dockerfile`): WISE icon, favicon, dark sign-in background; light theme; dashboard logo header and brand colours. Buttons and menus keep Grafana's own colours (Grafana OSS has no theme setting) | Done (within OSS limits) |
| SMS and ticket texts | Brand name in SMS and ticket texts from the integration service | Done ("WiseSphere …") |
| Monthly client report (RPT-01) | Will read `tokens.json` + logo | When WBS 5.3 is built |
| WiseSphere portal (Angular) | `web/portal/src/tokens.css` + `web/portal/public/brand/` | Done (WISE Group style) |
