-- Version: V001
-- 日期: 2026-10-29
-- 变更说明: 基线建表。由云开发 23 个 MongoDB 集合 → PostgreSQL 关系表。
--           同时建立迁移账本 schema_migrations（append-only + 校验和防篡改）。
-- 前置依赖: 无（首个脚本）
-- 目标库: medbox（严禁对 ragdb 执行）
-- 设计裁决:
--   1) dose_id 采用契约冻结表 §6 口径 = ${patient_id}_${due_date}_${slot}，
--      slot = due_minute(0-1439)。因此 checkin.submit 的幂等键 pid_date_slot 与 dose_id 完全等价。
--      （旧云开发方案 §3.7 写成 ${plan_id}_... 是被修正的旧稿，以契约冻结表为准。）
--   2) 所有时刻字段存 BIGINT UTC 毫秒；自然日 due_date 存东八区字符串 YYYY-MM-DD。
--   3) status 用 VARCHAR+CHECK 冻结四值，不用 PG enum。
--   4) plans.slots 用 JSONB 存对象数组 [{t,d,slot_key}]，times_minute 用 INT[] 派生双写供扫描。

BEGIN;

-- ── 迁移账本（执行器依赖；幂等）──────────────────────────────
CREATE TABLE IF NOT EXISTS schema_migrations (
    version       VARCHAR(16)  PRIMARY KEY,
    checksum      VARCHAR(64)  NOT NULL,
    applied_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    execution_ms  BIGINT       NOT NULL DEFAULT 0,
    script_name   VARCHAR(255) NOT NULL
);

-- ── users ────────────────────────────────────────────────────
-- _id(=openid) 直接做主键，沿用原设计省一次查询
CREATE TABLE IF NOT EXISTS users (
    openid                VARCHAR(64)  PRIMARY KEY,
    unionid               VARCHAR(64),
    nick_name             VARCHAR(128),
    avatar_url            VARCHAR(512),
    family_ids            TEXT[]       NOT NULL DEFAULT '{}',   -- 冗余，登录一次返回；可由 family_members 派生
    active_family_id      VARCHAR(64),
    setting               JSONB        NOT NULL DEFAULT '{}'::jsonb,  -- {sound_on,dnd_start,dnd_end,font_scale}
    entitlement           JSONB        NOT NULL DEFAULT '{}'::jsonb,  -- {ad_free_until,family_until,source}
    privacy_agreed_at     BIGINT,
    disclaimer_agreed_at  BIGINT,
    unreachable           BOOLEAN      NOT NULL DEFAULT FALSE,  -- 40003 标记
    created_at            BIGINT       NOT NULL,
    last_login_at         BIGINT
);

