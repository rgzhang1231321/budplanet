#!/usr/bin/env bash
# deploy/api-up.sh —— 只更新 API，绝不触碰数据库容器
#
# 隔离手段有两层：
#   1. compose project 不同（medbox-api vs medbox-infra），up/recreate 命中不了 DB
#   2. up 带 --no-deps，即使有人加了 depends_on 也不会顺带拉起/重建别的服务
# 本脚本还会在开头检查数据库是否在跑：不在就报错退出，**不会替你启动它**。
#
# 用法：
#   bash deploy/api-up.sh                     # 构建当前代码并上线（tag=git short sha）
#   bash deploy/api-up.sh --tag v1.2.0        # 指定镜像 tag
#   bash deploy/api-up.sh --use medbox-api:6d09011   # 不构建，直接切到已有镜像（回滚用）
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

IMAGE_REPO="${API_IMAGE_REPO:-medbox-api}"
USE_IMAGE=""
TAG=""
FORCE_BUILD=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --use) USE_IMAGE="${2:-}"; shift 2 ;;
    --tag) TAG="${2:-}"; shift 2 ;;
    --force-build) FORCE_BUILD=1; shift ;;
    *) die "未知参数：$1（支持 --tag <tag> / --use <image> / --force-build）" ;;
  esac
done

container_running "$DB_CONTAINER" \
  || die "数据库容器 $DB_CONTAINER 没在运行。先执行 bash deploy/infra-up.sh（本脚本不会碰数据库）"

# 生产两条启动硬校验之一：AppSecret 为空时容器启动即崩，别等到最后一步健康检查才报错
require_prod_appsecret

ensure_network

# 低内存机器禁止本机构建：dotnet restore/publish 单进程峰值 1G+，2核1G 必然 OOM，
# 更糟的是 OOM Killer 可能顺手杀掉正在跑的 API/数据库。本地构建用 release.sh 传镜像。
check_build_capacity() {
  local mem_mb
  mem_mb="$(free -m | awk '/^Mem:/ {print $2}')"
  [[ "${mem_mb:-0}" -ge 1800 ]] && return 0
  cat >&2 <<EOF
$(printf '\033[1;33m!\033[0m') 本机内存 ${mem_mb}MB，不足以在服务器上构建 .NET 镜像（至少 2G）。
  请在本地电脑执行（构建在本地、只把镜像传上来）：
      MEDBOX_HOST=<用户>@<服务器IP> bash deploy/release.sh
  若确实想在服务器上构建，加 --force-build（不推荐，会拖慢并可能 OOM）。
EOF
  return 1
}

if [[ -n "$USE_IMAGE" ]]; then
  docker image inspect "$USE_IMAGE" >/dev/null 2>&1 \
    || die "本机没有镜像 $USE_IMAGE。可用镜像：\n$(docker images "$IMAGE_REPO" --format '  {{.Repository}}:{{.Tag}}  {{.CreatedSince}}')"
  IMAGE="$USE_IMAGE"
  log "跳过构建，直接部署已有镜像 $IMAGE"
else
  if [[ $FORCE_BUILD -eq 0 ]]; then
    check_build_capacity || die "已中止（不想在服务器构建就用 deploy/release.sh）"
  else
    warn "--force-build：忽略内存检查，在服务器上直接构建（1G 机器大概率 OOM）"
  fi
  [[ -n "$TAG" ]] || TAG="$(git -C "$ROOT_DIR" rev-parse --short HEAD 2>/dev/null || date +%Y%m%d-%H%M%S)"
  IMAGE="$IMAGE_REPO:$TAG"
  log "构建 API 镜像 $IMAGE（首次会拉 .NET SDK 镜像，约 3-5 分钟）"
  docker build -t "$IMAGE" \
    -f "$ROOT_DIR/server/Dockerfile" \
    "$ROOT_DIR/server"
  docker tag "$IMAGE" "$IMAGE_REPO:latest"
  ok "镜像构建完成：$IMAGE"
fi

log "更新 API 容器（project=medbox-api，--no-deps，不带数据库）"
API_IMAGE="$IMAGE" docker compose -p medbox-api --project-directory "$DEPLOY_DIR" \
  -f "$DEPLOY_DIR/docker-compose.api.yml" \
  up -d --no-deps --force-recreate api

if wait_api_ready; then
  ok "API 已上线：$IMAGE"
else
  warn "新容器没通过健康检查，最近日志："
  docker logs --tail 40 "$API_CONTAINER" || true
  warn "回滚：bash deploy/api-up.sh --use <上一个 tag>   （docker images $IMAGE_REPO 可查）"
  exit 1
fi

cat <<EOF

  容器：$API_CONTAINER（restart=unless-stopped）
  监听：${API_BIND:-127.0.0.1:5080} → 容器内 5080
  数据库：未做任何操作（$DB_CONTAINER 保持原样）

EOF
