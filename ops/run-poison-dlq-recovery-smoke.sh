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
rabbit_user="lawwatcher"
rabbit_password="ChangeMe!123456"

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

cd "$repo_root"

tmp_dir="$(mktemp -d)"
project_name="lawwatcher-poison-dlq-$(random_suffix)"
env_file="${tmp_dir}/dev.env"
summary_path="${repo_root}/output/smoke/poison-dlq-recovery-summary.json"

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
ollama_host_port="$(get_free_port)"
worker_lite_health_port="$(get_free_port)"
worker_ai_health_port="$(get_free_port)"
worker_documents_health_port="$(get_free_port)"

write_env_file_from_example \
  "ops/env/dev.env.example" \
  "${env_file}" \
  "API_HOST_PORT=${api_port}" \
  "PORTAL_HOST_PORT=${portal_port}" \
  "SQLSERVER_HOST_PORT=${sql_port}" \
  "RABBITMQ_AMQP_PORT=${rabbit_amqp_port}" \
  "RABBITMQ_MANAGEMENT_PORT=${rabbit_management_port}" \
  "MINIO_API_PORT=${minio_api_port}" \
  "MINIO_CONSOLE_PORT=${minio_console_port}" \
  "OLLAMA_HOST_PORT=${ollama_host_port}" \
  "WORKER_LITE_HEALTH_PORT=${worker_lite_health_port}" \
  "WORKER_AI_HEALTH_PORT=${worker_ai_health_port}" \
  "WORKER_DOCUMENTS_HEALTH_PORT=${worker_documents_health_port}" \
  "LAWWATCHER__RUNTIME__CAPABILITIES__OCR=true" \
  "LAWWATCHER__BOOTSTRAP__ENABLEINITIALAPICLIENT=true" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTNAME=Portal Integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTIDENTIFIER=portal-integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTTOKEN=portal-integrator-demo-token" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTSCOPESCSV=integration:read,replays:write,backfills:write,ai:write,webhooks:write,profiles:write,subscriptions:write,api-clients:write"

export LAWWATCHER_COMPOSE_ENV_FILE="${env_file}"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  --env-file "${env_file}"
  --profile ai
)

if [[ "${build_local}" == "true" ]]; then
  compose_args+=(-f ops/compose/docker-compose.build.yml)
fi

cleanup() {
  docker "${compose_args[@]}" down --remove-orphans >/dev/null 2>&1 || true
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build --remove-orphans >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d --remove-orphans >/dev/null
fi

ensure_docker_ollama_model "${ai_model}" "${compose_args[@]}"
export LAWWATCHER_INTEGRATION_BEARER_TOKEN="portal-integrator-demo-token"

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/ready" >/dev/null
wait_http_body_contains "http://127.0.0.1:${worker_documents_health_port}/health/live" "Worker.Documents host is running." 60 "worker-documents live identity" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_documents_health_port}/health/ready" >/dev/null
echo "Waiting for seeded act OCR artifacts..." >&2
wait_default_seed_act_ocr_ready 120 "${compose_args[@]}"

management_base_url="http://127.0.0.1:${rabbit_management_port}/api"
poison_payload='not-json'
document_poison_queue_name="worker-documents-act-artifact-attached"
poison_queue_names=(
  "ai-enrichment-requested"
  "${document_poison_queue_name}"
  "document-text-extracted-ai-recovery"
)

for poison_queue_name in "${poison_queue_names[@]}"; do
  queue_json=""
  for ((attempt=0; attempt<60; attempt++)); do
    queue_json="$(curl -fsS -u "${rabbit_user}:${rabbit_password}" --max-time 10 "${management_base_url}/queues/%2F/${poison_queue_name}" 2>/dev/null || true)"
    if [[ -n "${queue_json}" ]]; then
      break
    fi
    sleep 2
  done

  if [[ -z "${queue_json:-}" ]]; then
    echo "RabbitMQ queue '${poison_queue_name}' did not become available." >&2
    exit 1
  fi

  publish_response="$(curl -fsS -u "${rabbit_user}:${rabbit_password}" -H "content-type: application/json" -X POST \
    "${management_base_url}/exchanges/%2F/amq.default/publish" \
    -d "$(node -e "process.stdout.write(JSON.stringify({properties:{content_type:'application/json'}, routing_key:process.argv[1], payload:process.argv[2], payload_encoding:'string'}));" "${poison_queue_name}" "${poison_payload}")")"

  publish_routed="$(printf '%s' "${publish_response}" | json_eval "process.stdout.write(String(Boolean(data.routed)));")"
  if [[ "${publish_routed}" != "true" ]]; then
    echo "Poison message was not routed to the broker queue '${poison_queue_name}'." >&2
    exit 1
  fi
done

