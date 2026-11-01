/**
 * AdSlot —— 广告容器（A/B/B'/D 位通用）
 * 固定高占位骨架 → 延迟 300ms 挂载 → error 折叠 0 高（不留白块）。
 * 开关只读后端下发的 config.adOn，不发版也能关。
 */
import { markShown } from '../../../utils/ad';
import { report } from '../../../utils/report';

Component({
  options: { addGlobalClass: true },
  properties: {
    adOn: { type: Boolean, value: false },
    unitId: { type: String, value: '' },
    height: { type: Number, value: 210 },
    elderHeight: { type: Number, value: 180 },
    elder: { type: Boolean, value: false },
    delay: { type: Number, value: 300 },
    slot: { type: String, value: 'b' },
  },
  data: { mounted: false, failed: false },
  lifetimes: {
    attached() {
      if (!this.data.adOn || !this.data.unitId) return;
      this._t = setTimeout(() => this.setData({ mounted: true }), this.data.delay);
    },
    detached() {
      clearTimeout(this._t);
    },
  },
  methods: {
    onLoad() {
      markShown(this.data.slot as any);
      report('ad_show', { unit: this.data.slot, mode: this.data.elder ? 'elder' : 'guardian' });
    },
    onError(e: any) {
      this.setData({ failed: true });
      report('ad_error', { unit: this.data.slot, code: (e.detail && e.detail.errCode) || 0 });
    },
  },
});
