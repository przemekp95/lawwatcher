#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

cd "${repo_root}"

require_cmd git
require_cmd docker
require_cmd bash

remaining_ps1=()
while IFS= read -r file_path; do
  [[ -z "${file_path}" ]] && continue
  if [[ -e "${file_path}" ]]; then
    remaining_ps1+=("${file_path}")
  fi
done < <(git ls-files '*.ps1')

if ((${#remaining_ps1[@]} > 0)); then
  echo "Tracked PowerShell files are not allowed in the supported Docker-first contract:" >&2
  printf '%s\n' "${remaining_ps1[@]}" >&2
  exit 1
fi

supported_paths=(
  README.md
  docs/RUNBOOK.md
  .github
  ops
  src
  tests
  Directory.Build.props
  .dockerignore
  .gitattributes
)

blocked_markers=(
  'docker\.exe'
  'cygpath'
  'PROGRAMFILES'
  'ProgramW6432'
  'LOCALAPPDATA'
  'powershell'
  'pwsh'
  '\.ps1\b'
  'AppData'
  'LocalDB'
  'C:\\'
)

for marker in "${blocked_markers[@]}"; do
  if git grep -n -I -E "${marker}" -- "${supported_paths[@]}" ':(exclude)ops/verify-docker-contract.sh' >/tmp/lawwatcher-docker-contract-grep.txt; then
    echo "Found blocked host-specific marker '${marker}' in the supported Docker-first contract:" >&2
    cat /tmp/lawwatcher-docker-contract-grep.txt >&2
    rm -f /tmp/lawwatcher-docker-contract-grep.txt
    exit 1
  fi
done
rm -f /tmp/lawwatcher-docker-contract-grep.txt

while IFS= read -r file_path; do
  [[ -z "${file_path}" ]] && continue
  bash -n "${file_path}"
done < <(find ops -type f -name '*.sh' | sort)

while IFS= read -r eol_line; do
  [[ -z "${eol_line}" ]] && continue
  if [[ "${eol_line}" == *"i/crlf"* ]]; then
    echo "Tracked file has CRLF in git index and breaks the Docker-first cross-platform contract:" >&2
    echo "${eol_line}" >&2
    exit 1
  fi
done < <(git ls-files --eol '*.sh' '*.yml' '*.yaml' '*.example' '.dockerignore' '.gitattributes')

docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example config --services >/dev/null
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev-laptop.env.example --profile ai config --services >/dev/null
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example --profile full-host --profile opensearch config --services >/dev/null
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.full-host.yml --env-file ops/env/full-host.env.example --profile ai --profile full-host config --services >/dev/null

echo "Docker-first contract verification passed."
