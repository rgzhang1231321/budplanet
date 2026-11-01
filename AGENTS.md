# BudPlanet（替你记着吃药）开发代理规范

你是 BudPlanet 项目的软件开发代理。目标：在不破坏既有设计与契约的前提下，用**最小、清晰、可验证**的改动完成需求，并保持分层架构、SQL 迁移纪律、前后端契约一致。

> 本文件是仓库唯一的开发规范。`CLAUDE.md` 只是指向本文件的入口，内容不重复。
> 与 `docs/` 冲突时，按 §2 的优先级裁决；与用户当前明确要求冲突时，**以用户当前要求为准，并回写本文件**。

---

## 0. 一分钟上下文（动手前必须知道）

- **产品**：微信小程序「替你记着吃药」——子女给父母设吃药提醒，老人到点收到订阅消息、点一下「我已服用」，子女实时看到今天吃没吃。**只做提醒 + 记录 + 家属可见**，不做问诊、不做电商、不做药品库。
- **技术栈**：微信原生小程序（TS，仅 tsc）+ 自建 **.NET 10 Web API** + **PostgreSQL 18**（库名 `medbox`）。UI 框架分层：老人端与主包**自研**，子女端分包用 **TDesign 1.17.0**。
- **当前仓库状态**：只有 `docs/`（8 份方案）、`db/`（V001–V003 已执行 + `apply.sh`）、`assets/icon/`。**`server/` 与 `miniprogram/` 尚未创建** —— 首次落代码必须严格按 `docs/后端技术方案.md` §2.1 与 `docs/前端技术方案.md` §2 的目录结构建，不要自创布局。
- **数据库现状**：`V001` 建 19 张业务表 + 账本 `schema_migrations`，`V002` 建性能索引，`V003` 种子（`app_config` + 6 个常用药）。**下一个可用版本号固定为 `V004`**。
- **契约现状**：11 个业务域已冻结（`user/family/patient/medicine/plan/schedule/checkin/notify/ad/report/config`），字段、错误码、`server_ts`、四态枚举逐字不可改。

---

## 1. 开始工作前

### 1.1 必读顺序（按任务取用，不要跳）

1. `docs/00-总方案.md` —— 决策总纲，判断"该不该做"。
2. `docs/契约冻结表.md` —— **前后端唯一真相源**，字段/错误码/命名。
3. 后端任务：`docs/后端技术方案.md` + `db/migrations/README.md` + `db/apply.sh`。
4. 前端任务：`docs/前端技术方案.md`（尤其 §5.1、§6、§11、§16 踩坑记录）+ `docs/UI-设计规范.md`。
5. 产品/文案/交互：`docs/PRD-小药盒.md` + `docs/UI-设计规范.md` + `docs/合规与通知文案.md`（隐私政策/用户协议/免责/订阅消息模板，**页面文案一律照抄，不要自己编**）。
6. `docs/后端云开发方案.md` —— **仅历史参考，技术选型已废弃**。可以读它的时间轮、额度池、errcode 判决表、模板申请表；**不得**照它写云函数、云数据库或 `wx.cloud.*`。
7. `docs/增长与变现方案.md` —— 只在上线/增长/广告优化任务时读。

### 1.2 开工前的状态说明（首次回复必须包含）

用 5 行内说清：需求目标、验收标准、改动范围、潜在影响、验证命令。低风险可逆改动直接做；以下情况**必须先说明影响并等用户确认**：

- 改任何**已冻结字段**、action 名、错误码、状态枚举；
- 改数据库结构（新脚本 `V004+`）、批量改/删数据；
- 改认证或权限模型（JWT Claims、`assertMember` 语义、角色矩阵）；
- 引入新依赖或第三方服务（含 NuGet / npm，版本必须精确锁定）；
- 跨前后端的重构、影响 3 个以上模块的改动；
- 停止任何非本任务启动的进程、容器。

---

## 2. 真相源与冲突裁决

**优先级（高 → 低）**：`契约冻结表.md` > `后端技术方案.md` / `前端技术方案.md` > `PRD` / `UI 设计规范` > `00-总方案.md` > `后端云开发方案.md`（历史）> 其他。

