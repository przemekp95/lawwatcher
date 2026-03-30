#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

env_file="${repo_root}/ops/env/production.env.example"

while (($# > 0)); do
  case "$1" in
    --env-file)
      env_file="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ ! -f "$env_file" ]]; then
  echo "Production env file was not found: $env_file" >&2
  exit 1
fi

while IFS= read -r raw_line || [[ -n "$raw_line" ]]; do
  line="${raw_line%$'\r'}"
  [[ -z "$line" ]] && continue
  [[ "$line" == \#* ]] && continue
  export "$line"
done < "$env_file"

require_non_empty() {
  local name="$1"
  local value="${!name:-}"
  if [[ -z "$value" ]]; then
    echo "Missing required production setting: $name" >&2
    exit 1
  fi
}

reject_placeholder() {
  local name="$1"
  local value="${!name:-}"
  if [[ -z "$value" ]]; then
    echo "Missing required production secret: $name" >&2
    exit 1
  fi

  local lowered
  lowered="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')"
  if [[ "$lowered" == *"changeme"* || "$lowered" == *"__change_me"* || "$lowered" == *"example"* ]]; then
    echo "Production secret still uses a placeholder value: $name" >&2
    exit 1
  fi
}

reject_latest_tag() {
  local name="$1"
  local value="${!name:-}"
  require_non_empty "$name"
  if [[ "$value" == *":latest" ]]; then
    echo "Production image must use a pinned SemVer tag instead of latest: $name=$value" >&2
    exit 1
  fi
}

for image_var in \
  LAWWATCHER_API_IMAGE \
  LAWWATCHER_PORTAL_IMAGE \
  LAWWATCHER_WORKER_AI_IMAGE \
  LAWWATCHER_WORKER_DOCUMENTS_IMAGE \
  LAWWATCHER_WORKER_PROJECTION_IMAGE \
  LAWWATCHER_WORKER_NOTIFICATIONS_IMAGE \
  LAWWATCHER_WORKER_REPLAY_IMAGE
do
  reject_latest_tag "$image_var"
done

for required_var in \
  SEARCH__OPENSEARCH__BASEURL \
  SEARCH__OPENSEARCH__INDEXNAME \
  AI__MODE \
  AI__OLLAMA__BASEURL \
  AI__OLLAMA__EMBEDDINGMODEL \
  STORAGE__MINIO__ENDPOINT \
  STORAGE__MINIO__ACCESSKEY \
  CONNECTIONSTRINGS__LAWWATCHERSQLSERVER \
  CONNECTIONSTRINGS__RABBITMQ
do
  require_non_empty "$required_var"
done

for secret_var in \
  SQLSERVER_SA_PASSWORD \
  RABBITMQ_DEFAULT_PASS \
  MINIO_ROOT_PASSWORD \
  STORAGE__MINIO__SECRETKEY \
  LAWWATCHER__BOOTSTRAP__SECRET \
  LAWWATCHER__WEBHOOKS__SIGNINGSECRET
do
  reject_placeholder "$secret_var"
done

if [[ "${ENABLE_OPENSEARCH:-false}" != "true" ]]; then
  echo "Production contract requires ENABLE_OPENSEARCH=true." >&2
  exit 1
fi

if [[ "${LAWWATCHER__RUNTIME__CAPABILITIES__OCR:-false}" != "true" ]]; then
  echo "Production contract requires OCR capability enabled." >&2
  exit 1
fi

if [[ "${LAWWATCHER__BOOTSTRAP__ENABLEDEMODATA:-false}" != "false" ]]; then
  echo "Demo data must stay disabled in the production contract." >&2
  exit 1
fi

echo "Production env contract validation passed for ${env_file}."
