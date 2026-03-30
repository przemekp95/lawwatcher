#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker

model="${1:-llama3.2:1b}"

cd "$repo_root"

compose_args=(
  compose
  -f ops/compose/docker-compose.yml
  --env-file ops/env/dev.env.example
  --profile ai
)

ensure_docker_ollama_model "$model" "${compose_args[@]}"
