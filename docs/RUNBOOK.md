# LawWatcher Runbook

This runbook covers first response, runtime recovery and safe state or projection recovery for the supported LawWatcher profiles.

The supported operational contract is Docker-first and host-OS-neutral: raw `docker compose` is the source of truth, and the checked-in `ops/*.sh` wrappers are only a thin POSIX shell layer over the same compose profiles.

## Supported Runtime Profiles

- `dev-laptop`: `sqlserver`, `rabbitmq`, `minio`, `api`, `portal`, `worker-lite`
- `ai`: `ollama`, `worker-ai`
- `full-host`: `api`, `portal`, `worker-projection`, `worker-notifications`, `worker-replay`, `worker-documents`, `sqlserver`, `rabbitmq`, `minio`, `ollama`
- `opensearch`: optional add-on for `full-host`
- when `ENABLE_OPENSEARCH=true`, Compose runs a one-shot `hybrid-init` preflight before `api` and `worker-projection`; it waits for `OpenSearch` and pulls the configured Ollama embedding model so `HybridVector` is computed truthfully on first boot

Primary entrypoints:

```bash
bash ops/run-docker-dev-laptop.sh
bash ops/run-docker-dev-laptop.sh --include-ai
bash ops/run-docker-full-host.sh
bash ops/run-docker-full-host.sh --include-opensearch
```

Stop scripts:

```bash
bash ops/stop-docker-dev-laptop.sh
bash ops/stop-docker-full-host.sh
```

The checked-in Docker env examples keep the demo operator account and demo API-client seeds disabled by default. Only the smoke scripts opt into those demo seeds explicitly when they need local browser or bearer auth proofs.

## First Response

When a runtime looks broken, do not start with retention cleanup or manual SQL deletes. Start with these checks in order.

### 1. Check host health

```bash
bash ops/run-host-health-smoke.sh
```

Expected:

- `GET /health/live` returns healthy for `api`, `worker-lite`, `worker-ai` when they are meant to be running
- `GET /health/ready` returns healthy and includes dependency checks such as `sqlserver`, `rabbitmq` and `ollama` when applicable

The shared Docker health and AI smoke helpers resolve the actual compose-published `ollama` host port before they probe `/api/tags`. Do not diagnose a health-smoke failure against a hard-coded `127.0.0.1:11434` assumption when the stack was started on random local ports.

If a host is not ready:

- read the matching `*-out.log` and `*-err.log` artifacts created by the smoke
- verify the profile you started actually includes that host
- restart only the affected profile first before touching storage or retention

### 2. Check Docker or local process state

For Docker profiles:

```bash
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example ps
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example ps
```

If Docker is the chosen runtime, prefer restarting the affected profile through repo wrappers instead of `docker kill` on individual containers.

### 3. Check runtime messaging state

`GET /v1/system/messaging` is the source of truth for SQL outbox and inbox state.

- browser path: operator cookie auth
- machine-to-machine path: bearer with `api-clients:write`

Use it to answer:

- is the host running `rabbitmq` delivery or primary `sql-poller`
- which `message_type` rows are still pending or deferred in `outbox`
- which `consumer_name` entries have processed inbox rows
- which broker endpoints currently have ready or unacked messages
- whether broker consumers are attached to the expected endpoint queues
- whether fault or dead-letter queues are accumulating messages

If `deliveryMode=rabbitmq` and `pollerMode=fallback`, broker consumers are the main path and SQL poll loops are only safety nets.

### 4. Check current recovery-safe proofs

Before making changes, try the smallest supported smoke for the affected runtime profile. If the smoke is green, the problem is probably local state drift, not a general runtime outage.

- baseline Docker runtime: `bash ops/run-docker-dev-laptop-smoke.sh`
- Docker runtime with AI: `bash ops/run-docker-dev-laptop-smoke.sh --include-ai`
- full-host runtime: `bash ops/run-docker-full-host-smoke.sh`
- full-host with OpenSearch: `bash ops/run-docker-full-host-smoke.sh --include-opensearch`
- MinIO-backed AI grounding: `ops/run-act-ai-grounding-minio-smoke.sh`
- write-path nonblocking proof: `bash ops/run-rabbitmq-write-path-nonblocking-smoke.sh`
- retention proof: `bash ops/run-retention-smoke.sh`
- structured-log proof: `bash ops/run-structured-log-proof.sh`
- operator auth and admin CRUD: `ops/run-operator-admin-smoke.sh`
- browser admin CRUD: `ops/run-operator-admin-browser-smoke.sh`

## Common Incident Playbooks

### API Is Up But Not Ready

Symptoms:

- `/health/live` is healthy
- `/health/ready` is unhealthy

Actions:

1. Check whether the failing dependency is `sqlserver`, `rabbitmq`, `ollama` or `opensearch`.
2. Restart only the affected dependency first.
3. Re-run `ops/run-host-health-smoke.sh`.
4. If readiness is still red, inspect logs and matching smoke summaries under `output`.

Do not start retention cleanup for a readiness failure.

### RabbitMQ Is Down Or Broker Delivery Stalls

Symptoms:

- broker smoke fails before or during delivery
- `/v1/system/messaging` shows growing pending `outbox`
- no matching `inbox` movement for target consumer

Actions:

1. Restart the affected Docker profile so `rabbitmq` comes back through the supported runtime path.
2. Re-run the smallest supported profile smoke for the affected flow.
3. Confirm `/v1/system/messaging` now shows `published` outbox rows and matching `inbox` entries.
4. In broker mode, also confirm the broker section no longer shows growing `readyCount`, `faultCount` or `deadLetterCount` for the affected endpoint.
4. Only if the stack still looks inconsistent, restart the affected runtime profile again from the repo wrapper.