-- ── families ─────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS families (
    family_id         VARCHAR(64)  PRIMARY KEY,
    name              VARCHAR(128) NOT NULL,
    owner_openid      VARCHAR(64)  NOT NULL,
    invite_token      VARCHAR(64),
    invite_expire_at  BIGINT,
    tier              VARCHAR(16)  NOT NULL DEFAULT 'free'
                        CHECK (tier IN ('free','ad_free','family')),
    patient_count     INTEGER      NOT NULL DEFAULT 0,
    member_count      INTEGER      NOT NULL DEFAULT 0,
    history_forever_at BIGINT,                          -- 家庭级 HISTORY_FOREVER 权益解锁时间
    created_at        BIGINT       NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_families_invite_token
    ON families (invite_token) WHERE invite_token IS NOT NULL;

-- ── family_members（复合主键天然防重复加入）──────────────────
CREATE TABLE IF NOT EXISTS family_members (
    family_id        VARCHAR(64) NOT NULL,
    openid           VARCHAR(64) NOT NULL,
    role             VARCHAR(16) NOT NULL CHECK (role IN ('admin','member','elder')),
    relation         VARCHAR(32),
    is_patient       BOOLEAN      NOT NULL DEFAULT FALSE,
    self_patient_id  VARCHAR(64),                     -- elder 绑定的服药人（决定数据范围与提醒对象）
    notify_enabled   BOOLEAN      NOT NULL DEFAULT TRUE,
    joined_at        BIGINT       NOT NULL,
    PRIMARY KEY (family_id, openid)
);

-- ── patients（服药人）────────────────────────────────────────
CREATE TABLE IF NOT EXISTS patients (
    patient_id     VARCHAR(64)  PRIMARY KEY,
    family_id      VARCHAR(64)  NOT NULL,
    name           VARCHAR(128) NOT NULL,
    alias          VARCHAR(64),
    avatar_file_id VARCHAR(512),
    self_openid    VARCHAR(64),                       -- 仅 family.join 写入一次，之后无接口可改
    chronic_tags   TEXT[]       NOT NULL DEFAULT '{}',  -- 仅展示，禁止用于任何用药建议（合规红线）
    status         VARCHAR(16)  NOT NULL DEFAULT 'active'
                     CHECK (status IN ('active','archived')),
    created_at     BIGINT       NOT NULL
);

-- ── medicines_common（常用药预设，公开只读）──────────────────
CREATE TABLE IF NOT EXISTS medicines_common (
    id            VARCHAR(64)  PRIMARY KEY,
    name          VARCHAR(128) NOT NULL,
    spec          VARCHAR(128),
    default_dose  VARCHAR(32),
    dose_amount   NUMERIC(10,3),
    dose_unit     VARCHAR(16),
    tags          TEXT[]       NOT NULL DEFAULT '{}',
    created_at    BIGINT       NOT NULL
);

-- ── medicines ────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS medicines (
    medicine_id    VARCHAR(64)  PRIMARY KEY,
    family_id      VARCHAR(64)  NOT NULL,
    patient_id     VARCHAR(64)  NOT NULL,
    name           VARCHAR(128) NOT NULL,
    spec           VARCHAR(128),
    default_dose   VARCHAR(32),                       -- 展示串，后端不解析数值、不归一单位
    dose_amount    NUMERIC(10,3),
    dose_unit      VARCHAR(16),
    remark         VARCHAR(255),
    photo_file_id  VARCHAR(512),
    drug_code      VARCHAR(64),                       -- 未来条码识别留口子
    status         VARCHAR(16)  NOT NULL DEFAULT 'active'
                     CHECK (status IN ('active','archived')),
    created_by     VARCHAR(64),
    created_at     BIGINT       NOT NULL
);

-- ── plans（1 药品 : 1 计划，多 slot 落 slots JSONB）───────────
CREATE TABLE IF NOT EXISTS plans (
    plan_id         VARCHAR(64)  PRIMARY KEY,
    family_id       VARCHAR(64)  NOT NULL,
    patient_id      VARCHAR(64)  NOT NULL,
    medicine_id     VARCHAR(64)  NOT NULL,
    slots           JSONB        NOT NULL DEFAULT '[]'::jsonb,  -- [{t:"08:00",d:"1片",slot_key:"s0800"}]
    times_minute    INTEGER[]    NOT NULL DEFAULT '{}',          -- 由 slots 派生，扫描按分钟走，同事务更新
    cycle_type      VARCHAR(16)  NOT NULL DEFAULT 'long_term'
                      CHECK (cycle_type IN ('long_term','days_n')),
    cycle_days      INTEGER,
    cycle_end_date  VARCHAR(10),
    start_date      VARCHAR(10)  NOT NULL,
    status          VARCHAR(16)  NOT NULL DEFAULT 'active'
                      CHECK (status IN ('active','paused','finished')),
    advance_min     INTEGER      NOT NULL DEFAULT 0,
    created_by      VARCHAR(64),
    updated_at      BIGINT       NOT NULL
);
-- 契约：1 药品 : 1 计划，唯一约束防 materializer 生成重复剂量
CREATE UNIQUE INDEX IF NOT EXISTS uq_plans_medicine
    ON plans (medicine_id) WHERE status <> 'finished';

-- ── dose_logs ★ 核心表，量最大 ───────────────────────────────
CREATE TABLE IF NOT EXISTS dose_logs (
    dose_id          VARCHAR(128) PRIMARY KEY,          -- ${patient_id}_${due_date}_${slot(=due_minute)}
    family_id        VARCHAR(64)  NOT NULL,
    patient_id       VARCHAR(64)  NOT NULL,
    plan_id          VARCHAR(64)  NOT NULL,
    medicine_id      VARCHAR(64)  NOT NULL,
    dose_text        VARCHAR(32),                       -- 该 slot 展开后的剂量快照，禁 join 现算
    med_name_snapshot VARCHAR(128),                     -- 药名快照，改名/删除不影响历史
    due_date         VARCHAR(10)  NOT NULL,             -- 东八区 YYYY-MM-DD
    slot             INTEGER      NOT NULL,             -- = due_minute，0-1439
    due_ts           BIGINT       NOT NULL,             -- UTC 毫秒，扫描索引主键
    status           VARCHAR(16)  NOT NULL DEFAULT 'pending'
                       CHECK (status IN ('pending','taken','missed','skipped')),
    taken_at         BIGINT,
    taken_by         VARCHAR(64),
    is_late          BOOLEAN      NOT NULL DEFAULT FALSE,
    skip_reason      VARCHAR(255),
    snooze_count     INTEGER      NOT NULL DEFAULT 0,
    remind_status    SMALLINT     NOT NULL DEFAULT 0,   -- 0未推 1已推 2失败 3无额度降级
    remind_at        BIGINT,
    remind_errcode   INTEGER,
    alert_status     SMALLINT     NOT NULL DEFAULT 0,   -- 0未通知 1已通知 2失败
    alert_at         BIGINT,
    week_key         VARCHAR(8),                        -- 2025-W03
    is_corrected     BOOLEAN      NOT NULL DEFAULT FALSE,
    checked_by       VARCHAR(64),
    checked_at       BIGINT,
    created_at       BIGINT       NOT NULL
);

-- ── subscriptions（订阅消息额度池）───────────────────────────
CREATE TABLE IF NOT EXISTS subscriptions (
    openid            VARCHAR(64) NOT NULL,
    tmpl_key          VARCHAR(16) NOT NULL
                        CHECK (tmpl_key IN ('CHECKIN','MISSED','WEEKLY','RECHECK')),
    tmpl_id           VARCHAR(128),
    quota             INTEGER      NOT NULL DEFAULT 0,
    main_switch       BOOLEAN      NOT NULL DEFAULT TRUE,
    granted_total     INTEGER      NOT NULL DEFAULT 0,
    used_total        INTEGER      NOT NULL DEFAULT 0,
    last_grant_at     BIGINT,
    last_used_at      BIGINT,
    exhaust_warned_at BIGINT,
    PRIMARY KEY (openid, tmpl_key)
);

-- ── push_tasks（推送任务队列，落库削峰）──────────────────────
CREATE TABLE IF NOT EXISTS push_tasks (
    task_id        VARCHAR(128) PRIMARY KEY,           -- md5(openid+分钟桶+tmpl_key)
    shard          SMALLINT      NOT NULL DEFAULT 0,
    openid         VARCHAR(64)  NOT NULL,
    tmpl_key       VARCHAR(16)  NOT NULL,
    priority       SMALLINT      NOT NULL DEFAULT 0,   -- 1=漏服优先
    status         VARCHAR(16)   NOT NULL DEFAULT 'queued'
                     CHECK (status IN ('queued','sending','sent','dropped','failed')),
    payload        JSONB         NOT NULL DEFAULT '{}'::jsonb,
    retry_count    SMALLINT      NOT NULL DEFAULT 0,
    next_retry_at  BIGINT        NOT NULL DEFAULT 0,
    errcode        INTEGER,
    created_at     BIGINT        NOT NULL,
    sent_at        BIGINT
);

-- ── entitlements（激励视频解锁权益，无现金支付）──────────────
CREATE TABLE IF NOT EXISTS entitlements (
    entitlement_id VARCHAR(64)  PRIMARY KEY,
    family_id      VARCHAR(64)  NOT NULL,
    openid         VARCHAR(64)  NOT NULL,
    product_id     VARCHAR(32)  NOT NULL,             -- HISTORY_FOREVER / MED_SLOT_4 ...
    credits        INTEGER      NOT NULL DEFAULT 0,
    source         VARCHAR(16)  NOT NULL DEFAULT 'ad',
    expire_at      BIGINT,
    created_at     BIGINT       NOT NULL
);

-- ── ad_events（ad.unlock 防重放：唯一键 + 30s 窗 + 日上限）──
CREATE TABLE IF NOT EXISTS ad_events (
    ad_trace     VARCHAR(128) PRIMARY KEY,
    openid       VARCHAR(64)  NOT NULL,
    family_id    VARCHAR(64)  NOT NULL,
    product_id   VARCHAR(32)  NOT NULL,
    granted      BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at   BIGINT       NOT NULL
);

-- ── weekly_stats（归档后永久保留的聚合）──────────────────────
CREATE TABLE IF NOT EXISTS weekly_stats (
    family_id            VARCHAR(64) NOT NULL,
    patient_id           VARCHAR(64) NOT NULL,
    week_key             VARCHAR(8)  NOT NULL,
    total                INTEGER      NOT NULL DEFAULT 0,
    taken_cnt            INTEGER      NOT NULL DEFAULT 0,
    missed_cnt           INTEGER      NOT NULL DEFAULT 0,
    skipped_cnt          INTEGER      NOT NULL DEFAULT 0,
    manual_correct_count INTEGER      NOT NULL DEFAULT 0,  -- 单列，不计入完成率
    updated_at           BIGINT       NOT NULL,
    PRIMARY KEY (family_id, patient_id, week_key)
);

-- ── dose_views（已读回执，增长 P1，recentView 数据来源）─────
CREATE TABLE IF NOT EXISTS dose_views (
    view_id      VARCHAR(64)  PRIMARY KEY,
    family_id    VARCHAR(64)  NOT NULL,
    patient_id   VARCHAR(64),
    viewer_openid VARCHAR(64) NOT NULL,
    viewer_role  VARCHAR(16),
    viewer_alias VARCHAR(64),
    viewed_at    BIGINT       NOT NULL
);

-- ── stats_daily（增长看板日指标）─────────────────────────────
CREATE TABLE IF NOT EXISTS stats_daily (
    stat_date    VARCHAR(10)  NOT NULL,
    metric_key   VARCHAR(32)  NOT NULL,
    metric_value JSONB        NOT NULL DEFAULT '{}'::jsonb,
    created_at   BIGINT       NOT NULL,
    PRIMARY KEY (stat_date, metric_key)
);

-- ── correct_logs（人工改判独立审计表，checkin.correct 专用）──
CREATE TABLE IF NOT EXISTS correct_logs (
    log_id           VARCHAR(64)  PRIMARY KEY,
    dose_id          VARCHAR(128) NOT NULL,
    family_id        VARCHAR(64)  NOT NULL,
    operator_openid  VARCHAR(64)  NOT NULL,
    from_status      VARCHAR(16)  NOT NULL,
    to_status        VARCHAR(16)  NOT NULL,
    reason           VARCHAR(255),
    created_at       BIGINT       NOT NULL
);

-- ── idem_keys（checkin.submit 幂等键，10 分钟 TTL）───────────
CREATE TABLE IF NOT EXISTS idem_keys (
    idem_key    VARCHAR(128) PRIMARY KEY,
    response    JSONB        NOT NULL,
    created_at  BIGINT       NOT NULL,
    expire_at   BIGINT       NOT NULL
);

-- ── audit_logs（越权 403 等审计）─────────────────────────────
CREATE TABLE IF NOT EXISTS audit_logs (
    log_id      VARCHAR(64)  PRIMARY KEY,
    openid      VARCHAR(64),
    family_id   VARCHAR(64),
    action      VARCHAR(64)  NOT NULL,
    result_code INTEGER      NOT NULL,
    detail      JSONB        NOT NULL DEFAULT '{}'::jsonb,
    created_at  BIGINT       NOT NULL
);

-- ── app_config（远端下发开关，广告总开关不发版生效）─────────
CREATE TABLE IF NOT EXISTS app_config (
    config_key   VARCHAR(32) PRIMARY KEY,
    config_value JSONB       NOT NULL,
    updated_at   BIGINT      NOT NULL,
    updated_by   VARCHAR(64)
);

COMMIT;
