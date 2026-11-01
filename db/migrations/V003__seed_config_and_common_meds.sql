-- Version: V003
-- 日期: 2026-10-29
-- 变更说明: 种子数据。app_config 初始开关（严格对齐契约冻结表 §三 camelCase 字段）、
--           常用药预设。用 ON CONFLICT DO NOTHING 保证可重复执行。
-- 前置依赖: V001（V002 索引非必需，但按序执行）
-- 目标库: medbox
-- 注意: 配置值以 JSONB 原样存 camelCase key，与前端 config 契约逐字一致，后端不做 key 转换。

BEGIN;

-- ── app_config 初始开关（契约冻结表 §三全量字段）────────────
INSERT INTO app_config (config_key, config_value, updated_at, updated_by) VALUES
('runtime', '{
  "adOn": true,
  "adSwitch": { "interstitial": true, "elderInterstitial": false },
  "interstitialCool": 30,
  "interstitialDayMax": 3,
  "freeMedLimit": 3,
  "freePatientLimit": 1,
  "tmplIds": { "CHECKIN": "", "MISSED": "", "WEEKLY": "" },
  "quietHours": { "start": "22:00", "end": "07:00" },
  "missedThresholdMin": 60,
  "snoozeDeadlineMin": 45,
  "notice": ""
}'::jsonb, 1761753600000, 'system')
ON CONFLICT (config_key) DO NOTHING;

-- ── 常用药预设（ medicines_common，公开只读 ）────────────────
-- default_dose 为展示串，后端不解析数值；dose_amount/unit 仅供结构化统计
INSERT INTO medicines_common (id, name, spec, default_dose, dose_amount, dose_unit, tags, created_at) VALUES
('cm_001', '苯磺酸氨氯地平片', '5mg×28片', '1片', 1, '片', ARRAY['降压','慢病'], 1761753600000),
('cm_002', '盐酸二甲双胍片',   '0.5g×20片','1片', 1, '片', ARRAY['降糖','慢病'], 1761753600000),
('cm_003', '阿司匹林肠溶片',   '100mg×30片','1片', 1, '片', ARRAY['心脑血管'],   1761753600000),
('cm_004', '阿托伐他汀钙片',   '20mg×7片',  '1片', 1, '片', ARRAY['降脂','慢病'], 1761753600000),
('cm_005', '碳酸钙D3片',       '600mg×60片','2片', 2, '片', ARRAY['补钙'],       1761753600000),
('cm_006', '左甲状腺素钠片',   '50μg×100片','半片', 0.5, '片', ARRAY['甲状腺'],   1761753600000)
ON CONFLICT (id) DO NOTHING;

COMMIT;
