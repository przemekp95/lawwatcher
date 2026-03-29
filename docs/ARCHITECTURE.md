# LawWatcher Architecture

LawWatcher is a modular monolith with explicit bounded contexts and worker-driven async execution. The codebase is optimized for backend/platform review rather than frontend presentation.

## Architectural Shape

- Bounded contexts are split into `Domain`, `Application`, `Contracts`, and `Infrastructure` projects under `src/BoundedContexts`.
- `LawWatcher.Api` owns HTTP transport, runtime composition, auth, and orchestration entrypoints.
- Worker hosts own asynchronous execution paths:
  - `Worker.Lite`: alert generation, projection refresh, replay, backfill, notification delivery, webhook dispatch
  - `Worker.Ai`: AI enrichment execution and recovery
  - `Worker.Documents`: OCR and document artifact processing
  - `Worker.Projection`, `Worker.Notifications`, `Worker.Replay`: expanded full-host topology
- Shared cross-cutting concerns live in `BuildingBlocks`; the `SharedKernel` remains intentionally small.

## Domain Boundaries

- Domain code models aggregates and domain events inside each bounded context rather than burying rules in API transport or storage adapters.
- Application services coordinate repositories, projections, and integration events.
- Contracts stay transport-facing and do not depend on infrastructure.
- Infrastructure adapters provide file-backed, SQL-backed, broker-backed, and object-storage-backed implementations behind interfaces.

## Read/Write and Async Flow

- Browser and machine-to-machine writes enter through `LawWatcher.Api`.
- Core async flows are modeled through integration events, outbox/inbox persistence, and worker consumers instead of synchronous transport coupling.
- Read models such as search, event feed, alerts, and admin projections are refreshed asynchronously and exposed through the API and portal.
- The repo currently supports both laptop-first file-backed persistence and SQL-backed persistence, with RabbitMQ/MassTransit used when broker mode is enabled.

## HTTP, Auth, and Browser Protections

- Browser admin requests use operator cookie auth plus CSRF protection.
- Machine-to-machine endpoints stay bearer-only and do not rely on browser sessions.
- This separation is intentional and should stay explicit in review discussions: CSRF matters for browser cookie flows, not for bearer-only JSON integrations.

## Runtime Profiles

- `dev-laptop`: baseline Docker-first stack with SQL Server, RabbitMQ, MinIO, API, portal, and `worker-lite`
- `ai`: adds the on-demand local LLM path through Ollama and `worker-ai`
- `full-host`: expanded worker topology for projection, notifications, replay, documents, and AI
- `opensearch`: companion profile for hybrid/vector search in the full-host done-state

## Ports and Adapters Story

- External HTTP, broker, SQL, and object storage interactions are isolated behind repository, dispatcher, store, projection, and document storage abstractions.
- The repo is not a pure hexagonal architecture, but it does preserve meaningful adapter boundaries.
- The current shape is best described as a layered modular monolith with DDD influence, not full CQRS or microservices.
