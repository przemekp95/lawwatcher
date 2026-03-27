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
project_name="lawwatcher-operator-admin-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
cookie_jar="${tmp_dir}/cookies.txt"
summary_path="${repo_root}/output/smoke/operator-admin-summary.json"

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
worker_lite_health_port="$(get_free_port)"

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
  "LAWWATCHER__SEEDDATA__ENABLEWEBHOOKSUBSCRIPTIONSEED=false"

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

wait_http_ok "http://127.0.0.1:${api_port}/v1/system/capabilities" >/dev/null

last_status=""
last_body=""

request_no_body() {
  local method="$1"
  local url="$2"
  shift 2
  local response_file="${tmp_dir}/response-$(random_suffix).json"
  local args=(-sS -o "${response_file}" -w "%{http_code}" -X "${method}" "${url}" -b "${cookie_jar}" -c "${cookie_jar}" --max-time 20)
  while (($# > 0)); do
    args+=(-H "$1")
    shift
  done
  last_status="$(curl "${args[@]}")"
  last_body="$(cat "${response_file}" 2>/dev/null || true)"
}

request_json() {
  local method="$1"
  local url="$2"
  local body="$3"
  shift 3
  local response_file="${tmp_dir}/response-$(random_suffix).json"
  local args=(-sS -o "${response_file}" -w "%{http_code}" -X "${method}" "${url}" -b "${cookie_jar}" -c "${cookie_jar}" --max-time 20 -H "Content-Type: application/json" --data "${body}")
  while (($# > 0)); do
    args+=(-H "$1")
    shift
  done
  last_status="$(curl "${args[@]}")"
  last_body="$(cat "${response_file}" 2>/dev/null || true)"
}

assert_status() {
  local expected="$1"
  local label="$2"
  if [[ "${last_status}" != "${expected}" ]]; then
    echo "${label} expected HTTP ${expected} but got ${last_status}. Body: ${last_body}" >&2
    exit 1
  fi
}

api_base="http://127.0.0.1:${api_port}"

request_no_body GET "${api_base}/v1/operators"
assert_status 401 "anonymous operators read"

request_no_body GET "${api_base}/v1/operator/session"
assert_status 200 "initial operator session"
initial_csrf="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.csrfRequestToken));")"

login_body='{"email":"admin@lawwatcher.local","password":"Admin123!"}'
request_json POST "${api_base}/v1/operator/login" "${login_body}"
assert_status 400 "login without csrf"

request_json POST "${api_base}/v1/operator/login" "${login_body}" "X-LawWatcher-CSRF: ${initial_csrf}"
assert_status 200 "login with csrf"

request_no_body GET "${api_base}/v1/operator/session"
assert_status 200 "session after login"
csrf="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.csrfRequestToken));")"
authenticated_after_login="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(Boolean(data.isAuthenticated)));")"

request_no_body GET "${api_base}/v1/operator/me"
assert_status 200 "operator me"
authenticated_email="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.email));")"

operator_email="ops.$(random_suffix)@lawwatcher.local"
create_operator_body="$(OPERATOR_EMAIL="${operator_email}" node -e "const payload={email:process.env.OPERATOR_EMAIL, displayName:'Ops Tester', password:'OpsTester123!', permissions:['profiles:write','subscriptions:write']}; process.stdout.write(JSON.stringify(payload));")"
request_json POST "${api_base}/v1/operators" "${create_operator_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "create operator"
operator_id="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.id));")"
create_operator_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

update_operator_body='{"displayName":"Ops Tester Updated","permissions":["profiles:write","subscriptions:write","webhooks:write"]}'
request_json PATCH "${api_base}/v1/operators/${operator_id}" "${update_operator_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "update operator"
update_operator_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

reset_operator_body='{"newPassword":"OpsTester123!Reset"}'
request_json POST "${api_base}/v1/operators/${operator_id}/reset-password" "${reset_operator_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "reset operator password"
reset_operator_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/operators"
assert_status 200 "operators listing"
operator_count="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"

