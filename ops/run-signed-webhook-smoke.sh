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

while (($# > 0)); do
  case "$1" in
    --build-local)
      build_local=true
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

cd "$repo_root"

tmp_dir="$(mktemp -d)"
project_name="lawwatcher-signed-webhook-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
listener_script="${tmp_dir}/signed-webhook-listener.js"
capture_path="${tmp_dir}/captured-webhooks.json"
summary_path="${repo_root}/output/smoke/signed-webhook-summary.json"
listener_log="${tmp_dir}/listener.log"
signing_secret="signed-webhook-smoke-secret"
expected_dispatch_count=1

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
worker_lite_health_port="$(get_free_port)"
listener_port="$(get_free_port)"

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
  "WORKER_LITE_HEALTH_PORT=${worker_lite_health_port}" \
  "WORKERS__LITE__MAXCONCURRENCY=1" \
  "LAWWATCHER__SEEDDATA__ENABLEWEBHOOKSUBSCRIPTIONSEED=false" \
  "LAWWATCHER__SEEDDATA__ENABLEDEFAULTAPICLIENTSEED=true" \
  "LAWWATCHER__WEBHOOKS__BACKEND=SignedHttp" \
  "LAWWATCHER__WEBHOOKS__SIGNINGSECRET=${signing_secret}"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  --env-file "${env_file}"
)

if [[ "${build_local}" == "true" ]]; then
  compose_args+=(-f ops/compose/docker-compose.build.yml)
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

mkdir -p "$(dirname "${summary_path}")"

cat > "${listener_script}" <<'EOF'
const fs = require('fs');
const http = require('http');

const port = Number(process.env.LISTENER_PORT);
const capturePath = process.env.CAPTURE_PATH;
const expectedDispatchCount = Number(process.env.EXPECTED_DISPATCH_COUNT);
const captures = [];

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on('data', chunk => chunks.push(chunk));
  req.on('end', () => {
    const body = Buffer.concat(chunks).toString('utf8');
    const headers = Object.fromEntries(Object.entries(req.headers).map(([key, value]) => [key, Array.isArray(value) ? value.join(',') : String(value)]));
    captures.push({
      method: req.method,
      url: `http://127.0.0.1:${port}${req.url}`,
      headers,
      body,
      receivedAtUtc: new Date().toISOString()
    });

    fs.writeFileSync(capturePath, JSON.stringify(captures, null, 2));
    res.statusCode = 200;
    res.setHeader('content-type', 'application/json');
    res.end('{"received":true}');

    if (captures.length >= expectedDispatchCount) {
      setTimeout(() => server.close(() => process.exit(0)), 100);
    }
  });
});

server.listen(port, '0.0.0.0');
setTimeout(() => {
  console.error(`Timed out waiting for ${expectedDispatchCount} webhook dispatches.`);
  server.close(() => process.exit(1));
}, 90000);
EOF

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

wait_http_ok "http://127.0.0.1:${api_port}/v1/alerts" >/dev/null
wait_http_ok "http://127.0.0.1:${worker_lite_health_port}/health/ready" >/dev/null

LISTENER_PORT="${listener_port}" CAPTURE_PATH="${capture_path}" EXPECTED_DISPATCH_COUNT="${expected_dispatch_count}" node "${listener_script}" >"${listener_log}" 2>&1 &
listener_pid=$!

callback_url="http://host.docker.internal:${listener_port}/hooks/alerts"
registration_payload="$(CALLBACK_URL="${callback_url}" node -e "const payload={name:'Signed webhook smoke', callbackUrl:process.env.CALLBACK_URL, eventTypes:['alert.created']}; process.stdout.write(JSON.stringify(payload));")"
registration_status="$(curl -sS -o "${tmp_dir}/registration.json" -w "%{http_code}" -X POST "http://127.0.0.1:${api_port}/v1/webhooks" -H "Authorization: Bearer portal-integrator-demo-token" -H "Content-Type: application/json" -d "${registration_payload}")"
if [[ "${registration_status}" != "202" ]]; then
  echo "Webhook registration failed with HTTP ${registration_status}. Body: $(cat "${tmp_dir}/registration.json")" >&2
  exit 1
