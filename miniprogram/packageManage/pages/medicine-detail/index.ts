/** packageManage/pages/medicine-detail —— P21 药品详情（返回列表时触发插屏 C，仅子女端） */
import { bootstrap, refreshToday } from '../../../store/user';
import { get, isElder } from '../../../store/index';
import { call, get as apiGet } from '../../../utils/request';
import { showC } from '../../../utils/ad';

Page({
  data: {
    mid: '',
    med: null as any,
    patientAlias: '',
    timesText: '',
  },

  async onLoad(q: Record<string, string>) {
    this.setData({ mid: q.mid || '' });
    await bootstrap().catch(() => {});
    await this.load();
  },

  onUnload() {
    // 插屏只在"已返回列表"后触发，避免打断详情阅读。
    // 必须按当前端传：写死 'guardian' 会让老人端也弹插屏（是否真的弹由 showC 里的
    // adSwitch.elderInterstitial 决定，默认 false）。
    showC(isElder() ? 'elder' : 'guardian');
  },

  async load() {
    const s = get();
    const d: any = await apiGet('medicine', 'list', { fid: s.family.fid }, { silent: true });
    const med = (d.items || []).find((x: any) => x.mid === this.data.mid);
    const p = s.family.patients.find((x) => med && x.uid === med.uid);
    // slots 未登记在契约表里（见 mock/index.ts 顶部清单）：真后端若严格照契约返回，
    // 这里会拿到空数组 —— 退化成只显示默认剂量，而不是显示成一片空白。
    const slots = med ? (med.slots && med.slots.length ? med.slots : [{ t: '', d: med.default_dose }]) : [];
    this.setData({
      med,
      patientAlias: p ? p.alias : '',
      timesText: slots.map((sl: any) => `${sl.t} ${sl.d}`.trim()).join(' · '),
    });
  },

  onEdit() {
    // 改药/改计划要 admin|member，老人端只读
    if (get().family.role === 'elder') {
      wx.showToast({ title: '让子女在手机上修改', icon: 'none' });
      return;
    }
    wx.navigateTo({ url: `/packageManage/pages/medicine-form/index?mid=${this.data.mid}` });
  },

  onPlan() {
    if (get().family.role === 'elder') {
      wx.showToast({ title: '让子女在手机上修改', icon: 'none' });
      return;
    }
    wx.navigateTo({ url: `/packageManage/pages/plan-form/index?mid=${this.data.mid}` });
  },

  onArchive() {
    if (get().family.role === 'elder') {
      wx.showToast({ title: '让子女在手机上删除', icon: 'none' });
      return;
    }
    const s = get();
    wx.showModal({
      title: '删除这个药',
      content: `删除「${this.data.med.name}」后，它的提醒也会停掉。历史记录保留。`,
      confirmText: '删除',
      confirmColor: '#E0402F',
      success: async (r) => {
        if (!r.confirm) return;
        try {
          await call('medicine', 'archive', { fid: s.family.fid, mid: this.data.mid });
          await refreshToday();
          wx.showToast({ title: '已删除', icon: 'none' });
          setTimeout(() => wx.navigateBack(), 600);
        } catch (e: any) {
          wx.showToast({ title: (e && e.msg) || '删不掉', icon: 'none' });
        }
      },
    });
  },
});