profile_without_csrf_body='{"name":"No CSRF","alertPolicy":"immediate","digestIntervalMinutes":null,"keywords":["vat"]}'
request_json POST "${api_base}/v1/profiles" "${profile_without_csrf_body}"
assert_status 400 "profile write without csrf"

profile_name="Operator Created Profile $(random_suffix)"
create_profile_body="$(PROFILE_NAME="${profile_name}" node -e "const payload={name:process.env.PROFILE_NAME, alertPolicy:'immediate', digestIntervalMinutes:null, keywords:['vat','cit']}; process.stdout.write(JSON.stringify(payload));")"
request_json POST "${api_base}/v1/profiles" "${create_profile_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "create profile"
profile_id="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.id));")"
create_profile_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_json POST "${api_base}/v1/profiles/${profile_id}/rules" '{"keyword":"akcyza"}' "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "add profile rule"
add_profile_rule_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_json PATCH "${api_base}/v1/profiles/${profile_id}/alert-policy" '{"alertPolicy":"digest","digestIntervalMinutes":180}' "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "change profile alert policy"
change_profile_policy_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/profiles"
assert_status 200 "profiles listing"
profile_count="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"

subscription_email="ops.subscription.$(random_suffix)@example.test"
create_subscription_body="$(PROFILE_ID="${profile_id}" SUBSCRIPTION_EMAIL="${subscription_email}" node -e "const payload={profileId:process.env.PROFILE_ID, subscriber:process.env.SUBSCRIPTION_EMAIL, channel:'email', alertPolicy:'immediate', digestIntervalMinutes:null}; process.stdout.write(JSON.stringify(payload));")"
request_json POST "${api_base}/v1/subscriptions" "${create_subscription_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "create subscription"
subscription_id="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.id));")"
create_subscription_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_json PATCH "${api_base}/v1/subscriptions/${subscription_id}/alert-policy" '{"alertPolicy":"digest","digestIntervalMinutes":240}' "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "change subscription alert policy"
change_subscription_policy_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/subscriptions"
assert_status 200 "subscriptions listing"
subscription_count="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"

request_no_body DELETE "${api_base}/v1/subscriptions/${subscription_id}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "deactivate subscription"
deactivated_subscription_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/subscriptions"
assert_status 200 "subscriptions after deactivate"
subscription_count_after_deactivate="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"

request_no_body DELETE "${api_base}/v1/profiles/${profile_id}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "deactivate profile"
deactivated_profile_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/profiles"
assert_status 200 "profiles after deactivate"
profile_count_after_deactivate="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"

webhook_name="Operator Managed Webhook $(random_suffix)"
webhook_url="https://hooks.example.test/operator-admin/$(random_suffix)"
create_webhook_body="$(WEBHOOK_NAME="${webhook_name}" WEBHOOK_URL="${webhook_url}" node -e "const payload={name:process.env.WEBHOOK_NAME, callbackUrl:process.env.WEBHOOK_URL, eventTypes:['alert.created']}; process.stdout.write(JSON.stringify(payload));")"
request_json POST "${api_base}/v1/webhooks" "${create_webhook_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "create webhook"
webhook_id="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.id));")"
create_webhook_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

