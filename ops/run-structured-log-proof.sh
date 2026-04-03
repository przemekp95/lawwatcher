#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker
require_cmd curl
require_cmd node

build_local=false
ai_model="llama3.2:1b"
signing_secret="structured-log-proof-secret"

while (($# > 0)); do
  case "$1" in
    --build-local)
      build_local=true
      shift
      ;;
    --ai-model)
      ai_model="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

cd "${repo_root}"

tmp_dir="$(mktemp -d)"
project_name="lawwatcher-structured-logs-$(random_suffix)"
env_file="${tmp_dir}/production.env"
summary_path="${repo_root}/output/smoke/structured-log-proof-summary.json"

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

write_env_file_from_example \
  "ops/env/production.env.example" \
  "${env_file}" \
  "SQLSERVER_SA_PASSWORD=StructuredSqlServer!12345" \
  "RABBITMQ_DEFAULT_PASS=StructuredRabbitMq!12345" \
  "MINIO_ROOT_PASSWORD=StructuredMinio!12345" \
  "CONNECTIONSTRINGS__LAWWATCHERSQLSERVER=Server=sqlserver,1433;Database=LawWatcher;User Id=sa;Password=StructuredSqlServer!12345;TrustServerCertificate=True;Encrypt=False" \
  "CONNECTIONSTRINGS__RABBITMQ=amqp://lawwatcher:StructuredRabbitMq!12345@rabbitmq:5672/" \
  "STORAGE__MINIO__SECRETKEY=StructuredMinio!12345" \
  "LAWWATCHER__BOOTSTRAP__SECRET=StructuredBootstrapSecret!12345" \
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
  "WORKERS__DOCUMENTS__MAXCONCURRENCY=1" \
  "WORKERS__NOTIFICATIONS__MAXCONCURRENCY=1" \
  "WORKERS__REPLAY__MAXCONCURRENCY=1" \
  "WORKERS__PROJECTION__MAXCONCURRENCY=1" \
  "ENABLE_OPENSEARCH=true" \
  "LAWWATCHER__BOOTSTRAP__ENABLEDEMODATA=true" \
  "LAWWATCHER__BOOTSTRAP__ENABLEINITIALAPICLIENT=true" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTNAME=Portal Integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTIDENTIFIER=portal-integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTTOKEN=portal-integrator-demo-token" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTSCOPESCSV=integration:read,replays:write,backfills:write,ai:write,webhooks:write,profiles:write,subscriptions:write,api-clients:write" \
  "LAWWATCHER__WEBHOOKS__BACKEND=SignedHttp" \
  "LAWWATCHER__WEBHOOKS__SIGNINGSECRET=${signing_secret}"

export LAWWATCHER_COMPOSE_ENV_FILE="${env_file}"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  -f ops/compose/docker-compose.production.yml
  --env-file "${env_file}"
  --profile production
  --profile opensearch
)

if [[ "${build_local}" == "true" ]]; then
  compose_args+=(
    -f ops/compose/docker-compose.build.yml
    -f ops/compose/docker-compose.production.build.yml
  )
fi

cleanup() {
  docker "${compose_args[@]}" down --remove-orphans >/dev/null 2>&1 || true
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

if [[ "${build_local}" != "true" ]]; then
  pull_compose_images_or_use_local "${compose_args[@]}"
fi

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build opensearch ollama >/dev/null
else
  docker "${compose_args[@]}" up -d opensearch ollama >/dev/null
fi

wait_http_ok "http://127.0.0.1:9200/_cluster/health?wait_for_status=yellow&timeout=1s" >/dev/null
ensure_docker_ollama_model "${ai_model}" "${compose_args[@]}"
export LAWWATCHER_INTEGRATION_BEARER_TOKEN="portal-integrator-demo-token"

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build --remove-orphans >/dev/null
else
  docker "${compose_args[@]}" up -d --remove-orphans >/dev/null
fi

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_projection_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_notifications_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_replay_health_port}/health/ready" >/dev/null
wait_http_body_contains "http://127.0.0.1:${worker_documents_health_port}/health/live" "Worker.Documents host is running." 60 "worker-documents live identity" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_documents_health_port}/health/ready" >/dev/null
echo "Waiting for seeded act OCR artifacts..." >&2
wait_default_seed_act_ocr_ready 120 "${compose_args[@]}"

