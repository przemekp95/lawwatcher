# LawWatcher

LawWatcher is an on-prem, single-tenant modular monolith for legislative monitoring. The default runtime is laptop-first: it should run calmly on local hardware without always-hot AI and without `OpenSearch` in the steady state.

## Runtime Profiles

- `dev-laptop`: always-on `sqlserver`, `rabbitmq`, `minio`, `api`, `portal`, `worker-lite`.
- `ai`: on-demand local LLM through `ollama` and `worker-ai`.
- `full-host`: expanded worker set plus the local LLM; the final done-state for this profile includes production semantic search.
- `opensearch`: the compose companion profile that currently brings in the required `OpenSearch` services for the `full-host` done-state.

## Quick Start

Operational recovery and incident procedures are in [docs/RUNBOOK.md](docs/RUNBOOK.md).

Container start is image-first. Raw `docker compose pull` and `docker compose up -d` are the primary runtime contract on every host OS. They consume the same prebuilt Linux images from `GHCR`, and the checked-in `ops/*.sh` wrappers are only thin convenience helpers around that same Docker contract.

Runtime prerequisites for the supported Docker-first path:

- `docker` with the Compose plugin on `PATH`
- `bash`
- `curl`
- `node` plus `npm` or `npx` for browser and smoke tooling

The repo intentionally does not contain host-specific `PowerShell`, `Git Bash` path probing or Windows-only bootstrap logic in the supported runtime path. If `docker` is not on `PATH`, fix the host environment instead of relying on repo-local shell hacks.

The checked-in `ops/env/*.env.example` files now default to `ghcr.io/przemekp95/lawwatcher-*:<tag>`, which matches the public GitHub remote `https://github.com/przemekp95/lawwatcher`. If you run a fork or another org, override `LAWWATCHER_*_IMAGE` to your own `GHCR` owner.

The checked-in Docker env examples keep the demo operator account and demo API-client tokens disabled by default. Enable `LAWWATCHER__SEEDDATA__ENABLEDEFAULTOPERATORSEED=true` or `LAWWATCHER__SEEDDATA__ENABLEDEFAULTAPICLIENTSEED=true` only for local demo or smoke lanes; the supported operational path does not depend on those seeds.

`publish-images.yml` is optimized for iteration speed: pushes to `main` and `master` publish `linux/amd64` images with long `sha-<full-commit>` tags, while release tags `v*` publish the slower multi-arch `linux/amd64,linux/arm64` variants. The same workflow also exposes GHCR-backed ops proofs on `workflow_dispatch` for `write-path-nonblocking`, retention, signed-webhook and structured-log verification. Fresh local Docker proofs are green; the remaining remote GHCR rerun is tracked separately in [docs/ABSOLUTE_DONE_TODO.md](docs/ABSOLUTE_DONE_TODO.md).

Laptop-first baseline through raw compose and prebuilt images:

```bash
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example pull
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example up -d
```

Laptop-first baseline through the local shell wrapper and prebuilt images:

```bash
bash ops/run-docker-dev-laptop.sh
```

Local build override for development or CI:

```bash
bash ops/run-docker-dev-laptop.sh --build-local
```

On-demand local AI through raw compose and prebuilt images:

```bash
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example --profile ai pull
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example --profile ai up -d
```

On-demand local AI through the local shell wrapper and prebuilt images:

```bash
bash ops/run-docker-dev-laptop.sh --include-ai
```

The wrapper boots the `ai` profile and then ensures the pinned model is pulled inside the compose `ollama` container:

```bash
bash ops/ensure-docker-ollama-model.sh llama3.2:1b
```

Stop the Docker laptop-first stack:

```bash
bash ops/stop-docker-dev-laptop.sh
```

Docker laptop-first smoke against prebuilt images:

```bash
bash ops/run-docker-dev-laptop-smoke.sh
bash ops/run-docker-dev-laptop-smoke.sh --include-ai
```

Local build override for smoke and CI:

```bash
bash ops/run-docker-dev-laptop-smoke.sh --build-local
bash ops/run-docker-dev-laptop-smoke.sh --build-local --include-ai
```

Expanded full-host profile through raw compose and prebuilt images:

```bash
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example --profile full-host pull
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example --profile full-host up -d
```

Expanded full-host profile through the local shell wrapper and prebuilt images:

```bash
bash ops/run-docker-full-host.sh
```

Local build override for development or CI:

```bash
bash ops/run-docker-full-host.sh --build-local
```

Full-host with optional OpenSearch through raw compose and prebuilt images:

```bash
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example --profile full-host --profile opensearch pull
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example --profile full-host --profile opensearch up -d
```

