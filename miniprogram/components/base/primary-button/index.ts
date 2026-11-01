/**
 * PrimaryButton —— 全站主/次按钮
 * 防误触（老人手抖）：位移 >24px 或长按 >1.5s 直接丢弃点击；按下 vibrateShort。
 * 永不出现"提交中"loading（乐观 UI 由页面负责）。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    label: { type: String, value: '' },
    type: { type: String, value: 'primary' }, // primary | sub | outline | text | ghost
    disabled: { type: Boolean, value: false },
    arrow: { type: Boolean, value: false },
  },
  data: { pressed: false },
  methods: {
    onTouchStart(e: any) {
      const t = e.touches[0];
      this._s = { x: t.clientX, y: t.clientY, ts: Date.now() };
      if (!this.data.disabled) this.setData({ pressed: true });
    },
    /** 只做否决：位移 >24px 或长按 >1.5s 视为误触，让随后的 tap 不算点击 */
    onTouchEnd(e: any) {
      const s = this._s;
      this._s = null;
      this.setData({ pressed: false });
      if (!s) return;
      const t = e.changedTouches[0];
      const dist = Math.hypot(t.clientX - s.x, t.clientY - s.y);
      if (dist > 24 || Date.now() - s.ts > 1500) {
        this._veto = true;
        setTimeout(() => {
          this._veto = false;
        }, 400);
      }
    },
    onTouchCancel() {
      this._s = null;
      this.setData({ pressed: false });
    },
    /**
     * 真正的触发点用原生 tap（touch 系列在部分机型/工具里不派发，只有 touch 会点不动）。
     * 必须用 catchtap：原生 tap 会跨组件边界冒泡到页面上 <primary-button bind:tap>，
     * 加上这里的 triggerEvent('tap')，同一个点击会让页面处理函数跑两次
     * （medicine-form 的 onNext 会因此连跳两步并直接落进保存）。
     */
    onTap() {
      if (this.data.disabled || this._veto) return;
      wx.vibrateShort({ type: 'heavy', fail: () => {} } as any);
      this.triggerEvent('tap');
    },
  },
});
