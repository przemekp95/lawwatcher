#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
source "${script_dir}/lib/docker-smoke-common.sh"

ensure_docker_on_path
require_cmd docker
require_cmd curl
require_cmd node

build_local=false
sql_sa_password="ChangeMe!123456"

while (($# > 0)); do
  case "$1" in
    --build-local)
      build_local=true
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

cd "${repo_root}"

tmp_dir="$(mktemp -d)"
project_name="lawwatcher-retention-$(random_suffix)"
env_file="${tmp_dir}/dev-laptop.env"
summary_path="${repo_root}/output/smoke/retention-smoke-summary.json"

api_port="$(get_free_port)"
portal_port="$(get_free_port)"
sql_port="$(get_free_port)"
rabbit_amqp_port="$(get_free_port)"
rabbit_management_port="$(get_free_port)"
minio_api_port="$(get_free_port)"
minio_console_port="$(get_free_port)"
worker_lite_health_port="$(get_free_port)"

write_env_file_from_example \
  "ops/env/dev-laptop.env.example" \
  "${env_file}" \
  "API_HOST_PORT=${api_port}" \
  "PORTAL_HOST_PORT=${portal_port}" \
  "SQLSERVER_HOST_PORT=${sql_port}" \
  "RABBITMQ_AMQP_PORT=${rabbit_amqp_port}" \
  "RABBITMQ_MANAGEMENT_PORT=${rabbit_management_port}" \
  "MINIO_API_PORT=${minio_api_port}" \
  "MINIO_CONSOLE_PORT=${minio_console_port}" \
  "WORKER_LITE_HEALTH_PORT=${worker_lite_health_port}" \
  "LAWWATCHER__SEEDDATA__ENABLEDEFAULTAPICLIENTSEED=true"

compose_args=(
  compose
  -p "${project_name}"
  -f ops/compose/docker-compose.yml
  --env-file "${env_file}"
)

if [[ "${build_local}" == "true" ]]; then
  compose_args+=(-f ops/compose/docker-compose.build.yml)
fi

cleanup() {
  docker "${compose_args[@]}" down --remove-orphans >/dev/null 2>&1 || true
  rm -rf "${tmp_dir}"
}
trap cleanup EXIT

if [[ "${build_local}" == "true" ]]; then
  docker "${compose_args[@]}" up -d --build >/dev/null
else
  pull_compose_images_or_use_local "${compose_args[@]}"
  docker "${compose_args[@]}" up -d >/dev/null
fi

wait_http_ok "http://127.0.0.1:${api_port}/health/ready" >/dev/null

task_id="$(node -e "process.stdout.write(require('crypto').randomUUID());")"
subject_id="$(node -e "process.stdout.write(require('crypto').randomUUID());")"
seed_sql="$(cat <<SQL
INSERT INTO [lawwatcher].[ai_enrichment_tasks]
(
  [task_id],
  [kind],
  [subject_type],
  [subject_id],
  [subject_title],
  [status],
  [model],
  [content],
  [error],
  [citations_json],
  [requested_at_utc],
  [started_at_utc],
  [completed_at_utc],
  [failed_at_utc]
)
VALUES
(
  '${task_id}',
  'act-summary',
  'act',
  '${subject_id}',
  'Retention smoke task',
  'completed',
  'retention-smoke-model',
  'Retention smoke content',
  NULL,
  '[]',
  DATEADD(day, -30, SYSUTCDATETIME()),
  DATEADD(day, -30, SYSUTCDATETIME()),
  DATEADD(day, -30, SYSUTCDATETIME()),
  NULL
);
SQL
)"
docker "${compose_args[@]}" exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "${sql_sa_password}" -Q "${seed_sql}" >/dev/null

retention_payload='{"publishedOutboxRetentionHours":24,"processedInboxRetentionHours":24,"eventFeedRetentionHours":24,"searchDocumentsRetentionHours":24,"aiTasksRetentionHours":24,"documentArtifactsRetentionHours":24}'
retention_json="$(curl -fsS --max-time 20 -X POST "http://127.0.0.1:${api_port}/v1/system/maintenance/retention" \
  -H "Authorization: Bearer portal-integrator-demo-token" \
  -H "Content-Type: application/json" \
  -d "${retention_payload}")"

deleted_ai_tasks="$(printf '%s' "${retention_json}" | json_eval "process.stdout.write(String(data.deletedAiTasksCount));")"
document_artifacts_applied="$(printf '%s' "${retention_json}" | json_eval "process.stdout.write(String(Boolean(data.documentArtifactsRetentionApplied)));")"
remaining_ai_tasks="$(docker "${compose_args[@]}" exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "${sql_sa_password}" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [lawwatcher].[ai_enrichment_tasks] WHERE [task_id] = '${task_id}';" | tr -d '\r' | tail -n 1 | xargs)"

if [[ "${deleted_ai_tasks}" -lt 1 ]]; then
  echo "Expected retention to delete at least one completed AI task." >&2
  exit 1
fi

if [[ "${document_artifacts_applied}" != "False" ]]; then
  echo "Expected document artifact retention to stay unavailable until safe ownership metadata exists." >&2
  exit 1
fi

if [[ "${remaining_ai_tasks}" != "0" ]]; then
  echo "Expected seeded AI retention row to be deleted, but ${remaining_ai_tasks} row(s) remain." >&2
  exit 1
fi

mkdir -p "$(dirname "${summary_path}")"

TASK_ID="${task_id}" RETENTION_JSON="${retention_json}" node -e "const result=JSON.parse(process.env.RETENTION_JSON); const summary={verifiedAtUtc:new Date().toISOString(), seededAiTaskId:process.env.TASK_ID, deletedAiTasksCount:result.deletedAiTasksCount, aiTasksRetentionApplied:result.aiTasksRetentionApplied, documentArtifactsRetentionApplied:result.documentArtifactsRetentionApplied, documentArtifactsRetentionReason:result.documentArtifactsRetentionReason}; process.stdout.write(JSON.stringify(summary, null, 2));" | tee "${summary_path}"