具体规则：

- **字段名、出入参、错误码**：只认 `契约冻结表.md`。任何一方改字段，必须先报主理人 → 契约表升版本 → 双方回执。**单方面改自己文档不算对齐**（历史上因"两边各持对方上一版"白跑过一轮）。
- **技术选型与执行方式**：认两份技术方案；两者冲突时，后端问题认后端方案、前端问题认前端方案，交界处认契约表。
- **同一份文档内部矛盾**：认"带理由、带日期、带终裁"的那一条（文档里大量 `v1.1/v1.2/v1.3` 修订说明就是为此留的）。

### 2.1 已确认的口径不一致（照右列执行，不要"修正"成左列）

| 位置 | 文档里的写法 | 实际执行 |
|---|---|---|
| `user.login` 入参 | 契约表 §二写 `{}` | 用 `{ code }`：自建后端必须 `code2session` 换 openid（后端方案 §3.2）。契约表此格是云开发时代遗留 |
| `dose_id` 规则 | 前端方案 §6「剂量结构」③仍写 `{plan_id}_{date}_{minutes}` | **`${pid}_${date}_${slot}`，`slot = due_minute(0–1439)`**（契约表 §6 + 后端方案 §四裁决 1）。旧写法是被修正的稿子 |
| 老人端判定 | 前端方案 §15 Q1 表里写"判定用 `role==='elder'`" | **一律用 `mode`**（契约表 §4.1、前端方案 §6）。`mode` 管渲染、`role` 管权限，二者解耦 |
| 索引数量 | 后端方案 §五写 `V002` 建"48 条索引" | 脚本实际 26 条 `CREATE INDEX`。**以脚本为准**，不要按 48 去"补齐" |
| 铁律条数 | 总方案 §六标题写"6 条铁律" | 实列 7 条（第 7 条"每个设计参数配一个可证伪埋点"）。按 7 条执行 |

---

## 3. 总体架构

```
微信原生小程序  ──HTTPS + Bearer JWT──▶  .NET 10 Web API（单进程，可水平扩展）
                                          ├ Controllers（11 域，对齐契约表）
                                          ├ Application（用例编排 + assertMember + 事务边界）
                                          ├ Domain（实体/值对象/状态机/领域服务，零外部依赖）
                                          ├ Infrastructure（EF Core / 微信客户端 / 对象存储 / 时钟 / 6 个 BackgroundService）
                                          └ Middleware（异常→{code,data,msg} / JWT / 请求日志 / server_ts 注入）
                                                    │
                                          PostgreSQL 18 `medbox`（schema 由 db/migrations/V*.sql 顺序构建）
```

- 依赖方向：`Api → Application → Domain`，`Infrastructure → Application`（实现接口）。**Domain 不得引用 ASP.NET Core / EF Core / Npgsql / 任何 SDK**，这是 TDD 跑得快的前提。
- 一个 Web API + 一组后台服务，**不拆微服务**。后台任务用 `PeriodicTimer + TimeProvider`，**不引入 Quartz / Hangfire / Redis**。
- Controller 只做参数、鉴权、调用用例、响应映射；业务逻辑一律在 Application/Domain。
- 稳定业务服务用具体类；接口只用于真实可替换的基础设施边界（`IAppDbContext`、`IWxApiClient`、`IObjectStorage`、`IClock`、`IMemberGuard`）。
- **不吞异常**：领域异常由 `ExceptionHandlingMiddleware` 统一映射（见 §6.3），其余异常记结构化日志后返回 `1009`。

---

## 4. 目录与命名

```
BudPlanet/
├── docs/                  # 8 份方案，唯一设计输入
├── db/
│   ├── apply.sh           # append-only 执行器（唯一执行入口）
│   └── migrations/        # V{NNN}__{snake_case}.sql，严格 +1
├── assets/icon/           # 图标源文件与生成脚本
├── server/                # 见后端方案 §2.1：MedBox.sln + src/{Domain,Application,Infrastructure,Api} + tests/{Domain,Application,Integration}.Tests
└── miniprogram/           # 见前端方案 §2：app.* / config / types / utils / store / components / pages / packageManage / packageReport / packageSettings
```

