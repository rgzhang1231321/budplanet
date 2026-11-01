/** pages/medicine —— P20 药品列表（tab2 薄壳：分组列表 + 空态，表单在分包） */
import { bootstrap, refreshToday } from '../../store/user';
import { get, isElder, on } from '../../store/index';
import { get as apiGet } from '../../utils/request';
import { statusBarHeight } from '../../utils/theme';
import { AD_UNITS } from '../../config/index';

Page({
  data: {
    statusBar: 20,
    elder: false,
    groups: [] as any[],
    isEmpty: false,
    adOn: false,
    adUnitB: AD_UNITS.b,
    total: 0,
    quotaText: '',
    quotaHint: '',
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
              // slots 未登记在契约表里（见 mock 顶部清单）：缺失时退化成只显示默认剂量，
              // 不要显示成"这个药没有吃药时间"。
              times: (m.slots && m.slots.length ? m.slots : [{ t: '', d: m.default_dose }]).map((sl: any) => ({
                t: sl.t,
                d: sl.d || m.default_dose,
              })),
            }));
          return { uid: p.uid, alias: p.alias, initial: (p.alias || '家').slice(0, 1), list };
        })
        .filter((g: any) => g.list.length);
      // 名额要和用户已有的解锁一起算：只看 freeMedLimit 会出现"第 4 个起解锁"、
      // 而用户其实已经有 2 个解锁名额的错位（mock 里默认就是 5 种药 = 3 + 2）。
      const limit = s.user.config.freeMedLimit || 0;
      const credits = (s.user.entitlement && s.user.entitlement.credits) || 0;
      const capacity = limit + credits;
      const remain = capacity - items.length;
      this.setData({
        elder: isElder(),
        groups,
        isEmpty: items.length === 0,
        adOn: !!s.user.config.adOn,
        total: items.length,
        quotaText: `已添加 ${items.length} 种 · 免费 ${limit} 个${credits > 0 ? ` + 已解锁 ${credits} 个` : ''}`,
        quotaHint: remain > 0 ? `还能再加 ${remain} 个` : `名额用完了，加第 ${items.length + 1} 个前看 15 秒视频`,
      });
    } catch (e) {
      /* 读失败保留旧列表 */
    }
  },

  onAdd() {
    const s = get();
    // 加药/加服药人都要 admin|member（后端 assertMember "admin","member"），
    // 老人端 role=elder 点了必然 1003。别让老人白填一遍表单再被踢回首页。
    if (s.family.role === 'elder') {
      wx.showToast({ title: '让子女在手机上帮你添加', icon: 'none' });
      return;
    }
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
