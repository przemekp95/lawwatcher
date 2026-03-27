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
project_name="lawwatcher-health-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
summary_path="${repo_root}/output/health/host-health-summary.json"

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
  "WORKER_AI_HEALTH_PORT=${worker_ai_health_port}"

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

api_live="$(wait_http_ok "http://127.0.0.1:${api_port}/health/live")"
api_ready="$(wait_http_ok "http://127.0.0.1:${api_port}/health/ready")"
worker_lite_live="$(wait_http_ok "http://127.0.0.1:${worker_lite_health_port}/health/live")"
worker_lite_ready="$(wait_http_ok "http://127.0.0.1:${worker_lite_health_port}/health/ready")"
worker_ai_live="$(wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/live")"
worker_ai_ready="$(wait_http_ok "http://127.0.0.1:${worker_ai_health_port}/health/ready")"
capabilities_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/system/capabilities")"
services_json="$(docker_compose_json_array "${compose_args[@]}")"

API_LIVE="${api_live}" \
API_READY="${api_ready}" \
WORKER_LITE_LIVE="${worker_lite_live}" \
WORKER_LITE_READY="${worker_lite_ready}" \
WORKER_AI_LIVE="${worker_ai_live}" \
WORKER_AI_READY="${worker_ai_ready}" \
CAPABILITIES_JSON="${capabilities_json}" \
SERVICES_JSON="${services_json}" \
API_PORT="${api_port}" \
WORKER_LITE_PORT="${worker_lite_health_port}" \
WORKER_AI_PORT="${worker_ai_health_port}" \
node -e "const apiLive=JSON.parse(process.env.API_LIVE); const apiReady=JSON.parse(process.env.API_READY); const workerLiteLive=JSON.parse(process.env.WORKER_LITE_LIVE); const workerLiteReady=JSON.parse(process.env.WORKER_LITE_READY); const workerAiLive=JSON.parse(process.env.WORKER_AI_LIVE); const workerAiReady=JSON.parse(process.env.WORKER_AI_READY); const capabilities=JSON.parse(process.env.CAPABILITIES_JSON); const services=JSON.parse(process.env.SERVICES_JSON); const summary={verifiedAtUtc:new Date().toISOString(), api:{baseUrl:'http://127.0.0.1:' + process.env.API_PORT, liveStatus:apiLive.status, readyStatus:apiReady.status, readyEntries:Object.keys(apiReady.entries||{})}, workerLite:{baseUrl:'http://127.0.0.1:' + process.env.WORKER_LITE_PORT, liveStatus:workerLiteLive.status, readyStatus:workerLiteReady.status, readyEntries:Object.keys(workerLiteReady.entries||{})}, workerAi:{baseUrl:'http://127.0.0.1:' + process.env.WORKER_AI_PORT, liveStatus:workerAiLive.status, readyStatus:workerAiReady.status, readyEntries:Object.keys(workerAiReady.entries||{})}, apiCapabilitiesProfile:capabilities.runtimeProfile, services:Object.fromEntries(services.map(item => [item.Service, item.State]))}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
