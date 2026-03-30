#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker
require_cmd curl
require_cmd node

include_ai=false
build_local=false
ai_model="llama3.2:1b"
env_example="ops/env/dev.env.example"

while (($# > 0)); do
  case "$1" in
    --env-file)
      env_example="$2"
      shift 2
      ;;
    --include-ai)
      include_ai=true
      shift
      ;;
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
env_file="${tmp_dir}/dev.env"
project_name="lawwatcher-dev-$(random_suffix)"
worker_documents_health_port="$(get_free_port)"
ollama_host_port="$(get_free_port)"
ocr_capability="false"

if [[ "$include_ai" == "true" ]]; then
  ocr_capability="true"
fi

write_env_file_from_example \
  "$env_example" \
  "${env_file}" \
  "LAWWATCHER__BOOTSTRAP__ENABLEINITIALAPICLIENT=true" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTNAME=Portal Integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTIDENTIFIER=portal-integrator" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTTOKEN=portal-integrator-demo-token" \
  "LAWWATCHER__BOOTSTRAP__INITIALAPICLIENTSCOPESCSV=integration:read,replays:write,backfills:write,ai:write,webhooks:write,profiles:write,subscriptions:write,api-clients:write" \
  "WORKER_DOCUMENTS_HEALTH_PORT=${worker_documents_health_port}" \
  "OLLAMA_HOST_PORT=${ollama_host_port}" \
  "LAWWATCHER__RUNTIME__CAPABILITIES__OCR=${ocr_capability}"

export LAWWATCHER_COMPOSE_ENV_FILE="${env_file}"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  --env-file "${env_file}"
)

if [[ "$build_local" == "true" ]]; then
  compose_args+=(-f ops/compose/docker-compose.build.yml)
fi

if [[ "$include_ai" == "true" ]]; then
  compose_args+=(--profile ai)
fi

