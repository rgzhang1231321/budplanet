#!/usr/bin/env bash
# deploy/infra-up.sh —— 只负责数据库（基础设施），独立于 API
#
# 幂等语义（这是本脚本的核心）：
#   * 容器已存在 → 什么都不改；只在它"停着"时 start，绝不 up/recreate
#   * 容器不存在 → 用 docker-compose.infra.yml 首次创建
#   * 只有显式 --recreate 才会重建（数据在宿主机目录，不会丢）
#
# 用法：bash deploy/infra-up.sh
#      bash deploy/infra-up.sh --recreate    # 改了 postgres 参数才需要
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

RECREATE=0
[[ "${1:-}" == "--recreate" ]] && RECREATE=1
[[ $# -gt 0 && "${1:-}" != "--recreate" ]] && die "未知参数：$1（只支持 --recreate）"

ensure_network

if container_exists "$DB_CONTAINER"; then
  if [[ $RECREATE -eq 0 ]]; then
    ok "数据库容器 $DB_CONTAINER 已存在 —— 不做任何改动"
    if container_running "$DB_CONTAINER"; then
      ok "运行中（数据目录 ${DATA_ROOT}/pgdata）"
    else
      log "容器当前是停止状态，仅 start（不重建、不改配置）"
      docker start "$DB_CONTAINER" >/dev/null
    fi
    wait_db_ready
    exit 0
  fi

  warn "--recreate：将删除并重建 $DB_CONTAINER"
  warn "数据在宿主机目录 ${DATA_ROOT}/pgdata，bind mount 不会删；仍是高危操作。"
  read -r -p "确认重建？输入 yes 继续：" confirm
  [[ "$confirm" == "yes" ]] || die "已取消"
  dc_infra down --remove-orphans || true
fi

log "首次创建数据库容器（镜像 postgres:18-alpine，数据目录 ${DATA_ROOT}/pgdata）"
dc_infra up -d
wait_db_ready

cat <<EOF

$(ok "数据库已就绪")

  容器：$DB_CONTAINER（project medbox-infra，restart=unless-stopped）
  数据：${DATA_ROOT}/pgdata/data
  连接：127.0.0.1:${PG_PORT:-5432}/${POSTGRES_DB}（仅本机，公网不可达）

下一步：bash deploy/migrate.sh 应用表结构
EOF
