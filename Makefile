# WiseSphere platform - common tasks.  `make help`
SHELL := /bin/bash
PY ?= python3

.PHONY: help test unit workflows check lab-env lab-up lab-bootstrap lab-test lab-down lab-reset lab-logs hub-config brand lab-poc-test

help:
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-15s %s\n", $$1, $$2}'

unit: ## Unit tests: n8n decision library + Python helpers
	node --test n8n/test/*.test.mjs
	$(PY) -m pytest -q tests/unit

workflows: ## Rebuild n8n/workflows/*.json from n8n/src + config
	$(PY) n8n/build_workflows.py

check: ## CI checks: unit tests, workflow JSON up to date, compose files valid, shell syntax
	$(MAKE) unit
	$(PY) n8n/build_workflows.py --check
	cp -n hub/n8n.env.base hub/n8n.env 2>/dev/null || true
	docker compose -f hub/docker-compose.yml --env-file hub/.env.example --profile app --profile zabbix config -q
	cp -n lab/n8n.env.base lab/n8n.env 2>/dev/null || true
	docker compose -f lab/docker-compose.yml --env-file lab/.env.example config -q
	docker compose -f spoke/docker-compose.yml --env-file spoke/.env.example --profile wazuh-relay config -q
	bash -n spoke/install-spoke.sh ops/*.sh scripts/*.sh

lab-env: ## Create lab/.env with random secrets
	scripts/lab-env.sh

lab-up: lab-env ## Start the lab (GLPI, Zabbix, n8n, simulated devices, SMS mock)
	cd lab && docker compose up -d

lab-bootstrap: ## Provision GLPI, Zabbix and n8n in the lab (idempotent)
	scripts/bootstrap_lab.sh

lab-test: ## End-to-end tests TC-01..TC-07 against the lab
	$(PY) tests/e2e_lab.py

lab-poc-test: ## The PoC test: stop the agent container -> ticket, start it -> solved (~6 min)
	tests/poc_agent_down.sh

lab-logs: ## Follow n8n logs
	docker logs -f ws-n8n

lab-down: ## Stop the lab (data kept)
	cd lab && docker compose --profile grafana stop

lab-reset: ## Delete the lab and all its data
	cd lab && docker compose --profile grafana down -v && rm -f n8n.env glpi-ids.json

brand: ## Render the brand CSS preview (branding/build)
	$(PY) branding/apply_branding.py --render
