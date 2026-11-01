/**
 * DisclaimModal —— 免责确认 + 首次使用的两项同意
 * 规格：宽 = 屏宽 − 96rpx、radius-xl、白底 shadow-2、无关闭叉、单出口、无"以后再说"。
 * 1.2s 内按钮置灰且不显示倒计时数字（倒计时会让老人等着看，反而更慢）。
 *
 * 为什么必须有两个勾选框（docs/合规与通知文案.md §五，个保法第 29 条）：
 *   ① 同意《用户协议》《隐私政策》—— 一般个人信息处理
 *   ② 单独同意"记录用药信息"—— 医疗健康属敏感个人信息，必须单独同意，不能混在第①项里
 *   两个都勾，主按钮才可用；只勾第一个时给一条轻提示，不再弹二级弹窗。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    show: { type: Boolean, value: false },
    /** 规格跟着"他刚点的那个按钮"走：点「我自己吃药」用老人规格 */
    elder: { type: Boolean, value: false },
    title: { type: String, value: '仅提供提醒服务' },
    content: {
      type: String,
      value: '本工具仅提供服药提醒服务，不提供任何医疗建议，用药请遵医嘱。',
    },
    confirmText: { type: String, value: '我知道了' },
  },
  data: {
    canConfirm: false,
    hintShown: false,
    /** ① 已阅读并同意《用户协议》《隐私政策》 */
    agreeDoc: false,
    /** ② 单独同意处理用药信息（敏感个人信息） */
    agreeSensitive: false,
  },
  observers: {
    show(v: boolean) {
      clearTimeout(this._t);
      if (v) {
        this.setData({ canConfirm: false, hintShown: false, agreeDoc: false, agreeSensitive: false });
        this._t = setTimeout(() => this.setData({ canConfirm: true }), 1200);
      }
    },
  },
  methods: {
    toggleDoc() {
      this.setData({ agreeDoc: !this.data.agreeDoc });
    },
    toggleSensitive() {
      this.setData({ agreeSensitive: !this.data.agreeSensitive });
    },
    /** 两个链接都指向 pages/legal（catchtap：点链接不能顺手把勾选框切换了） */
    onLegal(e: any) {
      const type = e.currentTarget.dataset.type;
      wx.navigateTo({ url: `/pages/legal/index?type=${type}` });
    },
    onConfirm() {
      // 1.2s 未到就点：既不能放行（免责声明必须有最短阅读时间），也不能静默 return ——
      // 静默就是用户眼里的"点了没反应"。给一次轻提示，只提示一次，不刷屏。
      if (!this.data.canConfirm) {
        if (!this.data.hintShown) {
          this.setData({ hintShown: true });
          wx.showToast({ title: '请稍等一下', icon: 'none', duration: 900 });
        }
        return;
      }
      if (!this.data.agreeDoc) {
        wx.showToast({ title: '请先阅读并同意《用户协议》和《隐私政策》', icon: 'none' });
        return;
      }
      if (!this.data.agreeSensitive) {
        // 文案照抄 docs/合规与通知文案.md §五 交互规则第 2 条
        wx.showToast({ title: '需要同意用药信息处理才能开启提醒', icon: 'none' });
        return;
      }
      this.triggerEvent('confirm');
    },
    noop() {},
  },
});