fi

profile_payload="$(node -e "const suffix=Date.now().toString(36); const payload={name:'Signed Webhook Smoke ' + suffix, alertPolicy:'immediate', digestIntervalMinutes:null, keywords:['vat']}; process.stdout.write(JSON.stringify(payload));")"
profile_status="$(curl -sS -o "${tmp_dir}/profile.json" -w "%{http_code}" -X POST "http://127.0.0.1:${api_port}/v1/profiles" -H "Authorization: Bearer portal-integrator-demo-token" -H "Content-Type: application/json" -d "${profile_payload}")"
if [[ "${profile_status}" != "202" ]]; then
  echo "Profile creation failed with HTTP ${profile_status}. Body: $(cat "${tmp_dir}/profile.json")" >&2
  exit 1
fi

listener_completed=false
for ((attempt=0; attempt<140; attempt++)); do
  if ! kill -0 "${listener_pid}" >/dev/null 2>&1; then
    wait "${listener_pid}" || {
      echo "Listener failed. Log: $(cat "${listener_log}")" >&2
      exit 1
    }
    listener_completed=true
    break
  fi
  sleep 1
done

if [[ "${listener_completed}" != "true" ]]; then
  echo "Signed webhook listener did not complete in time." >&2
  exit 1
fi

captures_json="$(cat "${capture_path}")"
capture_count="$(printf '%s' "${captures_json}" | json_eval "process.stdout.write(String(data.length));")"
if [[ "${capture_count}" -lt "${expected_dispatch_count}" ]]; then
  echo "Captured only ${capture_count} signed webhook request(s)." >&2
  exit 1
fi

dispatches_json="$(curl -fsS --max-time 10 "http://127.0.0.1:${api_port}/v1/system/webhook-dispatches")"

verification_json="$(CAPTURES_JSON="${captures_json}" DISPATCHES_JSON="${dispatches_json}" SIGNING_SECRET="${signing_secret}" CALLBACK_URL="${callback_url}" node -e "const crypto=require('crypto'); const captures=JSON.parse(process.env.CAPTURES_JSON); const dispatches=JSON.parse(process.env.DISPATCHES_JSON); const secret=process.env.SIGNING_SECRET; const callbackUrl=process.env.CALLBACK_URL; const signatureFor=payload => 'sha256=' + crypto.createHmac('sha256', secret).update(payload, 'utf8').digest('hex'); const payloads=captures.map(capture => JSON.parse(capture.body)); const allSignaturesMatch=captures.every(capture => capture.headers['x-lawwatcher-signature']===signatureFor(capture.body)); const allPayloadsAreAlertCreated=payloads.every(payload => payload.type==='alert.created'); const matchingDispatches=dispatches.filter(item => item.callbackUrl===callbackUrl); const result={capturedRequestCount:captures.length, firstCallbackUrl:captures[0]?.url ?? '', firstEventTypeHeader:captures[0]?.headers['x-lawwatcher-event-type'] ?? '', firstSignatureHeader:captures[0]?.headers['x-lawwatcher-signature'] ?? '', allSignaturesMatch, allPayloadsAreAlertCreated, apiDispatchRecordCount:matchingDispatches.length}; process.stdout.write(JSON.stringify(result));")"

VERIFICATION_JSON="${verification_json}" EXPECTED_DISPATCH_COUNT="${expected_dispatch_count}" node -e "const verification=JSON.parse(process.env.VERIFICATION_JSON); const expected=Number(process.env.EXPECTED_DISPATCH_COUNT); if (!verification.allSignaturesMatch || !verification.allPayloadsAreAlertCreated || verification.apiDispatchRecordCount < expected || verification.capturedRequestCount < expected) process.exit(1);"

VERIFICATION_JSON="${verification_json}" EXPECTED_DISPATCH_COUNT="${expected_dispatch_count}" node -e "const verification=JSON.parse(process.env.VERIFICATION_JSON); const summary={verifiedAtUtc:new Date().toISOString(), expectedDispatchCount:Number(process.env.EXPECTED_DISPATCH_COUNT), ...verification}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
