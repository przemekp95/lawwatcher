# LawWatcher Verification

This document is the source of truth for standard developer verification and how it relates to the heavier operational proof lanes.

## Standard Validation Lane

Use this first for local development and for candidate-facing repo evaluation:

```bash
dotnet build LawWatcher.slnx -c Release
dotnet test LawWatcher.slnx -c Release --collect:"XPlat Code Coverage"
```

This is the default repo health signal. It should stay green without requiring a full Docker runtime.

## PR-Gated Validation

The fast PR gate lives in `.github/workflows/docker-smoke.yml` and should cover:

- Docker contract validation
- .NET restore/build/test validation with TRX and coverage artifacts
- baseline Docker smoke
- AI Docker smoke
- host health smoke

These lanes are the main confidence path for pull requests.

## Heavier Operational Proofs

The repo also keeps broader smoke and ops proofs for runtime confidence. These remain useful, but they are a second-level signal rather than the default entrypoint for understanding repo quality.

Examples:

- `bash ops/run-docker-dev-laptop-smoke.sh --build-local`
- `bash ops/run-docker-dev-laptop-smoke.sh --build-local --include-ai`
- `bash ops/run-docker-full-host-smoke.sh --build-local --include-opensearch`
- `bash ops/run-host-health-smoke.sh --build-local`
- `bash ops/run-operator-admin-smoke.sh --build-local`
- `bash ops/run-operator-admin-browser-smoke.sh --build-local`
- `bash ops/run-rabbitmq-write-path-nonblocking-smoke.sh --build-local`
- `bash ops/run-retention-smoke.sh --build-local`
- `bash ops/run-signed-webhook-smoke.sh --build-local`
- `bash ops/run-structured-log-proof.sh --build-local`

## Coverage and Test Strategy

- Architecture rules are enforced through automated tests.
- Application behavior is covered by executable scenario-style tests running under `dotnet test`.
- Docker smoke scripts remain the runtime contract proof for health, broker paths, browser admin flow, and full-host topology.
- Coverage is collected and published, but there is currently no hard coverage threshold gate.

## Environment Notes

- If Windows host policy blocks assemblies from a downloaded workspace, confirm the same `dotnet test` lane in CI or from a non-blocked local path before treating it as a repo defect.
- Docker-first scripts remain supported and should continue writing summaries under `output/`.