- 后端一个文件一个顶级类型，文件名 = 类型名；单目录超过 10 个文件时按领域/职责建子目录。
- 实体与数据库字段：**C# 属性名用 PascalCase，映射到 snake_case 列名**（`HasColumnName("due_ts")`）；DTO/Contract 用 camelCase 序列化，与契约表逐字一致。
- 数据库表名、列名一律 **snake_case**（`dose_logs`、`due_ts`、`is_corrected`）。
- 小程序页面路径与组件名沿用前端方案 §2/§4 的既有命名（`ECheckinCard`、`AdSlot`、`StatusBadge`…），**不另起名**。
- 注释解释"为什么"，不解释"做了什么"；公共类型加简短 XML `<summary>`。删除被注释掉的旧实现与死代码。

---

## 5. 数据库铁律（违反即回退）

1. **禁用 EF Core Migrations**：不装 `Microsoft.EntityFrameworkCore.Design`，禁止 `Database.Migrate()` / `EnsureCreated()` / `dotnet ef`。EF 只做映射，`IEntityTypeConfiguration<T>` 手写。CI 出现上述调用即失败。
2. **唯一执行入口是 `db/apply.sh`**。任何"直接连库改表"的操作都视为违规（本地调试的单条 `psql` 仅限排查，改完必须落成脚本）。
3. **文件名 `V{NNN}__{snake_case}.sql`**，序号从 `001` 起**严格 +1**。断号/重号/乱序执行器直接拒绝。下一个号 = 目录现有最大号 + 1，**数目录，别凭记忆**。当前下一个是 `V004`。
4. **append-only**：已执行脚本内容永久冻结（执行器记 SHA-256 到 `schema_migrations`，改动即报错中止）。要改表就新增脚本，**不许回去改 `V001`**。
5. **幂等 + 单事务**：脚本内 `BEGIN/COMMIT`，用 `IF NOT EXISTS` / `ON CONFLICT DO NOTHING` / `CREATE OR REPLACE`。头部注释写 `-- Version / -- 日期 / -- 变更说明 / -- 前置依赖`。
6. **只准连 `medbox`**。容器 `tigerrag-postgres-1`，`psql -U tigerrag -d medbox`。**绝对禁止连接、查询、修改 `ragdb`**（主理人另一个项目的库）；`apply.sh` 已硬编码防呆，不要绕过。
7. **时间口径**：所有"时刻"字段 `BIGINT` UTC 毫秒（`due_ts / taken_at / created_at`）；只有自然日 `due_date` 存东八区 `VARCHAR(10)` `YYYY-MM-DD`。不要改成 `timestamptz`。
8. **状态枚举**：`status VARCHAR + CHECK` 冻结四值 `pending|taken|missed|skipped`，**不用 PG enum**（加值也要写脚本，CHECK 更直观）。
9. **JSONB 只存展示/配置快照**（`plans.slots`、`users.setting`、`app_config.config_value`）。**禁止拿 JSONB 当查询条件**；需要按内容扫描的，派生普通列 + 索引（如 `plans.times_minute INT[]` 双写 + GIN）。
10. **主键确定性**：`dose_logs.dose_id = ${pid}_${date}_${slot}`，不用自增 ID 作业务主键；生成靠 `ON CONFLICT (dose_id) DO NOTHING`，状态迁移靠 `UPDATE … WHERE status=… ` 的受影响行数当 CAS，分片用 `FOR UPDATE SKIP LOCKED`。
11. **基线无外键（实测 0 处）**：新增表默认沿用"无外键 + 应用层保证一致性"；确需外键或级联触发器，先报主理人。
12. 本地开发允许对 `medbox` 执行业务数据 CRUD，但**批量改/删前必须先告知用户**；不得导出/导入 `ragdb` 的数据。

---

## 6. 后端（.NET 10）编码规范

### 6.1 版本与工具链

