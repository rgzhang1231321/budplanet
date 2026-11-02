/**
 * pages/weekly —— P30 周报
 * 口径：完成率只算「系统判定 + 老人自主打卡」，人工修改单列、不进分子分母。
 * familyCount 是家庭集体计数（"咱家这周 21 次"），不是个人连续天数。
 */
import { bootstrap } from '../../store/user';
import { get, isElder, pureElderView } from '../../store/index';
import { get as apiGet } from '../../utils/request';
import { addDays, dateCN, partOfDay, shortDate, weekdayOf, weekStartOf } from '../../utils/time';
import { unlockByAd, PRODUCT } from '../../utils/ad';
import { AD_UNITS } from '../../config/index';
import { report } from '../../utils/report';
import { requestOne } from '../../utils/subscribe';

Page({
  data: {
    elder: false,
    /** 纯服药人视角（role=elder）：广告/激励视频一律不给看，详细报告入口也不给 */
    pureElder: false,
    rangeText: '',
    rate: 0,
    delta: '',
    deltaDown: false,
    misses: [] as any[],
    missCount: 0,
    days: [] as any[],
    familyText: '',
    manualText: '',
    adOn: false,
    adUnitB: AD_UNITS.b,
    isEmpty: false,
    week: '',
    unlocked: false,
    showWeeklyCta: false,
  },

  async onShow() {
    const tb = typeof this.getTabBar === 'function' ? (this.getTabBar() as any) : null;
    if (tb && tb.sync) tb.sync(2);
    await bootstrap().catch(() => {});
    this.load();
  },

  async onPullDownRefresh() {
    await this.load();
    wx.stopPullDownRefresh();
  },

  async load() {
    const s = get();
    const ws = weekStartOf(dateCN());
    try {
      const d: any = await apiGet('schedule', 'week', { fid: s.family.fid, weekStart: ws }, { silent: true });
      const prev: any = await apiGet(
        'schedule',
        'week',
        { fid: s.family.fid, weekStart: addDays(ws, -7) },
        { silent: true },
      );
      const deltaNum = (d.rate || 0) - (prev.rate || 0);
      const today = dateCN();
      // 数据视角判 role（契约 §4.1）：纯服药人只看自己的药，且不显示"这是谁的药"
      const pureElder = pureElderView();
      const missDates = (d.missList || []).map((m: any) => m.date);
      const days = Array.from({ length: 7 }, (_, i) => {
        const date = addDays(ws, i);
        const future = date > today;
        return {
          key: date,
          label: weekdayOf(date).replace('周', ''),
          state: future ? 'future' : missDates.indexOf(date) >= 0 ? 'miss' : 'ok',
        };
      });
      this.setData({
        elder: isElder(),
        pureElder,
        week: ws,
        rangeText: `${shortDate(ws)} - ${shortDate(addDays(ws, 6))}`,
        rate: d.rate || 0,
        delta: deltaNum === 0 ? '' : `${deltaNum > 0 ? '+' : ''}${deltaNum}%`,
        deltaDown: deltaNum < 0,
        // 时段用 partOfDay 归类（0-5 凌晨 / 5-9 早上 / 9-11:30 上午 / 11:30-13 中午 / 13-18 下午 / 18-24 晚上）。
        // 原来按 slot<720 一刀切，12:00-17:59 全被说成"晚"，与事实不符。
        misses: (d.missList || []).slice(0, 5).map((m: any) => ({
          text: `${weekdayOf(m.date)} ${partOfDay(m.slot)} · ${m.medicine_name}`,
          dose: m.dose_text,
          // 家庭有 ≥2 位服药人时才需要说清"是谁的"；纯服药人不显示他人信息（也不显示自己的名字）
          who: pureElder ? '' : m.patient || '',
        })),
        missCount: (d.missList || []).length,
        days,
        familyText: `咱家这周 ${d.familyCount || 0} 次按时吃药`,
        manualText: d.manualCorrectCount ? `另有 ${d.manualCorrectCount} 次手动修改，不计入完成率` : '',
        adOn: !!s.user.config.adOn,
        isEmpty: !d.rate && !(d.missList || []).length,
        // WEEKLY 额度为 0 才提示：一次性订阅授权 1 次 = 1 条，发完下一条又得重新点。
        // 判据是"我是被照护方"（role=elder），不是"开着大字版"，也不是"有没有自绑"。
        showWeeklyCta: !pureElder && (s.user.quota.WEEKLY || 0) <= 0,
      });
      report('weekly_view', { rate: d.rate || 0 });
    } catch (e) {
      /* 保留上次数据 */
    }
  },

  /** 广告位 D：看 15 秒视频换详细报告（成本在前、权益在后）；纯服药人不给看 */
  onOpenDetail() {
    // 静默 return 会表现成"点了没反应"：服药人点了也要给一句人话（不能出现"失败/错误"）
    if (this.data.pureElder) {
      wx.showToast({ title: '详细报告是给家人看的', icon: 'none' });
      return;
    }
    unlockByAd(PRODUCT.WEEK_REPORT, (d: any) => {
      this.setData({ unlocked: true });
      wx.navigateTo({ url: `/packageReport/pages/report-detail/index?week=${this.data.week}` });
      void d;
    });
  },

  onHistory() {
    wx.navigateTo({ url: '/packageReport/pages/history/index' });
  },

  /** WEEKLY 授权：必须在 TAP 同步栈内直接调（前置 await 就会丢上下文，弹不出系统框） */
  onSubscribeWeekly() {
    report('subscribe_request', { scene: 'weekly', tmpl: 'WEEKLY', quota_before: get().user.quota.WEEKLY });
    requestOne('WEEKLY', 'weekly').then((res) => {
      this.setData({ showWeeklyCta: false });
      if (res.unconfigured) return;
      wx.showToast({ title: res.accepted ? '每周日推给你' : '没开启也能在小程序里看到', icon: 'none' });
    });
  },

  onSubscribeWeeklyLater() {
    this.setData({ showWeeklyCta: false });
  },

  onShareAppMessage() {
    const s = get();
    // 同上：非管理员拿不到邀请码，不要拼空的 `ivt=`（会静默退化成普通欢迎页）
    const ivt = s.family.inviteToken;
    return {
      title: `${s.family.name || '我家'}这周服药完成率 ${this.data.rate}%`,
      path: ivt ? `/pages/launch/index?from=weekly&fid=${s.family.fid}&ivt=${ivt}` : '/pages/launch/index?from=weekly',
      success: () => report('share_click', { channel: 'wechat', from: 'weekly' }),
    };
  },
});
