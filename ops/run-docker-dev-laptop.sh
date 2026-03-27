#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker

include_ai=false
build_local=false
ai_model="llama3.2:1b"

while (($# > 0)); do
  case "$1" in
    --include-ai)
      include_ai=true
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
  --env-file ops/env/dev-laptop.env.example
)

if [[ "$build_local" == "true" ]]; then
  compose_args+=(-f ops/compose/docker-compose.build.yml)
fi

if [[ "$include_ai" == "true" ]]; then
  compose_args+=(--profile ai)
fi

if [[ "$build_local" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d
fi

if [[ "$include_ai" == "true" ]]; then
  ensure_docker_ollama_model "$ai_model" "${compose_args[@]}"
fi
