#!/usr/bin/env bash
# 小程序前端静态检查（本地与 CI 通用）：全部通过时只打印一行，有问题时打印明细并返回非 0。
# 用法： bash miniprogram/scripts/check.sh
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

FAIL=0
DIRS="pages packageManage packageReport packageSettings"

# ── CI-1 页面内禁止写死字号 ──
# 老人端靠变量整体放大一档，写死一处就会在那处掉回 12px，且不报错。
OUT=$(grep -rEo "font-size: *[0-9]+rpx" $DIRS 2>/dev/null)
if [ -n "$OUT" ]; then
  echo "[CI-1] 页面内硬编码字号（应改用 var(--fs-*)）："
  echo "$OUT"
  FAIL=1
fi

# ── CI-2 每个页面必须有 p-root ──
# 双模变量、页面底色都挂在 .p-root 上，缺了会整页样式失配。
OUT=$(grep -rL "p-root" pages/*/*.wxml packageManage/pages/*/*.wxml \
        packageReport/pages/*/*.wxml packageSettings/pages/*/*.wxml 2>/dev/null)
if [ -n "$OUT" ]; then
  echo "[CI-2] 页面缺少 p-root 根节点："
  echo "$OUT"
  FAIL=1
fi

# ── CI-3 showModal 的 confirmText 不得超过 4 个字 ──
# 微信限制 4 字，超了整个弹窗静默 fail——点了没反应，最难排查。
#
# 必须按**字符数**数，不能按字节数：grep 在 C locale（LANG 为空时 Windows Git Bash 的默认）
# 下 `[^']{5,}` 按字节匹配，两个汉字 = 6 字节就直接命中，于是 '加入'/'删除' 这种 2 字按钮
# 全仓误报——检查一直在红，就再也没人看它，真正超限的那天也会被当成噪音划掉。
# 做法与 locale 无关：先把每个完整 UTF-8 序列折叠成一个 X，折叠后的长度就是字符数。
OUT=$(grep -rEo "confirmText: '[^']*'" --include="*.ts" $DIRS 2>/dev/null | while IFS= read -r line; do
  txt="${line#*confirmText: \'}"
  txt="${txt%\'}"
  folded=$(printf '%s' "$txt" | sed 's/[\xc0-\xff][\x80-\xbf]*/X/g')
  if [ "${#folded}" -gt 4 ]; then echo "$line（${#folded} 字）"; fi
done)
if [ -n "$OUT" ]; then
  echo "[CI-3] confirmText 超过 4 字（弹窗会静默失败）："
  echo "$OUT"
  FAIL=1
fi

# ── CI-4 自定义导航页面必须使用 page-nav ──
# 用了 navigationStyle:custom 却没有固定导航栏，页面滚动时内容会从状态栏和
# 右上角胶囊按钮后面穿出来。launch 是全屏落地页，没有固定头，豁免。
for f in $(grep -rl '"navigationStyle": "custom"' --include="*.json" $DIRS 2>/dev/null); do
  w="${f%.json}.wxml"
  [ -f "$w" ] || continue
  case "$w" in
    *pages/launch/index.wxml) continue ;;
  esac
  if ! grep -q "page-nav" "$w"; then
    echo "[CI-4] 自定义导航页缺 page-nav（会导致滚动穿模）： $w"
    FAIL=1
  fi
done

# ── CI-5 禁止把导航栏背景写成 transparent ──
# page-nav 默认底色是 var(--bg-page)，显式透明会让内容从导航后面透出。
OUT=$(grep -rn 'bg="transparent"' --include="*.wxml" $DIRS 2>/dev/null)
if [ -n "$OUT" ]; then
  echo "[CI-5] 导航栏背景不得为 transparent："
  echo "$OUT"
  FAIL=1
fi

# ── 类型检查 ──
if [ -x node_modules/.bin/tsc ]; then
  if ! ./node_modules/.bin/tsc -p tsconfig.json --noEmit; then
    echo "[tsc] 类型检查失败"
    FAIL=1
  fi
else
  echo "[tsc] 未找到 node_modules/.bin/tsc，跳过类型检查"
fi

if [ "$FAIL" -eq 0 ]; then
  echo "全部通过（CI-1..5 + tsc）"
fi
exit "$FAIL"
