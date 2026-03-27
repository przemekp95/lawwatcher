#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

require_cmd curl
require_cmd node

portal_base_url="${LawWatcherPortalBaseUrl:-http://127.0.0.1:8081}"
api_base_url="${LawWatcherApiBaseUrl:-http://127.0.0.1:8080}"
output_root="${repo_root}/output/playwright"
tmp_dir="$(mktemp -d)"
summary_path="${output_root}/browser-summary.json"

cleanup() {
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

ensure_playwright_chromium
mkdir -p "${output_root}"

wait_http_ok "${portal_base_url}/" >/dev/null
api_capabilities_json="$(wait_http_ok "${api_base_url}/v1/system/capabilities")"
admin_html="$(wait_http_ok "${portal_base_url}/admin")"
search_html="$(wait_http_ok "${portal_base_url}/search?q=VAT")"

take_screenshot() {
  local url="$1"
  local selector="$2"
  local target_path="$3"
  local args=(-y -p @playwright/test playwright screenshot --browser chromium --viewport-size=1440,1200 --full-page --wait-for-timeout 2500)
  if [[ -n "${selector}" ]]; then
    args+=(--wait-for-selector "${selector}")
  fi
  args+=("${url}" "${target_path}")
  npx "${args[@]}" >/dev/null
}

take_screenshot "${portal_base_url}/" "text=Legislative monitoring dashboard" "${output_root}/home.png"
take_screenshot "${portal_base_url}/search?q=VAT" "" "${output_root}/search.png"
take_screenshot "${portal_base_url}/activity" "text=Alerts and event feed" "${output_root}/activity.png"
take_screenshot "${portal_base_url}/admin" "text=Operator access" "${output_root}/admin.png"

SEARCH_HTML="${search_html}" \
ADMIN_HTML="${admin_html}" \
API_CAPABILITIES_JSON="${api_capabilities_json}" \
OUTPUT_ROOT="${output_root}" \
node -e "const fs=require('fs'); const path=require('path'); const screenshots=['home','search','activity','admin'].map(name => { const filePath=path.join(process.env.OUTPUT_ROOT, name + '.png'); const stats=fs.statSync(filePath); return { name, url: name==='home' ? '/' : '/' + name, path:filePath, bytes:stats.size }; }); const summary={verifiedAtUtc:new Date().toISOString(), portalStatusCode:200, apiStatusCode:200, searchContainsHitLabel:process.env.SEARCH_HTML.includes('hit(s) for'), searchContainsVatTitle:process.env.SEARCH_HTML.includes('Ustawa o zmianie VAT'), adminContainsOperatorAccess:process.env.ADMIN_HTML.includes('Operator access'), adminContainsSignIn:process.env.ADMIN_HTML.includes('Sign in'), runtimeProfile:JSON.parse(process.env.API_CAPABILITIES_JSON).runtimeProfile, screenshots}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
