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

bash ops/run-docker-dev-laptop-smoke.sh
bash ops/run-docker-dev-laptop-smoke.sh --include-ai
bash ops/run-docker-full-host-smoke.sh
bash ops/run-docker-full-host-smoke.sh --include-opensearch

summary_path="${repo_root}/output/smoke/ghcr-image-smoke-summary.json"
mkdir -p "$(dirname "${summary_path}")"

IMAGE_TAG="${image_tag}" OWNER="${owner}" node -e "const summary={verifiedAtUtc:new Date().toISOString(), owner:process.env.OWNER, imageTag:process.env.IMAGE_TAG, profiles:['dev-laptop','dev-laptop+ai','full-host','full-host+opensearch']}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