Full-host with optional OpenSearch through the local shell wrapper and prebuilt images:

```bash
bash ops/run-docker-full-host.sh --include-opensearch
```

Local build override for development or CI:

```bash
bash ops/run-docker-full-host.sh --build-local --include-opensearch
```

Stop the Docker full-host stack:

```bash
bash ops/stop-docker-full-host.sh
```

Docker full-host smoke against prebuilt images:

```bash
bash ops/run-docker-full-host-smoke.sh
bash ops/run-docker-full-host-smoke.sh --include-opensearch
```

Local build override for smoke and CI:

```bash
bash ops/run-docker-full-host-smoke.sh --build-local
bash ops/run-docker-full-host-smoke.sh --build-local --include-opensearch
```

`ops/compose/docker-compose.build.yml` and `ops/compose/docker-compose.full-host.build.yml` exist only as opt-in local build overrides for wrappers and CI smoke scripts. They are not the primary runtime contract.

Browser-level verification against the local portal:

```bash
bash ops/verify-browser-dev.sh
```

Backend operator auth and admin CRUD smoke:

```bash
bash ops/run-operator-admin-smoke.sh
```

Browser operator auth and admin CRUD smoke:

```bash
bash ops/run-operator-admin-browser-smoke.sh
```

Host liveness/readiness smoke:

```bash
bash ops/run-host-health-smoke.sh
```

The host-health and AI smoke helpers do not assume a fixed Docker host port for `ollama`. They resolve the current compose-published port before probing `/api/tags`, so isolated and random-port smoke runs stay inside the supported Docker-first contract.

MinIO-backed act AI grounding smoke:

```bash
bash ops/run-act-ai-grounding-minio-smoke.sh
```

Legacy host-process wrappers, non-container SQL bootstrap and App Control workarounds are no longer part of the supported runtime contract.

## Operational Notes