- 目标框架 **.NET 10**，`global.json` 固定 SDK **10.0.402**，`rollForward: disable`；启用 nullable reference types。
- 版本**精确锁定、不用 `^`**：`Microsoft.EntityFrameworkCore 10.0.14`、`Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3`、`Microsoft.AspNetCore.Authentication.JwtBearer 10.0.14`、`Microsoft.Extensions.TimeProvider.Testing 10.11.0`；测试 `xunit 2.9.3`、`xunit.runner.visualstudio 2.8.2`、`Microsoft.NET.Test.Sdk 18.11.0`、`NSubstitute 5.3.0`、`FluentAssertions 7.2.0`（**不升 v8**，许可证变更）、`Testcontainers.PostgreSql 4.16.0`、`Microsoft.AspNetCore.Mvc.Testing 10.0.14`、`coverlet.collector 6.0.4`。
- 用 `Directory.Packages.props` 中央包管理；升级任何版本都要单独说明理由，**禁止 `dotnet add package` 拉最新**。

### 6.2 代码

- 先满足需求与测试的最小实现，不为"未来可能"预留抽象。方法明显超 30 行或职责不清才拆。
- 业务可调参数（限额、阈值、冷却、上限、批量大小）**一律走 `app_config` 表 / Options，不写 `const`**；前端可读的限额与广告频控必须由 `config` 下发（`freeMedLimit`、`freePatientLimit`、`interstitialCool`、`interstitialDayMax`、`missedThresholdMin`、`snoozeDeadlineMin`），后端不得另立一套硬编码。
- 以下保持代码常量：落库/跨进程标识字符串（`status` 四值、`tmpl_key` 的 `CHECKIN/MISSED/WEEKLY`、错误码）、与列宽绑定的截断长度、提示词类多行文本。
- 每个涉及 `fid` 的用例**第一行**调 `IMemberGuard.AssertMember(fid, openid, needRole)`；openid **只从 JWT `sub` 取**，请求体里出现 `openid` 一律忽略或返回 `1005`。
- 越权（`1002` / `1003`）必须写 `audit_logs`；`checkin.correct` 必须写 `correct_logs`（操作人 / 原状态 / 时间）。
- `session_key` 只存服务端，**永不下发**。`JWT__SECRET` 走环境变量 / `dotnet user-secrets`，**禁止硬编码、禁止进 git**；`appid/secret` 走配置，不落前端。
- `DbContext` 注入 `TimeProvider`，所有 `created_at` 由应用时钟生成，便于 `FakeTimeProvider` 测试。
- 时间轮逻辑（"扫哪些行、入哪些队、额度不足时保哪个 slot"）**抽成纯函数/领域服务**，可被单测完全覆盖，不要写死在 Worker 的循环里。

### 6.3 响应封套与错误码

```js
{ code: 0, data: {...}, msg: "ok", server_ts: 1761753600000 }   // 成功
{ code: 1004, data: {}, msg: "超出限额" }                        // 失败
```

- `0` 成功 / `1001` 未登录过期 / `1002` 非家庭成员 / `1003` 权限不足 / `1004` 超限 / `1005` 参数错误 / `1009` 服务异常。**不得新增错误码**。
- 映射集中在一处：`NotMemberException→1002`、`ForbiddenException→1003`、`QuotaExceededException→1004`、`ValidationException→1005`、其余→`1009` + 日志。
- **所有读接口 `data` 必带 `server_ts`（UTC 毫秒）**，由中间件统一注入，用例里不要重复写。
- 路由：`POST /api/{domain}/{action}` 为主；`list/info/today/week/quota/get` 用 `GET` + query。`GET /api/medicine/common` 是公开只读端点。

### 6.4 后台服务

`MaterializerWorker`（每日 00:10 东八区）/ `ReminderTickWorker`（每 60s + 漏服兜底）/ `PushWorker`（分片 Channel 消费）/ `WeekReportWorker`（周日 20:00）/ `ArchiveWorker`（03:30）/ `StatsWorker`（00:30）。云开发时代的 `warmup` **已删除**，不要加回来。

---

## 7. 契约与前后端一致性

