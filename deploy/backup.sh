#!/usr/bin/env bash
# deploy/backup.sh —— 逻辑备份（pg_dump -Fc），保留 7 天
#
# 建议加到 crontab：
#   0 3 * * * cd /opt/medbox && bash deploy/backup.sh >> /var/log/medbox-backup.log 2>&1
# ⚠️ 这行会无限追加日志，配 cron 的同时必须配 logrotate，做法见 deploy/README.md §5。
set -euo pipefail
# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

load_env
require_docker
container_running "$DB_CONTAINER" || die "数据库没在运行，无法备份"

mkdir -p "${DATA_ROOT}/backup"
stamp="$(date +%Y%m%d-%H%M%S)"
out="${DATA_ROOT}/backup/medbox-${stamp}.dump"
tmp="${out}.part"

log "导出 ${POSTGRES_DB} → $out"
# 先写 .part 再改名：中途失败不会留下一个看起来正常的坏备份
docker exec "$DB_CONTAINER" pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --format=custom --no-owner > "$tmp"
mv "$tmp" "$out"
ok "备份完成：$(du -h "$out" | awk '{print $1}')"

# 只清理本脚本生成的备份，不碰目录里其它文件
find "${DATA_ROOT}/backup" -maxdepth 1 -type f -name 'medbox-*.dump' -mtime +7 -print -delete \
  | sed 's/^/  清理过期备份：/' || true

cat <<EOF

恢复（到同版本或更高版本的库）：
  docker exec -i $DB_CONTAINER pg_restore -U $POSTGRES_USER -d $POSTGRES_DB --clean --if-exists < $out
EOF
