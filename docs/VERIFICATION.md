# LawWatcher Verification

This document is the source of truth for repo health, product contract validation, and release-level verification.

## Baseline Repo Gate

Run this first for any code change:

```bash
dotnet build LawWatcher.slnx -c Release
dotnet test LawWatcher.slnx -c Release --collect:"XPlat Code Coverage"
```

This is the minimum signal that the repo is healthy.

On Windows hosts where the repo lives under `Downloads`, prefer:

```powershell
powershell -File ops/run-local-verification.ps1
```

That wrapper mirrors the repo to a clean temp workspace outside `Downloads`, excludes runtime artifacts, uses a stable single-node build lane, and then runs the same `build` plus `test` contract there.

## Runtime Contract Gates

- `dev` contract:

```bash
bash ops/run-docker-dev-smoke.sh --env-file ops/env/dev.env.example
```

- `production` contract:

```bash
bash ops/validate-production-env.sh --env-file ops/env/production.env.example
bash ops/run-docker-production-smoke.sh --env-file ops/env/production.env.example
```

The supported production gate requires the full platform with AI, OCR, and OpenSearch enabled.

The smoke lanes assume the frozen `1.x` integration contract:

- integration `GET` endpoints are bearer-gated
- the canonical integration read scope is `integration:read`
- operator/browser flows remain cookie + CSRF

## Release Validation

- Versioned GHCR images are validated through `.github/workflows/publish-images.yml`.
- Release bundle generation runs from the same workflow on SemVer tags.
- Image-first verification is expected to exercise:
  - dev contract
  - dev contract with AI
  - production contract

## Additional Operational Proofs

The repo also keeps deeper proof scripts for runtime confidence:

- `bash ops/run-rabbitmq-write-path-nonblocking-smoke.sh`
- `bash ops/run-retention-smoke.sh`
- `bash ops/run-signed-webhook-smoke.sh`
- `bash ops/run-structured-log-proof.sh`
- `bash ops/run-poison-dlq-recovery-smoke.sh`

These remain important, but they are second-level proofs after the baseline and product contract gates.
