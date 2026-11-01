-- Version: V008
-- 日期: 2026-11-02
-- 变更说明: 新增 medicine_usage_stats —— 全平台药名引用次数（P22「常用药」Top5 与药名联想的数据源）。
--           口径（主理人定）：**所有家庭**填写/使用这个药的次数，每次添加 +1、每次删除（归档）−1。
--           表按 name_key 归一（去首尾空白 + 小写）跨家庭唯一；不存任何 family_id / patient_id，
--           这张表本身不含可回指到个人的信息。
-- 前置依赖: V001（medicines / medicines_common）、V002
-- 目标库: medbox
-- 幂等: CREATE TABLE IF NOT EXISTS + ON CONFLICT DO NOTHING，可安全重跑。
--
-- 为什么单独一张表、不在 medicines 上加计数列：
--   引用次数是"跨家庭、按药名"的聚合，medicines 是"按家庭、按服药人"的行。
--   在 medicines 上加计数列，同一个药名会在每个家庭各存一份互相矛盾的数，
--   取全平台前 5 就得全表扫描 + GROUP BY，而这个接口是 P22 一打开就调的读接口。

BEGIN;

CREATE TABLE IF NOT EXISTS medicine_usage_stats (
    name_key      VARCHAR(128) PRIMARY KEY,   -- 归一药名：lower(btrim(name)) + 去全角空格，跨家庭唯一
    name          VARCHAR(128) NOT NULL,      -- 展示名（最近一次添加时的写法）
    spec          VARCHAR(128),
    default_dose  VARCHAR(32),
    use_count     INTEGER      NOT NULL DEFAULT 0,
    last_used_at  BIGINT       NOT NULL       -- UTC 毫秒（铁律 7）
);

-- 榜单查询：use_count DESC 走索引，last_used_at 做同分定序
CREATE INDEX IF NOT EXISTS ix_med_usage_rank
    ON medicine_usage_stats (use_count DESC, last_used_at DESC);

COMMENT ON TABLE  medicine_usage_stats IS '全平台药名引用次数（药名已归一）。仅药名/规格/默认剂量/计数，无家庭与个人标识。';
COMMENT ON COLUMN medicine_usage_stats.use_count IS '净添加次数：每次添加 +1、每次删除（归档）−1，下限 0；为 0 的行不参与榜单。';
COMMENT ON COLUMN medicine_usage_stats.last_used_at IS '最近一次添加的 UTC 毫秒，同分时的次级排序键（删除不动它）。';

-- 回填历史：把库里**在用的** medicines 行按归一药名聚一次，避免上线后榜单从 0 重来。
-- 只算 status='active'：归档的行在口径里等于"已经减过 1"，再算进来就重复了。
-- 展示值取"最近一次添加"的写法（array_agg 按 created_at DESC 取第一个）。
-- ON CONFLICT DO NOTHING：重跑不重复累加（脚本只会执行一次，这层是崩溃恢复保险）。
INSERT INTO medicine_usage_stats (name_key, name, spec, default_dose, use_count, last_used_at)
SELECT lower(btrim(m.name, E' \t\r\n' || chr(12288)))                AS name_key,
       (array_agg(m.name         ORDER BY m.created_at DESC))[1]      AS name,
       (array_agg(m.spec         ORDER BY m.created_at DESC))[1]      AS spec,
       (array_agg(m.default_dose ORDER BY m.created_at DESC))[1]      AS default_dose,
       count(*)::int                                                 AS use_count,
       max(m.created_at)                                             AS last_used_at
FROM medicines m
WHERE m.status = 'active'
  AND btrim(m.name, E' \t\r\n' || chr(12288)) <> ''
GROUP BY lower(btrim(m.name, E' \t\r\n' || chr(12288)))
ON CONFLICT (name_key) DO NOTHING;

COMMIT;
