# LawWatcher

LawWatcher is an on-prem, single-tenant modular monolith for legislative monitoring. The repo is aimed at backend/platform review: bounded contexts, explicit async flows, browser-safe admin auth, and Docker-first operational verification.

## What This Repo Demonstrates

- modular monolith boundaries with domain/application/contracts/infrastructure slices
- browser admin flow with cookie auth plus CSRF, separate from bearer-only machine-to-machine APIs
- async processing through workers, outbox/inbox persistence, webhook delivery, and broker-backed execution
- Docker-first runtime verification for baseline, AI, and full-host profiles

## Evaluator Path

Prerequisites:

- .NET SDK `10.0.201`
- Docker with Compose
- `bash`
- Node.js for browser/smoke tooling

Fast validation:

```bash
dotnet build LawWatcher.slnx -c Release
dotnet test LawWatcher.slnx -c Release --collect:"XPlat Code Coverage"
```

Optional runtime proof:

```bash
bash ops/run-docker-dev-laptop-smoke.sh --build-local
```

## Docs

- [Architecture](docs/ARCHITECTURE.md)
- [Verification](docs/VERIFICATION.md)
- [Runbook](docs/RUNBOOK.md)

## Repo Layout

```text
.
|-- docs/
|-- ops/
|-- src/
|-- tests/
|-- .github/workflows/
|-- Directory.Build.props
|-- global.json
`-- LawWatcher.slnx
```

## Notes

- Production-facing HTTP and async contracts under `/v1/**` are intentionally unchanged by the repo cleanup work.
- Heavy operational and smoke details live in the linked docs instead of this entrypoint.
