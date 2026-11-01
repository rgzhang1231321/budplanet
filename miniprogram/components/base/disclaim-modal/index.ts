/**
 * DisclaimModal —— 免责确认
 * 规格：宽 = 屏宽 − 96rpx、radius-xl、白底 shadow-2、无关闭叉、单出口、无"以后再说"。
 * 1.2s 内按钮置灰且不显示倒计时数字（倒计时会让老人等着看，反而更慢）。
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
  data: { canConfirm: false, hintShown: false },
  observers: {
    show(v: boolean) {
      clearTimeout(this._t);
      if (v) {
        this.setData({ canConfirm: false, hintShown: false });
        this._t = setTimeout(() => this.setData({ canConfirm: true }), 1200);
      }
    },
  },
  methods: {
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
      this.triggerEvent('confirm');
    },
    noop() {},
  },
});