alerts_ready="false"
for ((attempt=0; attempt<120; attempt++)); do
  alerts_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/alerts" 2>/dev/null || true)"
  if [[ -n "${alerts_json}" ]]; then
    alerts_ready="$(printf '%s' "${alerts_json}" | json_eval "process.stdout.write(String(Array.isArray(data) && data.length > 0));")"
    if [[ "${alerts_ready}" == "true" ]]; then
      break
    fi
  fi
  sleep 2
done

if [[ "${alerts_ready}" != "true" ]]; then
  echo "Expected seeded alerts to be available before profile-subscription catch-up logging." >&2
  exit 1
fi

acts_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
profiles_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/profiles")"
act_id="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
profile_id="$(printf '%s' "${profiles_json}" | json_eval "process.stdout.write(String(data[0].id));")"
unique_suffix="$(node -e "process.stdout.write(require('crypto').randomUUID().replace(/-/g,'').slice(0,8));")"

subscription_payload="$(PROFILE_ID="${profile_id}" RECIPIENT="structured-${unique_suffix}@example.test" node -e "const payload={profileId:process.env.PROFILE_ID, subscriber:process.env.RECIPIENT, channel:'email', alertPolicy:'immediate', digestIntervalMinutes:null}; process.stdout.write(JSON.stringify(payload));")"
curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/subscriptions" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${subscription_payload}" >/dev/null

ai_payload="$(ACT_ID="${act_id}" ACT_TITLE_JSON="${act_title}" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Udowodnij marker flow=ai w logach strukturalnych.'}; process.stdout.write(JSON.stringify(payload));")"
ai_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${ai_payload}")"
ai_task_id="$(printf '%s' "${ai_accepted}" | json_eval "process.stdout.write(String(data.id));")"

replay_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/replays" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d '{"scope":"structured-log-proof-replay"}')"
replay_id="$(printf '%s' "${replay_accepted}" | json_eval "process.stdout.write(String(data.id));")"
completed_replay_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/replays" "${replay_id}")"

backfill_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/backfills" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d '{"source":"sejm","scope":"structured-log-proof-backfill","requestedFrom":"2026-01-01","requestedTo":"2026-03-31"}')"
backfill_id="$(printf '%s' "${backfill_accepted}" | json_eval "process.stdout.write(String(data.id));")"
completed_backfill_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/backfills" "${backfill_id}")"

profile_payload="$(node -e "const suffix=Date.now().toString(36); const payload={name:'Structured Log Proof ' + suffix, alertPolicy:'immediate', digestIntervalMinutes:null, keywords:['vat']}; process.stdout.write(JSON.stringify(payload));")"
curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/profiles" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${profile_payload}" >/dev/null

wait_compose_logs_match "flow=ai" 90 "${compose_args[@]}" -- api worker-ai >/dev/null
wait_compose_logs_match "flow=document-ocr" 90 "${compose_args[@]}" -- worker-documents >/dev/null
wait_compose_logs_match "flow=document-text-projection" 90 "${compose_args[@]}" -- worker-projection >/dev/null
wait_compose_logs_match "flow=replay" 90 "${compose_args[@]}" -- api worker-replay >/dev/null
wait_compose_logs_match "flow=backfill" 90 "${compose_args[@]}" -- api worker-replay >/dev/null
wait_compose_logs_match "flow=profile-subscription" 90 "${compose_args[@]}" -- api worker-notifications >/dev/null

ai_task_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/ai/tasks" | AI_TASK_ID="${ai_task_id}" node -e "const fs=require('fs'); const data=JSON.parse(fs.readFileSync(0,'utf8')); const task=(Array.isArray(data)?data:[]).find(item => String(item.id)===process.env.AI_TASK_ID); if (!task) process.exit(1); process.stdout.write(JSON.stringify(task));")" || {
  echo "Expected structured-log proof AI task '${ai_task_id}' to remain queryable after the flow=ai marker." >&2
  curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/ai/tasks" >&2 || true
  exit 1
}

mkdir -p "$(dirname "${summary_path}")"

AI_TASK_JSON="${ai_task_json}" \
COMPLETED_REPLAY_JSON="${completed_replay_json}" \
COMPLETED_BACKFILL_JSON="${completed_backfill_json}" \
node -e "const aiTask=JSON.parse(process.env.AI_TASK_JSON); const replay=JSON.parse(process.env.COMPLETED_REPLAY_JSON); const backfill=JSON.parse(process.env.COMPLETED_BACKFILL_JSON); const summary={verifiedAtUtc:new Date().toISOString(), aiTaskStatus:aiTask.status, replayStatus:replay.status, backfillStatus:backfill.status, verifiedFlows:['ai','document-ocr','document-text-projection','replay','backfill','profile-subscription']}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
