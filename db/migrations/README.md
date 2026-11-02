# 数据库变更规范（SQL 脚本先行 · 铁律）

> 适用库：**`medbox`**（PostgreSQL 18，本机 docker 实例 `tigerrag-postgres-1`，127.0.0.1:5432）
> **严禁连接或改动 `ragdb`** —— 那是主理人在用的其他项目库。本目录所有脚本只针对 `medbox`。
> **严禁使用 EF Core Migrations**（`dotnet ef migrations add` / `Database.Migrate()` / `EnsureCreated()`）。所有建表、改字段、加索引一律手写 SQL 脚本。

---

## 一、编号铁律（违反即拒绝执行）

1. 文件名格式固定：`V{NNN}__{snake_case_描述}.sql`，例：`V001__baseline_schema.sql`。
   - `V` 大写；`{NNN}` 为 **3 位零填充十进制序号**；分隔符为**双下划线** `__`。
2. 序号从 `001` 起，**严格 +1 递增，不得跳号、不得回退、不得重复、不得随意构造**。
   - 下一个版本号 **永远** = 当前目录已存在最大序号 `+1`。要加变更，先数目录，别凭记忆写号。
   - 断号（如缺 `V005` 直接出现 `V006`）、重号、乱序 —— 执行器一律 **拒绝并中止**。
3. **append-only（只追加）**：已经合入/执行过的脚本 **内容永久冻结，禁止修改、禁止删除、禁止重排**。
   - 执行器对每个已执行版本记录 SHA-256 校验和；发现文件内容被改动 → **拒绝执行并报错**，要求改用新版本号修复。
   - 需要"改表"时，正确做法是**新增下一个脚本**（如 `V004__alter_dose_logs_add_xxx.sql`），而不是回去改 `V001`。
4. 每个脚本 **必须可重复安全执行**：用 `IF NOT EXISTS` / `CREATE OR REPLACE` / 事务包裹；DDL 尽量幂等。
   - 但"可重跑"不等于"可以改已执行文件"—— 幂等是为了崩溃恢复，版本冻结是为了审计，两者都要。
5. **一个脚本一个事务**。执行器把单个脚本包在 `BEGIN; ... COMMIT;` 里；失败则整体回滚，不写账本。
6. 脚本头部必须写注释：`-- Version: Vxxx` / `-- 日期` / `-- 变更说明` / `-- 前置依赖版本`。

## 二、版本号从哪来（防"随意构造"）

```bash
# 列出当前最大版本（执行器内部也是这么算的）
ls db/migrations/V*.sql | sed -E 's/.*V([0-9]{3}).*/\1/' | sort -n | tail -1
# 下一个 = 上面结果 + 1，零填充 3 位
```

**本目录当前进度**：`V001` → … → `V012`（见下表）。**下一个可用版本 = `V013`**，任何人不得跳号。

> 2026-11-02 校正：这行此前一直写"当前进度 V001→V003，下一个是 V004"——V004–V012 应用时都没回来更新它。
> 一个指向旧版本号的 README 比没有更危险：它会让人以为 `V004` 还空着，而执行器其实会拒绝重号。
> **每次新增迁移脚本，顺手把这行和下表改掉。**