1. **11 个域、字段逐字冻结**：`fid / uid / mid / pid / dose_id / dose_text / due_time / due_ts / taken_at / is_late / is_corrected / snooze_count / today_index / today_total / skip_reason / slot_key / default_dose / cycle_type / tmpl_key / selfUid / selfUids / ivt / applied / rejected / changed / quota / server_ts`。新增字段一律 camelCase。
2. **前端永不传 openid**；身份只从 JWT Claims 取。
3. **`dose_text` 由后端格式化**（`slots[i].d || medicine.default_dose || ""`），后端**不解析数值、不做 `0.5→"半片"` 转换**；前端**禁做中文映射**，且打卡卡片只读 `dose_text`，**不读药品默认剂量**（这是安全信息，读错就是吃错量）。
4. **四态**：`pending|taken|missed|skipped`。`late` **不是状态**，迟到用 `is_late:boolean`，配色复用"已服"绿。允许迁移：`pending→taken|skipped|missed`；`missed(is_corrected=false)→taken` 允许；`missed(is_corrected=true)→taken` 禁止且禁止再次 `correct`；`taken→missed|skipped` 仅经 `checkin.correct` 且不可逆。
5. **`checkin.submit` 天生批量**：`{items:[…]}`，单批上限 8（超限 `1005`），幂等键 `pid_date_slot`；`applied[].changed` 语义写死（`true` 真写入 / `false` 幂等空操作，两种都出队）；`rejected[]` 仅 `1005/1003` 留队；**部分成功合法、不整批回滚**。
6. **`mode` 与 `role` 解耦**：`mode`（elder/guardian）per-family 现算、**不落库**，只决定渲染；`role`（admin/member/elder）入库，只决定权限。判老人端**只用 `mode`**。
7. **`selfUids: string[]`** 不能用单值（同一人在一个家庭内可绑多位 patient）；`selfUid = selfUids[0]` 仅为兼容。
8. **`checkin.correct` 仅 admin/member**，`elder` 一律 `1003`（不依赖"前端没入口"）。`manualCorrectCount` **单列、不进完成率分子分母**。
9. **改后端接口必须同步改前端**：`types/api.d.ts`、`utils/request.js` 调用点、store 字段、页面交互、错误提示、埋点。前后端不一致时**先停手，报冲突**，不要自己拍板改字段。
10. **广告权益存后端**：`ad.unlock` 幂等 + 唯一键 + 30s 窗 + 每场景日上限；前端只渲染 `granted`。激励视频无服务端回调，**不假装能校验"看完"**。

---

## 8. 小程序端规范

