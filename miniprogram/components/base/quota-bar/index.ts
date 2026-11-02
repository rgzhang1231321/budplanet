/**
 * quota-bar —— 提醒额度耗尽告警细条（P0-2）
 * 只在 CHECKIN 额度为 0 时出现：额度归零后后端直接跳过推送，前端不会收到任何错误，
 * 用户会以为"我设了提醒"，实际第二天起就收不到。必须让它可见、可关闭（当天）。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    text: { type: String, value: '提醒额度用完了，明天起收不到到点提醒' },
  },
  methods: {
    onAction() {
      this.triggerEvent('action');
    },
    onClose() {
      this.triggerEvent('close');
    },
  },
});
