#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker
require_cmd curl
require_cmd node
ensure_playwright_chromium

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

tmp_dir="${repo_root}/output/playwright/tmp-admin-browser-$(random_suffix)"
project_name="lawwatcher-admin-browser-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
test_file="${tmp_dir}/operator-admin-browser.spec.js"
summary_path="${repo_root}/output/playwright/operator-admin-browser-summary.json"
authenticated_screenshot="${repo_root}/output/playwright/admin-authenticated.png"
logged_out_screenshot="${repo_root}/output/playwright/admin-logged-out.png"

mkdir -p "$(dirname "${summary_path}")" "${tmp_dir}"

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

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

wait_http_ok "http://127.0.0.1:${api_port}/v1/system/capabilities" >/dev/null
wait_http_ok "http://127.0.0.1:${portal_port}/admin" >/dev/null
npx -y -p @playwright/test playwright --version >/dev/null
playwright_test_node_path="$(resolve_npx_package_node_path "@playwright/test")"
if [[ -z "${playwright_test_node_path}" ]]; then
  echo "Could not resolve cached node_modules path for @playwright/test." >&2
  exit 1
fi

cat > "${test_file}" <<'EOF'
const { test, expect } = require('@playwright/test');
const fs = require('fs');

const portalBaseUrl = process.env.PORTAL_BASE_URL;
const summaryPath = process.env.SUMMARY_PATH;
const authenticatedScreenshot = process.env.AUTHENTICATED_SCREENSHOT;
const loggedOutScreenshot = process.env.LOGGED_OUT_SCREENSHOT;

