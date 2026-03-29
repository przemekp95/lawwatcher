# LawWatcher Runbook

This runbook covers incident response, recovery order, and safe operational actions.

It does not restate the full runtime contract. For supported profiles, startup commands, environment examples, and
API surface, use [README.md](../README.md). For codebase shape and verification lanes, use
[ARCHITECTURE.md](ARCHITECTURE.md) and [VERIFICATION.md](VERIFICATION.md).

## Scope

- [README.md](../README.md) is the entrypoint for the repo and points to the deeper technical docs.
- [ARCHITECTURE.md](ARCHITECTURE.md) is the source of truth for bounded contexts, host responsibilities, and
  transport or async boundaries.
- [VERIFICATION.md](VERIFICATION.md) is the source of truth for standard test and smoke verification lanes.
- This runbook is the source of truth for incident handling, recovery-safe proof selection, escalation evidence, and
  actions that are explicitly unsafe.
- The supported operational contract is still Docker-first and host-OS-neutral: raw `docker compose` is the source
  of truth, and the checked-in `ops/*.sh` wrappers are only a thin POSIX shell layer over the same compose profiles.

## First Response

Do not start with retention cleanup, replay, backfill, or manual SQL edits. Use this order.

### 1. Identify the failing slice

Pick the smallest affected slice before you touch anything:

- baseline runtime
- AI runtime
- full-host runtime
- full-host with `OpenSearch`
- browser admin flow
- broker delivery or projection refresh

### 2. Run the smallest safe proof first

Start with host readiness:

```bash
bash ops/run-host-health-smoke.sh
```

Then run the narrowest proof for the failing slice:

- baseline Docker runtime: `bash ops/run-docker-dev-laptop-smoke.sh`
- baseline Docker runtime with AI: `bash ops/run-docker-dev-laptop-smoke.sh --include-ai`
- full-host runtime: `bash ops/run-docker-full-host-smoke.sh`
- full-host runtime with hybrid/vector search: `bash ops/run-docker-full-host-smoke.sh --include-opensearch`
- browser admin flow: `bash ops/run-operator-admin-smoke.sh`, then `bash ops/run-operator-admin-browser-smoke.sh`
- signed webhook delivery: `bash ops/run-signed-webhook-smoke.sh`
- retention lane: `bash ops/run-retention-smoke.sh`
- write-path nonblocking lane: `bash ops/run-rabbitmq-write-path-nonblocking-smoke.sh`
- structured logs: `bash ops/run-structured-log-proof.sh`
- MinIO-backed act grounding: `bash ops/run-act-ai-grounding-minio-smoke.sh`

If the smallest proof is green, treat the issue as local state drift or an environment-specific problem before you
assume a general runtime regression.

### 3. Check host and messaging diagnostics

For host readiness issues:

- inspect the matching `*-out.log` and `*-err.log`
- inspect the matching summary under `output/health` or `output/smoke`
- verify that the selected profile actually includes the host you expect

For async or broker issues, use `GET /v1/system/messaging` as the source of truth for:

- `deliveryMode` and `pollerMode`
- pending or deferred outbox rows
- processed inbox rows by `consumer_name`
- broker endpoint `readyCount`, `unackedCount`, `faultCount`, and `deadLetterCount`
- whether OCR recovery is happening through `document-text-extracted-ai-recovery`

### 4. Restart only the smallest affected slice

Prefer the smallest restart that matches the failure:

- restart one dependency first
- then restart the affected runtime profile through the supported wrapper
- restart the whole stack only if smaller restarts did not recover the flow

Do not mix repo wrappers with ad hoc `docker kill` or manual container surgery unless you are collecting low-level
evidence.

## Incident Playbooks

### API is live but not ready

Symptoms:

- `/health/live` is healthy
- `/health/ready` is unhealthy

Actions:

1. Identify the failing dependency: `sqlserver`, `rabbitmq`, `ollama`, or `opensearch`.
2. Restart only that dependency or the smallest affected profile.
3. Re-run `bash ops/run-host-health-smoke.sh`.
4. If readiness is still red, keep the smoke summary and host logs before any wider restart.

Do not start retention cleanup for a readiness failure.

### RabbitMQ is down or broker delivery stalls

Symptoms:

- broker-oriented smoke fails before or during delivery
- `/v1/system/messaging` shows growing pending outbox rows
- no matching inbox movement for the expected consumer

