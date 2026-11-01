/**
 * DisclaimerBar —— 常驻告知条（落地页底部 + 设置页）
 * 禁止：警示色 / ⚠ 图标 / 关闭叉 / 闪烁；不可关闭、随内容滚动、不吸顶。
 * 不放打卡页，也不放首页：告知已由落地页承担，打卡页是唯一零噪声页。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    elder: { type: Boolean, value: false },
    text: { type: String, value: '本工具仅提供服药提醒服务，不提供任何医疗建议，用药请遵医嘱。' },
  },
  methods: {
    onTap() {
      wx.navigateTo({ url: '/pages/legal/index?type=disclaimer' });
    },
  },
});
