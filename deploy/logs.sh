#!/usr/bin/env bash
# deploy/logs.sh —— 看容器日志
#   bash deploy/logs.sh api      API 日志（默认）
#   bash deploy/logs.sh db       数据库日志
#   bash deploy/logs.sh edge     HTTPS 反代 / 证书签发日志
#   bash deploy/logs.sh api 500  只看最近 500 行（不跟随）
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

TARGET="${1:-api}"
TAIL="${2:-200}"

case "$TARGET" in
  api|app)  docker logs --tail "$TAIL" "$API_CONTAINER" ;;
  db|pg)    docker logs --tail "$TAIL" "$DB_CONTAINER" ;;
  edge|tls) docker logs --tail "$TAIL" "${EDGE_CONTAINER:-medbox-caddy}" ;;
  *) die "用法：bash deploy/logs.sh [api|db|edge] [行数]" ;;
esac
