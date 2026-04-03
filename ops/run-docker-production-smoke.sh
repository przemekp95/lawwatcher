#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker
require_cmd curl
require_cmd node

env_file="ops/env/production.env.example"
build_local=false
ai_model="llama3.2:1b"
embedding_model="nomic-embed-text"

while (($# > 0)); do
  case "$1" in
    --env-file)
      env_file="$2"
      shift 2
      ;;
    --build-local)
      build_local=true
      shift
      ;;
    --ai-model)
      ai_model="$2"
      shift 2
      ;;
    --embedding-model)
      embedding_model="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

cd "$repo_root"

tmp_dir="$(mktemp -d)"
validated_env="${tmp_dir}/production-contract.env"
smoke_env="${tmp_dir}/production-smoke.env"
write_env_file_from_example \
  "$env_file" \
  "$validated_env" \
  "SQLSERVER_SA_PASSWORD=SmokeSqlServer!12345" \
  "RABBITMQ_DEFAULT_PASS=SmokeRabbitMq!12345" \
  "MINIO_ROOT_PASSWORD=SmokeMinio!12345" \
  "CONNECTIONSTRINGS__LAWWATCHERSQLSERVER=Server=sqlserver,1433;Database=LawWatcher;User Id=sa;Password=SmokeSqlServer!12345;TrustServerCertificate=True;Encrypt=False" \
  "CONNECTIONSTRINGS__RABBITMQ=amqp://lawwatcher:SmokeRabbitMq!12345@rabbitmq:5672/" \
  "STORAGE__MINIO__SECRETKEY=SmokeMinio!12345" \
  "LAWWATCHER__BOOTSTRAP__SECRET=SmokeBootstrapSecret!12345" \
  "LAWWATCHER__WEBHOOKS__SIGNINGSECRET=SmokeWebhookSecret!12345"
bash ops/validate-production-env.sh --env-file "$validated_env"

project_name="lawwatcher-production-$(random_suffix)"
api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
ollama_host_port="$(get_free_port)"
worker_ai_health_port="$(get_free_port)"
worker_projection_health_port="$(get_free_port)"
worker_notifications_health_port="$(get_free_port)"
worker_replay_health_port="$(get_free_port)"
worker_documents_health_port="$(get_free_port)"
opensearch_http_port="$(get_free_port)"
opensearch_transport_port="$(get_free_port)"

write_env_file_from_example \
  "$validated_env" \
  "$smoke_env" \
  "API_HOST_PORT=${api_port}" \
  "PORTAL_HOST_PORT=${portal_port}" \
  "SQLSERVER_HOST_PORT=${sql_port}" \
  "RABBITMQ_AMQP_PORT=${rabbit_amqp_port}" \
  "RABBITMQ_MANAGEMENT_PORT=${rabbit_management_port}" \
  "MINIO_API_PORT=${minio_api_port}" \
  "MINIO_CONSOLE_PORT=${minio_console_port}" \
  "OLLAMA_HOST_PORT=${ollama_host_port}" \
  "WORKER_AI_HEALTH_PORT=${worker_ai_health_port}" \
  "WORKER_PROJECTION_HEALTH_PORT=${worker_projection_health_port}" \
  "WORKER_NOTIFICATIONS_HEALTH_PORT=${worker_notifications_health_port}" \
  "WORKER_REPLAY_HEALTH_PORT=${worker_replay_health_port}" \
  "WORKER_DOCUMENTS_HEALTH_PORT=${worker_documents_health_port}" \
  "OPENSEARCH_HTTP_PORT=${opensearch_http_port}" \
  "OPENSEARCH_TRANSPORT_PORT=${opensearch_transport_port}" \
  "LAWWATCHER__BOOTSTRAP__ENABLEDEMODATA=true" \
  "LAWWATCHER__BOOTSTRAP__ENABLEINITIALAPICLIENT=true" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTNAME=Portal Integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTIDENTIFIER=portal-integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTTOKEN=portal-integrator-demo-token" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTSCOPESCSV=integration:read,replays:write,backfills:write,ai:write,webhooks:write,profiles:write,subscriptions:write,api-clients:write"