cleanup() {
  docker "${compose_args[@]}" down --remove-orphans >/dev/null 2>&1 || true
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

if [[ "$build_local" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

export LAWWATCHER_INTEGRATION_BEARER_TOKEN="portal-integrator-demo-token"

api_health="$(wait_http_ok "http://127.0.0.1:8080/health/ready")"
portal_health="$(wait_http_ok "http://127.0.0.1:8081/health/ready")"
portal_root="$(wait_http_ok "http://127.0.0.1:8081/")"
portal_admin="$(wait_http_ok "http://127.0.0.1:8081/admin")"
acts_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:8080/v1/acts")"
search_json="$(wait_search_projection "http://127.0.0.1:8080/v1/search?q=VAT")"
capabilities_json="$(curl_with_optional_bearer -fsS --max-time 10 "http://127.0.0.1:8080/v1/system/capabilities")"
curl -fsS --max-time 10 "http://127.0.0.1:8080/openapi/integration-v1.json" >/dev/null

completed_ai_task_json='null'
verified_ai_model='null'
ocr_search_json='null'

if [[ "$include_ai" == "true" ]]; then
  ensure_docker_ollama_model "$ai_model" "${compose_args[@]}"
  verified_ai_model="$(node -e "process.stdout.write(JSON.stringify(process.argv[1]));" "$ai_model")"
  wait_http_body_contains "http://127.0.0.1:${worker_documents_health_port}/health/live" "Worker.Documents host is running." 60 "worker-documents live identity" >/dev/null
  wait_http_ok "http://127.0.0.1:${worker_documents_health_port}/health/ready" >/dev/null

  ocr_enabled="$(printf '%s' "$capabilities_json" | json_eval "process.stdout.write(String(Boolean(data.ocrEnabled)));")"
  if [[ "$ocr_enabled" != "true" ]]; then
    echo "Expected OCR capability to be enabled in dev AI smoke." >&2
    exit 1
  fi

  echo "Waiting for seeded act OCR artifacts..." >&2
  wait_default_seed_act_ocr_ready 120 "${compose_args[@]}"

  act_id="$(printf '%s' "$acts_json" | json_eval "process.stdout.write(String(data[0].id));")"
  act_title="$(printf '%s' "$acts_json" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
  act_eli="$(printf '%s' "$acts_json" | json_eval "process.stdout.write(String(data[0].eli));")"
  ai_payload="$(ACT_ID="$act_id" ACT_TITLE_JSON="$act_title" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Podsumuj opublikowany akt i uwzglednij material zrodlowy.'}; process.stdout.write(JSON.stringify(payload));")"
  accepted_ai="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:8080/v1/ai/tasks" \
    -H "Authorization: Bearer portal-integrator-demo-token" \
    -H "Content-Type: application/json" \
    -d "$ai_payload")"
  ai_task_id="$(printf '%s' "$accepted_ai" | json_eval "process.stdout.write(String(data.id));")"
  completed_ai_task_json="$(wait_entity_completed "http://127.0.0.1:8080/v1/ai/tasks" "$ai_task_id" 320)"

  citations_ok="$(printf '%s' "$completed_ai_task_json" | ACT_ELI="$act_eli" node -e "const fs=require('fs'); const item=JSON.parse(fs.readFileSync(0,'utf8')); const citations=[...new Set(item.citations||[])]; const hasEli=citations.includes(process.env.ACT_ELI); const hasDoc=citations.some(value => value.startsWith('document://legal-corpus/')); process.stdout.write(hasEli && hasDoc ? '1' : '0');")"
  if [[ "$citations_ok" != "1" ]]; then
    echo "Expected dockerized AI task to include ELI and document://legal-corpus citations." >&2
    exit 1
  fi

  ocr_search_json="$(wait_search_projection "http://127.0.0.1:8080/v1/search?q=1%20kwietnia%202026")"
  ocr_search_has_act_hit="$(printf '%s' "$ocr_search_json" | json_eval "process.stdout.write(String((data.hits || []).some(hit => String(hit.type || '') === 'act')));")"
  if [[ "$ocr_search_has_act_hit" != "true" ]]; then
    echo "Expected dockerized OCR/document flow to refresh the search projection with an act hit for the grounded act source text." >&2
    exit 1
  fi
fi

services_json="$(docker_compose_json_array "${compose_args[@]}")"
summary_path="$repo_root/output/smoke/docker-dev-linux-summary.json"
mkdir -p "$(dirname "$summary_path")"

INCLUDE_AI="$include_ai" \
API_HEALTH="$api_health" \
PORTAL_ROOT="$portal_root" \
PORTAL_ADMIN="$portal_admin" \
ACTS_JSON="$acts_json" \
SEARCH_JSON="$search_json" \
CAPABILITIES_JSON="$capabilities_json" \
SERVICES_JSON="$services_json" \
COMPLETED_AI_TASK_JSON="$completed_ai_task_json" \
OCR_SEARCH_JSON="$ocr_search_json" \
VERIFIED_AI_MODEL="$verified_ai_model" \
node -e "const acts=JSON.parse(process.env.ACTS_JSON); const search=JSON.parse(process.env.SEARCH_JSON); const capabilities=JSON.parse(process.env.CAPABILITIES_JSON); const services=JSON.parse(process.env.SERVICES_JSON); const includeAi=process.env.INCLUDE_AI.toLowerCase()==='true'; const task=process.env.COMPLETED_AI_TASK_JSON==='null'?null:JSON.parse(process.env.COMPLETED_AI_TASK_JSON); const ocrSearch=process.env.OCR_SEARCH_JSON==='null'?null:JSON.parse(process.env.OCR_SEARCH_JSON); const verifiedModel=process.env.VERIFIED_AI_MODEL==='null'?null:JSON.parse(process.env.VERIFIED_AI_MODEL); const summary={verifiedAtUtc:new Date().toISOString(), includeAi, apiHealthStatusCode:200, portalRootStatusCode:200, portalAdminStatusCode:200, actsCount:acts.length, searchHitCount:(search.hits||[]).length, ocrSearchHitCount:ocrSearch?(ocrSearch.hits||[]).length:null, ocrEnabled:Boolean(capabilities.ocrEnabled), verifiedAiModel:verifiedModel, completedAiTaskId:task?task.id:null, completedAiTaskStatus:task?task.status:null, services:Object.fromEntries(services.map(item => [item.Service, item.State]))}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "$summary_path"
