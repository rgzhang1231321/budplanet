# CLAUDE.md

本仓库的开发规范全部在 **`AGENTS.md`**，请先完整阅读该文件再动手：

- 架构与分层：§3
- 数据库迁移铁律（append-only / 严格递增 / 禁 EF Migration / 只用 `medbox`）：§5
- 后端规范（.NET 10 / 封套 / 错误码 / assertMember）：§6
- 契约与前后端一致性（11 域、四态、`dose_text`、`mode` vs `role`）：§7
- 小程序规范（主包零 npm / 双模 Token / TAP 上下文 / 离线队列）：§8
- TDD 与测试边界（Domain ≥85%、Application ≥75%、不调真实微信接口）：§9
- 产品铁律与"有意为之的不一致"（不要统一化）：§10
- 合规红线：§11
- 验证命令：§13
- 交付要求：§14

冲突裁决顺序：`docs/契约冻结表.md` > `docs/后端技术方案.md` / `docs/前端技术方案.md` > `docs/PRD-小药盒.md` / `docs/UI-设计规范.md` > `docs/00-总方案.md` > `docs/后端云开发方案.md`（历史，仅参考）。
