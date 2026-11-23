# WiseSphere Grafana: the official OSS image + the WISE Group brand. Grafana OSS has no logo / custom-CSS setting,
# so the brand files (grafana/branding/, from branding/apply_branding.py) replace Grafana's icon, favicon and
# sign-in background wherever they appear (also the hashed copies in public/build). Built by lab/ and hub/ compose.
ARG GRAFANA_TAG=13.2.6
FROM grafana/grafana:${GRAFANA_TAG}
USER root
COPY branding/ /tmp/brand/
RUN set -e; cd /usr/share/grafana/public; \
    mkdir -p img/wisesphere; cp /tmp/brand/* img/wisesphere/; \
    find . -name 'grafana_icon*.svg' -exec cp /tmp/brand/icon.svg {} \; ; \
    find . -name 'g8_login_*.svg' -exec cp /tmp/brand/login-bg.svg {} \; ; \
    find . -name 'fav32*.png' -exec cp /tmp/brand/favicon.png {} \; ; \
    find . -name 'apple-touch-icon*.png' -exec cp /tmp/brand/favicon.png {} \; ; \
    rm -rf /tmp/brand
USER 472
