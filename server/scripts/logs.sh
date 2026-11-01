#!/usr/bin/env bash
# 查运行日志（app_logs）。后端 ILogger 与小程序端上报都在这一张表。
#
# 用法：
#   bash server/scripts/logs.sh                 最近 20 条
#   bash server/scripts/logs.sh errors          最近的 error/critical
#   bash server/scripts/logs.sh warn            最近的 warning 及以上（含业务异常）
#   bash server/scripts/logs.sh client          最近的小程序端上报
#   bash server/scripts/logs.sh trace 0HNPMI…   按请求串联同一次调用的全部日志
#   bash server/scripts/logs.sh sql "category like 'MedBox.Infrastructure.Workers.%'"  自定义条件
#
# 说明：只读。日志由 DbLogWriter 批量写入，写失败不影响业务，所以偶尔可能缺条。
set -euo pipefail

CONTAINER="tigerrag-postgres-1"
PGUSER="tigerrag"
DB="medbox"
LIMIT="${LIMIT:-20}"

# 统一输出列，避免每次手改 SELECT
COLS="log_id, level, source, category, left(message, 70) AS message, http_path, left(openid, 18) AS openid, to_char(to_timestamp(ts_ms/1000) AT TIME ZONE 'Asia/Shanghai', 'MM-DD HH24:MI:SS') AS at"

run() {
  docker exec "$CONTAINER" psql -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -c "$1"
}

MODE="${1:-recent}"

case "$MODE" in
  recent)
    run "SELECT $COLS FROM app_logs ORDER BY log_id DESC LIMIT $LIMIT;"
    ;;
  errors)
    run "SELECT $COLS, left(exception, 300) AS exception FROM app_logs WHERE level_num >= 4 ORDER BY log_id DESC LIMIT $LIMIT;"
    ;;
  warn)
    run "SELECT $COLS FROM app_logs WHERE level_num >= 3 ORDER BY log_id DESC LIMIT $LIMIT;"
    ;;
  client)
    run "SELECT $COLS, page, left(exception, 200) AS stack FROM app_logs WHERE source = 'client' ORDER BY log_id DESC LIMIT $LIMIT;"
    ;;
  trace)
    TRACE="${2:-}"
    if [[ -z "$TRACE" ]]; then echo "用法: bash server/scripts/logs.sh trace <trace_id>" >&2; exit 1; fi
    run "SELECT $COLS, left(exception, 300) AS exception FROM app_logs WHERE trace_id = '$TRACE' ORDER BY log_id;"
    ;;
  sql)
    WHERE="${2:-}"
    if [[ -z "$WHERE" ]]; then echo "用法: bash server/scripts/logs.sh sql \"<where 条件>\"" >&2; exit 1; fi
    run "SELECT $COLS FROM app_logs WHERE $WHERE ORDER BY log_id DESC LIMIT $LIMIT;"
    ;;
  *)
    echo "未知模式: $MODE" >&2
    echo "可用: recent | errors | warn | client | trace <id> | sql \"<where>\"" >&2
    exit 1
    ;;
esac