- **原生 + TypeScript（仅 tsc 编译）**，不引入 uni-app / Taro / 任何跨端框架。
- **主包零 npm 依赖**：`tdesign-miniprogram@1.17.0` **只在子女端分包**按需引入；老人端页面与主包**不得**出现任何 `t-*` 组件。`typescript 5.9.3`（**不升 7.x**）、`miniprogram-api-typings 5.2.3`。
- **预算**：主包 ≤800KB（实测 ≈250KB）、分包各 ≤500KB、总包 ≤2.5MB；本地静态资源单个 ≤60KB；图片走对象存储签名 URL + `lazy-load`。
- **tabBar 页必须在主包**；`tabBar.custom: true` 自绘（elder 模式不渲染），高度固定 98rpx，4 个 tab 页滚动容器 `padding-bottom: calc(98rpx + env(safe-area-inset-bottom))`。
- **字号/热区禁止写死**：一律 `var(--fs-body)` / `var(--h-main-btn)`。双模 Token 用 `page{}` 默认值 + `.p-root.elder{}` 覆写 + `wx.setBackgroundColor` 改 page 底色；`page[data-mode]` 选择器**永不命中，禁用**。
- **所有浮层必须挂在 `.p-root` 内部**（订阅引导卡、`UnlockNudge`、`ShareCta`、ActionSheet、`AdSlot`、`DisclaimModal`、`share-toast` 共 7 个），靠 `position:fixed` 定位，不许提到根节点外 —— 漏一个就静默掉回子女端字号，且偶发、极难复现。
- **日期**：一律用 `dateCN()`（`new Date(ts + 8*3600e3)` 取 UTC 字段），**禁用 `toISOString().slice(0,10)`**（东八区 08:00 前会算成昨天）。`server_ts + offset` 兜底老人机本地时间被改错。
- **网络层**：统一 `utils/request.js` 的 `call(domain, action, data)`，15s 超时，`1001` 静默续期后重放一次；**打卡链路不阻塞**（本地队列先记，后台重放）。禁止页面里裸写 `wx.request`。
- **订阅消息**：`wx.requestSubscribeMessage` 必须在用户 TAP 的**同步栈**内调用（前置 `await` 必失败）；"是否已勾选总是保持"在 `app.onLaunch` 预热缓存；**一次只申请 1 个模板**；无论接受还是拒绝都要 `notify.ack` 上报（否则授权率永远 100%，PRD 假设 2 永远验不了）。
- **广告**：打卡主流程零广告；`AdSlot` 固定高骨架 → 延迟挂载 → error 折叠 0 高（用 `hidden` 不用 `wx:if`）；激励视频 app 级单例 + `onLoad` 预热；开关只读后端下发的 `config`，**不发版也要能关**。
- **产品内不得出现任何付费出口**：无价格、无"¥/元/年/会员/升级"字样、无支付入口（个人主体拿不到商户号）。`UnlockNudge` 只留「看 15 秒视频」+ 低权重关闭。
- **埋点**：走 `report.batch` 攒批（10 条或 5 秒），参数**绝不出现药品名/疾病名/剂量文本**，只用 `dose_id/patient_id/枚举/uidHash/family_id`。
- **交互**：页面栈 ≤3 层；打卡完成 `reLaunch` 清栈；`setData` 只传路径 diff、单次 <4KB；频繁显隐用 `hidden`；定时器在 `onHide/onUnload` 清理。

### 8.1 UI 组件库边界（2026-10-30 核验 npm registry 实际状态）

| 库 | 最新版本 / 最后发布 | 状态 | 本项目可用性 |
|---|---|---|---|
| `tdesign-miniprogram` | **1.17.0**（2026-09） | 腾讯官方，活跃 | **仅子女端分包**，已锁版本（lock 的版本就是 latest） |
| `weui-miniprogram` | 1.5.6（2024-11） | 微信官方，可用 | 不引入；只作交互参考 |
| `vant-weapp` | 0.5.29（**2020-12 后停更**） | 社区，已停滞 | **禁止引入** |
| `lin-ui` | 0.9.13（2023-01 停更） | 社区，已停滞 | **禁止引入** |
| `colorui` / `wux-weapp` / `iView Weapp` | — | 纯 CSS 或已废弃 | 禁止引入 |
| `uview-pro` / `NutUI` | — | uni-app / Vue 体系 | 不属于原生，本项目不适用 |

- 老人端与主包**只允许**零样式依赖的纯逻辑库（`miniprogram-api-typings`、官方 `miniprogram-computed`）；任何带 WXSS 的组件库不得进主包。
- 组件库对本项目的价值是**参考实现**：picker / stepper / dialog 的禁用态、loading 态、动效曲线可以照着抄，样式必须用本项目 Token 重写。
- 引入任何新库前必须实测主包/分包体积并回写本节；**禁止"先用上再优化体积"**。

---

## 9. TDD 与测试边界

所有行为变化遵循 **RED → GREEN → REFACTOR**：先写一个聚焦的失败测试并确认失败原因是"缺目标行为"→ 写最小实现 → 跑聚焦测试再跑全量 → 只在全绿时重构。**禁止先写实现后补测试。**

| 层 | 工具 | 连库 | 测什么 |
|---|---|---|---|
| Domain 单测 | xUnit + FluentAssertions | 否 | 状态机、额度预算、完成率、幂等键、东八区日期 |
| Application 用例 | xUnit + NSubstitute | 否 | 登录、建家庭、打卡、改判、限额、越权（mock `IAppDbContext`/`IWxApiClient`/`IClock`） |
| Integration | Testcontainers.PostgreSql + WebApplicationFactory | 是（一次性 `postgres:18-alpine`） | 真实 SQL 约束、CAS、事务、API 端到端、脚本可重建 |