updated_webhook_name="${webhook_name} Updated"
updated_webhook_url="${webhook_url}/v2"
update_webhook_body="$(WEBHOOK_NAME="${updated_webhook_name}" WEBHOOK_URL="${updated_webhook_url}" node -e "const payload={name:process.env.WEBHOOK_NAME, callbackUrl:process.env.WEBHOOK_URL, eventTypes:['alert.created','bill.imported']}; process.stdout.write(JSON.stringify(payload));")"
request_json PATCH "${api_base}/v1/webhooks/${webhook_id}" "${update_webhook_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "update webhook"
update_webhook_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/webhooks"
assert_status 200 "webhooks listing"
webhook_count="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"
updated_webhook_name_read_model="$(printf '%s' "${last_body}" | WEBHOOK_ID="${webhook_id}" node -e "const fs=require('fs'); const items=JSON.parse(fs.readFileSync(0,'utf8')); const match=items.find(item => String(item.id)===process.env.WEBHOOK_ID); process.stdout.write(String(match?.name ?? ''));")"
updated_webhook_url_read_model="$(printf '%s' "${last_body}" | WEBHOOK_ID="${webhook_id}" node -e "const fs=require('fs'); const items=JSON.parse(fs.readFileSync(0,'utf8')); const match=items.find(item => String(item.id)===process.env.WEBHOOK_ID); process.stdout.write(String(match?.callbackUrl ?? ''));")"
updated_webhook_event_count="$(printf '%s' "${last_body}" | WEBHOOK_ID="${webhook_id}" node -e "const fs=require('fs'); const items=JSON.parse(fs.readFileSync(0,'utf8')); const match=items.find(item => String(item.id)===process.env.WEBHOOK_ID); process.stdout.write(String((match?.eventTypes ?? []).length));")"

request_no_body DELETE "${api_base}/v1/webhooks/${webhook_id}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "deactivate webhook"
deactivated_webhook_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

api_client_identifier="operator-managed-client-$(random_suffix)"
api_client_token="operator-managed-token-$(random_suffix)"
rotated_api_client_token="operator-managed-token-rotated-$(random_suffix)"
api_client_name="Operator Managed API Client"
updated_api_client_name="Operator Managed API Client Updated"
create_api_client_body="$(API_CLIENT_NAME="${api_client_name}" API_CLIENT_IDENTIFIER="${api_client_identifier}" API_CLIENT_TOKEN="${api_client_token}" node -e "const payload={name:process.env.API_CLIENT_NAME, clientIdentifier:process.env.API_CLIENT_IDENTIFIER, token:process.env.API_CLIENT_TOKEN, scopes:['replays:write','webhooks:write']}; process.stdout.write(JSON.stringify(payload));")"
request_json POST "${api_base}/v1/api-clients" "${create_api_client_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "create api client"
api_client_id="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.id));")"
create_api_client_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

update_api_client_body="$(UPDATED_NAME="${updated_api_client_name}" ROTATED_TOKEN="${rotated_api_client_token}" node -e "const payload={name:process.env.UPDATED_NAME, token:process.env.ROTATED_TOKEN, scopes:['replays:write','api-clients:write']}; process.stdout.write(JSON.stringify(payload));")"
request_json PATCH "${api_base}/v1/api-clients/${api_client_id}" "${update_api_client_body}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "update api client"
update_api_client_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_no_body GET "${api_base}/v1/api-clients"
assert_status 200 "api clients listing"
api_client_count="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.length));")"
updated_api_client_name_read_model="$(printf '%s' "${last_body}" | API_CLIENT_ID="${api_client_id}" node -e "const fs=require('fs'); const items=JSON.parse(fs.readFileSync(0,'utf8')); const match=items.find(item => String(item.id)===process.env.API_CLIENT_ID); process.stdout.write(String(match?.name ?? ''));")"
updated_api_client_fingerprint="$(printf '%s' "${last_body}" | API_CLIENT_ID="${api_client_id}" node -e "const fs=require('fs'); const items=JSON.parse(fs.readFileSync(0,'utf8')); const match=items.find(item => String(item.id)===process.env.API_CLIENT_ID); process.stdout.write(String(match?.tokenFingerprint ?? ''));")"
updated_api_client_scope_count="$(printf '%s' "${last_body}" | API_CLIENT_ID="${api_client_id}" node -e "const fs=require('fs'); const items=JSON.parse(fs.readFileSync(0,'utf8')); const match=items.find(item => String(item.id)===process.env.API_CLIENT_ID); process.stdout.write(String((match?.scopes ?? []).length));")"

replay_body='{"scope":"sql-projections"}'
request_json POST "${api_base}/v1/replays" "${replay_body}" "Authorization: Bearer ${rotated_api_client_token}"
assert_status 202 "replay with rotated token"
replay_with_rotated_token_status="${last_status}"

