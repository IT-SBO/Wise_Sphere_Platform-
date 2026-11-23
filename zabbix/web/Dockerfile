# WiseSphere Zabbix web UI: the official image + the WISE Group brand (logos, colours, Cairo font, footer).
# Built by lab/ and hub/ docker compose. Brand files in zabbix/web/brand/ come from branding/apply_branding.py.
ARG ZABBIX_TAG=alpine-8.0.0
FROM zabbix/zabbix-web-nginx-pgsql:${ZABBIX_TAG}
USER root
COPY brand/ /tmp/brand/
RUN set -e; cd /usr/share/zabbix; \
    mkdir -p assets/img/wisesphere local/conf; \
    cp /tmp/brand/*.svg /tmp/brand/*.png /tmp/brand/*.woff2 assets/img/wisesphere/; \
    cp /tmp/brand/brand.conf.php local/conf/brand.conf.php; \
    sed -i -f /tmp/brand/theme.sed assets/styles/blue-theme.css; \
    cat /tmp/brand/wisesphere.css >> assets/styles/blue-theme.css; \
    cp /tmp/brand/favicon.png favicon.ico; \
    chown -R 1997:0 assets/img/wisesphere local/conf; rm -rf /tmp/brand
USER 1997
