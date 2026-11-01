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
