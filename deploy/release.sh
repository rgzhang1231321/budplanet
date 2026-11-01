#!/usr/bin/env bash
# deploy/release.sh —— 在**本地电脑**构建镜像 → 传到服务器 → 只替换 API 容器
#
# 为什么不在服务器上 docker build：
#   目标机是 2 核 1G，dotnet restore/publish 会直接 OOM（.NET SDK 编译单进程就要 1G+）。
#   本地构建好做成 tar 传过去，服务器只做 docker load —— 内存占用几乎为零，
#   而且构建产物和本地测过的完全一致。
#
# 用法（在本地 Windows Git Bash / macOS / Linux 执行）：
#   MEDBOX_HOST=root@1.2.3.4 bash deploy/release.sh
#   MEDBOX_HOST=root@1.2.3.4 bash deploy/release.sh --tag v1.0.1
#   MEDBOX_HOST=root@1.2.3.4 bash deploy/release.sh --no-sync   # 不改代码只重发镜像
#
# 安全性：只 load 镜像 + 调 api-up.sh --use，全程不碰 medbox-db 容器。
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

HOST="${MEDBOX_HOST:-}"
[[ -n "$HOST" ]] || die "缺少 MEDBOX_HOST，例如：MEDBOX_HOST=root@1.2.3.4 bash deploy/release.sh"

REMOTE_DIR="${MEDBOX_HOME:-/opt/medbox}"
IMAGE_REPO="${API_IMAGE_REPO:-medbox-api}"
SYNC=1
TAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="${2:-}"; shift 2 ;;
    --no-sync) SYNC=0; shift ;;
    *) die "未知参数：$1（支持 --tag <tag> / --no-sync）" ;;
  esac
done

[[ -n "$TAG" ]] || TAG="$(git -C "$ROOT_DIR" rev-parse --short HEAD 2>/dev/null || date +%Y%m%d-%H%M%S)"
IMAGE="$IMAGE_REPO:$TAG"

log "① 本地构建 $IMAGE（构建在你这台机器上，不占服务器内存）"
docker build -t "$IMAGE" -f "$ROOT_DIR/server/Dockerfile" "$ROOT_DIR/server"
docker tag "$IMAGE" "$IMAGE_REPO:latest"

# 镜像体积 350MB 左右，gzip 后约 100MB；-1 是为速度，压缩率够用
log "② 传输镜像到 $HOST（约 100MB，看上行带宽，通常 1-3 分钟）"
docker save "$IMAGE" | gzip -1 | ssh "$HOST" "gunzip | docker load" \
  || die "镜像传输失败：检查 SSH 连通性/磁盘余量（服务器 / 至少留 1G）"

ssh "$HOST" "docker image inspect '$IMAGE' >/dev/null" \
  || die "镜像在服务器上不可用（可能传输中断），可重复执行本脚本"

if [[ $SYNC -eq 1 ]]; then
  log "③ 同步编排脚本与迁移文件"
  tar -czf - -C "$ROOT_DIR" \
    --exclude='server/**/bin' \
    --exclude='server/**/obj' \
    --exclude='server/**/TestResults' \
    --exclude='deploy/.env' \
    --exclude='deploy/.env.bak*' \
    --exclude='deploy/.last_good' \
    server db deploy \
    | ssh "$HOST" "mkdir -p '$REMOTE_DIR' && tar -xzf - -C '$REMOTE_DIR'"
  ok "代码已同步"
fi

log "④ 只替换 API 容器（--no-deps，数据库保持原样）"
ssh "$HOST" "cd '$REMOTE_DIR' && bash deploy/api-up.sh --use '$IMAGE'" \
  || die "远程 api-up 失败，查看：ssh $HOST 'bash $REMOTE_DIR/deploy/logs.sh api'"

ok "上线完成：$IMAGE"
echo
echo "  回滚到上一个版本："
echo "    ssh $HOST 'docker images $IMAGE_REPO --format \"{{.Tag}} {{.CreatedSince}}\"'"
echo "    ssh $HOST 'cd $REMOTE_DIR && bash deploy/api-up.sh --use ${IMAGE_REPO}:<上一个 tag>'"