- `SQL Server` is the base event store and read store.
- `MinIO` stores document and enrichment artifacts behind the `IDocumentStore` port.
- `LawWatcher.Api` exposes `GET /health/live` and `GET /health/ready`.
- `LawWatcher.Worker.Lite`, `LawWatcher.Worker.Ai` and `LawWatcher.Worker.Documents` expose the same health endpoints when `LawWatcher:Health:Urls` is configured. The local dev scripts wire `worker-lite` and `worker-ai` to `http://127.0.0.1:5291` and `http://127.0.0.1:5292`, while the Docker `full-host` profile exposes both `worker-ai` and `worker-documents` on their own health ports.
- `LawWatcher.Api` now selects the document store backend at runtime: `Storage:Minio:*` enables the final S3-compatible `IDocumentStore` path for the supported document/artifact flow, while the laptop-first local filesystem adapter under `Storage:LocalDocumentsRoot` remains only a non-final fallback/dev adapter.
- The local LLM runtime is now project-managed through the compose `ollama` service. The pinned default model is `llama3.2:1b`, and `bash ops/ensure-docker-ollama-model.sh llama3.2:1b` ensures the container runtime has the pinned model before `worker-ai` or Docker smoke runs.
- Shared Docker smoke helpers now resolve the actual compose-published `ollama` host port before they call `/api/tags`, so `host-health`, AI grounding and broker smokes do not depend on a fixed `127.0.0.1:11434` mapping.
- Seeded `LegalCorpus` acts now write real source artifacts into the selected document store, `worker-documents` turns those source artifacts into derived OCR/text artifacts under the dedicated `document-artifacts` bucket, and `worker-ai` grounds `subjectType=act` tasks from those persisted derived text artifacts before calling the local LLM. Completed task citations include both the act ELI and the stored artifact URI.
- `OpenSearch` is not part of the laptop-first baseline, but it is required for the `full-host` done-state and production semantic search. In `full-host`, `ops/run-docker-full-host-smoke.sh --include-opensearch` is now freshly green and proves truthful `HybridVector` capability reporting plus real hybrid/vector search over `OpenSearch + Ollama` embeddings.
- Current laptop-first runtime persists `imported bills`, `legislative processes`, `published acts`, `monitoring profiles`, `api clients`, `ai tasks`, `replays`, `backfills`, `alerts`, `profile subscriptions`, `webhook registrations`, `notification dispatches`, `webhook event dispatches`, `event feed` and the search index under the shared `artifacts/state` root configured by `LawWatcher:Storage:StateRoot`, so those async flows, core read models and machine-to-machine auth metadata survive host restarts even before the SQL event store is wired in.
- Alerting is based on data already present in the local LawWatcher store and projections. The current supported runtime does not automatically ingest the whole parliamentary term or historical corpus on demand, so a bill or act that never entered the local store cannot produce alerts.
- `MonitoringProfiles`, `ProfileSubscriptions`, `ReplayRequests`, `BackfillRequests`, `AiEnrichmentTasks`, `WebhookRegistrations`, `WebhookEventDispatches`, `ApiClients`, `ImportedBills`, `LegislativeProcesses`, `PublishedActs`, `BillAlerts`, `AlertNotificationDispatches`, `EventFeed` and `SearchIndex` can already run on real SQL-backed write/read slices when `LawWatcher:Storage:Provider=sqlserver` and `ConnectionStrings:LawWatcherSqlServer` points to a prepared database.
- `IdentityAndAccess` now seeds the local development operator account only when `LawWatcher:SeedData:EnableDefaultOperatorSeed=true`. The default development credentials are `admin@lawwatcher.local` / `Admin123!`, and the checked-in Docker env examples keep that demo seed disabled by default.
- The seeded demo machine-to-machine clients are also opt-in through `LawWatcher:SeedData:EnableDefaultApiClientSeed=true`. The supported runtime contract does not require those seeded tokens; the checked-in smoke scripts enable them explicitly only for dev/demo proof lanes.
- In `sqlserver` mode, `worker-ai` still supports the shared SQL outbox/inbox flow for `AiEnrichmentRequestedIntegrationEvent`, and `worker-lite` does the same for `ReplayRequestedIntegrationEvent`, `BackfillRequestedIntegrationEvent`, `BillAlertCreatedIntegrationEvent`, `BillImportedIntegrationEvent`, `BillDocumentAttachedIntegrationEvent`, `MonitoringProfileCreatedIntegrationEvent`, `MonitoringProfileRuleAddedIntegrationEvent`, `MonitoringProfileAlertPolicyChangedIntegrationEvent`, `ProfileSubscriptionCreatedIntegrationEvent`, `ProfileSubscriptionAlertPolicyChangedIntegrationEvent`, `WebhookRegisteredIntegrationEvent`, `WebhookDeactivatedIntegrationEvent`, `LegislativeProcessStartedIntegrationEvent`, `LegislativeStageRecordedIntegrationEvent`, `PublishedActRegisteredIntegrationEvent` and `ActArtifactAttachedIntegrationEvent`, recording inbox idempotency and marking processed outbox rows as `published`.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` now relays `AiEnrichmentRequestedIntegrationEvent`, `ReplayRequestedIntegrationEvent` and `BackfillRequestedIntegrationEvent` from the shared SQL outbox to RabbitMQ through MassTransit publishers. `worker-ai` consumes the AI event through a MassTransit broker consumer, consumes `DocumentTextExtractedIntegrationEvent` through a dedicated recovery consumer for queued OCR-dependent tasks, and `worker-lite` does the same for replay/backfill instead of using the local SQL poll loop for those two queues.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` also relays `BillAlertCreatedIntegrationEvent` from the shared SQL outbox to RabbitMQ. In that broker mode, `worker-lite` consumes the bill-alert notification and webhook dispatch flows through dedicated MassTransit consumers instead of using the local `AlertCreatedOutboxProcessor` poll loop.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` also relays `BillImportedIntegrationEvent` and `BillDocumentAttachedIntegrationEvent` from the shared SQL outbox to RabbitMQ. In that broker mode, `worker-lite` consumes those bill projection refresh events through a MassTransit consumer instead of using the local `BillProjectionOutboxProcessor` poll loop.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` also relays `LegislativeProcessStartedIntegrationEvent` and `LegislativeStageRecordedIntegrationEvent` from the shared SQL outbox to RabbitMQ. In that broker mode, `worker-lite` consumes those process projection refresh events through a MassTransit consumer instead of using the local `ProcessProjectionOutboxProcessor` poll loop.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` also relays `PublishedActRegisteredIntegrationEvent` and `ActArtifactAttachedIntegrationEvent` from the shared SQL outbox to RabbitMQ. In that broker mode, `worker-lite` consumes those act projection refresh events through a MassTransit consumer instead of using the local `ActProjectionOutboxProcessor` poll loop.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` also relays `MonitoringProfileCreatedIntegrationEvent`, `MonitoringProfileRuleAddedIntegrationEvent`, `MonitoringProfileAlertPolicyChangedIntegrationEvent` and `MonitoringProfileDeactivatedIntegrationEvent` from the shared SQL outbox to RabbitMQ. In that broker mode, `worker-lite` consumes those monitoring-profile projection refresh events through a MassTransit consumer instead of using the local `MonitoringProfileProjectionOutboxProcessor` poll loop.
- When `ConnectionStrings:RabbitMq` is configured, `LawWatcher.Api` also relays `ProfileSubscriptionCreatedIntegrationEvent`, `ProfileSubscriptionAlertPolicyChangedIntegrationEvent`, `ProfileSubscriptionDeactivatedIntegrationEvent`, `WebhookRegisteredIntegrationEvent`, `WebhookUpdatedIntegrationEvent` and `WebhookDeactivatedIntegrationEvent` from the shared SQL outbox to RabbitMQ through MassTransit publishers. In that broker mode, `worker-lite` consumes the subscription and webhook catch-up flows through broker consumers instead of using the local SQL poll loops for those two categories.
- In `sqlserver` mode, a newly created `alert.created` webhook registration is now caught up through its own outbox processor before the generic webhook dispatch scan fallback runs.
- `LawWatcher.Api`, `LawWatcher.Worker.Lite` and `LawWatcher.Worker.Ai` are configured to point to the same shared state root by default.
- `LawWatcher.Api` owns HTTP read/write transport only for those laptop-first async flows; `LawWatcher.Worker.Ai` owns AI execution and `LawWatcher.Worker.Lite` owns alert generation, search projection refresh, replay, backfill, alert notification and integration webhook dispatch loops.
- That persistence is an interim laptop-first adapter. The long-term target remains `SQL Server + outbox/inbox + RabbitMQ/MassTransit`.
- Bootstrap SQL scripts live under `ops/sql/sqlserver`.
- `src/Server/LawWatcher.Api/appsettings.json` and the worker `appsettings.json` files still expose `LawWatcher:Storage:Provider` and `ConnectionStrings:LawWatcherSqlServer`; the default connection string is now Linux/container-friendly and should be overridden by env for real deployments.
- `src/Server/LawWatcher.Api/appsettings.json` also exposes `Storage:LocalDocumentsRoot` and `Storage:Minio:*`; the existing compose env vars `STORAGE__MINIO__ENDPOINT`, `STORAGE__MINIO__ACCESSKEY` and `STORAGE__MINIO__SECRETKEY` now map to a real S3-compatible document store backend.
- `ops/compose/docker-compose.yml` is now a real laptop-first image-first runtime, not just scaffolding: it pulls `api`, `portal`, `worker-lite` and `worker-ai` from the configured images, applies SQL bootstrap through `sql-init`, and the fresh local build-override smoke is green for both baseline and `--include-ai`.
- The latest Docker smoke summary is written to `output/smoke/docker-dev-laptop-summary.json`.
- `ops/compose/docker-compose.full-host.yml` is now a real expanded image-first runtime: it pulls `api`, `portal`, `worker-ai`, `worker-projection`, `worker-notifications`, `worker-replay`, `worker-documents`, `sqlserver`, `rabbitmq`, `minio` and `ollama`, and the fresh local build-override smoke is green for that full topology.
- `ops/run-docker-full-host-smoke.sh --include-opensearch` is also freshly green: it proves healthy `OpenSearch`, backend `HybridVector (1)` on both `/v1/search` and `/v1/system/capabilities`, real `nomic-embed-text` embeddings, completed AI/replay/backfill flows and search hits from the expanded runtime.
- `full-host + opensearch` now serializes first boot through a one-shot `hybrid-init` preflight in Compose: it waits for `OpenSearch`, ensures the configured Ollama embedding model is present, and only then starts `api` plus `worker-projection`, so `HybridVector` capability reporting stays truthful on the first container start instead of depending on a later restart.
- The broker-mode projection loop now performs up to six startup catch-up passes spaced by five seconds before it settles into steady-state broker-only processing. That bounded warm-up absorbs cold-start races between API seeding and projection startup, and the full-host smoke waits for legislative search hits instead of assuming search is ready the moment `/health/ready` flips green.
- `publish-images.yml` is the supported image-first publish lane: it pushes `ghcr.io/przemekp95/lawwatcher-*` images tagged with long `sha-<full-commit>` tags, runs an Ubuntu GHCR smoke through `bash ops/run-ghcr-image-smoke.sh`, and exposes GHCR-backed `workflow_dispatch` ops proofs for `write-path-nonblocking`, retention, signed-webhook and structured logs. Fresh local counterparts are green; a fresh remote GHCR rerun is still pending.
- `docker-smoke.yml` stays the fast PR gate for Docker contract and build-local regression smoke; the heavier operational proofs still exist as shell scripts, but the image-first GHCR proof lane now lives in `publish-images.yml` on `workflow_dispatch`.
- In the current `full-host` runtime, `worker-documents` is the dedicated documents/OCR host, and `worker-ai` is the dedicated asynchronous AI execution plus OCR-recovery host. `worker-projection`, `worker-notifications` and `worker-replay` exist as dedicated host projects and are built by Docker from the repo.
- The declared `full-host` contract no longer includes a speculative `worker-ingest` host. The current supported host set is exactly `worker-ai`, `worker-projection`, `worker-notifications`, `worker-replay` and `worker-documents`.
- The latest Docker full-host smoke summary is written to `output/smoke/docker-full-host-summary.json`.
- Runtime embeddings are now truthful to the supported contract: `IEmbeddingService` is only composed in hybrid `OpenSearch + Ollama` mode, while the deterministic embedding adapter remains test-only and is no longer part of the supported runtime path.

