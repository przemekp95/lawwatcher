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
  docs/INSTALL.md
  docs/CONFIGURATION.md
  docs/BACKUP-RESTORE.md
  docs/UPGRADES.md
  docs/SUPPORT.md
  .github
  ops
  src
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

legacy_runtime_markers=(
  "full""-host"
  "dev""-laptop"
)

legacy_scope_markers=(
  "search"":read"
  "alerts"":read"
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

legacy_runtime_pattern="$(IFS='|'; printf '%s' "${legacy_runtime_markers[*]}")"
if git grep -n -I -E "\\b(${legacy_runtime_pattern})\\b" -- "${supported_paths[@]}" ':(exclude)ops/sql/**' >/tmp/lawwatcher-docker-contract-grep.txt; then
  echo "Found legacy runtime marker in the supported Docker-first contract:" >&2
  cat /tmp/lawwatcher-docker-contract-grep.txt >&2
  rm -f /tmp/lawwatcher-docker-contract-grep.txt
  exit 1
fi
rm -f /tmp/lawwatcher-docker-contract-grep.txt

legacy_scope_pattern="$(IFS='|'; printf '%s' "${legacy_scope_markers[*]}")"
if git grep -n -I -E "\\b(${legacy_scope_pattern})\\b" -- "${supported_paths[@]}" ':(exclude)ops/sql/**' >/tmp/lawwatcher-docker-contract-grep.txt; then
  echo "Found legacy integration read scope marker in the supported Docker-first contract:" >&2
  cat /tmp/lawwatcher-docker-contract-grep.txt >&2
  rm -f /tmp/lawwatcher-docker-contract-grep.txt
  exit 1
fi
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

tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

validated_production_env="${tmp_dir}/production-contract.env"
write_env_file_from_example \
  "ops/env/production.env.example" \
  "${validated_production_env}" \
  "SQLSERVER_SA_PASSWORD=ContractSqlServer!12345" \
  "RABBITMQ_DEFAULT_PASS=ContractRabbitMq!12345" \
  "MINIO_ROOT_PASSWORD=ContractMinio!12345" \
  "CONNECTIONSTRINGS__LAWWATCHERSQLSERVER=Server=sqlserver,1433;Database=LawWatcher;User Id=sa;Password=ContractSqlServer!12345;TrustServerCertificate=True;Encrypt=False" \
  "CONNECTIONSTRINGS__RABBITMQ=amqp://lawwatcher:ContractRabbitMq!12345@rabbitmq:5672/" \
  "STORAGE__MINIO__SECRETKEY=ContractMinio!12345" \
  "LAWWATCHER__BOOTSTRAP__SECRET=ContractBootstrapSecret!12345" \
  "LAWWATCHER__WEBHOOKS__SIGNINGSECRET=ContractWebhookSecret!12345"

bash ops/validate-production-env.sh --env-file "${validated_production_env}" >/dev/null

docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev.env.example config --services >/dev/null
docker compose -f ops/compose/docker-compose.yml --env-file ops/env/dev.env.example --profile ai config --services >/dev/null
docker compose -f ops/compose/docker-compose.yml -f ops/compose/docker-compose.production.yml --env-file "${validated_production_env}" --profile production --profile opensearch config --services >/dev/null

echo "Docker-first contract verification passed."
