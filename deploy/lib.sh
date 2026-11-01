#!/usr/bin/env bash
# deploy/lib.sh —— 部署脚本公共函数（被 source，不单独执行）
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$DEPLOY_DIR/.." && pwd)"
ENV_FILE="$DEPLOY_DIR/.env"
NETWORK_NAME="medbox-net"

if [[ -t 1 ]]; then
  C_BLUE=$'\033[1;34m'; C_GREEN=$'\033[1;32m'; C_YELL=$'\033[1;33m'; C_RED=$'\033[1;31m'; C_OFF=$'\033[0m'
else
  C_BLUE=''; C_GREEN=''; C_YELL=''; C_RED=''; C_OFF=''
fi

log()  { printf '%s▶%s %s\n' "$C_BLUE" "$C_OFF" "$*"; }
ok()   { printf '%s✓%s %s\n' "$C_GREEN" "$C_OFF" "$*"; }
warn() { printf '%s!%s %s\n' "$C_YELL" "$C_OFF" "$*" >&2; }
die()  { printf '%s✗%s %s\n' "$C_RED" "$C_OFF" "$*" >&2; exit 1; }

load_env() {
  [[ -f "$ENV_FILE" ]] || die "缺少 $ENV_FILE —— 先执行：bash deploy/setup-host.sh"
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
  : "${DATA_ROOT:=/data/medbox}"
  : "${DB_CONTAINER:=medbox-db}"
  : "${API_CONTAINER:=medbox-api}"
  : "${EDGE_CONTAINER:=medbox-caddy}"
  : "${POSTGRES_DB:=medbox}"
  : "${POSTGRES_USER:=medbox}"
  [[ "${POSTGRES_PASSWORD:-}" != "__GENERATED__" && -n "${POSTGRES_PASSWORD:-}" ]] \
    || die "POSTGRES_PASSWORD 还没生成：重新执行 bash deploy/setup-host.sh"
  [[ "${JWT__SECRET:-}" != "__GENERATED__" && -n "${JWT__SECRET:-}" ]] \
    || die "JWT__SECRET 还没生成：重新执行 bash deploy/setup-host.sh"
}

require_docker() {
  command -v docker >/dev/null 2>&1 || die "未安装 docker —— 先执行：bash deploy/setup-host.sh"
  docker compose version >/dev/null 2>&1 || die "缺少 docker compose 插件 —— 先执行：bash deploy/setup-host.sh"
  docker info >/dev/null 2>&1 || die "当前用户无权访问 docker（试试 sudo，或把用户加入 docker 组）"
}

# 生产环境 API 有两条启动硬校验（Program.cs）：JWT__SECRET 至少 32 字符、Wx:AppSecret 非空。
# JWT 已经在 load_env 里拦住了；AppSecret 为空的话，容器会**启动即崩**，
# 而 api-up.sh 只会在最后一步白等 90 秒健康检查超时 —— 这里提前拦住并说清怎么填。
# 只给 api-up.sh 调用：backup/status/logs 这些不该因为缺 secret 就用不了。
require_prod_appsecret() {
  [[ -n "${WX__SECRET:-}" ]] || die "WX__SECRET 还没填 —— 生产环境 API 会拒绝启动（防所有用户塌成同一个 openid）。
  小程序后台 → 开发管理 → 开发设置 → AppSecret，填进 $ENV_FILE 的 WX__SECRET= 这一行，再重跑。
  ⚠️ compose 里 ASPNETCORE_ENVIRONMENT 是硬编码 Production 的，没有"跳过校验"的开关。"
}

# infra 与 api 用不同的 compose project：API 的任何操作都命中不了数据库容器
dc_infra() {
  docker compose -p medbox-infra --project-directory "$DEPLOY_DIR" \
    -f "$DEPLOY_DIR/docker-compose.infra.yml" "$@"
}
dc_api() {
  docker compose -p medbox-api --project-directory "$DEPLOY_DIR" \
    -f "$DEPLOY_DIR/docker-compose.api.yml" "$@"
}
# 边缘层（Caddy）：第三个 project，保证更新 API / DB 时它完全不受影响
dc_edge() {
  docker compose -p medbox-edge --project-directory "$DEPLOY_DIR" \
    -f "$DEPLOY_DIR/docker-compose.edge.yml" "$@"
}

container_exists() { docker inspect "$1" >/dev/null 2>&1; }
container_running() { [[ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null || true)" == "true" ]]; }

ensure_network() {
  if docker network inspect "$NETWORK_NAME" >/dev/null 2>&1; then
    return 0
  fi
  log "创建容器网络 $NETWORK_NAME"
  docker network create "$NETWORK_NAME" >/dev/null
}

wait_db_ready() {
  local tries="${1:-60}"
  log "等待数据库就绪 …"
  for _ in $(seq 1 "$tries"); do
    if docker exec "$DB_CONTAINER" pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; then
      ok "数据库已就绪（$DB_CONTAINER）"
      return 0
    fi
    sleep 2
  done
  die "数据库 120s 内未就绪 —— 执行 bash deploy/logs.sh db 查看原因"
}

# API 健康检查走宿主机回环，aspnet 镜像里没有 curl/wget，不进容器查
api_health() {
  local bind="${API_BIND:-127.0.0.1:5080}"
  local port="${bind##*:}"
  curl -fsS --max-time 3 "http://127.0.0.1:${port}/health" 2>/dev/null
}

wait_api_ready() {
  local tries="${1:-45}"
  log "等待 API 就绪 …"
  for _ in $(seq 1 "$tries"); do
    if api_health >/dev/null 2>&1; then
      ok "API 已就绪：$(api_health)"
      return 0
    fi
    sleep 2
  done
  warn "API 90s 内没通过健康检查 —— 执行 bash deploy/logs.sh api 查看原因"
  return 1
}

gen_secret() {
  local n="${1:-48}"
  LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c "$n"
}
