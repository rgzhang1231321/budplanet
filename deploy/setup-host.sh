#!/usr/bin/env bash
# deploy/setup-host.sh —— 宿主机一次性初始化（可重复执行，已存在的都不动）
#
# 做四件事：
#   1. 装 docker + compose 插件（已装则跳过）
#   2. 建目录：/opt/medbox（编排文件）、$DATA_ROOT/pgdata（数据库数据盘）、$DATA_ROOT/backup
#   3. 建容器网络 medbox-net（infra 与 api 共用）
#   4. 生成 deploy/.env（随机数据库口令 + JWT 秘钥，权限 600）
#
# 用法：sudo bash deploy/setup-host.sh
#      sudo DATA_ROOT=/data/medbox bash deploy/setup-host.sh   # 指定数据盘
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

# 需要 root：统一 re-exec（避免把 sudo 散落到每个 docker 调用里）
if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  command -v sudo >/dev/null 2>&1 || die "需要 root 权限（当前不是 root，也没有 sudo）"
  exec sudo -E bash "$0" "$@"
fi

install_docker() {
  # 先配镜像加速：国内直连 registry-1.docker.io 基本拉不动
  configure_registry_mirror

  if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    ok "docker 已安装：$(docker --version | cut -d, -f1)"
  else
    log "安装 docker（官方脚本，约 1-2 分钟）…"
    if command -v curl >/dev/null 2>&1; then
      curl -fsSL https://get.docker.com -o /tmp/get-docker.sh || die "下载安装脚本失败，检查服务器出网/把 get.docker.com 换镜像源"
    elif command -v wget >/dev/null 2>&1; then
      wget -qO /tmp/get-docker.sh https://get.docker.com || die "下载安装脚本失败"
    else
      die "服务器没有 curl/wget，先装一个：apt-get install -y curl"
    fi
    sh /tmp/get-docker.sh
    command -v docker >/dev/null 2>&1 || die "docker 安装后仍不可用"
  fi

  if command -v systemctl >/dev/null 2>&1; then
    systemctl enable --now docker >/dev/null 2>&1 || warn "systemctl 启动 docker 失败，手动检查：systemctl status docker"
  fi
  docker info >/dev/null 2>&1 || die "docker 守护进程没跑起来：systemctl status docker"
  docker compose version >/dev/null 2>&1 || die "缺少 docker compose 插件（需要 v2）"
}

# 国内直连 Docker Hub 基本不可用；腾讯云 CVM 有内网镜像源（免流量、速度快）
configure_registry_mirror() {
  local conf=/etc/docker/daemon.json
  if [[ -f "$conf" ]] && grep -q "registry-mirrors" "$conf" 2>/dev/null; then
    ok "docker 镜像加速已配置（$conf）"
    return 0
  fi
  log "配置 docker 镜像加速（腾讯云内网源 + 公共备用源）"
  mkdir -p /etc/docker
  [[ -f "$conf" ]] && cp "$conf" "$conf.bak.$(date +%s)"
  cat > "$conf" <<'JSON'
{
  "registry-mirrors": [
    "https://mirror.ccs.tencentyun.com",
    "https://docker.m.daocloud.io"
  ],
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "live-restore": true
}
JSON
  ok "已写入 $conf（重启 docker 后生效）"
  if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet docker 2>/dev/null; then
    systemctl restart docker >/dev/null 2>&1 || warn "重启 docker 失败，手动执行：systemctl restart docker"
  fi
}

# 1G 内存必做：PG + .NET + Caddy 同时在场，没有 swap 会被 OOM Killer 随机杀进程
ensure_swap() {
  local want_mb="${SWAP_MB:-2048}"
  if [[ "$want_mb" -le 0 ]]; then
    warn "SWAP_MB=0，跳过 swap（1G 机器不建议）"
    return 0
  fi

  local cur_mb
  cur_mb="$(free -m | awk '/^Swap:/ {print $2}')"
  cur_mb="${cur_mb:-0}"
  if [[ "$cur_mb" -ge "$want_mb" ]]; then
    ok "swap 已就绪：${cur_mb}MB"
  else
    log "创建 swap ${want_mb}MB（当前 ${cur_mb}MB）"
    if [[ ! -f /swapfile ]]; then
      fallocate -l "${want_mb}M" /swapfile 2>/dev/null \
        || dd if=/dev/zero of=/swapfile bs=1M count="$want_mb" status=none
      chmod 600 /swapfile
      mkswap /swapfile >/dev/null
    fi
    swapon /swapfile 2>/dev/null || warn "swapon 失败（可能已启用）"
    grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
    ok "swap 已启用：$(free -m | awk '/^Swap:/ {print $2}')MB"
  fi

  sysctl -q -w vm.swappiness=10 2>/dev/null || true
  grep -q '^vm.swappiness' /etc/sysctl.conf 2>/dev/null || echo 'vm.swappiness=10' >> /etc/sysctl.conf
}

