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
env_file="${tmp_dir}/full-host.env"
listener_script="${tmp_dir}/listener.js"
listener_log="${tmp_dir}/listener.log"
capture_path="${tmp_dir}/captures.json"
summary_path="${repo_root}/output/smoke/structured-log-proof-summary.json"

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
ollama_host_port="$(get_free_port)"
worker_projection_health_port="$(get_free_port)"
worker_notifications_health_port="$(get_free_port)"
worker_replay_health_port="$(get_free_port)"
worker_documents_health_port="$(get_free_port)"
listener_port="$(get_free_port)"

write_env_file_from_example \
  "ops/env/full-host.env.example" \
  "${env_file}" \
  "API_HOST_PORT=${api_port}" \
  "PORTAL_HOST_PORT=${portal_port}" \
  "SQLSERVER_HOST_PORT=${sql_port}" \
  "RABBITMQ_AMQP_PORT=${rabbit_amqp_port}" \
  "RABBITMQ_MANAGEMENT_PORT=${rabbit_management_port}" \
  "MINIO_API_PORT=${minio_api_port}" \
  "MINIO_CONSOLE_PORT=${minio_console_port}" \
  "OLLAMA_HOST_PORT=${ollama_host_port}" \
  "WORKER_PROJECTION_HEALTH_PORT=${worker_projection_health_port}" \
  "WORKER_NOTIFICATIONS_HEALTH_PORT=${worker_notifications_health_port}" \
  "WORKER_REPLAY_HEALTH_PORT=${worker_replay_health_port}" \
  "WORKER_DOCUMENTS_HEALTH_PORT=${worker_documents_health_port}" \
  "WORKERS__DOCUMENTS__MAXCONCURRENCY=1" \
  "WORKERS__NOTIFICATIONS__MAXCONCURRENCY=1" \
  "WORKERS__REPLAY__MAXCONCURRENCY=1" \
  "WORKERS__PROJECTION__MAXCONCURRENCY=1" \
  "LAWWATCHER__SEEDDATA__ENABLEWEBHOOKSUBSCRIPTIONSEED=false" \
  "LAWWATCHER__WEBHOOKS__BACKEND=SignedHttp" \
  "LAWWATCHER__WEBHOOKS__SIGNINGSECRET=${signing_secret}"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  -f ops/compose/docker-compose.full-host.yml
  --env-file "${env_file}"
  --profile full-host
)

if [[ "${build_local}" == "true" ]]; then
  compose_args+=(
    -f ops/compose/docker-compose.build.yml
    -f ops/compose/docker-compose.full-host.build.yml
  )
fi

cleanup() {
  if [[ -n "${listener_pid:-}" ]] && kill -0 "${listener_pid}" >/dev/null 2>&1; then
    kill "${listener_pid}" >/dev/null 2>&1 || true
    wait "${listener_pid}" >/dev/null 2>&1 || true
  fi
  docker "${compose_args[@]}" down --remove-orphans >/dev/null 2>&1 || true
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

cat > "${listener_script}" <<'EOF'
const fs = require('fs');
const http = require('http');

const port = Number(process.env.LISTENER_PORT);
const capturePath = process.env.CAPTURE_PATH;
const captures = [];

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on('data', chunk => chunks.push(chunk));
  req.on('end', () => {
    captures.push({
      method: req.method,
      path: req.url,
      headers: req.headers,
      body: Buffer.concat(chunks).toString('utf8'),
      receivedAtUtc: new Date().toISOString()
    });
    fs.writeFileSync(capturePath, JSON.stringify(captures, null, 2));
    res.statusCode = 200;
    res.setHeader('content-type', 'application/json');
    res.end('{"received":true}');
    setTimeout(() => server.close(() => process.exit(0)), 100);
  });
});

server.listen(port, '0.0.0.0');
setTimeout(() => {
  console.error('Timed out waiting for signed webhook dispatch.');
  server.close(() => process.exit(1));
}, 120000);
EOF

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build --remove-orphans >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d --remove-orphans >/dev/null
fi

ensure_docker_ollama_model "${ai_model}" "${compose_args[@]}"

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_projection_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_notifications_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_replay_health_port}/health/ready" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_documents_health_port}/health/ready" >/dev/null

LISTENER_PORT="${listener_port}" CAPTURE_PATH="${capture_path}" node "${listener_script}" >"${listener_log}" 2>&1 &
listener_pid=$!

