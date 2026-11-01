/**
 * ShareCta —— 成功态分享入口
 * 文案定稿「让家人知道我吃了」：是"我的动作"，不是"我给你看"。
 * 绝不做"分享得权益"（诱导分享，封号级）。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    label: { type: String, value: '让家人知道我吃了' },
    elder: { type: Boolean, value: false },
    kind: { type: String, value: 'checkin' },
  },
  methods: {
    onShareTap() {
      this.triggerEvent('sharetap');
    },
  },
});
