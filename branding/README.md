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

The values in `tokens.json` today are **placeholders**.

## Apply
```bash
python3 branding/apply_branding.py --render          # preview: branding/build/glpi-custom.css
python3 branding/apply_branding.py --glpi-url http://localhost:8090/apirest.php --admin-password <glpi admin>
```

## Where the brand appears
| Surface | Mechanism | Status |
|---|---|---|
| GLPI client portal + staff UI | Custom CSS on the root entity, inherited by every client entity | Script ready |
| Grafana | `BRAND_NAME` → `GF_BRANDING_APP_TITLE`. Custom logo on login/menu needs Grafana Enterprise | Title only in OSS |
| SMS and ticket texts | Brand name in n8n messages | Done ("WiseSphere …") |
| Monthly client report (RPT-01) | Will read `tokens.json` + logo | When WBS 5.3 is built |
| Custom web portal | **Not in Release 1** (PRD R-06, §4.4: GLPI + Grafana are the client portal) | Needs a scope decision if the design includes one |
