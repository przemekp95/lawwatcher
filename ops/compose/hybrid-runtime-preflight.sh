#!/usr/bin/env sh
set -eu

enabled="$(printf '%s' "${ENABLE_OPENSEARCH:-false}" | tr '[:upper:]' '[:lower:]')"
if [ "${enabled}" != "true" ]; then
  echo "Hybrid search disabled; skipping OpenSearch and Ollama embedding preflight."
  exit 0
fi

opensearch_base_url="${SEARCH__OPENSEARCH__BASEURL:-}"
ollama_base_url="${AI__OLLAMA__BASEURL:-}"
embedding_model="${AI__OLLAMA__EMBEDDINGMODEL:-}"

if [ -z "${opensearch_base_url}" ]; then
  echo "SEARCH__OPENSEARCH__BASEURL must be set when hybrid search is enabled." >&2
  exit 1
fi

if [ -z "${ollama_base_url}" ]; then
  echo "AI__OLLAMA__BASEURL must be set when hybrid search is enabled." >&2
  exit 1
fi

if [ -z "${embedding_model}" ]; then
  echo "AI__OLLAMA__EMBEDDINGMODEL must be set when hybrid search is enabled." >&2
  exit 1
fi

wait_for_http_ok() {
  url="$1"
  description="$2"
  attempt=1
  max_attempts=90

  while [ "${attempt}" -le "${max_attempts}" ]; do
    if curl -fsS "${url}" >/dev/null 2>&1; then
      return 0
    fi

    sleep 2
    attempt=$((attempt + 1))
  done

  echo "Timed out waiting for ${description} at ${url}." >&2
  return 1
}

echo "Waiting for OpenSearch cluster health..."
wait_for_http_ok "${opensearch_base_url}/_cluster/health?wait_for_status=yellow&timeout=1s" "OpenSearch cluster health"

echo "Waiting for Ollama API..."
wait_for_http_ok "${ollama_base_url}/api/tags" "Ollama API"

echo "Ensuring Ollama embedding model '${embedding_model}' is available..."
curl -fsS \
  -H "Content-Type: application/json" \
  -d "{\"name\":\"${embedding_model}\"}" \
  "${ollama_base_url}/api/pull" >/dev/null

echo "Hybrid search preflight completed."
