#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

ensure_docker_on_path() {
  if command -v docker >/dev/null 2>&1; then
    return 0
  fi

  local candidates=(
    "/c/Program Files/Docker/Docker/resources/bin"
    "/c/Program Files/Docker/Docker/resources"
  )

  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -x "${candidate}/docker.exe" ]]; then
      export PATH="${candidate}:${PATH}"
      return 0
    fi
  done
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "Missing required command: $1" >&2
    exit 1
  }
}

json_eval() {
  local code="$1"
  node -e "const fs=require('fs'); const data=JSON.parse(fs.readFileSync(0,'utf8')); ${code}"
}

wait_http_ok() {
  local url="$1"
  local attempts="${2:-120}"
  local body

  for ((attempt=0; attempt<attempts; attempt++)); do
    body="$(curl -fsS --max-time 5 "$url" 2>/dev/null || true)"
    if [[ -n "$body" ]]; then
      printf '%s' "$body"
      return 0
    fi
    sleep 2
  done

  echo "HTTP endpoint did not become ready in time: $url" >&2
  exit 1
}

wait_search_projection() {
  local url="$1"
  local attempts="${2:-180}"
  local body
  local count

  for ((attempt=0; attempt<attempts; attempt++)); do
    body="$(curl -fsS --max-time 10 "$url" 2>/dev/null || true)"
    if [[ -n "$body" ]]; then
      count="$(printf '%s' "$body" | json_eval "const hits=Array.isArray(data.hits)?data.hits:[]; const count=hits.filter(hit => hit && (hit.type==='bill' || hit.type==='act')).length; process.stdout.write(String(count));")"
      if [[ "${count}" -gt 0 ]]; then
        printf '%s' "$body"
        return 0
      fi
    fi
    sleep 2
  done

  echo "Search projection did not return legislative hits in time: $url" >&2
  exit 1
}

wait_entity_completed() {
  local url="$1"
  local entity_id="$2"
  local attempts="${3:-120}"
  local body
  local status
  local matched

  for ((attempt=0; attempt<attempts; attempt++)); do
    body="$(curl -fsS --max-time 10 "$url" 2>/dev/null || true)"
    if [[ -n "$body" ]]; then
      status="$(JSON_PAYLOAD="$body" ENTITY_ID="$entity_id" node -e "const data=JSON.parse(process.env.JSON_PAYLOAD); const items=Array.isArray(data)?data:(data.value||[]); const target=process.env.ENTITY_ID; const match=items.find(item => String(item.id)===target); if (match) process.stdout.write(String(match.status||''));")"
      if [[ "$status" == "completed" ]]; then
        matched="$(JSON_PAYLOAD="$body" ENTITY_ID="$entity_id" node -e "const data=JSON.parse(process.env.JSON_PAYLOAD); const items=Array.isArray(data)?data:(data.value||[]); const target=process.env.ENTITY_ID; const match=items.find(item => String(item.id)===target); if (match) process.stdout.write(JSON.stringify(match));")"
        printf '%s' "$matched"
        return 0
      fi
      if [[ "$status" == "failed" ]]; then
        echo "Entity '$entity_id' at '$url' failed." >&2
        exit 1
      fi
    fi
    sleep 1
  done

  echo "Entity '$entity_id' at '$url' did not reach completed state in time." >&2
  exit 1
}

wait_notification_dispatch_for_recipient() {
  local url="$1"
  local recipient="$2"
  local attempts="${3:-120}"
  local body
  local found
  local matched

  for ((attempt=0; attempt<attempts; attempt++)); do
    body="$(curl -fsS --max-time 10 "$url" 2>/dev/null || true)"
    if [[ -n "$body" ]]; then
      found="$(JSON_PAYLOAD="$body" RECIPIENT="$recipient" node -e "const data=JSON.parse(process.env.JSON_PAYLOAD); const items=Array.isArray(data)?data:(data.value||[]); const target=process.env.RECIPIENT; process.stdout.write(items.some(item => item.recipient===target) ? '1' : '0');")"
      if [[ "$found" == "1" ]]; then
        matched="$(JSON_PAYLOAD="$body" RECIPIENT="$recipient" node -e "const data=JSON.parse(process.env.JSON_PAYLOAD); const items=Array.isArray(data)?data:(data.value||[]); const target=process.env.RECIPIENT; const match=items.find(item => item.recipient===target); if (match) process.stdout.write(JSON.stringify(match));")"
        printf '%s' "$matched"
        return 0
      fi
    fi
    sleep 1
  done

  echo "Notification dispatch for '$recipient' did not appear in time." >&2
  exit 1
}

ensure_docker_ollama_model() {
  local model="$1"
  shift
  local compose_args=("$@")

  wait_http_ok "http://127.0.0.1:11434/api/tags" 120 >/dev/null

  if docker "${compose_args[@]}" exec -T ollama ollama list | grep -Fq "$model"; then
    return 0
  fi

  docker "${compose_args[@]}" exec -T ollama ollama pull "$model" >/dev/null
}

docker_compose_json_array() {
  docker "$@" ps --format json | node -e "const fs=require('fs'); const payload=fs.readFileSync(0,'utf8').trim(); if (!payload) { process.stdout.write('[]'); process.exit(0); } const lines=payload.split(/\r?\n/).filter(Boolean); if (lines.length===1 && lines[0].trim().startsWith('[')) { process.stdout.write(lines[0]); } else { process.stdout.write(JSON.stringify(lines.map(line => JSON.parse(line)))); }"
}
