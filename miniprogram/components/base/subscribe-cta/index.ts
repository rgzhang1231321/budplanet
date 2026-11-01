/**
 * SubscribeCta —— 订阅授权引导卡（底部卡片，非系统弹窗先行）
 * 文案必须说出「勾选『总是保持以上选择』以后就不用再点了」——这句是授权率能否过 40% 的决定性文案。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    title: { type: String, value: '最后一步：让手机到点提醒' },
    desc: {
      type: String,
      value: '微信规定，每条提醒需要你点一次同意。勾选「总是保持以上选择」，以后就不用再点了。',
    },
    actionText: { type: String, value: '开启用药提醒' },
    secondaryText: { type: String, value: '先不用，我知道了' },
    done: { type: Boolean, value: false },
  },
  methods: {
    onAction() {
      this.triggerEvent('action');
    },
    onSecondary() {
      this.triggerEvent('secondary');
    },
  },
});
