-- Version: V004
-- 日期: 2026-10-30
-- 变更说明: 写入三个订阅消息模板 ID 到 app_config.runtime.tmplIds。
--           模板来源：微信公众平台「订阅消息 → 一次性订阅 → 我的模板」（审核通过后复制）。
--           与后端 PushTaskBuilder.TemplateData 严格对应：
--             CHECKIN → 药剂更换提醒   (编号 10596)  字段 short_thing7 / time6
--             MISSED  → 工作任务待办通知 (编号 49925)  字段 thing18 / time3 / thing14
--             WEEKLY  → 周报更新提醒   (编号 1542)   字段 time5 / thing6
-- 前置依赖: V003（runtime 行已种子写入，tmplIds 为空串占位）
-- 目标库: medbox
-- 铁律: 必须经由 db/apply.sh 执行（append-only）；禁止直接 psql 连库 UPDATE app_config。

BEGIN;

UPDATE app_config
SET config_value = jsonb_set(
        config_value,
        '{tmplIds}',
        '{
          "CHECKIN": "goqfpenKaS9IILIzItZUpt1SaNjZ5XcHgHqIEbitS_w",
          "MISSED":  "Dv3W1DnoL-bxD5GRf8NJ3aC35z0DoqkOCrUw2QQDEaQ",
          "WEEKLY":  "Ip4U864D5NGg6gG4ITj5CN9kMSxLysQNZpEq70va8Rs"
        }'::jsonb
    )
WHERE config_key = 'runtime';

-- 校验：三件套任一为空说明替换未完成，整个事务回滚（配合 apply.sh 的 ON_ERROR_STOP）
DO $$
DECLARE
    v jsonb;
BEGIN
    SELECT config_value -> 'tmplIds' INTO v FROM app_config WHERE config_key = 'runtime';
    IF v ->> 'CHECKIN' = '' OR v ->> 'MISSED' = '' OR v ->> 'WEEKLY' = '' THEN
        RAISE EXCEPTION 'tmplIds 写入不完整，存在空值';
    END IF;
END $$;

COMMIT;