Actions:

1. Restart the affected runtime profile through the supported Docker wrapper path.
2. Re-run the smallest proof for the affected flow.
3. Re-check `/v1/system/messaging` and confirm the original stuck condition is gone.
4. If `deliveryMode=rabbitmq`, verify that broker counts no longer accumulate on the affected endpoint.

Do not manually delete `outbox` or `inbox` rows.

### Search, events, or alerts look stale

Symptoms:

- search results, event feed, alerts, or profile views do not reflect recent writes
- a smoke for that slice fails on projection assertions

In broker mode, projection refresh already does bounded startup catch-up. After a cold boot or redeploy, give that
warm-up window a chance to finish before you treat an initially empty projection as stuck.

Actions:

1. Check `/v1/system/messaging` for the relevant `message_type` and `consumer_name`.
2. Run the matching projection-oriented smoke.
3. If the smoke is green, re-check the user-visible endpoint.
4. If the smoke is red, keep the summary and logs, then restart the affected profile and re-run the same proof.

### Replay or backfill is needed intentionally

Replay and backfill are not first-response tools. Use them only when you are intentionally repopulating or re-driving
state.

Recommended sequence:

1. Capture current diagnostics first.
2. Prove the runtime is otherwise healthy with `bash ops/run-docker-full-host-smoke.sh`.
3. Only then issue the replay or backfill you actually mean to run.

For endpoint details and supported auth surface, use [README.md](../README.md).

### AI task fails or grounding looks wrong

Symptoms:

- AI task stays pending
- `worker-ai` readiness is red
- `worker-documents` readiness is red
- AI response lacks expected `ELI` or `document://` citations

Actions:

1. Run `bash ops/run-host-health-smoke.sh`.
2. Verify the container model:

```bash
bash ops/ensure-docker-ollama-model.sh llama3.2:1b
```

3. Re-run the smallest relevant proof:

- `bash ops/run-docker-dev-laptop-smoke.sh --include-ai`
- `bash ops/run-act-ai-grounding-minio-smoke.sh`

If the MinIO grounding proof is green but a user-facing answer is still poor, treat it as content or prompt quality
investigation, not storage or OCR recovery.

### Browser admin flow fails

Symptoms:

- login fails
- a browser write returns `400` because antiforgery is missing
- browser `/admin` behavior diverges from backend behavior

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

If backend admin proof is green but browser proof is red, treat it as a portal/client issue, not a backend auth-store
or API recovery issue.

## Safe Maintenance Actions

### Retention cleanup

`POST /v1/system/maintenance/retention` is safe only for age-based cleanup after diagnostics are already captured.

Use it this way:

- after evidence is collected
- not as a substitute for replay, backfill, broker recovery, or readiness recovery
- with the understanding that `documentArtifactsRetentionHours` prunes only derived OCR or AI artifacts, not source
  documents

### Restarts

Prefer the smallest supported restart path and use the documented wrapper commands from [README.md](../README.md).

Use raw manual container manipulation only when you are collecting evidence and you know why the wrapper path is not
enough.

## Evidence Before Escalation

Collect and keep:

- matching `output/smoke/*.json` summary
- matching `output/health/*.json` summary
- matching `output/playwright/*.json` summary and screenshots for browser issues
- matching host `*-out.log` and `*-err.log`
- `/v1/system/messaging` response snapshot when async or broker behavior is involved
- `/health/live` and `/health/ready` responses for affected hosts

For broker issues also capture:

- failing `message_type`
- expected `consumer_name`
- `deliveryMode`
- `pollerMode`
- affected broker `endpointName`
- broker `readyCount`, `unackedCount`, `faultCount`, and `deadLetterCount`

## Things Not To Do

- do not manually delete `outbox`, `inbox`, `event_feed`, or search rows as first response
- do not run retention before collecting diagnostics
- do not claim broker failure without checking `GET /v1/system/messaging`
- do not treat browser admin failures as backend auth failures until backend admin proof is checked
- do not treat AI grounding failures as RabbitMQ failures until host readiness and AI grounding proof are checked

## Recovery Exit Criteria

An incident is recovered only when:

- affected hosts are healthy on both `/health/live` and `/health/ready`
- the smallest relevant proof for the incident is green
- `/v1/system/messaging` no longer shows the original stuck condition when async delivery was involved
- the affected user-visible endpoint or browser flow is confirmed again
