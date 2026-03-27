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

find /bootstrap/sql -maxdepth 1 -type f -name '*.sql' | sort | while read -r script_path; do
  echo "Applying ${script_path}"
  "$sqlcmd_bin" -S "${sql_host},1433" -U sa -P "$sql_password" -C -b -d "$sql_database" -i "$script_path"
done
