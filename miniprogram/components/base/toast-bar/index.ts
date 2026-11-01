/**
 * ToastBar —— 顶部 64rpx 非阻断细条（打卡同步态唯一表达）
 * 三态：sending 琥珀「正在发送给家人…」/ error 深琥珀「网络不好，已记在本机，会自动重试」/ 隐藏。
 * 老人端禁用"失败/错误/异常"字样。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    show: { type: Boolean, value: false },
    state: { type: String, value: 'sending' }, // sending | error
    text: { type: String, value: '' },
  },
  methods: {
    onRetry() {
      this.triggerEvent('retry');
    },
  },
});
