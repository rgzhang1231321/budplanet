#!/usr/bin/env bash
# deploy/deploy.sh —— 一键部署入口（全部子步骤都幂等）
#
#   bash deploy/deploy.sh            首次/日常：数据库(幂等) → 迁移 → API → HTTPS 反代
#   bash deploy/deploy.sh api        只更新 API（等价 api-up.sh）
#   bash deploy/deploy.sh infra      只装数据库（等价 infra-up.sh，已存在则不动）
#   bash deploy/deploy.sh migrate    只跑 SQL 迁移
#   bash deploy/deploy.sh edge       只更新 HTTPS 反代
#
# 关键约束：这个入口调用 infra 时走 infra-up.sh 的幂等分支，
# 已存在的数据库容器只会被 start（甚至不 start），绝不会被 recreate。
#
# 注意：2 核 1G 机器上这里构建 API 会 OOM —— 小内存机器请用 deploy/release.sh
# （本地构建 + 传镜像），本脚本的 api 步骤会自己拦住。
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

CMD="${1:-all}"
shift || true

case "$CMD" in
  all)
    log "① 基础设施（数据库，已存在则跳过）"
    bash "$DEPLOY_DIR/infra-up.sh"
    log "② 数据库迁移"
    bash "$DEPLOY_DIR/migrate.sh"
    log "③ 应用（API 镜像 + 替换容器）"
    bash "$DEPLOY_DIR/api-up.sh" "$@"
    log "④ HTTPS 反向代理"
    bash "$DEPLOY_DIR/edge-up.sh"
    ;;
  api)     bash "$DEPLOY_DIR/api-up.sh" "$@" ;;
  infra)   bash "$DEPLOY_DIR/infra-up.sh" "$@" ;;
  migrate) bash "$DEPLOY_DIR/migrate.sh" "$@" ;;
  edge)    bash "$DEPLOY_DIR/edge-up.sh" "$@" ;;
  *)
    cat <<EOF
用法：
  bash deploy/deploy.sh            一键（数据库幂等 → 迁移 → API → 反代）
  bash deploy/deploy.sh api        只更新 API
  bash deploy/deploy.sh infra      只装数据库
  bash deploy/deploy.sh migrate    只跑迁移
  bash deploy/deploy.sh edge       只更新 HTTPS 反代

小内存（<2G）机器更新 API 请用本地执行：
  MEDBOX_HOST=root@1.2.3.4 bash deploy/release.sh
EOF
    exit 1
    ;;
esac
