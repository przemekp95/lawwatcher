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

while (($# > 0)); do
  case "$1" in
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

compose_args=(
  compose
  -f ops/compose/docker-compose.yml
  --env-file ops/env/dev-laptop.env.example
)

if [[ "$build_local" == "true" ]]; then
  compose_args+=(-f ops/compose/docker-compose.build.yml)
fi

if [[ "$include_ai" == "true" ]]; then
  compose_args+=(--profile ai)
fi

cleanup() {
  docker "${compose_args[@]}" down --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

if [[ "$build_local" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

api_health="$(wait_http_ok "http://127.0.0.1:8080/health/ready")"
portal_root="$(wait_http_ok "http://127.0.0.1:8081/")"
portal_admin="$(wait_http_ok "http://127.0.0.1:8081/admin")"
acts_json="$(curl -fsS --max-time 10 "http://127.0.0.1:8080/v1/acts")"
search_json="$(wait_search_projection "http://127.0.0.1:8080/v1/search?q=VAT")"

completed_ai_task_json='null'
verified_ai_model='null'

if [[ "$include_ai" == "true" ]]; then
  ensure_docker_ollama_model "$ai_model" "${compose_args[@]}"
  verified_ai_model="$(node -e "process.stdout.write(JSON.stringify(process.argv[1]));" "$ai_model")"

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
fi

services_json="$(docker_compose_json_array "${compose_args[@]}")"

summary_path="$repo_root/output/smoke/docker-dev-laptop-linux-summary.json"
mkdir -p "$(dirname "$summary_path")"

INCLUDE_AI="$include_ai" \
API_HEALTH="$api_health" \
PORTAL_ROOT="$portal_root" \
PORTAL_ADMIN="$portal_admin" \
ACTS_JSON="$acts_json" \
SEARCH_JSON="$search_json" \
SERVICES_JSON="$services_json" \
COMPLETED_AI_TASK_JSON="$completed_ai_task_json" \
VERIFIED_AI_MODEL="$verified_ai_model" \
node -e "const acts=JSON.parse(process.env.ACTS_JSON); const search=JSON.parse(process.env.SEARCH_JSON); const services=JSON.parse(process.env.SERVICES_JSON); const includeAi=process.env.INCLUDE_AI.toLowerCase()==='true'; const task=process.env.COMPLETED_AI_TASK_JSON==='null'?null:JSON.parse(process.env.COMPLETED_AI_TASK_JSON); const verifiedModel=process.env.VERIFIED_AI_MODEL==='null'?null:JSON.parse(process.env.VERIFIED_AI_MODEL); const summary={verifiedAtUtc:new Date().toISOString(), includeAi, apiHealthStatusCode:200, portalRootStatusCode:200, portalAdminStatusCode:200, actsCount:acts.length, searchHitCount:(search.hits||[]).length, verifiedAiModel:verifiedModel, completedAiTaskId:task?task.id:null, completedAiTaskStatus:task?task.status:null, services:Object.fromEntries(services.map(item => [item.Service, item.State]))}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "$summary_path"
