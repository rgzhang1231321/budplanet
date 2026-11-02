-- Version: V012
-- 日期: 2026-11-02
-- 变更说明: dose_logs 补 week_key 索引 + 回填历史 NULL（审计 §13.4.1，路线图决策 9a）。
--           三件套的另两件在同批代码里：WeekReportWorker 谓词改 week_key、
--           TimeCnWeekKeyTests 逐日断言两种口径等价。
-- 前置依赖: V001（dose_logs）、V002（ix_dose_logs_week 首列是 patient_id，按 week_key 过滤用不上）
-- 目标库: medbox
-- 幂等: CREATE INDEX IF NOT EXISTS + WHERE week_key IS NULL，可安全重跑。
--
-- 为什么不改 V002 的 ix_dose_logs_week：V002 是已执行脚本，内容永久冻结（append-only）。
-- 那条索引首列是 patient_id，"WHERE week_key = ?" 用不上它的前导列，只能全索引扫。
--
-- 为什么是部分索引（WHERE week_key IS NOT NULL）：本批之前生成的历史行该列为 NULL，
-- 全索引会把大量永不会被 week_key 谓词命中的 NULL 行也建进去。

BEGIN;

CREATE INDEX IF NOT EXISTS ix_dose_logs_week_key
    ON dose_logs (week_key) WHERE week_key IS NOT NULL;

-- 回填历史 NULL。推导式与 TimeCn.WeekKey 逐字一致（东八区 ISO 周）：
--   C#  ISOWeek.GetWeekOfYear / GetYear  →  "yyyy-Www"
--   SQL to_char(d, 'IYYY-"W"IW')         →  "yyyy-Www"
-- 已实测对齐：2020-01-01..2030-12-31 共 4018 天，两边输出零差异（含 2026-W53 长 ISO 年）。
-- due_date 是东八区自然日字符串，ISO 周只依赖"周内七天同属一周"这一性质，与时区偏移无关，
-- 所以直接对日期本身取 ISO 周即可，不需要先换算成 UTC 时刻。
UPDATE dose_logs
   SET week_key = to_char(to_date(due_date, 'YYYY-MM-DD'), 'IYYY-"W"IW')
 WHERE week_key IS NULL;

-- 收尾自检：留 NULL 就等于周报谓词改成 week_key 后会静默漏掉这些行
DO $$
DECLARE
    nulls INT;
    bad   INT;
BEGIN
    SELECT count(*) INTO nulls FROM dose_logs WHERE week_key IS NULL;
    IF nulls > 0 THEN
        RAISE EXCEPTION 'V012: 仍有 % 行 dose_logs.week_key 为 NULL', nulls;
    END IF;

    SELECT count(*) INTO bad FROM dose_logs WHERE week_key !~ '^20[0-9]{2}-W(0[1-9]|[1-4][0-9]|5[0-3])$';
    IF bad > 0 THEN
        RAISE EXCEPTION 'V012: 有 % 行 week_key 不符合 yyyy-Www', bad;
    END IF;
END $$;

COMMIT;
