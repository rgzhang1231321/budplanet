#!/usr/bin/env bash
# SQL 增量脚本执行器（append-only / 严格递增 / 校验和防篡改）
# 用法: bash db/apply.sh            应用所有未执行脚本到 medbox
#       bash db/apply.sh --dry-run  只校验不执行（CI 用）
#       bash db/apply.sh --allow-db <名字>   显式声明非默认库名（默认只允许 medbox）
# 安全: 目标库默认白名单为 medbox；任何指向 ragdb 的企图直接拒绝。
# 可变: CONTAINER / PGUSER / DB 可用环境变量覆盖（生产部署走 deploy/migrate.sh）
set -euo pipefail

CONTAINER="${CONTAINER:-tigerrag-postgres-1}"
PGUSER="${PGUSER:-tigerrag}"
DB="${DB:-medbox}"
DRY_RUN=0
ALLOW_DB=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)  DRY_RUN=1; shift ;;
    --allow-db)
      [[ $# -ge 2 && -n "$2" ]] || { echo "FATAL: --allow-db 后面要跟库名" >&2; exit 1; }
      ALLOW_DB="$2"; shift 2 ;;
    --allow-db=*) ALLOW_DB="${1#*=}"; shift ;;
    *) echo "FATAL: 未知参数 $1（支持 --dry-run / --allow-db <名字>）" >&2; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MIG_DIR="$SCRIPT_DIR/migrations"

# ── 防呆 ①（黑名单）：绝不允许对 ragdb 执行 ──────────────────────
if [[ "$DB" == *"ragdb"* ]]; then
  echo "FATAL: 拒绝执行，目标库疑似 ragdb。" >&2; exit 1
fi

# ── 防呆 ②（白名单）：只允许 medbox，其它库名必须显式声明 ────────
# 为什么必须有这一层：`CREATE TABLE IF NOT EXISTS` 在空库上**静默成功**，
# 所以 `DB=medbo bash db/apply.sh` 这种拼错会一本正经地跑完全部迁移、账本也建得好好的，
# 事后才发现数据写进了另一个库（真实数据在 medbox 里，新库只是空表）。
# 黑名单只能拦已知的坏名字，拦不住拼写错误 → 改成"只放行已知的好名字"。
if [[ "$DB" != "medbox" && "$DB" != "$ALLOW_DB" ]]; then
  cat >&2 <<EOF
FATAL: 目标库 "$DB" 不在白名单内（只允许 medbox）。
  拼错库名不会报错，但会在一个空库上跑完全量迁移，事后极难发现。
  确认这确实是你想要的库，就显式声明一次：
      bash db/apply.sh --allow-db "$DB"
  生产部署请用 bash deploy/migrate.sh（库名取自 deploy/.env 的 POSTGRES_DB）。
EOF
  exit 1
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
# 说明：账本表由这里 bootstrap 建，V001 里那条同名 CREATE 因 IF NOT EXISTS 不会再生效。
# V001 已执行、内容冻结（append-only），里面仍是 execution_ms INTEGER；这里用 BIGINT
# 防越界（迁移耗时算错时 INTEGER 会 out of range 直接中断 apply）。
# 两处定义不同是历史原因，**不要为了"对齐"去改 V001** —— 改冻结脚本会让 checksum
# 全部失配，下次 apply 直接中止。
psql_q -v ON_ERROR_STOP=1 -c "
CREATE TABLE IF NOT EXISTS schema_migrations (
  version VARCHAR(16) PRIMARY KEY,
  checksum VARCHAR(64) NOT NULL,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  execution_ms BIGINT NOT NULL DEFAULT 0,
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
  start=$(date +%s%N)
  # 脚本内自带 BEGIN/COMMIT；ON_ERROR_STOP=1 保证出错即中断并回滚
  psql_q -v ON_ERROR_STOP=1 -f - < "$path" >/dev/null
  end=$(date +%s%N)
  # date +%s%N 是纳秒级（19 位），换算成毫秒；%3N 在部分 date 上不截断会越界 INTEGER
  ms=$(( (end - start) / 1000000 ))
  psql_q -v ON_ERROR_STOP=1 -c \
    "INSERT INTO schema_migrations(version,checksum,execution_ms,script_name) VALUES('$ver','$checksum',$ms,'$base');" >/dev/null
  echo "    ✓ 完成 (${ms}ms)"
done

echo "✔ 全部完成。当前账本："
psql_q -Atc "SELECT version||'  '||to_char(applied_at,'YYYY-MM-DD HH24:MI')||'  '||execution_ms||'ms' FROM schema_migrations ORDER BY version;"