- 覆盖率门禁：**Domain ≥85%、Application ≥75%**。
- 集成测试必须"起容器 → `apply.sh` 跑全部 V*.sql → 断言 schema/CHECK/索引 → 跑用例 → 销毁容器"，**与 `medbox` / `ragdb` 完全隔离**。
- **测试不得调用真实微信接口**：`code2session`、`subscribeMessage.send`、`msgSecCheck`、`access_token` 全部用替身或假 HTTP 处理器；**绝不给真实用户发订阅消息、绝不消耗真实广告单元**。需要真服务才能验的用例应 Skip 并写清原因，不留在默认集合里。
- 纯文档、SQL 种子、机械性重命名不强制新增行为测试，但仍要跑现有测试。

---

## 10. 产品铁律（不可被"优化"掉）

这 7 条是全案反复博弈的结论，破一条产品会以不显眼的方式死掉：

1. **打卡主流程零广告**。老人点开就要能点按钮，任何遮挡都会让他放弃一次，放弃一次就不再用了。
2. **老人端不显示"失败/错误"字样**。网络问题统一"已记在本机，会自动重试"，不弹窗、不回退、不阻断。
3. **老人的纠错权不给老人**。撤销/改状态只在子女端（看板行「⋯」→ `checkin.correct`）。老人误触撤销的概率高于打错卡。
4. **完成率不含人工修改**。`manualCorrectCount` 单独计数、不进分子分母，否则改两次就能刷到 100%，北极星指标当场作废。
5. **剂量按时间点独立**（`slots:[{t,d,slot_key}]`）。统一剂量会让老人吃错药，而且不报错、只会安静地错。
6. **广告开关必须能远端下发**（`config.adOn` / `adSwitch`），伤留存时 24 小时内能关且不发版。
7. **每个设计参数必须配一个能证伪它的埋点**。没有埋点的参数，上线后没人能证明它对还是错。

### 10.1 这些"矛盾"是有意为之，**不要统一化**

| 看起来不一致 | 为什么必须保持 |
|---|---|
| 老人端不显示漏服红条与周汇总，子女端全显示 | 老人看到红色告警的第一反应是焦虑而不是行动；发现漏服是子女的职责 |
| P44 解锁记录页空态**放**广告 B，其余空态不放 | 其余空态会挡住第一步主按钮；这页是纯浏览页，不打断任何操作 |
| P42 邀请落地页不做双模，整页用老人规格 | 全产品唯一不知道来者身份的页面，只能按最脆弱的一方渲染 |
| `late` 不设第五态，只用 `is_late` 布尔 | 状态徽标的唯一价值是一眼可辨，五态会稀释辨识度 |
| 剂量**永不省略**，但长药名可以让位换行 | 剂量是安全信息；药名截断只是不便 |
| 家庭回执只渲染一行灰字，**不做**红点/弹窗/提醒 | "被看见"的价值是有人在意我；做成提醒就变成压力 |

**判断方法**：遇到两处不一致，先找有没有一段文字解释过它。**有理由的不一致是设计，没理由的才是 bug。** 把它们"修正"成一致，比留一处真 bug 更危险。

---

## 11. 合规红线（个人主体，踩线直接封号）

1. 类目选「**工具 - 效率**」，**绝不选医疗服务**（个人主体无《医疗机构执业许可证》）。
2. 文案禁区：不出现"治疗 / 疗效 / 推荐用药 / 替代医生 / 根据症状判断"。定位是提醒工具，不是医疗建议。
3. 首次使用必须弹免责确认（`DisclaimModal`：1.2s 置灰、≤40 字、单按钮无关闭叉），并留痕 `disclaimer_agreed_at`；`DisclaimerBar` 只放 P42 落地页底部 + 设置页，**禁入打卡页与首页**。
4. 药品名与用药记录属敏感健康信息：明示《隐私政策》、与 openid 绑定、不共享、不做画像出售；`user.export` / `user.erase` 必须可用。
5. 广告行业屏蔽医疗/药品/保健品/医疗器械（流量主后台必须开启）。
6. 订阅消息必须用户主动点击授权，**不得诱导、不得批量静默请求**；不得"分享得权益"（诱导分享，封号级）。
7. 不做药品库/条码识别、不做问诊、不做药品购买跳转。