request_json POST "${api_base}/v1/replays" "${replay_body}" "Authorization: Bearer ${api_client_token}"
assert_status 401 "replay with stale token"
replay_with_stale_token_status="${last_status}"

request_no_body DELETE "${api_base}/v1/api-clients/${api_client_id}" "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "deactivate api client"
deactivated_api_client_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_json POST "${api_base}/v1/operators/${operator_id}/deactivate" '{}' "X-LawWatcher-CSRF: ${csrf}"
assert_status 202 "deactivate operator"
deactivated_operator_status="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(data.status));")"

request_json POST "${api_base}/v1/operator/logout" '{}' "X-LawWatcher-CSRF: ${csrf}"
assert_status 200 "logout"
logout_authenticated="$(printf '%s' "${last_body}" | json_eval "process.stdout.write(String(Boolean(data.isAuthenticated)));")"

request_no_body GET "${api_base}/v1/operator/me"
assert_status 401 "me after logout"

AUTHORIZED_EMAIL="${authenticated_email}" \
OPERATOR_ID="${operator_id}" \
PROFILE_ID="${profile_id}" \
SUBSCRIPTION_ID="${subscription_id}" \
WEBHOOK_ID="${webhook_id}" \
API_CLIENT_ID="${api_client_id}" \
UPDATED_WEBHOOK_NAME="${updated_webhook_name_read_model}" \
UPDATED_WEBHOOK_URL="${updated_webhook_url_read_model}" \
UPDATED_WEBHOOK_EVENT_COUNT="${updated_webhook_event_count}" \
UPDATED_API_CLIENT_NAME="${updated_api_client_name_read_model}" \
UPDATED_API_CLIENT_FINGERPRINT="${updated_api_client_fingerprint}" \
UPDATED_API_CLIENT_SCOPE_COUNT="${updated_api_client_scope_count}" \
AUTHENTICATED_AFTER_LOGIN="${authenticated_after_login}" \
OPERATOR_COUNT="${operator_count}" \
CREATE_OPERATOR_STATUS="${create_operator_status}" \
UPDATE_OPERATOR_STATUS="${update_operator_status}" \
RESET_OPERATOR_STATUS="${reset_operator_status}" \
DEACTIVATED_OPERATOR_STATUS="${deactivated_operator_status}" \
PROFILE_COUNT="${profile_count}" \
CREATE_PROFILE_STATUS="${create_profile_status}" \
ADD_PROFILE_RULE_STATUS="${add_profile_rule_status}" \
CHANGE_PROFILE_POLICY_STATUS="${change_profile_policy_status}" \
DEACTIVATED_PROFILE_STATUS="${deactivated_profile_status}" \
PROFILE_COUNT_AFTER_DEACTIVATE="${profile_count_after_deactivate}" \
SUBSCRIPTION_COUNT="${subscription_count}" \
CREATE_SUBSCRIPTION_STATUS="${create_subscription_status}" \
CHANGE_SUBSCRIPTION_POLICY_STATUS="${change_subscription_policy_status}" \
DEACTIVATED_SUBSCRIPTION_STATUS="${deactivated_subscription_status}" \
SUBSCRIPTION_COUNT_AFTER_DEACTIVATE="${subscription_count_after_deactivate}" \
WEBHOOK_COUNT="${webhook_count}" \
CREATE_WEBHOOK_STATUS="${create_webhook_status}" \
UPDATE_WEBHOOK_STATUS="${update_webhook_status}" \
DEACTIVATED_WEBHOOK_STATUS="${deactivated_webhook_status}" \
API_CLIENT_COUNT="${api_client_count}" \
CREATE_API_CLIENT_STATUS="${create_api_client_status}" \
UPDATE_API_CLIENT_STATUS="${update_api_client_status}" \
REPLAY_WITH_ROTATED_TOKEN_STATUS="${replay_with_rotated_token_status}" \
REPLAY_WITH_STALE_TOKEN_STATUS="${replay_with_stale_token_status}" \
DEACTIVATED_API_CLIENT_STATUS="${deactivated_api_client_status}" \
LOGOUT_AUTHENTICATED="${logout_authenticated}" \
node -e "const parseNumber=value => value === undefined ? null : Number(value); const summary={verifiedAtUtc:new Date().toISOString(), unauthorizedOperatorsStatus:401, loginWithoutCsrfStatus:400, authenticatedAfterLogin:process.env.AUTHENTICATED_AFTER_LOGIN==='true', authenticatedEmail:process.env.AUTHORIZED_EMAIL, operatorCount:parseNumber(process.env.OPERATOR_COUNT), createdOperatorId:process.env.OPERATOR_ID, createOperatorStatus:process.env.CREATE_OPERATOR_STATUS, updateOperatorStatus:process.env.UPDATE_OPERATOR_STATUS, resetOperatorStatus:process.env.RESET_OPERATOR_STATUS, deactivatedOperatorStatus:process.env.DEACTIVATED_OPERATOR_STATUS, profileWithoutCsrfStatus:400, profileCount:parseNumber(process.env.PROFILE_COUNT), createdProfileId:process.env.PROFILE_ID, profileStatuses:[process.env.CREATE_PROFILE_STATUS, process.env.ADD_PROFILE_RULE_STATUS, process.env.CHANGE_PROFILE_POLICY_STATUS], deactivatedProfileStatus:process.env.DEACTIVATED_PROFILE_STATUS, profileCountAfterDeactivate:parseNumber(process.env.PROFILE_COUNT_AFTER_DEACTIVATE), subscriptionCount:parseNumber(process.env.SUBSCRIPTION_COUNT), createdSubscriptionId:process.env.SUBSCRIPTION_ID, subscriptionStatuses:[process.env.CREATE_SUBSCRIPTION_STATUS, process.env.CHANGE_SUBSCRIPTION_POLICY_STATUS], deactivatedSubscriptionStatus:process.env.DEACTIVATED_SUBSCRIPTION_STATUS, subscriptionCountAfterDeactivate:parseNumber(process.env.SUBSCRIPTION_COUNT_AFTER_DEACTIVATE), webhookCount:parseNumber(process.env.WEBHOOK_COUNT), createdWebhookId:process.env.WEBHOOK_ID, webhookStatuses:[process.env.CREATE_WEBHOOK_STATUS, process.env.UPDATE_WEBHOOK_STATUS], updatedWebhookName:process.env.UPDATED_WEBHOOK_NAME, updatedWebhookCallbackUrl:process.env.UPDATED_WEBHOOK_URL, updatedWebhookEventTypeCount:parseNumber(process.env.UPDATED_WEBHOOK_EVENT_COUNT), deactivatedWebhookStatus:process.env.DEACTIVATED_WEBHOOK_STATUS, apiClientCount:parseNumber(process.env.API_CLIENT_COUNT), createdApiClientId:process.env.API_CLIENT_ID, apiClientStatuses:[process.env.CREATE_API_CLIENT_STATUS, process.env.UPDATE_API_CLIENT_STATUS], updatedApiClientName:process.env.UPDATED_API_CLIENT_NAME, updatedApiClientFingerprint:process.env.UPDATED_API_CLIENT_FINGERPRINT, updatedApiClientScopeCount:parseNumber(process.env.UPDATED_API_CLIENT_SCOPE_COUNT), replayWithRotatedTokenStatus:parseNumber(process.env.REPLAY_WITH_ROTATED_TOKEN_STATUS), replayWithStaleTokenStatus:parseNumber(process.env.REPLAY_WITH_STALE_TOKEN_STATUS), deactivatedApiClientStatus:process.env.DEACTIVATED_API_CLIENT_STATUS, logoutAuthenticated:process.env.LOGOUT_AUTHENTICATED==='true'}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
