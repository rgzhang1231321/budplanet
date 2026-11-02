-- Version: V009
-- 日期: 2026-11-02
-- 变更说明: plans 增加 pause_until —— 暂停用药的到期日（P1-1）。
--           status 早已允许 'paused'，但缺"暂停到什么时候"，无法实现"到期自动恢复"。
--           暂停期间：不生成新剂量、不提醒、不计入完成率分母；到期后物化时自动恢复 active。
-- 前置依赖: V001（plans）
-- 目标库: medbox
-- 幂等: ADD COLUMN IF NOT EXISTS，可安全重跑。
--
-- 为什么用 VARCHAR(10) 而不是 DATE：
--   全库"自然日"口径统一为东八区 YYYY-MM-DD 字符串（due_date / start_date / cycle_end_date），
--   与 BIGINT UTC 毫秒的"时刻"字段严格分开（AGENTS §5 时间口径）。不要改成 DATE/timestamptz。

BEGIN;

ALTER TABLE plans ADD COLUMN IF NOT EXISTS pause_until VARCHAR(10);

-- 只为"暂停中"的行建部分索引：绝大多数计划是 active，全表索引没有意义。
CREATE INDEX IF NOT EXISTS ix_plans_paused
    ON plans (family_id, pause_until)
    WHERE status = 'paused';

COMMIT;