acts_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/acts")"
profiles_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/profiles")"
act_id="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(String(data[0].id));")"
act_title="$(printf '%s' "${acts_json}" | json_eval "process.stdout.write(JSON.stringify(data[0].title));")"
profile_id="$(printf '%s' "${profiles_json}" | json_eval "process.stdout.write(String(data[0].id));")"
unique_suffix="$(node -e "process.stdout.write(require('crypto').randomUUID().replace(/-/g,'').slice(0,8));")"

subscription_payload="$(PROFILE_ID="${profile_id}" RECIPIENT="structured-${unique_suffix}@example.test" node -e "const payload={profileId:process.env.PROFILE_ID, subscriber:process.env.RECIPIENT, channel:'email', alertPolicy:'immediate', digestIntervalMinutes:null}; process.stdout.write(JSON.stringify(payload));")"
curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/subscriptions" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${subscription_payload}" >/dev/null

callback_url="http://host.docker.internal:${listener_port}/hooks/alerts"
registration_payload="$(CALLBACK_URL="${callback_url}" node -e "const payload={name:'Structured log proof', callbackUrl:process.env.CALLBACK_URL, eventTypes:['alert.created']}; process.stdout.write(JSON.stringify(payload));")"
curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/webhooks" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${registration_payload}" >/dev/null

ai_payload="$(ACT_ID="${act_id}" ACT_TITLE_JSON="${act_title}" node -e "const title=JSON.parse(process.env.ACT_TITLE_JSON); const payload={kind:'act-summary', subjectType:'act', subjectId:process.env.ACT_ID, subjectTitle:title, prompt:'Udowodnij marker flow=ai w logach strukturalnych.'}; process.stdout.write(JSON.stringify(payload));")"
ai_accepted="$(curl -fsS --max-time 10 -X POST "http://127.0.0.1:${api_port}/v1/ai/tasks" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${ai_payload}")"
ai_task_id="$(printf '%s' "${ai_accepted}" | json_eval "process.stdout.write(String(data.id));")"
completed_ai_task_json="$(wait_entity_completed "http://127.0.0.1:${api_port}/v1/ai/tasks" "${ai_task_id}" 320)"

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

listener_completed=false
for ((attempt=0; attempt<140; attempt++)); do
  if ! kill -0 "${listener_pid}" >/dev/null 2>&1; then
    wait "${listener_pid}" || {
      echo "Structured-log signed webhook listener failed. Log: $(cat "${listener_log}")" >&2
      exit 1
    }
    listener_completed=true
    break
  fi
  sleep 1
done

if [[ "${listener_completed}" != "true" ]]; then
  echo "Structured-log signed webhook listener did not complete in time." >&2
  exit 1
fi

wait_compose_logs_match "flow=ai" 90 "${compose_args[@]}" -- api worker-documents >/dev/null
wait_compose_logs_match "flow=replay" 90 "${compose_args[@]}" -- api worker-replay >/dev/null
wait_compose_logs_match "flow=backfill" 90 "${compose_args[@]}" -- api worker-replay >/dev/null
wait_compose_logs_match "flow=profile-subscription|flow=webhook-registration" 90 "${compose_args[@]}" -- api worker-notifications >/dev/null
wait_compose_logs_match "flow=signed-webhook" 90 "${compose_args[@]}" -- api >/dev/null

mkdir -p "$(dirname "${summary_path}")"

COMPLETED_AI_TASK_JSON="${completed_ai_task_json}" \
COMPLETED_REPLAY_JSON="${completed_replay_json}" \
COMPLETED_BACKFILL_JSON="${completed_backfill_json}" \
CAPTURES_JSON="$(cat "${capture_path}")" \
node -e "const aiTask=JSON.parse(process.env.COMPLETED_AI_TASK_JSON); const replay=JSON.parse(process.env.COMPLETED_REPLAY_JSON); const backfill=JSON.parse(process.env.COMPLETED_BACKFILL_JSON); const captures=JSON.parse(process.env.CAPTURES_JSON); const summary={verifiedAtUtc:new Date().toISOString(), aiTaskStatus:aiTask.status, replayStatus:replay.status, backfillStatus:backfill.status, signedWebhookDispatchCount:captures.length, verifiedFlows:['ai','replay','backfill','admin-catch-up','signed-webhook']}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
