-- Version: V007
-- 日期: 2026-11-01
-- 变更说明: 关闭广告总开关（app_config.runtime.adOn = false）。
--           广告位 unitId 尚未申请（流量主未开通），此时开着总开关没有收益：
--           AD_UNITS 全为空串 → 广告位一个都渲染不出来，`ad_show` 埋点也不会触发，
--           但频控、插屏判定这些逻辑会全部跑一遍，白耗一轮计算且给排查增加噪音。
--           先把总开关关到"明确的关"，等开通流量主、填好 unitId 后再打开。
-- 前置依赖: V003（app_config.runtime 种子）
-- 目标库: medbox
--
-- 为什么用 UPDATE 而不是改 V003：
--   V003 已执行，是 append-only 的冻结脚本。改它的内容会让 sha256 与
--   schema_migrations 账本失配，db/apply.sh 会判「脚本被篡改」直接中止，
--   整个迁移链路卡死（V001 上已经出过一次这个事故）。配置变更一律追加新脚本。
--
-- 怎么重新打开（重要，别改本脚本）：
--   1. 去 mp.weixin.qq.com 开通流量主 → 广告位管理 → 新建广告位，拿到 unitId
--   2. 把 unitId 填进 miniprogram/config/index.ts 的 AD_UNITS
--   3. 新建 V00N__ads_on.sql，内容是把 adOn 改回 true（追加，不要改本文件）
--   4. 跑 bash db/apply.sh
--   两个条件必须同时满足才会真的出广告：adOn=true 且 unitId 非空。
--
-- 幂等：jsonb_set 重复写同值是幂等的；config_key='runtime' 不存在时不报错。

BEGIN;

UPDATE app_config
SET config_value = jsonb_set(config_value, '{adOn}', 'false'::jsonb, true),
    updated_at   = (extract(epoch from now()) * 1000)::bigint,
    updated_by   = 'migration:V007'
WHERE config_key = 'runtime'
  AND config_value ? 'adOn';

COMMIT;
