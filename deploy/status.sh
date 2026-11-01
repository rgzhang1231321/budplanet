#!/usr/bin/env bash
# deploy/status.sh —— 一眼看全：容器、健康、端口、磁盘
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker

echo "── 容器 ─────────────────────────────────────────────"
# name filter 是正则，容器名不带前导斜杠（写成 ^/ 会一条都匹配不到）
docker ps -a --filter "name=^${DB_CONTAINER}$" --filter "name=^${API_CONTAINER}$" \
  --filter "name=^${EDGE_CONTAINER:-medbox-caddy}$" \
  --format "table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}"

echo
echo "── 内存 ─────────────────────────────────────────────"
free -m | awk 'NR==1 || NR==2 || NR==3'

echo
echo "── 健康 ─────────────────────────────────────────────"
if container_running "$DB_CONTAINER"; then
  if docker exec "$DB_CONTAINER" pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; then
    ok "数据库：可连接"
  else
    warn "数据库：容器在跑，但 pg_isready 失败"
  fi
else
  warn "数据库：未运行"
fi

if response="$(api_health)"; then
  ok "API：$response"
else
  warn "API：健康检查失败（$(grep -o '[0-9.]*:[0-9]*$' <<<"${API_BIND:-127.0.0.1:5080}")/health）"
fi

if [[ -n "${DOMAIN:-}" ]]; then
  if https_resp="$(curl -fsS --max-time 6 "https://${DOMAIN}/health" 2>/dev/null)"; then
    ok "HTTPS：https://${DOMAIN}/health → $https_resp"
  else
    warn "HTTPS：https://${DOMAIN} 不通（证书未签发 / DNS 未解析 / 安全组未放行 443）"
  fi

  # ── 证书剩余天数 ────────────────────────────────────────────────────
  # "能通"不等于"还久"：证书 30 天续期失败时站点照样 200，等真过期才全站 502，
  # 那时往往在半夜。这里提前把剩余天数打出来，< 21 天标黄。
  # 取的是**线上实际在用的证书链**（不是本地文件），能顺带发现"本地换了证书但 Caddy 没 reload"。
  cert_enddate() {
    # 域名解析不了 / 443 被墙 / 证书链异常时，s_client 会失败或空输出 —— 全部交给调用方判空
    if command -v timeout >/dev/null 2>&1; then
      timeout 8 openssl s_client -servername "$1" -connect "${1}:443" </dev/null 2>/dev/null
    else
      openssl s_client -servername "$1" -connect "${1}:443" </dev/null 2>/dev/null
    fi | openssl x509 -noout -enddate 2>/dev/null
  }

  if ! command -v openssl >/dev/null 2>&1; then
    warn "证书：本机没装 openssl，跳过剩余天数检查（apt install openssl 后可查）"
  else
    # `|| true` 兜住所有失败；enddate 为空就走"拿不到"分支，绝不让 set -e 掐断整个 status.sh
    cert_end="$(cert_enddate "$DOMAIN" | sed -n 's/^notAfter=//p' | head -1)" || true
    if [[ -z "$cert_end" ]]; then
      warn "证书：取不到到期时间（DNS 未解析 / 443 不通 / 证书链异常）→ 本次跳过，不影响其它检查"
    else
      cert_end_epoch="$(date -d "$cert_end" +%s 2>/dev/null)" || true
      if [[ -z "${cert_end_epoch:-}" ]]; then
        warn "证书：到期时间格式无法解析（$cert_end）"
      else
        cert_days=$(( (cert_end_epoch - $(date +%s)) / 86400 ))
        if [[ $cert_days -lt 0 ]]; then
          warn "证书：已过期 $(( -cert_days )) 天！https 已不可用 → 立刻换证书并 bash deploy/edge-up.sh"
        elif [[ $cert_days -lt 21 ]]; then
          warn "证书：仅剩 ${cert_days} 天（$cert_end）→ 该续签了：腾讯云续签后重跑 bash deploy/edge-up.sh"
        else
          ok "证书：剩 ${cert_days} 天（$cert_end）"
        fi
      fi
    fi
  fi
fi

echo
echo "── 磁盘 ─────────────────────────────────────────────"
df -h "${DATA_ROOT}" | awk 'NR==1 || NR==2'
# pipefail 下 du 对不存在的目录会返回非 0，直接让 set -e 把脚本掐断（backup 目录首次部署前不存在）
du -sh "${DATA_ROOT}/pgdata" 2>/dev/null | awk '{print "  pgdata: "$1}' || true
du -sh "${DATA_ROOT}/backup" 2>/dev/null | awk '{print "  backup: "$1}' || true

echo
echo "── 迁移账本 ─────────────────────────────────────────"
docker exec "$DB_CONTAINER" psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atc \
  "SELECT version||'  '||to_char(applied_at,'YYYY-MM-DD HH24:MI') FROM schema_migrations ORDER BY version;" 2>/dev/null \
  | sed 's/^/  /' || warn "还没跑过迁移（bash deploy/migrate.sh）"
