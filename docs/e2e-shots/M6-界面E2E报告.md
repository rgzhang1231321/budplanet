# M6 界面级 E2E 验收报告（微信开发者工具 CLI）

- 执行人：寇豆码
- 环境：微信开发者工具（WeChatLib 3.17.6），模拟器 390×844 dpr3，项目 D:/code/BudPlanet/miniprogram
- 后端：http://127.0.0.1:5081（模拟器 storage `api_base` 实测值；非 5080），E2E 库
- 账号：dev_local_openid（家庭成员 admin，self_patient_id=P15b990c3a4；patients.self_openid=dev_local_openid）
- 截图目录：D:/code/BudPlanet/docs/e2e-shots/

## 用例结果

| # | 用例 | 结果 | 证据 |
|---|------|------|------|
| 1 | 老人端大字版（display_mode=large + modeOverride=elder） | 通过 | C1-01-elder-home.png（老人端）/ C1-02-guardian-home.png（子女端） |
| 2 | 老人端 missed 降级为「待服」，不出现漏服/失败/错误 | 通过（附 1 条文案缺口） | C2-01-elder-missed.png；elderRows[0].status=missed，徽标渲染为「待服」 |
| 3 | 老人端打卡主按钮热区高度 | 通过 | `.e-cta` 实测 height=83px（=160rpx，1rpx=0.52px）>44px |
| 4 | 子女端 3 秒撤销 / 老人端无撤销 | 通过 | C4-03-guardian-undo.png（截图可见「撤销」）；onTake 后 canUndo=true，.ok__undo 实测 80×37px；onUndo 后 viewState 回 pending 且 DB 仍 pending（未上行）。C4-02-elder-checkin-taken.png 老人端无「撤销」 |
| 5 | 关于页客服邮箱 + 三份法务页 + 隐私政策备案号 | 通过 | C5-01-about.png（1135909556@qq.com、v1.0.0）；C5-02/C5-03（隐私政策含「陕ICP备2026007728号-2X」）；C5-04-legal-agreement.png / C5-04-legal-disclaimer.png |
| 6 | 复诊报告导出（不卡生成中、能出图） | **失败** | C6-01/C6-02 正常；点「保存为图片」后 saving 持续 true 8.4s 无任何 toast/弹窗，6s 兜底未生效；页面版式被撑坏（C6-03-export-after-save-broken.png） |

## 问题清单

### P1 影响可用性
1. **复诊报告「保存为图片」永久卡在生成中**（pages/.../export-report/index.ts onSaveImage）
   - 现象：saving=true 持续 ≥8.4s，无 toast / 无 modal / console 无红字；6 秒兜底计时器未把按钮恢复
   - 佐证：`wx.createSelectorQuery().select('#reportCanvas').fields({node:true})` 的 exec 回调在本环境不返回（用 automation 单独查同一选择器也超时），所以画布分支根本进不去
   - 副作用：canvas 内部尺寸被设为 750*3 × 1060*3 后撑破页面版式（右侧文字被裁切、按钮消失），见 C6-03

### P2 观感 / 文案
2. **老人端首页看不到「过了时间了，现在补记也算」**：dose-row 的 subText 与 showTaken 绑定，老人端传 `show-taken="{{false}}"`，导致该安抚文案在老人端首页永不出现（徽标已正确降级为「待服」）。口径是否要改，请主理人定。
3. **老人端打卡页顶部文案挤压**：`.ck__summary` + `.ck__when` 同排，大字号下「今天 2 次，已吃 1 次」与「晚上 第 2 次 · 20:00 共 2 次」换行交错，可读性差（C4-01-elder-checkin-before.png）
4. **关于页底部免责声明被省略号截断**（"...用药请遵医嘱…"），首屏最后一行读不完整（C5-01-about.png）

## 造过的测试数据（已全部还原）
- dose_logs P15b990c3a4_2026-11-02_480：taken → missed → taken（taken_at=1793611948871）
- dose_logs P15b990c3a4_2026-11-02_1200：taken → pending → taken（taken_at=1793623440839），期间被点击打卡数次（含撤销）
- storage `app_pref_v1.modeOverride`：elder ↔ guardian 来回切换，结束已还原 elder
- patients.display_mode 未修改（本就 large）

## 已知环境噪声（非产品缺陷）
- console 有 `wx.getSystemInfoSync is deprecated` 告警（多处调用）
- 用 wx.reLaunch 后曾出现 `routeDone with a webviewId ... is not found`（开发者工具自身），改用 redirectTo/refresh 后消失
- 模拟器自动化坐标点击（element_action tap + x/y）只在部分场景命中，撤销按钮点击改用页面 onUndo 触发验证
