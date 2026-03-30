# LawWatcher Architecture

LawWatcher remains a layered modular monolith with DDD influence. Productization for `1.0` does not split the system into microservices; it freezes a supported deployment contract around the existing bounded contexts, worker hosts, and async messaging model.

## Architectural Shape

- Bounded contexts stay split into `Domain`, `Application`, `Contracts`, and `Infrastructure` projects under `src/BoundedContexts`.
- `LawWatcher.Api` owns HTTP transport, operator auth, integration auth, runtime composition, and the machine-readable integration contract.
- `LawWatcher.Portal` stays an operator console over internal admin endpoints.
- The supported production topology uses:
  - `Api`
  - `Portal`
  - `worker-ai`
  - `worker-documents`
  - `worker-projection`
  - `worker-notifications`
  - `worker-replay`
  - `SQL Server`
  - `RabbitMQ`
  - `MinIO`
  - `Ollama`
  - `OpenSearch`

## Product Surfaces

- Operator/browser surface:
  - cookie-authenticated
  - CSRF-protected
  - intended for portal-driven admin and operational flows
- Integration surface:
  - bearer-only
  - intended for read models, replay/backfill, AI, search, and webhook workflows
  - documented through generated OpenAPI
  - all integration `GET` endpoints require the canonical `integration:read` scope

This separation is intentional and is part of the frozen `1.x` contract: browser session behavior and bearer integrations are not treated as the same transport surface.

## Async And Data Boundaries

- Cross-context side effects stay modeled through integration events, outbox/inbox persistence, and worker consumers.
- `RabbitMQ` remains the broker and execution boundary, not the durable data boundary.
- Durable product data lives in:
  - `SQL Server`
  - `MinIO`
- Search and embedding behavior in the supported production topology relies on `OpenSearch` plus `Ollama`.

## Ports And Adapters

- External HTTP, SQL, broker, object storage, and search integrations remain behind repository, dispatcher, projection, and store abstractions.
- The repo is not a pure hexagonal implementation, but it preserves meaningful adapter boundaries and bounded-context isolation.
- Productization does not change the DDD-style shape; it freezes and documents it.
