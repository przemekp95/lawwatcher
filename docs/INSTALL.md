# Install

The supported LawWatcher `1.0` product is the `production` Docker-first bundle. It is the only supported installable runtime.

## Prerequisites

- Docker with Compose
- `bash`
- GHCR access for the tagged LawWatcher images you want to install
- host capacity for:
  - `SQL Server`
  - `RabbitMQ`
  - `MinIO`
  - `Ollama`
  - `OpenSearch`

## Supported Install Path

1. Start from `ops/env/production.env.example`.
2. Replace every placeholder secret before first boot.
3. Pin the file to the release tag you intend to run.
4. Keep `LAWWATCHER__BOOTSTRAP__SECRET` set to a real secret. It gates the first-run operator bootstrap.
5. Validate the contract:

```bash
bash ops/validate-production-env.sh --env-file ops/env/production.env.example
```

6. Start the platform:

```bash
bash ops/run-docker-production.sh --env-file ops/env/production.env.example
```

7. Verify:
  - API readiness at `/health/ready`
  - portal readiness at `/health/ready`
  - portal reachability at `/admin`
  - integration contract at `/openapi/integration-v1.json`

## First-Run Bootstrap

1. Open the portal at `/admin`.
2. Use the bootstrap secret from `LAWWATCHER__BOOTSTRAP__SECRET` to create the first operator.
3. Sign in through the cookie + CSRF operator flow.
4. Optionally create the initial API client from the bootstrap section or from the API-clients admin surface.

The supported first-run bootstrap endpoints are:

- `GET /v1/bootstrap/status`
- `POST /v1/bootstrap/operator` with `X-LawWatcher-Bootstrap-Secret`
- `POST /v1/bootstrap/api-client` after operator sign-in

## Unsupported Install Paths

- running `dev` as if it were production
- using `latest` image tags as the supported production release
- enabling production with missing `Ollama` or `OpenSearch`
- relying on demo seed flags instead of explicit first-run bootstrap