| 版本 | 文件 | 干了什么 |
|---|---|---|
| V001 | `V001__baseline_schema.sql` | 19 张业务表（0 外键）+ `schema_migrations` 账本 |
| V002 | `V002__performance_indexes.sql` | 26 条 `CREATE INDEX`（以脚本实际条数为准，不是文档里的 48） |
| V003 | `V003__seed_config_and_common_meds.sql` | `app_config` 种子 + 6 个常用药 |
| V004 | `V004__set_subscribe_template_ids.sql` | 三套订阅消息模板 ID 写入 `app_config` |
| V005 | `V005__create_app_logs.sql` | 客户端/服务端日志表 `app_logs` |
| V006 | `V006__fix_app_logs_created_at.sql` | 删 `app_logs.created_at`（时间口径统一走 `ts_ms`） |
| V007 | `V007__ads_off_by_default.sql` | 广告默认关（`config.adOn=false`），上线前远端开 |
| V008 | `V008__medicine_usage_stats.sql` | 全平台药名引用计数 `medicine_usage_stats`（只存药名+计数，不存任何家庭维度） |
| V009 | `V009__plan_pause_until.sql` | `plans.pause_until` + 暂停索引（暂停用药，到期自动恢复） |
| V010 | `V010__patient_display_mode.sql` | `patients.display_mode`（normal/large，子女给服药人设默认大字版） |
| V011 | `V011__backfill_family_invite_token.sql` | 回填存量家庭 `invite_token`（PL/pgSQL 逐行 + 撞号重试 + 零残留自检） |
| V012 | `V012__dose_logs_week_key_index_and_backfill.sql` | `ix_dose_logs_week_key` 部分索引 + 按 `due_date` 同源推导回填 `week_key` |

## 三、执行账本 `schema_migrations`

执行器在目标库自动维护账本表（不占业务版本号）：

| 列 | 说明 |
|---|---|
| `version` | 主键，`V001` 形式 |
| `checksum` | 脚本内容 SHA-256（十六进制） |
| `applied_at` | 执行完成时间（timestamptz） |
| `execution_ms` | 耗时 |
| `script_name` | 原始文件名 |

判定逻辑：`version` 不在账本 → 执行；已在账本但 `checksum` 不符 → **报错中止**（说明有人偷改了已执行脚本）。

## 四、如何执行

```bash
# 应用所有未执行脚本到 medbox（默认连 docker 内的 psql，无需本地装客户端）
bash db/apply.sh

# 只校验不执行（CI 用：检查断号/校验和/篡改）
bash db/apply.sh --dry-run
```

或手动单次执行某脚本（**仅限本地调试，正式以 apply.sh 为准**）：

```bash
docker exec -i tigerrag-postgres-1 psql -U tigerrag -d medbox < db/migrations/V001__baseline_schema.sql
```

## 五、与 TDD 的关系

- **单元测试**：不连真实库，mock `IAppDbContext` / `IWxApiClient` / `IClock`（见 `AGENTS.md` §9）。
  **不用 EF Core InMemory** —— 它不过真实 CHECK 约束与 `ON CONFLICT`，四态迁移与幂等这两类最要紧的规则它一个都测不到。
- **集成测试**：Testcontainers 起一次性 `postgres:18-alpine` 容器 → 按序执行本目录全部 `V*.sql` → 断言 schema 与约束 → 测试结束容器销毁。**测试库与 `medbox`/`ragdb` 完全隔离**。
- 生产/本地开发库 `medbox` 的 schema，永远等于"本目录脚本从头跑到尾"的结果，**不靠 EF 自动建、不靠手工点点点**。

## 六、时间戳约定（沿用契约冻结表，不改）

- 所有"时刻"字段存 **`BIGINT` UTC 毫秒**（`due_ts` / `taken_at` / `created_at`…），与前端 `server_ts` 同单位。
- 只有"自然日"字段（`due_date`）存东八区字符串 `YYYY-MM-DD`（`VARCHAR(10)`）。
- `status` 用 `VARCHAR` + `CHECK` 约束冻结四值（`pending|taken|missed|skipped`），**不用 PG 原生 enum**（加枚举值也要写迁移脚本，用 CHECK 更直观且便于 TDD）。
- 选 `BIGINT` 而非 `timestamptz`：忠实还原冻结契约的 ms 语义，规避 UTC/本地时区换算 bug；范围查询走 B 树索引，性能等价。

---

**变更流程**：改 schema → 数目录 → 写 `V{MAX+1}__*.sql` → 本地 `apply.sh` 验证 → 集成测试(Testcontainers) → 提交脚本 → 在 `medbox` 执行。任何跳过脚本直接改库的操作都视为违规。
