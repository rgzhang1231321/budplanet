-- Version: V010
-- 日期: 2026-11-02
-- 变更说明: patients 增加 display_mode —— 「帮父母设大字版」（P1-3 延伸）。
--           默认 normal（恒普通版）；子女在家庭组页可给「服药人」设 large，
--           老人本机手动切换优先（本机 modeOverride 覆盖）。
-- 前置依赖: V001（patients）
-- 目标库: medbox
-- 幂等: ADD COLUMN IF NOT EXISTS + 约束判重，可安全重跑。
--
-- 为什么放 patients 而不是 family_members：
--   "界面大小"是给「用手机的那位老人」的默认值，服药人就是那位老人；
--   放 patient 上，子女在家庭组里点「服药人」那一行即可设置，语义直接。

BEGIN;

ALTER TABLE patients ADD COLUMN IF NOT EXISTS display_mode VARCHAR(8) NOT NULL DEFAULT 'normal';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_patients_display_mode') THEN
        ALTER TABLE patients ADD CONSTRAINT ck_patients_display_mode
            CHECK (display_mode IN ('normal', 'large'));
    END IF;
END $$;

COMMIT;