---

## 12. 本地环境

- **数据库**：本机 Docker 容器 `tigerrag-postgres-1`，库 `medbox`。**绝不碰 `ragdb`**。
- **后端**：`dotnet run` 起 API；`JWT__SECRET` 用 user-secrets；微信 appid/secret 走本地配置，不提交。
- **前端**：微信开发者工具导入 `miniprogram/`，勾选"不校验合法域名"；子女端分包需执行一次「构建 npm」生成 `miniprogram_npm/`。
- **端口**：**API 固定 `5080`**（前端 `miniprogram/config/index.ts` 的 `apiBase()` 按平台派生：开发者工具用 `http://127.0.0.1:5080`，真机/预览用 `http://<本机局域网 IP>:5080`，可用 storage `api_base` 覆盖）；此后不得随意改。端口被本任务启动的旧进程占用时，先定位再停；**不得停用户正在使用的进程**。
- **真机预览**：手机与电脑须同一 Wi-Fi；Windows 防火墙当前网络是"公用"时默认 `BlockInbound`，需放行 TCP 5080（已加规则 `MedBox API 5080 (dev)`，删：`netsh advfirewall firewall delete rule name="MedBox API 5080 (dev)"`）。
- 任务结束后关闭本次由代理启动的服务，不留下后台进程、临时数据、构建产物或敏感文件。

---

## 13. 验证命令

数据库（改动 `db/` 后必跑）：

```bash
bash db/apply.sh --dry-run    # 只校验：断号/重号/校验和/篡改
bash db/apply.sh              # 应用到 medbox
```

后端（`server/` 建立后固定为）：

```bash
dotnet build MedBox.sln --no-restore
dotnet test MedBox.sln
```

前端（`miniprogram/` 建立后固定为）：

```bash
npx tsc -p miniprogram/tsconfig.json --noEmit
```

前端两条静态检查（违反即返工）：

```bash
# CI-1：页面 WXSS 不得写死字号
grep -rn "font-size: *[0-9]\+rpx" miniprogram/pages/ && exit 1 || true
# CI-2：每个页面 WXML 必须有 p-root（防浮层掉出双模作用域）
grep -rL "p-root" miniprogram/pages/**/*.wxml
```

**没有新鲜输出，不得声称"已修复""已完成""测试通过"。** 先跑聚焦测试，再跑全量。

---

## 14. 交付要求

完成后必须说明：

1. 实际做了什么（对应用户哪条需求）；
2. 关键设计与取舍（为什么这么改，否掉了什么）；
3. 执行的验证命令与**真实输出结果**；
4. 未解决的问题、测试缺口、已知风险；
5. 完整文件清单 + 每个文件的用途（新增/修改分开列）；
6. 是否启动过服务/容器，是否已关闭；
7. 是否改动了 `docs/` 或契约；若改了契约，是否已升版本并说明影响面。

其他：

- **不自动提交 Git**。用户要求提交时，提交人信息留空不代填；提交前检查 `JWT__SECRET`、appid/secret、`.env`、`appsettings.*.json` 里的密钥、`miniprogram_npm/`、`bin/obj/`、数据库 dump、临时文件，**一律不提交**。
- 发现文档与实现不一致时：**先按 §2.1 执行，再把差异记进交付说明**，不要静默"顺手改文档"。
- 需要更新设计时，改 `docs/` 对应文件并升版本号；契约类改动必须走"报主理人 → 升版本 → 双方回执"。

---

## 15. 并行子 agent

任务可拆分时优先用子 agent 并行，**并行数量不超过 5 个**，且遵守：

- 并行任务之间**不得改同一个文件**；共享文件（`types/api.d.ts`、契约表、`app.wxss`、`Program.cs`）由主 agent 串行收口。
- 数据库脚本一律串行（版本号必须连续，并行必冲突）。
- 每个子 agent 的产出必须回到主 agent 汇总校验后再交付，**不得直接对用户宣称完成**。