test('operator admin browser smoke', async ({ page }) => {
  const suffix = `${Date.now()}-${Math.random().toString(16).slice(2, 8)}`;
  const operatorEmail = `browser.ops.${suffix}@lawwatcher.local`;
  const operatorDisplayName = 'Browser Smoke Operator';
  const updatedOperatorDisplayName = 'Browser Smoke Operator Updated';
  const operatorPassword = 'BrowserSmoke123!';
  const profileName = `Browser Smoke Profile ${suffix}`;
  const subscriptionEmail = `browser.subscription.${suffix}@example.test`;
  const webhookName = `Browser Smoke Webhook ${suffix}`;
  const updatedWebhookName = `${webhookName} Updated`;
  const webhookCallbackUrl = `https://hooks.example.test/${suffix}`;
  const updatedWebhookCallbackUrl = `${webhookCallbackUrl}/v2`;
  const apiClientName = `Browser Smoke API Client ${suffix}`;
  const updatedApiClientName = `${apiClientName} Updated`;
  const apiClientIdentifier = `browser-client-${suffix}`;
  const apiClientToken = `browser-client-token-${suffix}`;
  const rotatedApiClientToken = `browser-client-token-rotated-${suffix}`;

  await page.goto(`${portalBaseUrl}/admin`, { waitUntil: 'networkidle' });
  await expect(page.getByText('Operator access')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();

  await page.locator('#operator-email').fill('admin@lawwatcher.local');
  await page.locator('#operator-password').fill('Admin123!');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByText('Operator session established.')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Create profile' })).toBeVisible();

  await page.locator('#operator-create-email').fill(operatorEmail);
  await page.locator('#operator-create-display').fill(operatorDisplayName);
  await page.locator('#operator-create-password').fill(operatorPassword);
  await page.locator('#operator-create-permissions').fill('profiles:write, subscriptions:write, webhooks:write, api-clients:write');
  await page.getByRole('button', { name: 'Create operator' }).click();
  await expect(page.locator('#operator-update-target')).toContainText(operatorEmail);

  await page.locator('#operator-update-target').selectOption({ label: operatorEmail });
  await page.locator('#operator-update-display').fill(updatedOperatorDisplayName);
  await page.locator('#operator-update-permissions').fill('profiles:write, subscriptions:write, webhooks:write, api-clients:write');
  await page.getByRole('button', { name: 'Update operator' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: updatedOperatorDisplayName })).toBeVisible();

  await page.locator('#profile-name').fill(profileName);
  await page.locator('#profile-keywords').fill('browser, smoke');
  await page.getByRole('button', { name: 'Create profile' }).click();
  await expect(page.locator('#subscription-profile')).toContainText(profileName);

  await page.locator('#subscription-profile').selectOption({ label: profileName });
  await page.locator('#subscription-subscriber').fill(subscriptionEmail);
  await page.getByRole('button', { name: 'Create subscription' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: subscriptionEmail })).toBeVisible();

  await page.locator('#webhook-name').fill(webhookName);
  await page.locator('#webhook-url').fill(webhookCallbackUrl);
  await page.locator('#webhook-events').fill('alert.created');
  await page.getByRole('button', { name: 'Create webhook' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: webhookName })).toBeVisible();

  await page.locator('#webhook-update-target').selectOption({ label: webhookName });
  await page.locator('#webhook-update-name').fill(updatedWebhookName);
  await page.locator('#webhook-update-url').fill(updatedWebhookCallbackUrl);
  await page.locator('#webhook-update-events').fill('alert.created, bill.imported');
  await page.getByRole('button', { name: 'Update webhook' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: updatedWebhookName })).toContainText(updatedWebhookCallbackUrl);

  await page.locator('#api-client-name').fill(apiClientName);
  await page.locator('#api-client-identifier').fill(apiClientIdentifier);
  await page.locator('#api-client-token').fill(apiClientToken);
  await page.locator('#api-client-scopes').fill('replays:write, webhooks:write');
  await page.getByRole('button', { name: 'Create API client' }).click();
  await expect(page.locator('#api-client-update-target')).toContainText(apiClientIdentifier);

  await page.locator('#api-client-update-target').selectOption({ label: apiClientIdentifier });
  await page.locator('#api-client-update-name').fill(updatedApiClientName);
  await page.locator('#api-client-update-scopes').fill('replays:write, api-clients:write');
  await page.locator('#api-client-update-token').fill(rotatedApiClientToken);
  await page.getByRole('button', { name: 'Update API client' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: updatedApiClientName })).toBeVisible();

  await page.locator('article.admin-item-card').filter({ hasText: updatedOperatorDisplayName }).getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: updatedOperatorDisplayName })).toContainText('inactive');

  await page.locator('article.admin-item-card').filter({ hasText: subscriptionEmail }).getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.getByText(subscriptionEmail)).toHaveCount(0);

  await page.locator('article.admin-item-card').filter({ hasText: profileName }).getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.getByText(profileName)).toHaveCount(0);

  await page.locator('article.admin-item-card').filter({ hasText: updatedWebhookName }).getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: updatedWebhookName })).toContainText('inactive');

  await page.locator('article.admin-item-card').filter({ hasText: updatedApiClientName }).getByRole('button', { name: 'Deactivate' }).click();
  await expect(page.locator('article.admin-item-card').filter({ hasText: updatedApiClientName })).toContainText('inactive');

  await page.screenshot({ path: authenticatedScreenshot, fullPage: true });

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByText('Operator access')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  await page.screenshot({ path: loggedOutScreenshot, fullPage: true });

  const summary = {
    verifiedAtUtc: new Date().toISOString(),
    apiBaseUrl: process.env.API_BASE_URL,
    portalBaseUrl,
    loginFormVisible: true,
    authenticatedAfterLogin: true,
    createdOperatorVisible: true,
    updatedOperatorVisible: true,
    deactivatedOperatorMarkedInactive: true,
    createdProfileVisible: true,
    createdSubscriptionVisible: true,
    createdWebhookVisible: true,
    updatedWebhookVisible: true,
    createdApiClientVisible: true,
    updatedApiClientVisible: true,
    subscriptionRemovedAfterDeactivate: true,
    profileRemovedAfterDeactivate: true,
    webhookMarkedInactiveAfterDeactivate: true,
    apiClientMarkedInactiveAfterDeactivate: true,
    loggedOutSignInVisible: true,
    operatorEmail,
    profileName,
    subscriptionEmail,
    webhookName: updatedWebhookName,
    apiClientIdentifier,
    screenshots: [
      { name: 'authenticated', path: authenticatedScreenshot },
      { name: 'loggedOut', path: loggedOutScreenshot }
    ]
  };

  fs.writeFileSync(summaryPath, JSON.stringify(summary, null, 2));
});
EOF

(
  export NODE_PATH="${playwright_test_node_path}"
  export PORTAL_BASE_URL="http://127.0.0.1:${portal_port}"
  export API_BASE_URL="http://127.0.0.1:${api_port}"
  export SUMMARY_PATH="${summary_path}"
  export AUTHENTICATED_SCREENSHOT="${authenticated_screenshot}"
  export LOGGED_OUT_SCREENSHOT="${logged_out_screenshot}"
  cd "${tmp_dir}"
  npx -y -p @playwright/test playwright test "$(basename "${test_file}")" --workers=1 --reporter=line
)

cat "${summary_path}"
