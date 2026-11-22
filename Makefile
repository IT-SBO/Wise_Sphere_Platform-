# WiseSphere platform - common tasks.  `make help`
SHELL := /bin/bash
PY ?= python3
DOTNET ?= scripts/dotnet.sh

.PHONY: web web-dev help build test unit check image lab-env lab-up lab-bootstrap lab-test lab-load-test lab-poc-test lab-logs lab-down lab-reset brand

help:
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-15s %s\n", $$1, $$2}'

build: ## Build the .NET solution
	$(DOTNET) build WiseSphere.slnx -c Release

unit: ## Unit tests: C# (xUnit) + Python tooling
	$(DOTNET) test WiseSphere.slnx
	$(PY) -m pytest -q tests/unit

test: unit ## Alias for unit

check: ## CI checks: unit tests, compose files valid, shell syntax
	$(MAKE) unit
	docker compose -f hub/docker-compose.yml --env-file hub/.env.example --profile app --profile zabbix config -q
	docker compose -f lab/docker-compose.yml --env-file lab/.env.example config -q
	docker compose -f spoke/docker-compose.yml --env-file spoke/.env.example --profile wazuh-relay config -q
	bash -n spoke/install-spoke.sh ops/*.sh scripts/*.sh tests/*.sh

image: ## Build the integration service and portal images (:dev)
	DOCKER_BUILDKIT=1 docker build -f src/WiseSphere.Integration/Dockerfile -t wisesphere/integration:dev .
	DOCKER_BUILDKIT=1 docker build -f src/WiseSphere.Portal/Dockerfile -t wisesphere/portal:dev .

web: ## Build the Angular portal (web/portal/dist)
	cd web/portal && npm ci && npx ng build

web-dev: ## Angular dev server on :4200, API proxied to the lab portal on :8089
	cd web/portal && npx ng serve

lab-env: ## Create lab/.env with random secrets
	scripts/lab-env.sh

lab-up: lab-env ## Start the lab (GLPI, Zabbix, integration service, simulated devices, SMS mock)
	cd lab && docker compose up -d --build

lab-bootstrap: ## Provision GLPI and Zabbix, connect the integration service (idempotent)
	scripts/bootstrap_lab.sh

lab-test: ## End-to-end tests TC-01..TC-07 against the lab
	$(PY) tests/e2e_lab.py

lab-load-test: ## TC-09: 500 alerts, none lost, p95 <= 60 s
	$(PY) tests/load_tc09.py

lab-poc-test: ## The PoC test: stop the agent container -> ticket, start it -> solved (~6 min)
	tests/poc_agent_down.sh

lab-logs: ## Follow the integration service logs
	docker logs -f ws-integration

lab-down: ## Stop the lab (data kept)
	cd lab && docker compose --profile grafana stop

lab-reset: ## Delete the lab and all its data
	cd lab && docker compose --profile grafana down -v && rm -f glpi-ids.json

brand: ## Apply branding/tokens.json: portal tokens.css + GLPI CSS preview
	$(PY) branding/apply_branding.py --render
