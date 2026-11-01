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

**本目录当前进度**：`V001` → `V002` → `V003`（首个基线三件套）。**下一个可用版本固定为 `V004`**，任何人不得跳到 `V010` / `V100`。

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

- **单元测试**：不连真实库，用 EF Core InMemory / 仓储 mock（见 `docs/后端技术方案.md`）。
- **集成测试**：Testcontainers 起一次性 `postgres:18-alpine` 容器 → 执行器按序跑本目录全部脚本 → 断言 schema 与约束 → 测试结束容器销毁。**测试库与 `medbox`/`ragdb` 完全隔离**。
- 生产/本地开发库 `medbox` 的 schema，永远等于"本目录脚本从头跑到尾"的结果，**不靠 EF 自动建、不靠手工点点点**。

## 六、时间戳约定（沿用契约冻结表，不改）

- 所有"时刻"字段存 **`BIGINT` UTC 毫秒**（`due_ts` / `taken_at` / `created_at`…），与前端 `server_ts` 同单位。
- 只有"自然日"字段（`due_date`）存东八区字符串 `YYYY-MM-DD`（`VARCHAR(10)`）。
- `status` 用 `VARCHAR` + `CHECK` 约束冻结四值（`pending|taken|missed|skipped`），**不用 PG 原生 enum**（加枚举值也要写迁移脚本，用 CHECK 更直观且便于 TDD）。
- 选 `BIGINT` 而非 `timestamptz`：忠实还原冻结契约的 ms 语义，规避 UTC/本地时区换算 bug；范围查询走 B 树索引，性能等价。

---

**变更流程**：改 schema → 数目录 → 写 `V{MAX+1}__*.sql` → 本地 `apply.sh` 验证 → 集成测试(Testcontainers) → 提交脚本 → 在 `medbox` 执行。任何跳过脚本直接改库的操作都视为违规。