export LAWWATCHER_COMPOSE_ENV_FILE="${smoke_env}"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  -f ops/compose/docker-compose.production.yml
  --env-file "${smoke_env}"
  --profile production
  --profile opensearch
)

if [[ "$build_local" == "true" ]]; then
  compose_args+=(
    -f ops/compose/docker-compose.build.yml
    -f ops/compose/docker-compose.production.build.yml
  )
fi

cleanup() {
  docker "${compose_args[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

if [[ "$build_local" != "true" ]]; then
  pull_compose_images_or_use_local "${compose_args[@]}"
fi

if [[ "$build_local" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build opensearch ollama >/dev/null
else
  docker "${compose_args[@]}" up -d opensearch ollama >/dev/null
fi

wait_http_ok "http://127.0.0.1:${opensearch_http_port}/_cluster/health?wait_for_status=yellow&timeout=1s" >/dev/null
ensure_docker_ollama_model "$ai_model" "${compose_args[@]}"
ensure_docker_ollama_model "$embedding_model" "${compose_args[@]}"

if [[ "$build_local" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build --remove-orphans >/dev/null
else
  docker "${compose_args[@]}" up -d --remove-orphans >/dev/null
fi

export LAWWATCHER_INTEGRATION_BEARER_TOKEN="portal-integrator-demo-token"

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${portal_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${portal_port}/" >/dev/null
wait_http_ok "http://127.0.0.1:${portal_port}/admin" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_projection_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_notifications_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_replay_health_port}/health/ready" >/dev/null
wait_http_body_contains "http://127.0.0.1:${worker_documents_health_port}/health/live" "Worker.Documents host is running." 60 "worker-documents live identity" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_documents_health_port}/health/ready" >/dev/null
echo "Waiting for seeded act OCR artifacts..." >&2
wait_default_seed_act_ocr_ready 120 "${compose_args[@]}"

acts_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
profiles_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/profiles")"
curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/openapi/integration-v1.json" >/dev/null

replay_payload='{"scope":"docker-production-replay-smoke"}'
replay_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/replays" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "$replay_payload")"
replay_id="$(printf '%s' "$replay_accepted" | json_eval "process.stdout.write(String(data.id));")"
completed_replay_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/replays" "$replay_id")"

backfill_payload='{"source":"sejm","scope":"docker-production-backfill-smoke","requestedFrom":"2026-01-01","requestedTo":"2026-03-31"}'
backfill_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/backfills" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "$backfill_payload")"
backfill_id="$(printf '%s' "$backfill_accepted" | json_eval "process.stdout.write(String(data.id));")"
completed_backfill_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/backfills" "$backfill_id")"

search_json="$(wait_search_projection "http://127.0.0.1:${api_port}/v1/search?q=VAT")"
capabilities_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/system/capabilities")"

ocr_enabled="$(printf '%s' "$capabilities_json" | json_eval "process.stdout.write(String(Boolean(data.ocrEnabled)));")"
if [[ "$ocr_enabled" != "true" ]]; then
  echo "Expected OCR capability to be enabled in production smoke." >&2
  exit 1
fi

backend="$(printf '%s' "$search_json" | json_eval "process.stdout.write(String(data.backend));")"
capabilities_backend="$(printf '%s' "$capabilities_json" | json_eval "process.stdout.write(String(data.search.backend));")"
if [[ "$backend" != "1" || "$capabilities_backend" != "1" ]]; then
  echo "Expected HybridVector (1) backend in production OpenSearch smoke." >&2
  exit 1
fi

profile_id="$(printf '%s' "$profiles_json" | json_eval "process.stdout.write(String(data[0].id));")"
unique_suffix="$(node -e "console.log(require('crypto').randomUUID().replace(/-/g,'').slice(0,8))")"
subscription_recipient="docker-production-${unique_suffix}@example.test"
subscription_payload="$(PROFILE_ID="$profile_id" RECIPIENT="$subscription_recipient" node -e "const payload={profileId:process.env.PROFILE_ID, subscriber:process.env.RECIPIENT, channel:'email', alertPolicy:'immediate', digestIntervalMinutes:null}; process.stdout.write(JSON.stringify(payload));")"
curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/subscriptions" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "$subscription_payload" >/dev/null
notification_dispatches_json="$(wait_notification_dispatch_for_recipient "http://127.0.0.1:${api_port}/v1/system/notification-dispatches" "$subscription_recipient")"

act_id="$(printf '%s' "$acts_json" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "$acts_json" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
act_eli="$(printf '%s' "$acts_json" | json_eval "process.stdout.write(String(data[0].eli));")"
ai_payload="$(ACT_ID="$act_id" ACT_TITLE_JSON="$act_title" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Podsumuj opublikowany akt i uwzglednij material zrodlowy.'}; process.stdout.write(JSON.stringify(payload));")"
ai_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "$ai_payload")"
ai_task_id="$(printf '%s' "$ai_accepted" | json_eval "process.stdout.write(String(data.id));")"
completed_ai_task_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/ai/tasks" "$ai_task_id" 320)"

citations_ok="$(printf '%s' "$completed_ai_task_json" | ACT_ELI="$act_eli" node -e "const fs=require('fs'); const item=JSON.parse(fs.readFileSync(0,'utf8')); const citations=[...new Set(item.citations||[])]; const hasEli=citations.includes(process.env.ACT_ELI); const hasDoc=citations.some(value => value.startsWith('document://legal-corpus/')); process.stdout.write(hasEli && hasDoc ? '1' : '0');")"
if [[ "$citations_ok" != "1" ]]; then
  echo "Expected production AI task to include ELI and document://legal-corpus citations." >&2
  exit 1
fi

ocr_search_json="$(wait_search_projection "http://127.0.0.1:${api_port}/v1/search?q=1%20kwietnia%202026")"
ocr_search_has_act_hit="$(printf '%s' "$ocr_search_json" | json_eval "process.stdout.write(String((data.hits || []).some(hit => String(hit.type || '') === 'act')));")"
if [[ "$ocr_search_has_act_hit" != "true" ]]; then
  echo "Expected production search projection to return an act hit for the grounded act source text." >&2
  exit 1
fi

services_json="$(docker_compose_json_array "${compose_args[@]}")"
summary_path="$repo_root/output/smoke/docker-production-linux-summary.json"
mkdir -p "$(dirname "$summary_path")"

ACTS_JSON="$acts_json" \
SEARCH_JSON="$search_json" \
CAPABILITIES_JSON="$capabilities_json" \
COMPLETED_REPLAY_JSON="$completed_replay_json" \
COMPLETED_BACKFILL_JSON="$completed_backfill_json" \
COMPLETED_AI_TASK_JSON="$completed_ai_task_json" \
NOTIFICATION_DISPATCHES_JSON="$notification_dispatches_json" \
OCR_SEARCH_JSON="$ocr_search_json" \
SERVICES_JSON="$services_json" \
AI_MODEL="$ai_model" \
EMBEDDING_MODEL="$embedding_model" \
node -e "const acts=JSON.parse(process.env.ACTS_JSON); const search=JSON.parse(process.env.SEARCH_JSON); const capabilities=JSON.parse(process.env.CAPABILITIES_JSON); const replay=JSON.parse(process.env.COMPLETED_REPLAY_JSON); const backfill=JSON.parse(process.env.COMPLETED_BACKFILL_JSON); const aiTask=JSON.parse(process.env.COMPLETED_AI_TASK_JSON); const dispatch=JSON.parse(process.env.NOTIFICATION_DISPATCHES_JSON); const ocrSearch=JSON.parse(process.env.OCR_SEARCH_JSON); const services=JSON.parse(process.env.SERVICES_JSON); const summary={verifiedAtUtc:new Date().toISOString(), includeOpenSearch:true, ocrEnabled:Boolean(capabilities.ocrEnabled), actsCount:acts.length, searchHitCount:(search.hits||[]).length, ocrSearchHitCount:(ocrSearch.hits||[]).length, searchBackend:search.backend, capabilitiesBackend:(capabilities.search||{}).backend, replayStatus:replay.status, backfillStatus:backfill.status, aiTaskStatus:aiTask.status, model:aiTask.model||process.env.AI_MODEL, embeddingModel:process.env.EMBEDDING_MODEL, notificationDispatchCount:dispatch ? 1 : 0, services:Object.fromEntries(services.map(item => [item.Service, item.State]))}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "$summary_path"
