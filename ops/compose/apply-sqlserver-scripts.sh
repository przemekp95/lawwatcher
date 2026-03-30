#!/usr/bin/env bash
set -euo pipefail

sqlcmd_bin="/opt/mssql-tools/bin/sqlcmd"
if [ ! -x "$sqlcmd_bin" ]; then
  sqlcmd_bin="/opt/mssql-tools18/bin/sqlcmd"
fi

if [ ! -x "$sqlcmd_bin" ]; then
  echo "sqlcmd was not found in the SQL bootstrap image." >&2
  exit 1
fi

sql_host="${SQLSERVER_HOST:-sqlserver}"
sql_password="${SQLSERVER_SA_PASSWORD:?SQLSERVER_SA_PASSWORD is required}"
sql_database="${SQLSERVER_DATABASE:-LawWatcher}"
lawwatcher_product_version="${LAWWATCHER_PRODUCT_VERSION:-dev-local}"

sql_scalar() {
  local query="$1"
  "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -d "$sql_database" -h -1 -W -Q "SET NOCOUNT ON; ${query}" \
    | tr -d '\r' \
    | xargs
}

for attempt in $(seq 1 90); do
  if "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi

  if [ "$attempt" -eq 90 ]; then
    echo "SQL Server did not become ready in time." >&2
    exit 1
  fi

  sleep 2
done

"$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -b -Q "IF DB_ID(N'${sql_database}') IS NULL CREATE DATABASE [${sql_database}];"

for attempt in $(seq 1 90); do
  if "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -d "$sql_database" -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi

  if [ "$attempt" -eq 90 ]; then
    echo "SQL Server database '${sql_database}' did not become ready in time." >&2
    exit 1
  fi

  sleep 2
done

"$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -b -d "$sql_database" -Q "
IF SCHEMA_ID(N'lawwatcher') IS NULL
    EXEC(N'CREATE SCHEMA [lawwatcher]');

IF OBJECT_ID(N'[lawwatcher].[schema_migrations]', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[schema_migrations]
    (
        [script_name] NVARCHAR(260) NOT NULL PRIMARY KEY,
        [applied_at_utc] DATETIME2 NOT NULL,
        [product_version] NVARCHAR(64) NULL,
        [applied_mode] NVARCHAR(32) NOT NULL,
        [notes] NVARCHAR(400) NULL
    );
END
"

applied_count="$(sql_scalar "SELECT COUNT(1) FROM [lawwatcher].[schema_migrations];")"
has_existing_schema="$(sql_scalar "SELECT CASE WHEN OBJECT_ID(N'[lawwatcher].[event_store]', N'U') IS NULL THEN 0 ELSE 1 END;")"

if [[ "${applied_count}" == "0" && "${has_existing_schema}" == "1" ]]; then
  echo "Detected an existing LawWatcher schema without a migration ledger. Baselining current numbered SQL scripts."
  find /bootstrap/sql -maxdepth 1 -type f -name '*.sql' | sort | while read -r script_path; do
    script_name="$(basename "${script_path}")"
    escaped_script_name="${script_name//\'/''}"
    "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -b -d "$sql_database" -Q "
IF NOT EXISTS (SELECT 1 FROM [lawwatcher].[schema_migrations] WHERE [script_name] = N'${escaped_script_name}')
BEGIN
    INSERT INTO [lawwatcher].[schema_migrations] ([script_name], [applied_at_utc], [product_version], [applied_mode], [notes])
    VALUES (N'${escaped_script_name}', SYSUTCDATETIME(), N'${lawwatcher_product_version}', N'baseline', N'Baselined from pre-1.0 schema.');
END
"
  done
fi

find /bootstrap/sql -maxdepth 1 -type f -name '*.sql' | sort | while read -r script_path; do
  script_name="$(basename "${script_path}")"
  escaped_script_name="${script_name//\'/''}"
  already_applied="$(sql_scalar "SELECT COUNT(1) FROM [lawwatcher].[schema_migrations] WHERE [script_name] = N'${escaped_script_name}';")"

  if [[ "${already_applied}" != "0" ]]; then
    echo "Skipping ${script_name}; already tracked in schema_migrations."
    continue
  fi

  echo "Applying ${script_path}"
  "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -b -d "$sql_database" -i "$script_path"
  "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -b -d "$sql_database" -Q "
INSERT INTO [lawwatcher].[schema_migrations] ([script_name], [applied_at_utc], [product_version], [applied_mode], [notes])
VALUES (N'${escaped_script_name}', SYSUTCDATETIME(), N'${lawwatcher_product_version}', N'apply', NULL);
"
done
