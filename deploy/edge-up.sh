#!/usr/bin/env bash
# deploy/edge-up.sh —— 只负责 HTTPS 反代（Caddy），独立于 api / db
#
# 幂等：配置没变就什么都不做；改了 Caddyfile 才 reload（不重建容器、证书不丢）
# 用法：
#   bash deploy/edge-up.sh              # 首次创建 / 配置变更后 reload
#   bash deploy/edge-up.sh --recreate   # 换域名、换端口时才需要
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

: "${DOMAIN:=}"
[[ -n "$DOMAIN" ]] || die "deploy/.env 里没有 DOMAIN（例：DOMAIN=api.apiyun.net）"

# 证书原地放在 deploy/api.apiyun.net_nginx/（不搬位置，免得改一堆引用）：
#   * 缺文件时 Caddy 会启动失败，所以提前拦住给明确提示；
#   * .key 由 .gitignore 挡死（*.key 全局规则），只存在于本地/服务器磁盘，不进 git。
CERT_DIR="$DEPLOY_DIR/api.apiyun.net_nginx"
CERT_FILE="$CERT_DIR/api.apiyun.net_bundle.crt"
KEY_FILE="$CERT_DIR/api.apiyun.net.key"

[[ -f "$CERT_FILE" ]] \
  || die "缺少证书链 $CERT_FILE（从腾讯云 SSL 下载 Nginx 版，*_bundle.crt 放到 deploy/api.apiyun.net_nginx/）"

if [[ ! -f "$KEY_FILE" ]]; then
  cat >&2 <<EOF
$(printf '\033[1;31m✗\033[0m') 缺少私钥 $KEY_FILE

  私钥故意不进 git（公开仓库泄漏 = 域名可被冒用），所以新机器/新克隆的目录里没有它，
  要手工放一次：

      # 把腾讯云下载的 api.apiyun.net.key 拷到证书目录
      cp api.apiyun.net.key $CERT_DIR/
      chmod 600 $KEY_FILE

  详见 deploy/README.md 第 4.1 节（证书怎么放、为什么不能进仓库、换机器怎么拿）。
EOF
  exit 1
fi

# 私钥权限：600 只有属主可读。这里只提醒不自动改（不动用户文件属性）
if command -v stat >/dev/null 2>&1; then
  key_mode="$(stat -c '%a' "$KEY_FILE" 2>/dev/null || echo 600)"
  [[ "$key_mode" == "600" ]] || warn "私钥权限是 $key_mode（建议 chmod 600 $KEY_FILE）"
fi

# 顺带提醒过期（腾讯云证书 3 个月一轮，忘了换会全站 502）
if command -v openssl >/dev/null 2>&1; then
  end="$(openssl x509 -in "$CERT_FILE" -noout -enddate 2>/dev/null | cut -d= -f2)"
  if [[ -n "$end" ]]; then
    if [[ "$(date -d "$end" +%s 2>/dev/null || echo 0)" -lt "$(date +%s)" ]]; then
      die "证书已过期（$end）：去腾讯云重新签发后覆盖 deploy/api.apiyun.net_nginx/ 下的 *_bundle.crt 与 .key 再执行"
    fi
    ok "证书有效期至 $end"
  fi
fi

RECREATE=0
if [[ "${1:-}" == "--recreate" ]]; then
  RECREATE=1
elif [[ $# -gt 0 ]]; then
  die "未知参数：$1（只支持 --recreate）"
fi

docker inspect "${API_CONTAINER:-medbox-api}" >/dev/null 2>&1 \
  || warn "API 容器还不存在 —— Caddy 会启动成功但反代会 502，先跑 api-up.sh"

ensure_network
mkdir -p "${DATA_ROOT}/caddy/data" "${DATA_ROOT}/caddy/config"

if container_exists "${EDGE_CONTAINER:-medbox-caddy}" && [[ $RECREATE -eq 0 ]]; then
  ok "反代容器已存在 —— 只同步配置，不重建"
  # 配置热加载：Caddyfile 变了才需要，reload 不会重新申请证书
  docker exec "${EDGE_CONTAINER:-medbox-caddy}" \
    caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile 2>/dev/null \
    && ok "Caddyfile 已热加载" \
    || warn "热加载失败（多半是容器没跑），执行 bash deploy/logs.sh edge 查看"
else
  [[ $RECREATE -eq 1 ]] && warn "--recreate：重建反代容器（证书缓存在 ${DATA_ROOT}/caddy，不会重新申请）"
  log "启动 HTTPS 反代：https://${DOMAIN} → medbox-api:5080"
  dc_edge up -d

  log "等待 HTTPS 就绪（证书用腾讯云签发的 api.apiyun.net，无需签发）…"
  local_ok=0
  for _ in $(seq 1 30); do
    if curl -fsS --max-time 5 "https://${DOMAIN}/health" >/dev/null 2>&1; then
      local_ok=1
      break
    fi
    sleep 2
  done
  if [[ $local_ok -eq 1 ]]; then
    ok "HTTPS 已通：https://${DOMAIN}/health"
  else
    warn "60 秒内 https://${DOMAIN}/health 没通，常见原因："
    warn "  1) 域名 A 记录还没指向本机公网 IP（dig +short ${DOMAIN}）"
    warn "  2) 安全组没放行 80/443"
    warn "  3) 证书文件缺失/名字不匹配（应有 deploy/api.apiyun.net_nginx/api.apiyun.net_bundle.crt 与 api.apiyun.net.key）"
    warn "  4) 域名未备案（大陆机房 80/443 会被拦）"
    warn "查看日志：bash deploy/logs.sh edge"
  fi
fi

cat <<EOF

  容器：${EDGE_CONTAINER:-medbox-caddy}（project medbox-edge）
  入口：http://${DOMAIN}  → 跳转 https
        https://${DOMAIN} → medbox-api:5080（容器网络内）
  证书：deploy/api.apiyun.net_nginx/ 下的 *_bundle.crt（公开，进 git）与 .key（私钥，不进 git）
        腾讯云 TrustAsia 签发，2027-01-30 到期；换证书后重跑本脚本即可，容器无需重建

EOF