# 内存太小就别在服务器上构建镜像了（dotnet restore 就会 OOM）
check_memory() {
  local mem_mb
  mem_mb="$(free -m | awk '/^Mem:/ {print $2}')"
  log "规格：${mem_mb}MB 内存 / $(nproc) 核 CPU"
  if [[ "$mem_mb" -lt 1800 ]]; then
    warn "内存 ${mem_mb}MB 偏小：API 镜像请在本地构建后上传（bash deploy/release.sh，见 README 第 5 节），不要在服务器上 docker build"
  fi
}

prepare_dirs() {
  [[ -n "${DATA_ROOT:-}" ]] || DATA_ROOT="/data/medbox"
  log "准备目录：$DATA_ROOT/{pgdata,backup,caddy}"
  mkdir -p "$DATA_ROOT/pgdata" "$DATA_ROOT/backup" \
           "$DATA_ROOT/caddy/data" "$DATA_ROOT/caddy/config"

  # 数据盘提醒：/data 不是独立挂载点时，数据其实在系统盘上（能跑，但扩容/快照语义不同）
  local mp
  mp="$(df -P "$DATA_ROOT" | awk 'NR==2 {print $6}')"
  if [[ "$mp" == "/" ]]; then
    warn "$DATA_ROOT 位于系统盘（未挂载独立数据盘）。建议在腾讯云挂一块云硬盘挂到 /data 后重跑本脚本。"
  else
    ok "数据目录位于独立挂载点 $mp（$(df -h "$DATA_ROOT" | awk 'NR==2 {print $4}') 可用）"
  fi
}

gen_env() {
  if [[ -f "$ENV_FILE" ]]; then
    ok ".env 已存在，保持原值（不覆盖口令/秘钥）"
    return 0
  fi

  log "生成 $ENV_FILE（随机口令 + JWT 秘钥）"
  cp "$DEPLOY_DIR/.env.example" "$ENV_FILE"
  local pg_pwd jwt_secret
  pg_pwd="$(gen_secret 32)"
  jwt_secret="$(gen_secret 48)"
  # 只替换前两个占位符，顺序：POSTGRES_PASSWORD 在前，JWT 在后
  sed -i "0,/__GENERATED__/s|__GENERATED__|${pg_pwd}|" "$ENV_FILE"
  sed -i "0,/__GENERATED__/s|__GENERATED__|${jwt_secret}|" "$ENV_FILE"

  # 记录实际部署目录，后续脚本据此定位
  sed -i "s|^MEDBOX_HOME=.*|MEDBOX_HOME=${DEPLOY_DIR%/deploy}|" "$ENV_FILE"
  sed -i "s|^DATA_ROOT=.*|DATA_ROOT=${DATA_ROOT}|" "$ENV_FILE"
  # 域名随环境变量落盘（DOMAIN=api.apiyun.net bash deploy/setup-host.sh）
  if [[ -n "${DOMAIN:-}" ]]; then
    sed -i "s|^DOMAIN=.*|DOMAIN=${DOMAIN}|" "$ENV_FILE"
  fi

  chmod 600 "$ENV_FILE"
  ok "已写入 $ENV_FILE（权限 600，不进 git）"
}

prepare_pgdata_owner() {
  # postgres 官方镜像以 postgres 用户初始化数据目录；bind mount 时先把属主给它，
  # 免得首次 initdb 因权限失败（Alpine 镜像 uid=70，Debian 镜像 uid=999，动态取）
  local uid
  uid="$(docker run --rm --entrypoint id postgres:18-alpine -u postgres 2>/dev/null || echo 70)"
  uid="${uid//[^0-9]/}"
  [[ -n "$uid" ]] || uid=70
  chown -R "${uid}:${uid}" "$DATA_ROOT/pgdata" 2>/dev/null \
    || warn "chown pgdata 失败（不影响首次初始化，entrypoint 会自己修复）"
}

main() {
  log "MedBox 宿主机初始化 —— 部署目录 $DEPLOY_DIR"
  check_memory
  ensure_swap
  install_docker
  prepare_dirs
  gen_env

  ensure_network
  ok "容器网络 $NETWORK_NAME 就绪"

  prepare_pgdata_owner

  cat <<EOF

$(ok "宿主机初始化完成")

下一步（按顺序）：
  1. 数据库（只建库，已存在则不动）：
       bash deploy/infra-up.sh
  2. 表结构：
       bash deploy/migrate.sh
  3. API（小内存机器用 release.sh 从本地推镜像，别在这里 docker build）：
       bash deploy/release.sh          # 在本地电脑执行
  4. HTTPS 反代：
       bash deploy/edge-up.sh

也可以一把跑完：bash deploy/deploy.sh（数据库幂等 → 迁移 → API → 反代）

别忘了腾讯云控制台「安全组」放行 80/443（22 只放自己的 IP）。
5432 不要对公网开放 —— 数据库只监听 127.0.0.1。
EOF
}

main "$@"
