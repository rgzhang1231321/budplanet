-- Version: V005
-- 日期: 2026-10-30
-- 变更说明: 新增 app_logs 运行日志表。后端 ILogger 与小程序的未捕获异常统一落库，
--           排查线上问题不必再依赖控制台（进程一停就没了）。
-- 前置依赖: V004
-- 目标库: medbox
--
-- 设计要点（改之前先看这里）：
--   1. append-only，只写不改。清理按 ts_ms 批量删，不 UPDATE。
--   2. level_num 与 .NET LogLevel 枚举同值（Trace=0 … Critical=5），方便范围查询。
--   3. props 是结构化字段（logger 的 state 键值对），用 jsonb 存，可按内容检索。
--   4. message/exception 里不存 token、密码：写入前由 LogSanitizer 打码，这里不做约束。
--   5. 写入走独立 Npgsql 连接与批量 INSERT，不经过 EF —— 否则 EF 的 SQL 日志会被再记一遍，形成递归。

BEGIN;

CREATE TABLE IF NOT EXISTS app_logs (
  log_id       bigserial   PRIMARY KEY,
  ts_ms        bigint      NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  level        text        NOT NULL,
  level_num    smallint    NOT NULL,
  source       text        NOT NULL DEFAULT 'server',
  category     text,
  event_id     text,
  message      text,
  exception    text,
  props        jsonb,
  trace_id     text,
  http_method  text,
  http_path    text,
  status_code  integer,
  openid       text,
  page         text
);

COMMENT ON TABLE  app_logs            IS '运行日志（后端 + 小程序端）。append-only，超保留期由 ArchiveWorker 批量删除。';
COMMENT ON COLUMN app_logs.ts_ms      IS '日志发生时刻（Unix 毫秒）。分区/清理都按它。';
COMMENT ON COLUMN app_logs.level_num  IS '与 .NET LogLevel 同值：0 Trace / 1 Debug / 2 Information / 3 Warning / 4 Error / 5 Critical';
COMMENT ON COLUMN app_logs.source     IS 'server = 后端 ILogger；client = 小程序上报';
COMMENT ON COLUMN app_logs.category   IS 'logger 分类名（如 MedBox.Infrastructure.Workers.PushWorker）';
COMMENT ON COLUMN app_logs.event_id   IS 'EventId.Name，没有结构化埋点时为空';
COMMENT ON COLUMN app_logs.props      IS '结构化字段（logger state 键值对），键名与代码里 {Placeholder} 一致';
COMMENT ON COLUMN app_logs.trace_id   IS '同一次请求/一次操作串联用；后端取 HttpContext.TraceIdentifier';
COMMENT ON COLUMN app_logs.page       IS '仅 client 侧：出错的页面路径';

-- 最近日志（排查默认按时间倒序）
CREATE INDEX IF NOT EXISTS idx_app_logs_ts
  ON app_logs (ts_ms DESC);

-- 按级别看错误：WHERE level_num >= 4 ORDER BY ts_ms DESC
CREATE INDEX IF NOT EXISTS idx_app_logs_level_ts
  ON app_logs (level_num, ts_ms DESC);

-- 按模块过滤（"推送 Worker 最近报了什么"）
CREATE INDEX IF NOT EXISTS idx_app_logs_category_ts
  ON app_logs (category, ts_ms DESC);

-- 按请求串联
CREATE INDEX IF NOT EXISTS idx_app_logs_trace
  ON app_logs (trace_id)
  WHERE trace_id IS NOT NULL;

COMMIT;