## HTTP v1 Notes

- Browser-safe operator auth now exists in the API through `GET /v1/operator/session`, `POST /v1/operator/login`, `POST /v1/operator/logout`, `GET /v1/operator/me`, `GET /v1/operators`, `POST /v1/operators`, `PATCH /v1/operators/{id}`, `POST /v1/operators/{id}/deactivate` and `POST /v1/operators/{id}/reset-password`.
- Browser admin requests use cookie auth plus `X-LawWatcher-CSRF` / `lawwatcher.api.csrf`. Machine-to-machine endpoints remain bearer-only and do not use cookies or browser sessions.
- `GET /v1/system/api-clients` exposes the opt-in seeded machine-to-machine clients for local development when `LawWatcher:SeedData:EnableDefaultApiClientSeed=true`.
- `GET /v1/api-clients` is now an admin alias for the same machine-to-machine client projection, and `POST /v1/api-clients`, `PATCH /v1/api-clients/{id}` plus `DELETE /v1/api-clients/{id}` expose the backend admin write surface for API clients, including optional secret rotation through an updated token fingerprint.
- `GET /v1/system/api-clients` reads from a durable laptop-first projection under the shared state root in `files` mode, and from a SQL-backed projection when `LawWatcher:Storage:Provider=sqlserver`.
- `GET /v1/system/notification-dispatches` exposes diagnostic records for asynchronously delivered alert notifications.
- `GET /v1/system/webhook-dispatches` exposes diagnostic records for asynchronously delivered integration webhook events.
- `GET /v1/system/messaging` exposes runtime diagnostics for SQL outbox/inbox state plus broker telemetry when `rabbitmq` is configured. The broker section reports endpoint queue state, consumer counts, ready/unacked messages, fault/dead-letter queue counts and aggregated redelivery count, while the top-level contract still truthfully reports whether the host is running `rabbitmq` delivery with SQL polling only as `fallback`, or primary `sql-poller` mode. This endpoint accepts the same admin access as `/v1/system/api-clients`: operator cookie auth or bearer scope `api-clients:write`.
- `POST /v1/system/maintenance/retention` runs admin-only SQL retention cleanup for `published outbox`, `processed inbox`, the `event_feed` projection, optional `search_documents`, terminal `ai_enrichment_tasks`, and derived OCR/AI document artifacts. It accepts the same admin access as `/v1/system/messaging`; browser requests require antiforgery, bearer requests do not. `search_documents` cleanup remains opt-in through `searchDocumentsRetentionHours`, `ai_enrichment_tasks` cleanup is opt-in through `aiTasksRetentionHours`, and `documentArtifactsRetentionHours` now prunes only derived/non-authoritative OCR or AI artifacts tracked in the document-artifact catalog; source documents remain authoritative product data and are not deleted by this lane.
- `GET /v1/system/capabilities` and `GET /v1/search` now report the effective backend truthfully: the supported Docker-first baseline is `ProjectionIndex`, `SqlFullText` appears only when it is explicitly enabled and the runtime can actually provide it, and `HybridVector` appears only when both `OpenSearch` and the configured embedding model are actually reachable.
- `GET /v1/events` now reads from a durable event feed projection refreshed by `worker-lite`: file-backed under the shared state root in `files` mode, and SQL-backed in `sqlserver` mode.
- `GET /v1/search` now reads from a durable search index refreshed by `worker-lite`: file-backed under the shared state root in `files` mode, and SQL-backed in `sqlserver` mode.
- When SQL Server Full-Text Search is available and `LawWatcher:Runtime:Capabilities:SqlFullText=true`, the SQL-backed search adapter uses native `CONTAINSTABLE` over `title`, `snippet` and flattened `keywords_text`; otherwise the supported baseline stays on the persisted `ProjectionIndex` matcher instead of scanning transient in-memory state.
- The laptop-first search projection now indexes `bills`, `published acts`, `legislative processes`, `profiles` and `alerts`.
- `GET /v1/alerts` now depends on alerts generated by `worker-lite`, not by API startup bootstrap logic.
- `POST /v1/profiles`, `POST /v1/profiles/{id}/rules`, `PATCH /v1/profiles/{id}/alert-policy` and `DELETE /v1/profiles/{id}` now expose backend admin commands for monitoring profiles and accept either operator cookie auth with antiforgery or bearer scope `profiles:write`.
- `POST /v1/subscriptions`, `PATCH /v1/subscriptions/{id}/alert-policy` and `DELETE /v1/subscriptions/{id}` now expose backend admin commands for profile subscriptions and accept either operator cookie auth with antiforgery or bearer scope `subscriptions:write`.
- `POST /v1/ai/tasks` requires bearer scope `ai:write` and queues a local AI enrichment task for eventual background execution.
- `bash ops/run-act-ai-grounding-smoke.sh` starts an isolated `API + Worker.Ai + Worker.Documents` stack and verifies that an `act` task is grounded through stored source artifacts, derived OCR/text artifacts and local LLM citations.
- `bash ops/run-act-ai-grounding-minio-smoke.sh` starts an isolated `API + Worker.Ai + Worker.Documents` stack with `Storage:Minio:*` pointed at a real local `MinIO` instance, verifies that `act` seed artifacts are written and read through the S3-compatible document-store backend, proves that `worker-documents` persisted derived OCR/text artifacts under `document-artifacts`, and fails if the local filesystem fallback is used instead of `MinIO`.
- The old fine-grained non-container broker smoke lane and `SqlServerSpecs` harness were removed from the supported runtime contract. Supported messaging proof is now the Docker profile smokes plus runtime diagnostics such as `GET /v1/system/messaging`.
- `POST /v1/webhooks` accepts either operator cookie auth with antiforgery or bearer scope `webhooks:write`, and registers an active integration webhook.
- `PATCH /v1/webhooks/{id}` accepts either operator cookie auth with antiforgery or bearer scope `webhooks:write`, and updates the webhook name, callback URL and subscribed event types.
- `DELETE /v1/webhooks/{id}` accepts either operator cookie auth with antiforgery or bearer scope `webhooks:write`, and deactivates an integration webhook.
- `PATCH /v1/api-clients/{id}` accepts either operator cookie auth with antiforgery or bearer scope `api-clients:write`, and updates the API client name, scopes and optional secret rotation token.
- `bash ops/run-signed-webhook-smoke.sh` starts an isolated `API + Worker.Lite` stack, disables the seeded notification webhook subscription, registers a local `alert.created` integration webhook and verifies signed HTTP delivery end-to-end.
- `bash ops/run-rabbitmq-write-path-nonblocking-smoke.sh` starts an isolated Docker runtime, stops `worker-ai`, proves that `POST /v1/ai/tasks` still returns `202 Accepted` without waiting for the broker consumer, asserts queued backlog through `GET /v1/system/messaging`, then restarts `worker-ai` and waits for task completion.
- `bash ops/run-retention-smoke.sh` seeds an old terminal `ai_enrichment_tasks` row into SQL, waits for derived document artifacts to be cataloged, executes `POST /v1/system/maintenance/retention` through the supported admin bearer path, and proves cleanup with `documentArtifactsRetentionApplied=true` for derived OCR/AI artifacts.
- `bash ops/run-structured-log-proof.sh` starts an isolated full-host Docker runtime, drives AI, replay, backfill, profile-subscription catch-up, webhook-registration catch-up and signed webhook delivery, and asserts stable structured log markers including `flow=ai`, `flow=document-ocr`, `flow=document-text-projection`, `flow=replay`, `flow=backfill`, `flow=profile-subscription`, `flow=webhook-registration` and `flow=signed-webhook`.
- `bash ops/run-host-health-smoke.sh` starts an isolated `API + Worker.Lite + Worker.Ai + Worker.Documents` stack, verifies `GET /health/live` and `GET /health/ready` across all four hosts, and writes `output/health/host-health-summary.json`.
- `POST /v1/replays` requires bearer scope `replays:write` and is completed asynchronously by `worker-lite`.
- `POST /v1/backfills` requires bearer scope `backfills:write` and is completed asynchronously by `worker-lite`.
- When `LawWatcher:SeedData:EnableDefaultApiClientSeed=true`, the seeded local demo token for those write paths is `portal-integrator-demo-token`.
- That opt-in demo token also carries `profiles:write`, `subscriptions:write` and `api-clients:write` so the backend admin endpoints can be exercised locally without editing seed data.
- `bash ops/run-operator-admin-smoke.sh` starts an isolated API instance, verifies `401` for anonymous operator reads, `400` for missing CSRF on login and profile writes, `200` login + `GET /v1/operator/me`, then exercises operator/profile/subscription/webhook/API-client CRUD including deactivation plus API client secret rotation and bearer proof for the rotated token.
- `bash ops/run-operator-admin-browser-smoke.sh` starts isolated API and portal hosts on random localhost ports, signs into `/admin` through the real operator cookie flow, exercises browser CRUD for operators, profiles, subscriptions, webhooks and API clients, captures authenticated and logged-out screenshots into `output/playwright`, and fails if any admin browser step or artifact is missing.
- `POST /v1/ai/tasks`, `POST /v1/replays` and `POST /v1/backfills` remain machine-to-machine JSON endpoints. They do not use browser cookies or sessions, so CSRF protections are not the applicable control for them.
- `POST /v1/ai/tasks` is executed asynchronously by `worker-ai`.
- `POST /v1/replays` and `POST /v1/backfills` are executed asynchronously by `worker-lite`.
- Alert notification dispatches and integration webhook dispatches are executed asynchronously by `worker-lite`.
- In `sqlserver` mode, newly created alerts are pushed first through `BillAlertCreatedIntegrationEvent` from the shared SQL outbox, while the existing scan-based dispatch loops remain as catch-up fallback for late subscriptions or late webhook registrations.
- In `sqlserver + RabbitMQ` mode, `BillAlertCreatedIntegrationEvent` is relayed by `LawWatcher.Api` from the shared SQL outbox to the broker, and `worker-lite` consumes it through separate notification and webhook MassTransit consumers instead of the normal local scan path.
- In `sqlserver + RabbitMQ` mode, `BillImportedIntegrationEvent` and `BillDocumentAttachedIntegrationEvent` are relayed by `LawWatcher.Api` from the shared SQL outbox to the broker, and `worker-lite` consumes them through `worker-lite.bill-projection-refresh` instead of the normal local scan path.
- In `sqlserver + RabbitMQ` mode, `LegislativeProcessStartedIntegrationEvent` and `LegislativeStageRecordedIntegrationEvent` are relayed by `LawWatcher.Api` from the shared SQL outbox to the broker, and `worker-lite` consumes them through `worker-lite.process-projection-refresh` instead of the normal local scan path.
- In `sqlserver + RabbitMQ` mode, `PublishedActRegisteredIntegrationEvent` and `ActArtifactAttachedIntegrationEvent` are relayed by `LawWatcher.Api` from the shared SQL outbox to the broker, and `worker-lite` consumes them through `worker-lite.act-projection-refresh` instead of the normal local scan path.
- In `sqlserver + RabbitMQ` mode, `MonitoringProfileCreatedIntegrationEvent`, `MonitoringProfileRuleAddedIntegrationEvent`, `MonitoringProfileAlertPolicyChangedIntegrationEvent` and `MonitoringProfileDeactivatedIntegrationEvent` are relayed by `LawWatcher.Api` from the shared SQL outbox to the broker, and `worker-lite` consumes them through `worker-lite.monitoring-profile-projection-refresh` instead of the normal local scan path.
- In `sqlserver` mode, imported bills and newly attached bill documents are pushed first through `BillImportedIntegrationEvent` and `BillDocumentAttachedIntegrationEvent` from the shared SQL outbox, and `worker-lite` uses them to refresh alerts, event feed and search projections before the periodic scan fallback kicks in.
- In `sqlserver` mode, newly created monitoring profiles, newly added profile rules, alert-policy changes and profile deactivations are pushed first through `MonitoringProfileCreatedIntegrationEvent`, `MonitoringProfileRuleAddedIntegrationEvent`, `MonitoringProfileAlertPolicyChangedIntegrationEvent` and `MonitoringProfileDeactivatedIntegrationEvent`, and `worker-lite` uses them to refresh alerts and search before the periodic scan fallback kicks in.
- In `sqlserver` mode, newly created profile subscriptions, alert-policy changes and subscription deactivations are pushed first through `ProfileSubscriptionCreatedIntegrationEvent`, `ProfileSubscriptionAlertPolicyChangedIntegrationEvent` and `ProfileSubscriptionDeactivatedIntegrationEvent`, and `worker-lite` uses them to catch up alert notifications for immediate subscriptions before the periodic scan fallback kicks in.
- In `sqlserver` mode, newly registered, updated and deactivated webhooks are pushed first through `WebhookRegisteredIntegrationEvent`, `WebhookUpdatedIntegrationEvent` and `WebhookDeactivatedIntegrationEvent`, and `worker-lite` uses them to catch up alert dispatches before the periodic scan fallback kicks in.
- In `sqlserver + RabbitMQ` mode, those `ProfileSubscription*` and `Webhook*` catch-up events are relayed by `LawWatcher.Api` from the shared SQL outbox to the broker, and `worker-lite` consumes them through MassTransit instead of the normal local scan path.
- In `sqlserver + RabbitMQ` mode, `worker-lite` now disables the normal periodic projection/notification scan loops for broker-covered categories, so broker consumers are the primary runtime path and SQL scan processors remain the non-broker fallback/recovery path.
- In `sqlserver` mode, newly started legislative processes and recorded stages are pushed first through `LegislativeProcessStartedIntegrationEvent` and `LegislativeStageRecordedIntegrationEvent`, and `worker-lite` uses them to refresh `search` and `events` before the periodic scan fallback kicks in.
- In `sqlserver` mode, newly registered acts and attached act artifacts are pushed first through `PublishedActRegisteredIntegrationEvent` and `ActArtifactAttachedIntegrationEvent`, and `worker-lite` uses them to refresh `search` and `events` before the periodic scan fallback kicks in.
- Event feed projection refresh is executed asynchronously by `worker-lite`.
- `ai tasks`, `replays`, `backfills`, `alerts`, `subscriptions`, `webhook registrations` and their dispatch records now survive API or worker restart because their request streams and read projections are persisted on disk in the laptop-first profile.
- Seed data includes an immediate `email` subscription and an immediate `webhook` subscription for the `Podatki CIT` profile, so laptop-first startup produces real asynchronous notification deliveries.
- `LawWatcher:SeedData:EnableWebhookSubscriptionSeed=false` disables only that seeded notification webhook subscription for isolated smoke runs where signed HTTP delivery should target a local listener instead of `audit.example.test`.
- `LawWatcher:Webhooks:Backend=SignedHttp` switches `IWebhookDispatcher` from the local in-memory recorder to real signed HTTP POST delivery with `X-LawWatcher-Signature` and `X-LawWatcher-Event-Type` headers.
- A newly registered `alert.created` integration webhook will receive existing pending alert events through the background dispatch loop in laptop-first startup.