Do not manually delete `outbox` or `inbox` rows.

### Projection Looks Stale

Symptoms:

- search, event feed, alerts or profile read model does not reflect recent writes
- broker smoke for the same flow fails on projection assertions

In broker mode, the projection loop already does bounded startup catch-up passes for alerts, event feed and search before it settles into steady-state broker-only processing. After a cold boot or redeploy, give that warm-up window a chance to finish before you treat an initially empty search result as a stuck projection.

Actions:

1. Check `/v1/system/messaging` for the relevant `message_type` and `consumer_name`.
2. Run the matching broker smoke for that projection slice.
3. If the smoke is green, recheck the user-visible read model through the same endpoint the smoke uses.
4. If the smoke is red, keep the failing summary JSON and host logs, then restart the affected runtime profile and re-run the same smoke.

Safe projection-oriented smoke map:

- laptop-first projection issues: `bash ops/run-docker-dev-laptop-smoke.sh`
- laptop-first with AI or grounding concerns: `bash ops/run-docker-dev-laptop-smoke.sh --include-ai`
- full-host projection issues: `bash ops/run-docker-full-host-smoke.sh`
- full-host with hybrid/vector search concerns: `bash ops/run-docker-full-host-smoke.sh --include-opensearch`

### Replay Or Backfill Is Needed

Use replay or backfill only when you need to repopulate or re-drive state intentionally, not as a first reaction to every incident.

Preferred proof:

```bash
bash ops/run-docker-full-host-smoke.sh
```

Use direct API commands only when you know the request you need to issue and you have already captured current diagnostics.

### AI Task Fails Or Grounding Looks Wrong

Symptoms:

- AI task stays pending
- readiness for `worker-ai` is red
- response lacks `ELI` or `document://` citations

Actions:

1. Run `ops/run-host-health-smoke.sh` and confirm `worker-ai` readiness.
2. Verify the container model:

```bash
bash ops/ensure-docker-ollama-model.sh llama3.2:1b
```

If the runtime was started through a smoke wrapper on random local ports, prefer the repo helpers over manual `curl` calls to `127.0.0.1:11434`; the helpers already resolve the current compose-published `ollama` port.

3. Re-run the smallest relevant proof:

- `bash ops/run-docker-dev-laptop-smoke.sh --include-ai`
- `ops/run-act-ai-grounding-minio-smoke.sh`

If the MinIO grounding smoke is green but a user task is wrong, treat it as content or prompt quality investigation, not storage-path recovery.

### Browser Admin Flow Fails

Symptoms:

- login fails
- CRUD action returns `400` due to missing antiforgery
- browser `/admin` flow diverges from backend smoke

Actions:

1. Run backend admin proof first:

```bash
bash ops/run-operator-admin-smoke.sh
```

2. Then run browser proof:

```bash
bash ops/run-operator-admin-browser-smoke.sh
```

3. Inspect:

- `output/playwright/operator-admin-browser-summary.json`
- `output/playwright/admin-authenticated.png`
- `output/playwright/admin-logged-out.png`

If backend admin smoke is green but browser smoke is red, treat it as a portal/client issue, not an auth-store or API recovery problem.

## Safe Maintenance Actions

### Retention Cleanup

`POST /v1/system/maintenance/retention` is safe only for removing old rows according to an explicit age policy.

Guidance:

- use retention after diagnostics are captured, not before
- do not use retention as a substitute for replay, backfill or broker recovery
- `search_documents` cleanup is opt-in through `searchDocumentsRetentionHours`
- terminal AI-task cleanup is opt-in through `aiTasksRetentionHours`
- `documentArtifactsRetentionHours` is exposed truthfully but currently returns an explicit unsupported reason; do not treat it as a working source-document delete lane

### Restart Policy

Prefer the smallest restart that matches the failing slice:

- single local dependency first
- then runtime profile wrapper
- then full stack restart only if smaller restarts did not recover the flow

Prefer:

```bash
bash ops/stop-docker-dev-laptop.sh
bash ops/run-docker-dev-laptop.sh
```

or:

```bash
bash ops/stop-docker-full-host.sh
bash ops/run-docker-full-host.sh --include-opensearch
```

Do not mix raw manual container surgery with repo wrappers unless you are collecting low-level evidence.

## Evidence To Collect Before Escalation

Keep these artifacts:

- matching `output/smoke/*.json` summary
- matching `output/health/*.json` summary
- matching `output/playwright/*.json` summary and screenshots for browser issues
- host `*-out.log` and `*-err.log`
- `/v1/system/messaging` response snapshot
- `/health/live` and `/health/ready` responses for affected hosts

When the issue is broker-related, include:

- the exact failing `message_type`
- the expected `consumer_name`
- whether `deliveryMode` was `rabbitmq` or `sql-poller`
- whether `pollerMode` was `fallback` or primary
- the affected broker `endpointName`
- current broker `readyCount`, `unackedCount`, `faultCount` and `deadLetterCount`

## Things Not To Do

- do not manually delete `outbox`, `inbox`, `event_feed` or `search_documents` rows as a first response
- do not run retention before collecting diagnostics
- do not claim broker failure without checking `GET /v1/system/messaging`
- do not treat browser admin failures as backend auth failures until `ops/run-operator-admin-smoke.sh` is checked
- do not treat AI grounding failures as RabbitMQ failures until `worker-ai` readiness and MinIO grounding smoke are checked

## Recovery Exit Criteria

An incident is considered recovered only when:

- affected hosts are healthy on both `/health/live` and `/health/ready`
- the smallest relevant smoke for the incident is green
- `/v1/system/messaging` no longer shows the original stuck condition
- user-visible projection or admin flow is confirmed again through the matching endpoint or browser smoke
