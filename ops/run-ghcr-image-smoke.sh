#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

image_tag="${GITHUB_SHA:+sha-${GITHUB_SHA}}"
owner="${GITHUB_REPOSITORY_OWNER:-przemekp95}"

while (($# > 0)); do
  case "$1" in
    --image-tag)
      image_tag="$2"
      shift 2
      ;;
    --owner)
      owner="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ -z "${image_tag}" ]]; then
  echo "Image tag is required. Pass --image-tag or set GITHUB_SHA." >&2
  exit 1
fi

export LAWWATCHER_API_IMAGE="ghcr.io/${owner}/lawwatcher-api:${image_tag}"
export LAWWATCHER_PORTAL_IMAGE="ghcr.io/${owner}/lawwatcher-portal:${image_tag}"
export LAWWATCHER_WORKER_LITE_IMAGE="ghcr.io/${owner}/lawwatcher-worker-lite:${image_tag}"
export LAWWATCHER_WORKER_AI_IMAGE="ghcr.io/${owner}/lawwatcher-worker-ai:${image_tag}"
export LAWWATCHER_WORKER_DOCUMENTS_IMAGE="ghcr.io/${owner}/lawwatcher-worker-documents:${image_tag}"
export LAWWATCHER_WORKER_NOTIFICATIONS_IMAGE="ghcr.io/${owner}/lawwatcher-worker-notifications:${image_tag}"
export LAWWATCHER_WORKER_PROJECTION_IMAGE="ghcr.io/${owner}/lawwatcher-worker-projection:${image_tag}"
export LAWWATCHER_WORKER_REPLAY_IMAGE="ghcr.io/${owner}/lawwatcher-worker-replay:${image_tag}"

cd "${repo_root}"

tmp_dir="$(mktemp -d)"
dev_env="${tmp_dir}/dev.env"
production_env="${tmp_dir}/production.env"
trap 'rm -rf "${tmp_dir}"' EXIT

write_env_file_from_example \
  "ops/env/dev.env.example" \
  "${dev_env}" \
  "LAWWATCHER_API_IMAGE=ghcr.io/${owner}/lawwatcher-api:${image_tag}" \
  "LAWWATCHER_PORTAL_IMAGE=ghcr.io/${owner}/lawwatcher-portal:${image_tag}" \
  "LAWWATCHER_WORKER_LITE_IMAGE=ghcr.io/${owner}/lawwatcher-worker-lite:${image_tag}" \
  "LAWWATCHER_WORKER_AI_IMAGE=ghcr.io/${owner}/lawwatcher-worker-ai:${image_tag}" \
  "LAWWATCHER_WORKER_DOCUMENTS_IMAGE=ghcr.io/${owner}/lawwatcher-worker-documents:${image_tag}" \
  "LAWWATCHER_WORKER_NOTIFICATIONS_IMAGE=ghcr.io/${owner}/lawwatcher-worker-notifications:${image_tag}" \
  "LAWWATCHER_WORKER_PROJECTION_IMAGE=ghcr.io/${owner}/lawwatcher-worker-projection:${image_tag}" \
  "LAWWATCHER_WORKER_REPLAY_IMAGE=ghcr.io/${owner}/lawwatcher-worker-replay:${image_tag}"

write_env_file_from_example \
  "ops/env/production.env.example" \
  "${production_env}" \
  "LAWWATCHER_API_IMAGE=ghcr.io/${owner}/lawwatcher-api:${image_tag}" \
  "LAWWATCHER_PORTAL_IMAGE=ghcr.io/${owner}/lawwatcher-portal:${image_tag}" \
  "LAWWATCHER_WORKER_LITE_IMAGE=ghcr.io/${owner}/lawwatcher-worker-lite:${image_tag}" \
  "LAWWATCHER_WORKER_AI_IMAGE=ghcr.io/${owner}/lawwatcher-worker-ai:${image_tag}" \
  "LAWWATCHER_WORKER_DOCUMENTS_IMAGE=ghcr.io/${owner}/lawwatcher-worker-documents:${image_tag}" \
  "LAWWATCHER_WORKER_NOTIFICATIONS_IMAGE=ghcr.io/${owner}/lawwatcher-worker-notifications:${image_tag}" \
  "LAWWATCHER_WORKER_PROJECTION_IMAGE=ghcr.io/${owner}/lawwatcher-worker-projection:${image_tag}" \
  "LAWWATCHER_WORKER_REPLAY_IMAGE=ghcr.io/${owner}/lawwatcher-worker-replay:${image_tag}" \
  "SQLSERVER_SA_PASSWORD=SmokeSqlServer!12345" \
  "RABBITMQ_DEFAULT_PASS=SmokeRabbitMq!12345" \
  "MINIO_ROOT_PASSWORD=SmokeMinio!12345" \
  "CONNECTIONSTRINGS__LAWWATCHERSQLSERVER=Server=sqlserver,1433;Database=LawWatcher;User Id=sa;Password=SmokeSqlServer!12345;TrustServerCertificate=True;Encrypt=False" \
  "CONNECTIONSTRINGS__RABBITMQ=amqp://lawwatcher:SmokeRabbitMq!12345@rabbitmq:5672/" \
  "STORAGE__MINIO__SECRETKEY=SmokeMinio!12345" \
  "LAWWATCHER__WEBHOOKS__SIGNINGSECRET=SmokeWebhookSecret!12345"

bash ops/run-docker-dev-smoke.sh --env-file "${dev_env}"
bash ops/run-docker-dev-smoke.sh --env-file "${dev_env}" --include-ai
bash ops/run-docker-production-smoke.sh --env-file "${production_env}"

summary_path="${repo_root}/output/smoke/ghcr-image-smoke-summary.json"
mkdir -p "$(dirname "${summary_path}")"

IMAGE_TAG="${image_tag}" OWNER="${owner}" node -e "const summary={verifiedAtUtc:new Date().toISOString(), owner:process.env.OWNER, imageTag:process.env.IMAGE_TAG, profiles:['dev','dev+ai','production']}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
