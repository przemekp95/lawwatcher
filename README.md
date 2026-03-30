# LawWatcher

LawWatcher is a private, on-prem, single-tenant legislative monitoring platform. The supported product contract is a self-hosted `production` bundle backed by versioned GHCR images, Docker Compose, SQL Server, RabbitMQ, MinIO, Ollama, and OpenSearch.

## Product Contract

- `production`: the only supported installable product
- `dev`: a developer profile for local work, explicitly not supported as a product install
- operator/browser surface: cookie auth + CSRF
- integration surface: bearer-authenticated reads and writes, plus signed webhooks

## Quick Start

Prerequisites:

- Docker with Compose
- `bash`
- access to the GHCR images for the release you want to install

Production install:

```bash
bash ops/validate-production-env.sh --env-file ops/env/production.env.example
bash ops/run-docker-production.sh --env-file ops/env/production.env.example
```

First-run bootstrap:

- open `/admin`
- create the first operator with `LAWWATCHER__BOOTSTRAP__SECRET`
- optionally create the first API client from the bootstrap section with `integration:read` and any required write scopes

Developer runtime:

```bash
bash ops/run-docker-dev.sh --env-file ops/env/dev.env.example
```

Baseline verification:

```bash
dotnet build LawWatcher.slnx -c Release
dotnet test LawWatcher.slnx -c Release --collect:"XPlat Code Coverage"
```

Windows note:

- if the repo lives under `Downloads` and Windows Code Integrity interferes with `dotnet test`, run `powershell -File ops/run-local-verification.ps1`

## Docs

- [Install](docs/INSTALL.md)
- [Configuration](docs/CONFIGURATION.md)
- [Backup And Restore](docs/BACKUP-RESTORE.md)
- [Upgrades](docs/UPGRADES.md)
- [Support](docs/SUPPORT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Verification](docs/VERIFICATION.md)
- [Runbook](docs/RUNBOOK.md)

## Notes

- The supported production bundle uses the full platform topology with `Api`, `Portal`, dedicated workers, `Ollama`, and `OpenSearch`.
- Default demo seeds are not part of the supported product contract.
- The machine-readable integration contract is exposed from the API host at `/openapi/integration-v1.json`.
- Integration `GET` endpoints require `Authorization: Bearer <token>` with the canonical `integration:read` scope.
