/**
 * AdSlot —— 广告容器（A/B/B'/D 位通用）
 * 固定高占位骨架 → 延迟 300ms 挂载 → error 折叠 0 高（不留白块）。
 * 开关只读后端下发的 config.adOn，不发版也能关。
 *
 * 两种"不展示"必须分开对待，否则排查全靠猜：
 *   ① adOn=false        → 运营/远端关的，正常，静默
 *   ② unitId=''         → 还没在公众平台开通流量主、没填 AD_UNITS，正常，静默（但页面看起来和 ① 一模一样）
 *   ③ 有 unitId 但 onError → 真的出问题了（unitId 与 appid 不匹配、无填充、审核未过），记 logClient
 */
import { markShown } from '../../../utils/ad';
import { report } from '../../../utils/report';
import { logClient } from '../../../utils/logger';

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
      if (!this.data.adOn) return; // ① 远端关着：静默折叠
      if (!this.data.unitId) return; // ② 未配置广告位：静默折叠，别上报（会天天刷屏且无从修复）
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
      const code = (e.detail && e.detail.errCode) || 0;
      // ③ 已配置却加载不出来：留痕到服务端，一条 SQL 就能看出是哪个位、什么错误码
      logClient('warn', `ad_slot ${this.data.slot} 加载不出来 ${code}`, {
        scene: 'ad_error',
        unit: this.data.slot,
        code,
      });
      report('ad_error', { unit: this.data.slot, code });
    },
  },
});