## Portal Notes

- `LawWatcher.Portal` is now a typed read-only client for the internal `v1` API, not a static template shell.
- `LawWatcher.Portal` now also exposes `/admin`, which signs into the internal API through the operator cookie flow and performs antiforgery-protected browser writes for `operators`, `profiles`, `subscriptions`, `webhooks` and `api clients`, including update and deactivation actions across that admin surface.
- The dashboard page reads `capabilities`, `profiles`, `bills`, `processes`, `acts`, `alerts`, `events` and `ai tasks`.
- The search page queries `GET /v1/search` and keeps the same contract whether the backend is `ProjectionIndex`, opt-in `SqlFullText` or a future hybrid/vector profile.
- The activity page surfaces `alerts` and the operational `event feed`.
- The admin page establishes an operator session against `LawWatcher.Api`, then uses the same scoped API cookie jar plus `X-LawWatcher-CSRF` for operator, profile, subscription, webhook and API client commands inside the Blazor Server circuit.
- Portal API reads are configured through `LawWatcher:PortalApi:BaseUrl`, which defaults to `http://127.0.0.1:5290` for local development.
- The supported runtime wrappers are now Docker-first shell scripts under `ops/*.sh`; the old Windows-only local host-process wrappers are no longer part of the supported contract.
- `ops/verify-browser-dev.sh` captures Chromium screenshots for `/`, `/search?q=VAT`, `/activity` and `/admin` into `output/playwright`, and writes a `browser-summary.json` with the verification metadata, including the browser admin entrypoint.
- `ops/run-operator-admin-browser-smoke.sh` is the stronger authenticated browser proof for `/admin`: it verifies `login -> CRUD -> deactivate -> logout` through the live Blazor Server UI and writes `operator-admin-browser-summary.json`, `admin-authenticated.png` and `admin-logged-out.png` under `output/playwright`.
- The removed non-container `SqlServerSpecs` lane is no longer part of the repo. Supported SQL verification now comes from the Docker `dev-laptop` and `full-host` runtime smokes on real `sqlserver` containers.
