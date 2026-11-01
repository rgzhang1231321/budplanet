/** pages/medicine —— P20 药品列表（tab2 薄壳：分组列表 + 空态，表单在分包） */
import { bootstrap, refreshToday } from '../../store/user';
import { get, isElder, on } from '../../store/index';
import { get as apiGet } from '../../utils/request';
import { statusBarHeight } from '../../utils/theme';

Page({
  data: {
    statusBar: 20,
    elder: false,
    groups: [] as any[],
    isEmpty: false,
    adOn: false,
    total: 0,
    limit: 3,
  },

  onLoad() {
    this.setData({ statusBar: statusBarHeight() });
    this._off = on(() => this.load(true));
  },

  onUnload() {
    if (this._off) this._off();
  },

  async onShow() {
    const tb = typeof this.getTabBar === 'function' ? (this.getTabBar() as any) : null;
    if (tb && tb.sync) tb.sync(1);
    await bootstrap().catch(() => {});
    this.load();
  },

  async onPullDownRefresh() {
    await this.load();
    wx.stopPullDownRefresh();
  },

  async load(silent = false) {
    const s = get();
    try {
      const d: any = await apiGet('medicine', 'list', { fid: s.family.fid }, { silent });
      const items = d.items || [];
      const groups = s.family.patients
        .map((p) => {
          const list = items
            .filter((m: any) => m.uid === p.uid)
            .map((m: any) => ({
              mid: m.mid,
              name: m.name,
              spec: m.spec,
              times: (m.slots || []).map((sl: any) => ({ t: sl.t, d: sl.d || m.default_dose })),
            }));
          return { uid: p.uid, alias: p.alias, initial: (p.alias || '家').slice(0, 1), list };
        })
        .filter((g: any) => g.list.length);
      this.setData({
        elder: isElder(),
        groups,
        isEmpty: items.length === 0,
        adOn: !!s.user.config.adOn,
        total: items.length,
        limit: s.user.config.freeMedLimit,
      });
    } catch (e) {
      /* 读失败保留旧列表 */
    }
  },

  onAdd() {
    const s = get();
    if (!s.family.patients.length) {
      wx.navigateTo({ url: '/packageManage/pages/patient/index' });
      return;
    }
    wx.navigateTo({ url: '/packageManage/pages/medicine-form/index' });
  },

  onOpen(e: any) {
    wx.navigateTo({ url: `/packageManage/pages/medicine-detail/index?mid=${e.currentTarget.dataset.mid}` });
  },
});
