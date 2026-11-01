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
#   bash deploy/api-up.sh --use medbox-api:6d09011   # 不构建，直接切到已有镜像（手动回滚用）
#
# 部署记录与自动回滚：
#   每次**通过健康检查**后，把镜像名写进 deploy/.last_good（机器本地，不进 git）。
#   健康检查失败时自动切回 .last_good 里的镜像并再检查一次 —— 只尝试一次、内联执行
#   （不递归调用本脚本），所以不会出现"回滚失败又回滚"的死循环。
#   半夜发版炸了不用再靠人记上一个 tag 是谁。
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

IMAGE_REPO="${API_IMAGE_REPO:-medbox-api}"
USE_IMAGE=""
TAG=""
FORCE_BUILD=0
# 上一次**通过健康检查**的镜像，由本脚本成功时写入。失败时靠它自动回滚。
# 放 deploy/ 下（已在 .gitignore），是机器本地状态，不跟着 git / tar 走。
LAST_GOOD_FILE="$DEPLOY_DIR/.last_good"

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

# 起一个指定镜像的 API 容器（部署和回滚共用这一段，避免两处逻辑走偏）
start_api() {
  API_IMAGE="$1" docker compose -p medbox-api --project-directory "$DEPLOY_DIR" \
    -f "$DEPLOY_DIR/docker-compose.api.yml" \
    up -d --no-deps --force-recreate api
}

# 部署记录：原子写（先写 .tmp 再 mv），避免断电/中断留下半行 tag 害下次回滚失败
record_last_good() {
  printf '%s\n' "$1" > "${LAST_GOOD_FILE}.tmp" && mv "${LAST_GOOD_FILE}.tmp" "$LAST_GOOD_FILE"
}
read_last_good() {
  [[ -f "$LAST_GOOD_FILE" ]] && head -1 "$LAST_GOOD_FILE" || true
}

# up 本身失败（端口冲突/网络不存在/镜像被 prune）也走回滚分支，不只管健康检查失败
if start_api "$IMAGE" && wait_api_ready; then
  record_last_good "$IMAGE" || warn "写入 $LAST_GOOD_FILE 失败，下次失败时无法自动回滚（本次上线是成功的）"
  ok "API 已上线：$IMAGE"
  ok "已记为 .last_good（下次健康检查失败会自动回滚到这个镜像）"
else
  warn "新容器没起来 / 没通过健康检查，最近日志："
  docker logs --tail 40 "$API_CONTAINER" || true
  warn "本次失败镜像：$IMAGE"

  # ── 自动回滚：单次内联执行，**不递归调用本脚本**，所以不存在"回滚失败又回滚"的死循环 ──
  last_good="$(read_last_good)"
  if [[ -z "$last_good" ]]; then
    warn "没有 .last_good 记录（首次部署，或上次也没成功）→ 无法自动回滚。"
    warn "手动查镜像：docker images $IMAGE_REPO  然后 bash deploy/api-up.sh --use <上一个 tag>"
    exit 1
  fi
  if [[ "$last_good" == "$IMAGE" ]]; then
    warn "失败的这个镜像本身就是上次成功的版本（$last_good）→ 回滚到它没有意义，跳过自动回滚。"
    warn "多半是配置/数据库/证书问题，不是镜像问题：bash deploy/logs.sh api 200 看完整日志"
    exit 1
  fi
  if ! docker image inspect "$last_good" >/dev/null 2>&1; then
    warn "上次成功的镜像 $last_good 已不在本机（被 prune 或换了机器）→ 无法自动回滚。"
    warn "手动：docker images $IMAGE_REPO  然后 bash deploy/api-up.sh --use <tag>"
    exit 1
  fi

  warn "自动回滚到上次成功镜像：$last_good（只尝试一次）"
  if start_api "$last_good" && wait_api_ready; then
    ok "已回滚到 $last_good，线上应已恢复可用。.last_good 保持不变。"
    warn "本次发布失败，修好再发。排查：bash deploy/logs.sh api 200"
  else
    warn "回滚也失败了 —— 线上仍是 $last_good 的容器，但健康检查没过，需要人工介入："
    docker logs --tail 40 "$API_CONTAINER" || true
    warn "常见原因：数据库没起 / .env 变了 / 磁盘满了。bash deploy/status.sh 先看一圈"
  fi
  exit 1
fi

cat <<EOF

  容器：$API_CONTAINER（restart=unless-stopped）
  监听：${API_BIND:-127.0.0.1:5080} → 容器内 5080
  数据库：未做任何操作（$DB_CONTAINER 保持原样）
  已记为 last_good：$(read_last_good)
  手动回滚：bash deploy/api-up.sh --use \$(cat deploy/.last_good)

EOF
