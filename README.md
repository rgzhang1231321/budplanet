# 替你记着吃药 · BudPlanet

子女给父母设吃药提醒，老人到点收到微信订阅消息、点一下「我已服用」，子女实时看到今天吃没吃。

**只做提醒 + 记录 + 家属可见**：不问诊、不卖药、不建药品库。

---

## 技术栈

| 层 | 选型 |
|---|---|
| 小程序 | 微信原生（TypeScript，仅 tsc 做类型门禁，编译交给开发者工具） |
| 后端 | .NET 10 Web API，四层（Domain / Application / Infrastructure / Api） |
| 数据库 | PostgreSQL 18（库名 `medbox`，schema 由 `db/migrations/V*.sql` 顺序构建） |
| 通知 | 微信订阅消息（一次性订阅，授权 1 次 = 1 条额度） |

## 目录

```
docs/         9 份方案：总方案 / PRD / UI 规范 / 前端方案 / 后端方案 / 契约冻结表 / 合规文案 / 增长方案 / 里程碑
db/           apply.sh（唯一执行入口）+ migrations/V001–V005
server/       MedBox.sln + src/{Domain,Application,Infrastructure,Api} + tests
miniprogram/  app.* + pages（17）+ components（14）+ store + utils + mock
assets/       图标与插画源文件
scripts/      里程碑同步等运维脚本
```

## 本地跑起来

```bash
# 1) 数据库（容器 tigerrag-postgres-1，库 medbox）
bash db/apply.sh

# 2) 后端，监听 0.0.0.0:5080
cd server && dotnet run --project src/MedBox.Api

# 3) 小程序：微信开发者工具导入 miniprogram/
#    接口地址按平台派生：开发者工具 127.0.0.1，真机预览走本机局域网 IP
#    真机要同 Wi-Fi，且放行 TCP 5080
```

## 验证

```bash
cd server && dotnet test MedBox.sln          # 111 passed
bash miniprogram/scripts/check.sh            # CI-1..5 + tsc
python server/scripts/smoke.py               # 全链路冒烟（需 API 已启动）
```

## 里程碑

进度按里程碑汇总，定义与备注见 **[docs/MILESTONES.md](docs/MILESTONES.md)**：

| | 里程碑 | 目标 |
|---|---|---|
| M1–M5 | 设计 / 数据 / 后端 / 小程序 / 推送 | ✅ 已完成 |
| M6 | 真机验收与环境加固 | 2026-11-08 |
| M7 | 上线发布（域名 + 备案 + HTTPS） | 2026-11-22 |
| M8 | 增长与变现（邀请 / 流量主 / 广告位） | 2026-12-20 |
| M9 | 运营与止损评估 | 2027-01-31 |

## 提交约定

一个批次 = 一个里程碑。commit 正文最后一行写 `[M6]` 标记，`git log --grep "\[M6\]"` 即可拉出该里程碑的全部改动。

## 合规

隐私政策、用户协议、免责声明、订阅消息模板文案见 `docs/合规与通知文案.md`；
页面文案一律照抄，不自行编写。`AppSecret`、`JWT__SECRET`、数据库口令一律走环境变量，不进仓库。
