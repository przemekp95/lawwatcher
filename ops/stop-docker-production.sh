#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

cd "$repo_root"
docker compose \
  -f ops/compose/docker-compose.yml \
  -f ops/compose/docker-compose.production.yml \
  --env-file ops/env/production.env.example \
  --profile production \
  --profile opensearch \
  down --remove-orphans
