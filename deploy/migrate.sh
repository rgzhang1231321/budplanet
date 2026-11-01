#!/usr/bin/env bash
# deploy/migrate.sh —— 应用 db/migrations 下的 SQL（append-only）
#
# 只对数据库执行 SQL，不创建/重启/重建任何容器。
# 幂等：db/apply.sh 用 schema_migrations 账本 + 校验和判断是否已执行。
#
# 用法：bash deploy/migrate.sh
#      bash deploy/migrate.sh --dry-run    # 只校验已执行脚本有没有被篡改
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

container_running "$DB_CONTAINER" \
  || die "数据库容器 $DB_CONTAINER 没在运行。先执行：bash deploy/infra-up.sh"

log "应用数据库迁移（容器 $DB_CONTAINER / 库 $POSTGRES_DB）"
CONTAINER="$DB_CONTAINER" PGUSER="$POSTGRES_USER" DB="$POSTGRES_DB" \
  bash "$ROOT_DIR/db/apply.sh" "$@"
