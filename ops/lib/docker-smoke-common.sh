#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

ensure_docker_on_path() {
  require_cmd docker
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

wait_http_status() {
  local url="$1"
  local attempts="${2:-120}"
  local status

  for ((attempt=0; attempt<attempts; attempt++)); do
    status="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    if [[ "${status}" =~ ^2[0-9][0-9]$ ]]; then
      printf '%s' "${status}"
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

wait_compose_logs_match() {
  local pattern="$1"
  local attempts="${2:-60}"
  shift 2

  local compose_args=()
  while (($# > 0)); do
    if [[ "$1" == "--" ]]; then
      shift
      break
    fi
    compose_args+=("$1")
    shift
  done
  local services=("$@")
  local logs=""

  if ((${#compose_args[@]} == 0)); then
    echo "wait_compose_logs_match requires docker compose arguments before '--'." >&2
    exit 1
  fi

  for ((attempt=0; attempt<attempts; attempt++)); do
    if ((${#services[@]} > 0)); then
      logs="$(docker "${compose_args[@]}" logs --no-color "${services[@]}" 2>&1 || true)"
    else
      logs="$(docker "${compose_args[@]}" logs --no-color 2>&1 || true)"
    fi

    if printf '%s' "$logs" | grep -Eq "$pattern"; then
      printf '%s' "$logs"
      return 0
    fi

    sleep 2
  done

  echo "Compose logs did not contain the expected pattern: $pattern" >&2
  printf '%s\n' "$logs" >&2
  exit 1
}

resolve_compose_published_port() {
  local service_name="$1"
  local container_port="$2"
  shift 2
  local compose_args=("$@")
  local mapping=""
  local published_port=""

  for ((attempt=0; attempt<30; attempt++)); do
    mapping="$(docker "${compose_args[@]}" port "${service_name}" "${container_port}" 2>/dev/null | head -n 1 | tr -d '\r' || true)"
    if [[ -n "${mapping}" ]]; then
      published_port="${mapping##*:}"
      if [[ "${published_port}" =~ ^[0-9]+$ ]]; then
        printf '%s' "${published_port}"
        return 0
      fi
    fi
    sleep 1
  done

  echo "Unable to resolve published port for ${service_name}:${container_port}" >&2
  exit 1
}

ensure_docker_ollama_model() {
  local model="$1"
  shift
  local compose_args=("$@")
  local ollama_host_port

  ollama_host_port="$(resolve_compose_published_port ollama 11434/tcp "${compose_args[@]}")"

  wait_http_ok "http://127.0.0.1:${ollama_host_port}/api/tags" 120 >/dev/null

  if docker "${compose_args[@]}" exec -T ollama ollama list | grep -Fq "$model"; then
    return 0
  fi

  docker "${compose_args[@]}" exec -T ollama ollama pull "$model" >/dev/null 2>&1
}

docker_compose_json_array() {
  docker "$@" ps --format json | node -e "const fs=require('fs'); const payload=fs.readFileSync(0,'utf8').trim(); if (!payload) { process.stdout.write('[]'); process.exit(0); } const lines=payload.split(/\r?\n/).filter(Boolean); if (lines.length===1 && lines[0].trim().startsWith('[')) { process.stdout.write(lines[0]); } else { process.stdout.write(JSON.stringify(lines.map(line => JSON.parse(line)))); }"
}

pull_compose_images_or_use_local() {
  local pull_log
  pull_log="$(mktemp)"

  if docker "$@" pull >"${pull_log}" 2>&1; then
    rm -f "${pull_log}"
    return 0
  fi

  local images_output
  images_output="$(docker "$@" config --images)"
  local missing_images=()
  local image

  while IFS= read -r image; do
    [[ -z "${image}" ]] && continue
    if ! docker image inspect "${image}" >/dev/null 2>&1; then
      missing_images+=("${image}")
    fi
  done <<< "${images_output}"

  if ((${#missing_images[@]} > 0)); then
    cat "${pull_log}" >&2
    rm -f "${pull_log}"
    printf 'Failed to pull compose images and missing local images: %s\n' "${missing_images[*]}" >&2
    exit 1
  fi

  rm -f "${pull_log}"
  echo "Compose image pull failed; using local cached images." >&2
}

random_suffix() {
  node -e "process.stdout.write(require('crypto').randomUUID().replace(/-/g,'').slice(0, 12));"
}

get_free_port() {
  node -e "const net=require('net'); const server=net.createServer(); server.listen(0, '127.0.0.1', () => { process.stdout.write(String(server.address().port)); server.close(); });"
}

write_env_file_from_example() {
  local source_path="$1"
  local target_path="$2"
  shift 2

  node - "$source_path" "$target_path" "$@" <<'NODE'
const fs = require('fs');
const [sourcePath, targetPath, ...entries] = process.argv.slice(2);
const overrideMap = new Map();

for (const entry of entries) {
  const separatorIndex = entry.indexOf('=');
  if (separatorIndex <= 0) {
    throw new Error(`Invalid env override: ${entry}`);
  }

  overrideMap.set(entry.slice(0, separatorIndex), entry.slice(separatorIndex + 1));
}

const sourceLines = fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/);
const outputLines = [];
const appliedKeys = new Set();

for (const line of sourceLines) {
  if (!line.includes('=') || /^\s*#/.test(line)) {
    outputLines.push(line);
    continue;
  }

  const separatorIndex = line.indexOf('=');
  const key = line.slice(0, separatorIndex);
  if (overrideMap.has(key)) {
    outputLines.push(`${key}=${overrideMap.get(key)}`);
    appliedKeys.add(key);
  } else {
    outputLines.push(line);
  }
}

for (const [key, value] of overrideMap.entries()) {
  if (!appliedKeys.has(key)) {
    outputLines.push(`${key}=${value}`);
  }
}

fs.writeFileSync(targetPath, `${outputLines.join('\n').replace(/\n*$/, '\n')}`);
NODE
}

ensure_playwright_chromium() {
  require_cmd npx

  local cache_root
  if [[ -n "${PLAYWRIGHT_BROWSERS_PATH:-}" ]]; then
    cache_root="${PLAYWRIGHT_BROWSERS_PATH}"
  elif [[ -n "${XDG_CACHE_HOME:-}" ]]; then
    cache_root="${XDG_CACHE_HOME}/ms-playwright"
  elif [[ -n "${HOME:-}" ]]; then
    cache_root="${HOME}/.cache/ms-playwright"
  else
    cache_root=""
  fi

  if [[ -n "${cache_root}" && -d "${cache_root}" ]] && find "${cache_root}" -maxdepth 1 -type d -name 'chromium-*' | grep -q .; then
    return 0
  fi

  npx -y -p @playwright/test playwright install chromium >/dev/null
}

resolve_npx_package_node_path() {
  local package_name="$1"
  require_cmd npm
  require_cmd node

  local cache_root
  cache_root="$(npm config get cache | tr -d '\r')"

  node - "${cache_root}" "${package_name}" <<'NODE'
const fs = require('fs');
const path = require('path');

const [cacheRoot, packageName] = process.argv.slice(2);
const npxRoot = path.join(cacheRoot, '_npx');

function findNewestNodeModules(root, packageName) {
  if (!fs.existsSync(root)) {
    return '';
  }

  const stack = [root];
  let best = { mtimeMs: -1, nodeModulesPath: '' };

  while (stack.length > 0) {
    const current = stack.pop();
    let entries = [];
    try {
      entries = fs.readdirSync(current, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const entry of entries) {
      if (!entry.isDirectory()) {
        continue;
      }

      const fullPath = path.join(current, entry.name);
      const packageJsonPath = path.join(fullPath, 'node_modules', ...packageName.split('/'), 'package.json');
      if (fs.existsSync(packageJsonPath)) {
        const stats = fs.statSync(packageJsonPath);
        if (stats.mtimeMs > best.mtimeMs) {
          best = {
            mtimeMs: stats.mtimeMs,
            nodeModulesPath: path.join(fullPath, 'node_modules')
          };
        }
      }

      stack.push(fullPath);
    }
  }

  return best.nodeModulesPath;
}

process.stdout.write(findNewestNodeModules(npxRoot, packageName));
NODE
}
