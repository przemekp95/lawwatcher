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
project_name="lawwatcher-write-path-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
summary_path="${repo_root}/output/smoke/rabbitmq-write-path-nonblocking-summary.json"

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
worker_lite_health_port="$(get_free_port)"
worker_ai_health_port="$(get_free_port)"
ollama_host_port="$(get_free_port)"

write_env_file_from_example \
  "ops/env/dev-laptop.env.example" \
  "${env_file}" \
  "API_HOST_PORT=${api_port}" \
  "PORTAL_HOST_PORT=${portal_port}" \
  "SQLSERVER_HOST_PORT=${sql_port}" \
  "RABBITMQ_AMQP_PORT=${rabbit_amqp_port}" \
  "RABBITMQ_MANAGEMENT_PORT=${rabbit_management_port}" \
  "MINIO_API_PORT=${minio_api_port}" \
  "MINIO_CONSOLE_PORT=${minio_console_port}" \
  "WORKER_LITE_HEALTH_PORT=${worker_lite_health_port}" \
  "WORKER_AI_HEALTH_PORT=${worker_ai_health_port}" \
  "OLLAMA_HOST_PORT=${ollama_host_port}" \
  "WORKERS__LITE__MAXCONCURRENCY=1" \
  "WORKERS__AI__MAXCONCURRENCY=1" \
  "LAWWATCHER__SEEDDATA__ENABLEDEFAULTAPICLIENTSEED=true"

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
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/ready" >/dev/null
ensure_docker_ollama_model "${ai_model}" "${compose_args[@]}"

docker "${compose_args[@]}" stop worker-ai >/dev/null

acts_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
act_id="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
ai_payload="$(ACT_ID="${act_id}" ACT_TITLE_JSON="${act_title}" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Udowodnij, ze zapis write-path nie czeka na broker consumer.'}; process.stdout.write(JSON.stringify(payload));")"

accepted_body="${tmp_dir}/accepted.json"
curl_stats="$(curl -sS --max-time 10 -o "${accepted_body}" -w '%{http_code} %{time_total}' -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${ai_payload}")"
accepted_status="${curl_stats%% *}"
accepted_time="${curl_stats##* }"

if [[ "${accepted_status}" != "202" ]]; then
  echo "Expected accepted write-path response, got HTTP ${accepted_status}. Body: $(cat "${accepted_body}")" >&2
  exit 1
fi

ACCEPTED_TIME="${accepted_time}" node -e "const duration=Number(process.env.ACCEPTED_TIME); if (!Number.isFinite(duration) || duration >= 3) { console.error('Expected write-path response under 3 seconds, got ' + duration); process.exit(1); }"

task_id="$(cat "${accepted_body}" | json_eval "process.stdout.write(String(data.id));")"

queued_task_json=""
messaging_json=""
for ((attempt=0; attempt<90; attempt++)); do
  queued_task_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/ai/tasks" || true)"
  messaging_json="$(curl -fsS --max-time 10 -H "Authorization: Bearer portal-integrator-demo-token" "http://127.0.0.1:${api_port}/v1/system/messaging" || true)"
  if [[ -n "${queued_task_json}" && -n "${messaging_json}" ]]; then
    backlog_ready="$(TASK_ID="${task_id}" node -e "const fs=require('fs'); const tasks=JSON.parse(process.env.TASKS_JSON); const messaging=JSON.parse(process.env.MESSAGING_JSON); const task=(Array.isArray(tasks)?tasks:[]).find(item => String(item.id)===process.env.TASK_ID); const endpoints=((messaging.broker||{}).endpoints||[]); const aiEndpoint=endpoints.find(item => String(item.endpointName||'').includes('ai-enrichment-requested')); const ok=task && task.status==='queued' && aiEndpoint && Number(aiEndpoint.readyCount||0) > 0 && Number(aiEndpoint.consumerCount||0) === 0; process.stdout.write(ok ? '1' : '0');" TASKS_JSON="${queued_task_json}" MESSAGING_JSON="${messaging_json}")"
    if [[ "${backlog_ready}" == "1" ]]; then
      break
    fi
  fi
  sleep 2
done

if [[ "${backlog_ready:-0}" != "1" ]]; then
  echo "Broker backlog did not appear for the stopped worker-ai consumer." >&2
  exit 1
fi

docker "${compose_args[@]}" start worker-ai >/dev/null
wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/ready" >/dev/null
completed_task_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/ai/tasks" "${task_id}" 320)"

mkdir -p "$(dirname "${summary_path}")"

ACCEPTED_TIME="${accepted_time}" \
TASK_ID="${task_id}" \
QUEUED_TASK_JSON="${queued_task_json}" \
MESSAGING_JSON="${messaging_json}" \
COMPLETED_TASK_JSON="${completed_task_json}" \
node -e "const queued=JSON.parse(process.env.QUEUED_TASK_JSON).find(item => String(item.id)===process.env.TASK_ID); const messaging=JSON.parse(process.env.MESSAGING_JSON); const completed=JSON.parse(process.env.COMPLETED_TASK_JSON); const endpoint=((messaging.broker||{}).endpoints||[]).find(item => String(item.endpointName||'').includes('ai-enrichment-requested')); const summary={verifiedAtUtc:new Date().toISOString(), taskId:process.env.TASK_ID, acceptedLatencySeconds:Number(process.env.ACCEPTED_TIME), queuedStatus:queued?.status ?? null, brokerEndpoint:endpoint?.endpointName ?? null, brokerReadyCount:Number(endpoint?.readyCount ?? 0), brokerConsumerCount:Number(endpoint?.consumerCount ?? 0), completedStatus:completed.status}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
