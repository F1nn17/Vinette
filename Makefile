# Single entry point for building, testing and running Vinette.
# Requires GNU Make, .NET SDK (see backend/global.json), Node.js LTS with corepack.

SHELL := /usr/bin/env bash
.SHELLFLAGS := -eu -o pipefail -c

BACKEND  := backend
FRONTEND := frontend
SLN      := Vinette.sln
API      := src/Vinette.Api/Vinette.Api.csproj
IMAGE    ?= vinette:dev
VERSION  ?= $(shell sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' $(BACKEND)/Directory.Build.props)

# Python 3 is needed only for `make seed`.
PYTHON ?= $(shell command -v python3 2>/dev/null || command -v python 2>/dev/null)
SEED_ARGS ?=

# pnpm is pinned by "packageManager" in frontend/package.json and resolved through corepack.
export COREPACK_ENABLE_DOWNLOAD_PROMPT := 0
DOTNET := cd $(BACKEND) && dotnet
PNPM := cd $(FRONTEND) && corepack pnpm

.DEFAULT_GOAL := help
.PHONY: help setup dev dev-backend dev-frontend dev-mail dev-mail-check dev-mail-down seed seed-reset build test test-integration e2e lint check-version check-bundle check-licenses format check api-types docker scan-image package clean

help: ## Show available targets
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-14s %s\n", $$1, $$2}'

setup: ## Restore backend and install frontend dependencies
	$(DOTNET) restore $(SLN)
	$(PNPM) install --frozen-lockfile

dev: ## Run backend and frontend dev servers in parallel
	$(MAKE) -j2 dev-backend dev-frontend

dev-backend:
	$(DOTNET) watch run --project src/Vinette.Api --launch-profile Vinette.Api

dev-frontend:
	$(PNPM) dev

dev-mail: ## Start the Dovecot dev mail server (alice@vinette.test / alice-pass)
	docker compose -f deploy/docker/compose.dovecot.yaml up -d --wait

dev-mail-check: ## Verify the IMAP capabilities Vinette needs on the dev mail server
	bash deploy/docker/dev-mail/check-capabilities.sh

dev-mail-down: ## Stop the dev mail server and discard its data
	docker compose -f deploy/docker/compose.dovecot.yaml down -v

seed: ## Load the test emails (backend/tests/fixtures) into alice's and bob's dev mailboxes; idempotent
	$(PYTHON) deploy/docker/dev-mail/seed.py $(SEED_ARGS)

seed-reset: ## Delete previously seeded emails and load them again
	$(PYTHON) deploy/docker/dev-mail/seed.py --reset $(SEED_ARGS)

build: ## Build backend (Release) and frontend
	$(DOTNET) build $(SLN) -c Release --no-restore
	$(PNPM) build

test: ## Run backend and frontend unit tests (no Docker needed)
	$(DOTNET) test --solution $(SLN) --no-restore -- --filter-not-trait "Category=Integration"
	$(PNPM) test
	node --test "scripts/*.test.mjs"

test-integration: ## Run backend integration tests against a real Dovecot in Docker (Testcontainers)
	$(DOTNET) test --solution $(SLN) --no-restore -- --filter-trait "Category=Integration" --ignore-exit-code 8

e2e: ## Run Playwright e2e tests against the running app (starts backend + frontend; E2E_MAIL=0 skips the dev mail server)
ifneq ($(E2E_MAIL),0)
	$(MAKE) dev-mail
	$(MAKE) seed
endif
	$(PNPM) e2e

lint: check-version check-licenses ## Check code style, static analysis, versions and dependency licenses
	$(DOTNET) format $(SLN) --verify-no-changes --no-restore
	$(PNPM) lint

check-bundle: ## The built front end is within its size budget (run after build; budgets: docs/performance.md)
	node scripts/check-bundle-size.mjs

check-version: ## Backend, frontend and Helm chart versions must match (Directory.Build.props is the source)
	node scripts/check-version.mjs

check-licenses: ## NuGet and npm dependency licenses must be compatible with Apache-2.0 (run make setup first)
	node scripts/check-licenses.mjs

format: ## Auto-format backend and frontend
	$(DOTNET) format $(SLN) --no-restore
	$(PNPM) format

check: lint test build check-bundle ## Everything CI runs: lint, test, build, bundle size

api-types: ## Regenerate TypeScript API types from the backend OpenAPI document
	$(DOTNET) build $(API) --no-restore --no-incremental -p:GenerateOpenApi=true
	$(PNPM) api-types

docker: ## Build the container image (IMAGE=vinette:dev)
	docker build -f deploy/docker/Dockerfile --build-arg VERSION=$(VERSION) -t $(IMAGE) .

scan-image: ## Scan the container image for vulnerabilities (IMAGE=vinette:dev); SBOM=file.json also writes its SBOM
	sh scripts/scan-image.sh $(IMAGE) $(SBOM)

package: ## Archive for a bare-metal install: artifacts/vinette-VERSION.tar.gz (the application with the front end, the unit file, the sample config)
	$(PNPM) build
	rm -rf artifacts/vinette-$(VERSION) artifacts/vinette-$(VERSION).tar.gz
	mkdir -p artifacts/vinette-$(VERSION)/app
	$(DOTNET) publish $(API) -c Release --no-self-contained -p:UseAppHost=false -o ../artifacts/vinette-$(VERSION)/app
	cp -r $(FRONTEND)/dist artifacts/vinette-$(VERSION)/app/wwwroot
	rm -f artifacts/vinette-$(VERSION)/app/appsettings.Development.json artifacts/vinette-$(VERSION)/app/web.config
	cp deploy/systemd/vinette.service deploy/config/appsettings.example.json LICENSE artifacts/vinette-$(VERSION)/
	cp docs/install-bare-metal.md artifacts/vinette-$(VERSION)/INSTALL.md
	tar -czf artifacts/vinette-$(VERSION).tar.gz -C artifacts vinette-$(VERSION)
	cd artifacts && sha256sum vinette-$(VERSION).tar.gz > vinette-$(VERSION).tar.gz.sha256
	@echo "artifacts/vinette-$(VERSION).tar.gz"

clean: ## Remove build outputs
	$(DOTNET) clean $(SLN) -v q || true
	find $(BACKEND) -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
	rm -rf artifacts $(FRONTEND)/dist $(FRONTEND)/coverage $(FRONTEND)/test-results $(FRONTEND)/playwright-report
