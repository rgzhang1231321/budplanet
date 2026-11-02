-- Version: V011
-- 日期: 2026-11-02
-- 变更说明: 回填 families.invite_token 为 NULL 的存量家庭（审计 §13.2-1，路线图决策 5）。
--           令牌格式与 FamilyCreateHandler.NewToken() 逐字一致："IVT" + 8 位大写十六进制。
-- 前置依赖: V001（families + uq_families_invite_token partial unique index）
-- 目标库: medbox
-- 幂等: 只在 invite_token IS NULL 的行上写，重跑时空转。
--
-- 为什么必须做：invite_token 可空，而 FamilyJoinPolicy 旧实现"家庭没令牌就放行"，
-- 于是令牌为 NULL 的家庭等于凭 fid 就能被任何人加入。V011（本脚本）补数据、
-- 同批代码把"没码就拒"补上，两道一起才收口。只改代码不收数据会把历史家庭锁在门外，
-- 只收数据不改代码则新出现的空令牌仍是口子。
--
-- 为什么是 PL/pgSQL 逐行循环而不是一条 UPDATE：
--   uq_families_invite_token 是 WHERE invite_token IS NOT NULL 的 partial unique index，
--   一条 UPDATE 一旦有任一行撞号，整句回滚、全部家庭都拿不到令牌。逐行 + 撞号重生成
--   才能保证"最坏情况也只是某一行多重试几次"。

BEGIN;

DO $$
DECLARE
    r          RECORD;
    candidate  VARCHAR(64);
    attempts   INT;
    backfilled INT := 0;
BEGIN
    -- ORDER BY 只是为了可复现的遍历顺序，与结果无关
    FOR r IN SELECT family_id FROM families WHERE invite_token IS NULL ORDER BY family_id
    LOOP
        attempts := 0;
        LOOP
            attempts := attempts + 1;
            IF attempts > 50 THEN
                RAISE EXCEPTION 'V011: 家庭 % 的 invite_token 连续 50 次撞号，放弃（请加长随机段）', r.family_id;
            END IF;

            -- 等价于 C# "IVT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()
            candidate := 'IVT' || upper(substr(replace(gen_random_uuid()::text, '-', ''), 1, 8));

            BEGIN
                -- 条件里再带一次 IS NULL：极端并发下也不会把别的写入覆盖掉
                UPDATE families
                   SET invite_token = candidate
                 WHERE family_id = r.family_id
                   AND invite_token IS NULL;
                EXIT; -- 没抛 unique_violation 就是这一行搞定了
            EXCEPTION WHEN unique_violation THEN
                NULL;   -- 撞号：重生成再试（子事务已回滚，不影响外层）
            END;
        END LOOP;

        backfilled := backfilled + 1;
    END LOOP;

    RAISE NOTICE 'V011: 回填 % 个家庭的 invite_token', backfilled;
END $$;

-- 收尾自检：任何一条没填上就让整个迁移失败，不要把"半收口"留在库里
DO $$
DECLARE
    nulls INT;
    bad   INT;
BEGIN
    SELECT count(*) INTO nulls FROM families WHERE invite_token IS NULL;
    IF nulls > 0 THEN
        RAISE EXCEPTION 'V011: 仍有 % 个家庭的 invite_token 为 NULL', nulls;
    END IF;

    SELECT count(*) INTO bad FROM families WHERE invite_token !~ '^IVT[0-9A-F]{8}$';
    IF bad > 0 THEN
        RAISE EXCEPTION 'V011: 有 % 个 invite_token 不符合 IVT+8 位大写十六进制', bad;
    END IF;
END $$;

COMMIT;
