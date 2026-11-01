/**
 * packageManage/pages/plan-form —— 时间与剂量（含"早晚不同剂量"）
 * 保存后必须整树重拉 schedule.today：改时间点会生成新 dose_id，旧 pending 行由后端删除。
 */
import { bootstrap, refreshToday } from '../../../store/user';
import { get } from '../../../store/index';
import { call, get as apiGet } from '../../../utils/request';
import { timeToMinute } from '../../../utils/time';

Page({
  data: {
    mid: '',
    name: '',
    times: [] as { t: string; d: string }[],
    cycle: 'long_term',
    saving: false,
    defaultDose: '1片',
  },

  async onLoad(q: Record<string, string>) {
    await bootstrap().catch(() => {});
    const s = get();
    const d: any = await apiGet('medicine', 'list', { fid: s.family.fid }, { silent: true });
    const m = (d.items || []).find((x: any) => x.mid === q.mid);
    if (!m) return;
    this.setData({
      mid: m.mid,
      name: m.name,
      defaultDose: m.default_dose,
      cycle: m.cycle_type || 'long_term',
      times: (m.slots && m.slots.length ? m.slots : [{ t: '08:00', d: m.default_dose }]).map((sl: any) => ({
        t: sl.t,
        d: sl.d || m.default_dose,
      })),
    });
  },

  onTime(e: any) {
    const i = e.currentTarget.dataset.i;
    const times = this.data.times.slice();
    times[i] = { ...times[i], t: e.detail.value };
    this.setData({ times });
  },

  onDose(e: any) {
    const i = e.currentTarget.dataset.i;
    const times = this.data.times.slice();
    times[i] = { ...times[i], d: e.detail.value };
    this.setData({ times });
  },

  addTime() {
    this.setData({ times: this.data.times.concat([{ t: '20:00', d: this.data.defaultDose }]) });
  },

  removeTime(e: any) {
    if (this.data.times.length <= 1) {
      wx.showToast({ title: '至少留一个时间点', icon: 'none' });
      return;
    }
    const times = this.data.times.slice();
    times.splice(e.currentTarget.dataset.i, 1);
    this.setData({ times });
  },

  onCycle(e: any) {
    this.setData({ cycle: e.currentTarget.dataset.cycle });
  },

  async onSave() {
    if (this.data.saving) return;
    this.setData({ saving: true });
    const s = get();
    try {
      await call('plan', 'update', {
        mid: this.data.mid,
        slots: this.data.times.map((x) => ({
          t: x.t,
          d: x.d || this.data.defaultDose,
          slot_key: `s${timeToMinute(x.t)}`,
        })),
        cycle_type: this.data.cycle,
        startDate: s.today.date,
      });
      await refreshToday();
      wx.showToast({ title: '已保存', icon: 'success' });
      setTimeout(() => wx.navigateBack(), 600);
    } catch (e: any) {
      this.setData({ saving: false });
      wx.showToast({ title: (e && e.msg) || '保存失败', icon: 'none' });
    }
  },
});
