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
env_file="${tmp_dir}/dev.env"
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
worker_documents_health_port="$(get_free_port)"

write_env_file_from_example \
  "ops/env/dev.env.example" \
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

mkdir -p "$(dirname "${summary_path}")"

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

ensure_docker_ollama_model "${ai_model}" "${compose_args[@]}"
export LAWWATCHER_INTEGRATION_BEARER_TOKEN="portal-integrator-demo-token"

echo "Waiting for api readiness on port ${api_port}..." >&2
wait_http_ok "http://127.0.0.1:${api_port}/health/ready" 120 "api readiness" >/dev/null
echo "Waiting for worker-documents live identity on port ${worker_documents_health_port}..." >&2
wait_http_body_contains "http://127.0.0.1:${worker_documents_health_port}/health/live" "Worker.Documents host is running." 60 "worker-documents live identity" >/dev/null
echo "Waiting for worker-documents readiness on port ${worker_documents_health_port}..." >&2
wait_http_ok "http://127.0.0.1:${worker_documents_health_port}/health/ready" 120 "worker-documents readiness" >/dev/null
echo "Waiting for MinIO readiness on port ${minio_api_port}..." >&2
minio_health_status="$(wait_http_status "http://127.0.0.1:${minio_api_port}/minio/health/live")"
echo "Waiting for seeded act OCR artifacts..." >&2
wait_default_seed_act_ocr_ready 120 "${compose_args[@]}"
acts_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
act_id="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
act_eli="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].eli));")"
payload="$(ACT_ID="${act_id}" ACT_TITLE_JSON="${act_title}" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Podsumuj opublikowany akt i uwzglednij material zrodlowy.'}; process.stdout.write(JSON.stringify(payload));")"
accepted_json="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" -H "Authorization: Bearer portal-integrator-demo-token" -H "Content-Type: application/json" -d "${payload}")"
task_id="$(printf '%s' "${accepted_json}" | json_eval "process.stdout.write(String(data.id));")"
echo "Waiting for AI task ${task_id} to complete..." >&2
completed_task_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/ai/tasks" "${task_id}" 320)"

citations_ok="$(printf '%s' "${completed_task_json}" | ACT_ELI="${act_eli}" node -e "const fs=require('fs'); const item=JSON.parse(fs.readFileSync(0,'utf8')); const citations=[...new Set(item.citations||[])]; const hasEli=citations.includes(process.env.ACT_ELI); const hasDoc=citations.some(value => value.startsWith('document://legal-corpus/')); process.stdout.write(hasEli && hasDoc ? '1' : '0');")"
if [[ "${citations_ok}" != "1" ]]; then
  echo "Expected MinIO-backed AI task to include ELI and document://legal-corpus citations." >&2
  exit 1
fi

echo "Waiting for OCR-derived search projection..." >&2
search_json="$(wait_search_projection "http://127.0.0.1:${api_port}/v1/search?q=1%20kwietnia%202026")"
search_has_act_hit="$(printf '%s' "${search_json}" | json_eval "process.stdout.write(String((data.hits || []).some(hit => String(hit.type || '') === 'act')));")"
if [[ "${search_has_act_hit}" != "true" ]]; then
  echo "Expected OCR-derived search projection to return an act hit for a phrase unique to the grounded act source text." >&2
  exit 1
fi

minio_object_count="$(docker "${compose_args[@]}" exec -T minio sh -lc "ls -R /data 2>/dev/null | wc -l" | tr -d '[:space:]')"
if [[ -z "${minio_object_count}" || "${minio_object_count}" -le 0 ]]; then
  echo "Expected MinIO container to contain stored objects after grounding smoke." >&2
  exit 1
fi

minio_derived_object_count="$(docker "${compose_args[@]}" exec -T minio sh -lc "count=0; for path in /data/document-artifacts/*/*/*/*/*/*.extracted.txt/xl.meta /data/document-artifacts/*/*/*/*/*/*/*/*.extracted.txt/xl.meta; do [ -e \"\$path\" ] && count=\$((count+1)); done; printf '%s' \"\$count\"" | tr -d '[:space:]')"
if [[ -z "${minio_derived_object_count}" || "${minio_derived_object_count}" -le 0 ]]; then
  echo "Expected worker-documents to persist derived OCR text artifacts in the document-artifacts bucket." >&2
  exit 1
fi

MINIO_HEALTH_STATUS="${minio_health_status}" \
COMPLETED_TASK_JSON="${completed_task_json}" \
MINIO_OBJECT_COUNT="${minio_object_count}" \
MINIO_DERIVED_OBJECT_COUNT="${minio_derived_object_count}" \
SEARCH_JSON="${search_json}" \
ACT_ELI="${act_eli}" \
node -e "const task=JSON.parse(process.env.COMPLETED_TASK_JSON); const search=JSON.parse(process.env.SEARCH_JSON); const summary={verifiedAtUtc:new Date().toISOString(), taskId:task.id, subjectType:task.subjectType, model:task.model, citations:[...new Set(task.citations||[])], containsEliCitation:(task.citations||[]).includes(process.env.ACT_ELI), containsDocumentCitation:(task.citations||[]).some(value => value.startsWith('document://legal-corpus/')), minioHealthStatusCode:Number(process.env.MINIO_HEALTH_STATUS), minioObjectCount:Number(process.env.MINIO_OBJECT_COUNT), minioDerivedObjectCount:Number(process.env.MINIO_DERIVED_OBJECT_COUNT), ocrSearchHitCount:(search.hits||[]).length}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
