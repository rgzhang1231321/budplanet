-- Version: V002
-- 日期: 2026-10-29
-- 变更说明: 性能索引。严格按旧方案 §3.7 的 5 条核心索引 + 各表查询路径补齐。
--           索引单独成脚本（append-only），后续加索引一律新增版本，不改 V001。
-- 前置依赖: V001
-- 目标库: medbox

BEGIN;

-- ★ dose_logs：reminder-tick 每分钟扫描，决定整个系统性能（第一优先）
CREATE INDEX IF NOT EXISTS ix_dose_logs_tick
    ON dose_logs (due_ts, status, remind_status);

-- 家庭看板：一天内某家庭全部服药人
CREATE INDEX IF NOT EXISTS ix_dose_logs_family_date
    ON dose_logs (family_id, due_date);

-- 漏服兜底扫描
CREATE INDEX IF NOT EXISTS ix_dose_logs_alert
    ON dose_logs (status, alert_status, due_ts);

-- 周报聚合
CREATE INDEX IF NOT EXISTS ix_dose_logs_week
    ON dose_logs (patient_id, week_key);

-- 归档清理（按 due_date 删 90 天前明细）
CREATE INDEX IF NOT EXISTS ix_dose_logs_due_date
    ON dose_logs (due_date);

-- elder 只查自己绑定的服药人
CREATE INDEX IF NOT EXISTS ix_dose_logs_patient_due
    ON dose_logs (patient_id, due_ts);

-- users
CREATE INDEX IF NOT EXISTS ix_users_unionid        ON users (unionid) WHERE unionid IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_users_last_login     ON users (last_login_at);

-- families
CREATE INDEX IF NOT EXISTS ix_families_owner       ON families (owner_openid);

-- family_members：按 openid 反查所在家庭（登录一次返回）
CREATE INDEX IF NOT EXISTS ix_members_openid       ON family_members (openid, family_id);
CREATE INDEX IF NOT EXISTS ix_members_family_role  ON family_members (family_id, role);

-- patients
CREATE INDEX IF NOT EXISTS ix_patients_family_status ON patients (family_id, status);
CREATE INDEX IF NOT EXISTS ix_patients_self_openid   ON patients (self_openid) WHERE self_openid IS NOT NULL;

-- medicines
CREATE INDEX IF NOT EXISTS ix_medicines_family_patient_status ON medicines (family_id, patient_id, status);
CREATE INDEX IF NOT EXISTS ix_medicines_patient               ON medicines (patient_id);

-- plans：materializer 扫描 status+start_date
CREATE INDEX IF NOT EXISTS ix_plans_scan            ON plans (status, start_date);
CREATE INDEX IF NOT EXISTS ix_plans_family_patient  ON plans (family_id, patient_id);
CREATE INDEX IF NOT EXISTS ix_plans_patient         ON plans (patient_id);
-- times_minute 用 GIN 支持数组包含查询（可选，加速按分钟定位）
CREATE INDEX IF NOT EXISTS ix_plans_times_minute    ON plans USING GIN (times_minute);

-- push_tasks：worker 按 shard+status+next_retry_at 拉取
CREATE INDEX IF NOT EXISTS ix_push_tasks_pick
    ON push_tasks (shard, status, priority, next_retry_at);

-- subscriptions：批量找有额度的人
CREATE INDEX IF NOT EXISTS ix_subscriptions_quota
    ON subscriptions (tmpl_key, quota, last_used_at);

-- ad_events：日上限统计
CREATE INDEX IF NOT EXISTS ix_ad_events_openid_day ON ad_events (openid, product_id, created_at);

-- dose_views：recentView 取"昨天、非本人"最近一条
CREATE INDEX IF NOT EXISTS ix_dose_views_recent
    ON dose_views (family_id, viewed_at DESC);

-- correct_logs / audit_logs 审计查询
CREATE INDEX IF NOT EXISTS ix_correct_logs_family  ON correct_logs (family_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_audit_logs_openid    ON audit_logs (openid, created_at DESC);
-- idem_keys 过期清理
CREATE INDEX IF NOT EXISTS ix_idem_keys_expire     ON idem_keys (expire_at);

COMMIT;
