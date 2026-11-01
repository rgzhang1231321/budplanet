/**
 * UnlockNudge —— 解锁引导（原 MemberNudge）
 * 只有两个出口：主按钮「看 15 秒视频」+ 低权重文字「关闭」。产品内不得出现任何付费出口。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    show: { type: Boolean, value: false },
    title: { type: String, value: '免费名额用完了' },
    desc: { type: String, value: '看 15 秒视频，免费再加 1 个药品名额，永久有效。' },
    actionText: { type: String, value: '看 15 秒视频，免费再加 1 个' },
  },
  methods: {
    onUnlock() {
      this.triggerEvent('unlock');
    },
    onClose() {
      this.triggerEvent('close');
    },
    noop() {},
  },
});
