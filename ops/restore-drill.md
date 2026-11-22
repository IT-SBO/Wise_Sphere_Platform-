# Restore drill – TC-11 / WBS 5.5 (quarterly after go-live)

Target: services start on **staging**, data ≤ 24 h old, total time ≤ 8 h (RPO 24 h / RTO 8 h).

1. Start the timer: note the time.
2. On staging: `restic -r $RESTIC_REPOSITORY snapshots --tag wisesphere` → pick the latest; `restic restore <id> --target /restore`.
3. Copy `hub-config-*.tgz` to `/opt/wisesphere/hub`, set staging host names in `.env`.
4. `docker compose --profile app --profile zabbix up -d glpi-db n8n-db zabbix-db`
5. GLPI: `gunzip -c glpi-*.sql.gz | docker exec -i ws-glpi-db mariadb -uroot -p... glpi`; restore `glpi-files-*.tgz` into the `glpi-data` volume (contains the GLPI encryption key – without it API client tokens cannot be read).
6. n8n: `docker exec -i ws-n8n-db pg_restore -U n8n -d n8n --clean < n8n-*.dump` (same `N8N_ENCRYPTION_KEY` from the vault).
7. Zabbix: `docker exec -i ws-zabbix-db pg_restore -U zabbix -d zabbix --clean < zabbix-*.dump`.
8. `docker compose --profile app --profile zabbix up -d`; check GLPI login, Zabbix dashboard, n8n executions, Grafana NOC wall.
9. Send a test event: `curl -X POST https://<n8n>/webhook/zbx-event -H "X-WS-Secret: …" -d '{…}'` → ticket appears.
10. Stop the timer. Record elapsed time and newest ticket date in the DR record (sign-off for gate G2).
