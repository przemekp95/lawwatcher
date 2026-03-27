#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker

cd "$repo_root"

docker compose \
  -f ops/compose/docker-compose.yml \
  -f ops/compose/docker-compose.full-host.yml \
  --env-file ops/env/full-host.env.example \
  --profile full-host \
  --profile opensearch \
  down --remove-orphans
