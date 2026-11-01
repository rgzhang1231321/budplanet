#!/usr/bin/env bash
# SQL 增量脚本执行器（append-only / 严格递增 / 校验和防篡改）
# 用法: bash db/apply.sh            应用所有未执行脚本到 medbox
#       bash db/apply.sh --dry-run  只校验不执行（CI 用）
# 安全: 目标库硬编码为 medbox；任何指向 ragdb 的企图直接拒绝。
set -euo pipefail

CONTAINER="tigerrag-postgres-1"
PGUSER="tigerrag"
DB="medbox"
DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MIG_DIR="$SCRIPT_DIR/migrations"

# ── 防呆：绝不允许对 ragdb 执行 ──────────────────────────────
if [[ "$DB" == *"ragdb"* ]]; then
  echo "FATAL: 拒绝执行，目标库疑似 ragdb。" >&2; exit 1
fi

echo "▶ 目标库: $DB (容器 $CONTAINER)  |  模式: $([ $DRY_RUN -eq 1 ] && echo DRY-RUN || echo APPLY)"

# ── 收集脚本并按版本号排序 ──────────────────────────────────
mapfile -t FILES < <(ls "$MIG_DIR"/V*.sql 2>/dev/null | sort)
if [[ ${#FILES[@]} -eq 0 ]]; then echo "FATAL: 未找到任何 V*.sql 脚本。" >&2; exit 1; fi

# 提取序号并检查：从 001 起严格 +1，断号/重号/乱序一律拒绝
PREV=0
declare -a VERS=()
for f in "${FILES[@]}"; do
  base="$(basename "$f")"
  if [[ ! "$base" =~ ^V([0-9]{3})__[a-z0-9_]+\.sql$ ]]; then
    echo "FATAL: 文件名不合规: $base（须为 VNNN__snake_case.sql）" >&2; exit 1
  fi
  n=$((10#${BASH_REMATCH[1]}))
  if [[ $n -ne $((PREV + 1)) ]]; then
    echo "FATAL: 版本号不连续或重复: 期望 V$(printf '%03d' $((PREV+1)))，实际 $base" >&2; exit 1
  fi
  PREV=$n
  VERS+=("$base")
done
echo "  发现 ${#VERS[@]} 个脚本: ${VERS[0]} ... ${VERS[-1]}  (连续 ✓)"

psql_q() { docker exec -i "$CONTAINER" psql -U "$PGUSER" -d "$DB" "$@"; }

# ── dry-run：仅校验账本校验和，不改动任何东西 ───────────────
if [[ $DRY_RUN -eq 1 ]]; then
  echo "  [dry-run] 检查已执行脚本是否被篡改…"
  existing=$(psql_q -Atc "SELECT version||'|'||checksum FROM schema_migrations ORDER BY version;" 2>/dev/null || true)
  fail=0
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    ver="${line%%|*}"; recorded="${line#*|}"
    path="$MIG_DIR/$(ls "$MIG_DIR" | grep "^${ver}__" | head -1)"
    [[ -f "$path" ]] || { echo "  ✗ 账本中的 $ver 找不到对应脚本文件" >&2; fail=1; continue; }
    actual=$(sha256sum "$path" | awk '{print $1}')
    if [[ "$actual" != "$recorded" ]]; then
      echo "  ✗ $ver 校验和不符（文件被改动过！应新增版本修复，不得改旧脚本）" >&2; fail=1
    else
      echo "  ✓ $ver 校验和一致"
    fi
  done <<< "$existing"
  [[ $fail -eq 0 ]] || { echo "DRY-RUN 失败。" >&2; exit 1; }
  echo "  DRY-RUN 通过。"
  exit 0
fi

# ── 确保账本表存在 ──────────────────────────────────────────
psql_q -v ON_ERROR_STOP=1 -c "
CREATE TABLE IF NOT EXISTS schema_migrations (
  version VARCHAR(16) PRIMARY KEY,
  checksum VARCHAR(64) NOT NULL,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  execution_ms INTEGER NOT NULL DEFAULT 0,
  script_name VARCHAR(255) NOT NULL
);" >/dev/null

# ── 按序应用未执行脚本 ─────────────────────────────────────
for base in "${VERS[@]}"; do
  ver="${base%%__*}"
  path="$MIG_DIR/$base"
  checksum=$(sha256sum "$path" | awk '{print $1}')

  applied=$(psql_q -Atc "SELECT checksum FROM schema_migrations WHERE version='$ver';")
  if [[ -n "$applied" ]]; then
    if [[ "$applied" != "$checksum" ]]; then
      echo "FATAL: $ver 已执行但校验和不符 —— 文件被篡改，拒绝继续。请新增版本修复。" >&2; exit 1
    fi
    echo "  = $ver 已执行，跳过"
    continue
  fi

  echo "  → 应用 $base …"
  start=$(date +%s%3N)
  # 脚本内自带 BEGIN/COMMIT；ON_ERROR_STOP=1 保证出错即中断并回滚
  psql_q -v ON_ERROR_STOP=1 -f - < "$path" >/dev/null
  end=$(date +%s%3N)
  ms=$((end - start))
  psql_q -v ON_ERROR_STOP=1 -c \
    "INSERT INTO schema_migrations(version,checksum,execution_ms,script_name) VALUES('$ver','$checksum',$ms,'$base');" >/dev/null
  echo "    ✓ 完成 (${ms}ms)"
done

echo "✔ 全部完成。当前账本："
psql_q -Atc "SELECT version||'  '||to_char(applied_at,'YYYY-MM-DD HH24:MI')||'  '||execution_ms||'ms' FROM schema_migrations ORDER BY version;"