messaging_json=""
ai_broker_ready="0"
document_broker_ready="0"
document_recovery_broker_ready="0"
for ((attempt=0; attempt<90; attempt++)); do
  messaging_json="$(curl -fsS --max-time 10 -H "Authorization: Bearer portal-integrator-demo-token" "http://127.0.0.1:${api_port}/v1/system/messaging" || true)"
  if [[ -n "${messaging_json}" ]]; then
    ai_broker_ready="$(MESSAGING_JSON="${messaging_json}" node -e "const data=JSON.parse(process.env.MESSAGING_JSON); const endpoint=(data.broker||{}).endpoints?.find(item => String(item.endpointName||'').includes('ai-enrichment-requested')); if (!endpoint) { process.stdout.write('0'); process.exit(0); } const count=Number(endpoint.faultCount||0)+Number(endpoint.deadLetterCount||0)+Number(endpoint.redeliveryCount||0); process.stdout.write(count > 0 ? '1' : '0');")"
    document_broker_ready="$(MESSAGING_JSON="${messaging_json}" DOCUMENT_QUEUE_NAME="${document_poison_queue_name}" node -e "const data=JSON.parse(process.env.MESSAGING_JSON); const endpoint=(data.broker||{}).endpoints?.find(item => String(item.endpointName||'')===process.env.DOCUMENT_QUEUE_NAME); if (!endpoint) { process.stdout.write('0'); process.exit(0); } const count=Number(endpoint.faultCount||0)+Number(endpoint.deadLetterCount||0)+Number(endpoint.redeliveryCount||0); process.stdout.write(count > 0 ? '1' : '0');")"
    document_recovery_broker_ready="$(MESSAGING_JSON="${messaging_json}" node -e "const data=JSON.parse(process.env.MESSAGING_JSON); const endpoint=(data.broker||{}).endpoints?.find(item => String(item.endpointName||'').includes('document-text-extracted-ai-recovery')); if (!endpoint) { process.stdout.write('0'); process.exit(0); } const count=Number(endpoint.faultCount||0)+Number(endpoint.deadLetterCount||0)+Number(endpoint.redeliveryCount||0); process.stdout.write(count > 0 ? '1' : '0');")"
    if [[ "${ai_broker_ready}" == "1" && "${document_broker_ready}" == "1" && "${document_recovery_broker_ready}" == "1" ]]; then
      break
    fi
  fi
  sleep 2
done

if [[ "${ai_broker_ready}" != "1" || "${document_broker_ready}" != "1" || "${document_recovery_broker_ready}" != "1" ]]; then
  echo "Poison messages did not produce fault, dead-letter or redelivery diagnostics for AI request, document OCR, and AI recovery queues in time." >&2
  exit 1
fi

acts_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
act_id="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
act_eli="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].eli));")"
ai_payload="$(ACT_ID="${act_id}" ACT_TITLE_JSON="${act_title}" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Podsumuj opublikowany akt po obsludze poison message.'}; process.stdout.write(JSON.stringify(payload));")"
ai_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${ai_payload}")"
ai_task_id="$(printf '%s' "${ai_accepted}" | json_eval "process.stdout.write(String(data.id));")"
completed_ai_task_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/ai/tasks" "${ai_task_id}" 320)"

citations_ok="$(printf '%s' "${completed_ai_task_json}" | ACT_ELI="${act_eli}" node -e "const fs=require('fs'); const item=JSON.parse(fs.readFileSync(0,'utf8')); const citations=[...new Set(item.citations||[])]; const hasEli=citations.includes(process.env.ACT_ELI); const hasDoc=citations.some(value => value.startsWith('document://legal-corpus/')); process.stdout.write(hasEli && hasDoc ? '1' : '0');")"
if [[ "${citations_ok}" != "1" ]]; then
  echo "Expected recovery AI task to include ELI and document citations after poison handling." >&2
  exit 1
fi

mkdir -p "$(dirname "${summary_path}")"

MESSAGING_JSON="${messaging_json}" \
COMPLETED_AI_TASK_JSON="${completed_ai_task_json}" \
DOCUMENT_POISON_QUEUE_NAME="${document_poison_queue_name}" \
node -e "const messaging=JSON.parse(process.env.MESSAGING_JSON); const endpoints=(messaging.broker||{}).endpoints||[]; const aiEndpoint=endpoints.find(item => String(item.endpointName||'').includes('ai-enrichment-requested')) ?? null; const documentEndpoint=endpoints.find(item => String(item.endpointName||'')===process.env.DOCUMENT_POISON_QUEUE_NAME) ?? null; const recoveryEndpoint=endpoints.find(item => String(item.endpointName||'').includes('document-text-extracted-ai-recovery')) ?? null; const aiTask=JSON.parse(process.env.COMPLETED_AI_TASK_JSON); const summary={verifiedAtUtc:new Date().toISOString(), poisonQueues:['ai-enrichment-requested',process.env.DOCUMENT_POISON_QUEUE_NAME,'document-text-extracted-ai-recovery'], aiFaultCount:Number(aiEndpoint?.faultCount ?? 0), aiDeadLetterCount:Number(aiEndpoint?.deadLetterCount ?? 0), aiRedeliveryCount:Number(aiEndpoint?.redeliveryCount ?? 0), documentFaultCount:Number(documentEndpoint?.faultCount ?? 0), documentDeadLetterCount:Number(documentEndpoint?.deadLetterCount ?? 0), documentRedeliveryCount:Number(documentEndpoint?.redeliveryCount ?? 0), recoveryFaultCount:Number(recoveryEndpoint?.faultCount ?? 0), recoveryDeadLetterCount:Number(recoveryEndpoint?.deadLetterCount ?? 0), recoveryRedeliveryCount:Number(recoveryEndpoint?.redeliveryCount ?? 0), recoveryTaskStatus:aiTask.status, recoveryTaskModel:aiTask.model}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
