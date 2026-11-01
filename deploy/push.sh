#!/usr/bin/env bash
# deploy/push.sh —— 从本地把代码同步到服务器（不依赖 rsync）
#
# 用法：
#   MEDBOX_HOST=root@1.2.3.4 bash deploy/push.sh              # 只同步代码
#   MEDBOX_HOST=root@1.2.3.4 bash deploy/push.sh api          # 同步后只更新 API
#   MEDBOX_HOST=root@1.2.3.4 bash deploy/push.sh deploy       # 同步后跑一键部署
#
# 也可把 MEDBOX_HOST 写进 ~/.ssh/config 的 Host 别名，然后 MEDBOX_HOST=medbox。
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

HOST="${MEDBOX_HOST:-}"
[[ -n "$HOST" ]] || die "缺少 MEDBOX_HOST，例如：MEDBOX_HOST=root@1.2.3.4 bash deploy/push.sh"

REMOTE_DIR="${MEDBOX_HOME:-/opt/medbox}"
ACTION="${1:-}"

log "同步 server/ db/ deploy/ → $HOST:$REMOTE_DIR"
ssh "$HOST" "mkdir -p '$REMOTE_DIR'"

# 只传部署需要的目录；bin/obj 是本机 Windows 产物，传上去不仅没用还会污染构建
# 特别排除 deploy/.env：服务器上的口令/秘钥绝不能被本地文件覆盖
tar -czf - -C "$ROOT_DIR" \
  --exclude='server/**/bin' \
  --exclude='server/**/obj' \
  --exclude='server/**/TestResults' \
  --exclude='deploy/.env' \
  --exclude='deploy/.env.bak*' \
  server db deploy \
  | ssh "$HOST" "tar -xzf - -C '$REMOTE_DIR'"

ok "代码已同步"

case "$ACTION" in
  "") ;;
  api)    ssh -t "$HOST" "cd '$REMOTE_DIR' && bash deploy/api-up.sh" ;;
  infra)  ssh -t "$HOST" "cd '$REMOTE_DIR' && bash deploy/infra-up.sh" ;;
  deploy) ssh -t "$HOST" "cd '$REMOTE_DIR' && bash deploy/deploy.sh" ;;
  *)
    warn "未知动作：$ACTION（支持 api / infra / deploy，留空则只同步）"
    exit 1
    ;;
esac
