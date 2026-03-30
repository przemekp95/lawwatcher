# Configuration

The supported product exposes two runtime classes:

- `production`: full platform, supported
- `dev`: developer-only, unsupported as a product install

## Required Production Configuration

- image tags pinned to a SemVer release
- SQL Server connection string
- RabbitMQ connection string
- MinIO endpoint and credentials
- webhook signing secret
- Ollama base URL and embedding model
- OpenSearch base URL and index name
- OCR enabled
- OpenSearch enabled

## Bootstrap Configuration

The production contract uses one bootstrap secret:

- `LAWWATCHER__BOOTSTRAP__SECRET`

It is sent once during `POST /v1/bootstrap/operator` through the `X-LawWatcher-Bootstrap-Secret` header. The first API client is then created from the authenticated operator surface; it is not seeded from env in the supported production path.

## Security Defaults

- browser admin flows stay on cookie auth + CSRF
- integrations stay bearer-authenticated for reads, writes, and signed-webhook delivery
- the canonical integration read scope is `integration:read`
- placeholder secrets are rejected by `ops/validate-production-env.sh`
- default operator and default API client seeds are not part of the supported production contract

## Health Contract

All supported hosts expose liveness and readiness endpoints:

- live: `/health/live`
- ready: `/health/ready`

The API host also exposes the machine-readable integration contract at `/openapi/integration-v1.json`.
