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

cd "$repo_root"

tmp_dir="$(mktemp -d)"
project_name="lawwatcher-ai-minio-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
summary_path="${repo_root}/output/smoke/act-ai-grounding-minio-summary.json"

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
ollama_port="$(get_free_port)"
worker_lite_health_port="$(get_free_port)"
worker_ai_health_port="$(get_free_port)"

write_env_file_from_example \
  "ops/env/dev-laptop.env.example" \
  "$env_file" \
  "API_HOST_PORT=${api_port}" \
  "PORTAL_HOST_PORT=${portal_port}" \
  "SQLSERVER_HOST_PORT=${sql_port}" \
  "RABBITMQ_AMQP_PORT=${rabbit_amqp_port}" \
  "RABBITMQ_MANAGEMENT_PORT=${rabbit_management_port}" \
  "MINIO_API_PORT=${minio_api_port}" \
  "MINIO_CONSOLE_PORT=${minio_console_port}" \
  "OLLAMA_HOST_PORT=${ollama_port}" \
  "WORKER_LITE_HEALTH_PORT=${worker_lite_health_port}" \
  "WORKER_AI_HEALTH_PORT=${worker_ai_health_port}" \
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

mkdir -p "$(dirname "${summary_path}")"

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

ensure_docker_ollama_model "${ai_model}" "${compose_args[@]}"

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null
minio_health_status="$(wait_http_status "http://127.0.0.1:${minio_api_port}/minio/health/live")"
acts_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
act_id="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
act_eli="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].eli));")"
payload="$(ACT_ID="${act_id}" ACT_TITLE_JSON="${act_title}" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Podsumuj opublikowany akt i uwzglednij material zrodlowy.'}; process.stdout.write(JSON.stringify(payload));")"
accepted_json="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" -H "Authorization: Bearer portal-integrator-demo-token" -H "Content-Type: application/json" -d "${payload}")"
task_id="$(printf '%s' "${accepted_json}" | json_eval "process.stdout.write(String(data.id));")"
completed_task_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/ai/tasks" "${task_id}" 320)"

citations_ok="$(printf '%s' "${completed_task_json}" | ACT_ELI="${act_eli}" node -e "const fs=require('fs'); const item=JSON.parse(fs.readFileSync(0,'utf8')); const citations=[...new Set(item.citations||[])]; const hasEli=citations.includes(process.env.ACT_ELI); const hasDoc=citations.some(value => value.startsWith('document://legal-corpus/')); process.stdout.write(hasEli && hasDoc ? '1' : '0');")"
if [[ "${citations_ok}" != "1" ]]; then
  echo "Expected MinIO-backed AI task to include ELI and document://legal-corpus citations." >&2
  exit 1
fi

minio_object_count="$(docker "${compose_args[@]}" exec -T minio sh -lc "ls -R /data 2>/dev/null | wc -l" | tr -d '[:space:]')"
if [[ -z "${minio_object_count}" || "${minio_object_count}" -le 0 ]]; then
  echo "Expected MinIO container to contain stored objects after grounding smoke." >&2
  exit 1
fi

MINIO_HEALTH_STATUS="${minio_health_status}" \
COMPLETED_TASK_JSON="${completed_task_json}" \
MINIO_OBJECT_COUNT="${minio_object_count}" \
ACT_ELI="${act_eli}" \
node -e "const task=JSON.parse(process.env.COMPLETED_TASK_JSON); const summary={verifiedAtUtc:new Date().toISOString(), taskId:task.id, subjectType:task.subjectType, model:task.model, citations:[...new Set(task.citations||[])], containsEliCitation:(task.citations||[]).includes(process.env.ACT_ELI), containsDocumentCitation:(task.citations||[]).some(value => value.startsWith('document://legal-corpus/')), minioHealthStatusCode:Number(process.env.MINIO_HEALTH_STATUS), minioObjectCount:Number(process.env.MINIO_OBJECT_COUNT)}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
