-- Version: V006
-- 日期: 2026-11-01
-- 变更说明: 删除 app_logs.created_at（timestamptz），修正对数据库铁律 7 的违反。
--           铁律 7 要求所有"时刻"字段一律 BIGINT UTC 毫秒；V005 建表时同时写了
--           ts_ms BIGINT（真正在用）与 created_at timestamptz DEFAULT now()（冗余），
--           两列语义重复且口径不一，保留两套时间口径迟早会有人按错的列做统计。
-- 前置依赖: V005
-- 目标库: medbox
--
-- 为什么是 DROP 而不是改成 BIGINT：
--   1. ts_ms 已经是同一时刻的 UTC 毫秒（写入方 DbLogWriter 只写 ts_ms，不写 created_at），
--      改类型只会多出一个永远与 ts_ms 相等的列，没有信息增量。
--   2. 已确认全仓没有任何代码读 app_logs.created_at：
--      · DbLogWriter 的 INSERT 列清单是 (ts_ms,level,level_num,source,category,event_id,message,exception,props,trace_id,http_method,http_path,status_code,openid,page)
--      · ArchiveWorker.CleanLogsAsync 按 ts_ms 批量删
--      · server/scripts/logs.sh 的展示列用 to_timestamp(ts_ms/1000)
--   3. 删列顺带回收 timestamptz 的 8 字节与默认值 now()，不改变任何查询路径。
--
-- 幂等：用 information_schema 判列存在再删，表不存在或列已删都能安全重跑。

BEGIN;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name   = 'app_logs'
      AND column_name  = 'created_at'
  ) THEN
    ALTER TABLE app_logs DROP COLUMN created_at;
  END IF;
END $$;

COMMENT ON COLUMN app_logs.ts_ms IS '日志发生时刻（Unix 毫秒）。全表唯一的时刻口径：分区/清理/展示都按它。';

COMMIT;
