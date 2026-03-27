#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker
require_cmd curl

include_opensearch=false
build_local=false
ai_model="llama3.2:1b"
embedding_model="nomic-embed-text"

while (($# > 0)); do
  case "$1" in
    --include-opensearch)
      include_opensearch=true
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
    --embedding-model)
      embedding_model="$2"
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
  -f ops/compose/docker-compose.full-host.yml
  --env-file ops/env/full-host.env.example
  --profile full-host
)

if [[ "$build_local" == "true" ]]; then
  compose_args+=(
    -f ops/compose/docker-compose.build.yml
    -f ops/compose/docker-compose.full-host.build.yml
  )
fi

if [[ "$include_opensearch" == "true" ]]; then
  compose_args+=(--profile opensearch)
fi

docker "${compose_args[@]}" down --remove-orphans || true
docker compose \
  -f ops/compose/docker-compose.yml \
  -f ops/compose/docker-compose.full-host.yml \
  --env-file ops/env/full-host.env.example \
  rm -f -s worker-ai worker-lite || true

if [[ "$build_local" == "true" ]]; then
  docker compose \
    -f ops/compose/docker-compose.yml \
    -f ops/compose/docker-compose.full-host.yml \
    -f ops/compose/docker-compose.build.yml \
    -f ops/compose/docker-compose.full-host.build.yml \
    --env-file ops/env/full-host.env.example \
    rm -f -s worker-ai worker-lite || true
fi

if [[ "$build_local" != "true" ]]; then
  pull_compose_images_or_use_local "${compose_args[@]}"
fi

if [[ "$include_opensearch" == "true" ]]; then
  docker "${compose_args[@]}" up -d opensearch ollama
  wait_http_ok "http://127.0.0.1:9200/_cluster/health?wait_for_status=yellow&timeout=1s" >/dev/null
else
  docker "${compose_args[@]}" up -d ollama
fi

ensure_docker_ollama_model "$ai_model" "${compose_args[@]}"
ensure_docker_ollama_model "$embedding_model" "${compose_args[@]}"

if [[ "$build_local" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build --remove-orphans
else
  docker "${compose_args[@]}" up -d --remove-orphans
fi
