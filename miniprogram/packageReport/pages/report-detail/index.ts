/** packageReport/pages/report-detail —— P31 详细报告（激励视频 D 解锁后进入） */
import { bootstrap } from '../../../store/user';
import { get, pureElderView } from '../../../store/index';
import { get as apiGet } from '../../../utils/request';
import { unlockLog } from '../../../utils/ad';
import { addDays, dateCN, shortDate, weekdayOf } from '../../../utils/time';
import { logClient } from '../../../utils/logger';

Page({
  data: {
    week: '',
    rangeText: '',
    rate: 0,
    missCount: 0,
    manualCount: 0,
    misses: [] as any[],
    days: [] as any[],
    /** P1-5：时段洞察（早/中/晚热力条） */
    byPeriod: [] as any[],
    /** P1-5：按药洞察（哪种药最容易漏） */
    byMed: [] as any[],
    /** P1-5：与上周对比的一句话结论 */
    insightCompare: '',
    topPeriodLabel: '',
    /** 读失败：不能停在一片空白的 0%，那看起来像"这周真的一次没漏" */
    loadError: false,
  },

  async onLoad(q: Record<string, string>) {
    await bootstrap().catch(() => {});
    // 这页是"看视频换来的东西"，也是分包页 —— 不能只靠入口不给进，URL 直开必须自己校验。
    // ① 纯服药人（role=elder）不给看（老人误点/被诱导点都没有意义，且周报是发给子女的）
    //    判 role 不判 mode：否则"自己也吃药又管全家"的 admin 会被自己的 mode 挡在门外。
    if (pureElderView()) {
      wx.showToast({ title: '详细报告是给家人看的', icon: 'none' });
      setTimeout(() => wx.navigateBack({ fail: () => wx.reLaunch({ url: '/pages/weekly/index' }) }), 600);
      return;
    }
    // ② 没解锁不给看（本地留痕即是解锁凭据；过期或换了设备就回周报页重新解锁）
    const unlocked = unlockLog().some((x) => x.kind === 'week_report');
    if (!unlocked) {
      wx.showToast({ title: '先看 15 秒视频解锁', icon: 'none' });
      setTimeout(() => wx.navigateBack({ fail: () => wx.reLaunch({ url: '/pages/weekly/index' }) }), 600);
      return;
    }
    await this.load(q.week);
  },

  onRetry() {
    this.setData({ loadError: false });
    this.load(this.data.week || '');
  },

  async load(week: string) {
    const s = get();
    try {
      const d: any = await apiGet('schedule', 'week', { fid: s.family.fid, weekStart: week }, { silent: true });
      const start = week || d.weekStart || '';
      const missDates = (d.missList || []).map((m: any) => m.date);

      // 「这是谁的药」只在家庭有 ≥2 位服药人时才说 —— 与历史页 / 周报**同源同形**，
      // 三处各算各的会出现"周报写了爸爸、详细报告没写"的口径打架。
      const needWho = s.family.patients.length >= 2;

      // P1-5：洞察全部从本周 missList 聚合，不新增接口。
      // 时段按 slot 分钟数分桶：<11:00 早 / 11:00-17:00 中 / ≥17:00 晚。
      const periodOf = (slot: number) => (slot < 11 * 60 ? '早' : slot < 17 * 60 ? '中' : '晚');
      const cur = { 早: 0, 中: 0, 晚: 0 } as Record<string, number>;
      (d.missList || []).forEach((m: any) => {
        const k = periodOf(m.slot);
        cur[k] += 1;
      });
      // 与上周对比：同桶再算一次（免费周报已给"漏了 3 次"，这里给"多漏/少漏"的增量）
      let prev = { 早: 0, 中: 0, 晚: 0 } as Record<string, number>;
      if (start) {
        const p: any = await apiGet(
          'schedule',
          'week',
          { fid: s.family.fid, weekStart: addDays(start, -7) },
          { silent: true },
        ).catch(() => null);
        (p && p.missList ? p.missList : []).forEach((m: any) => {
          const k = periodOf(m.slot);
          prev[k] += 1;
        });
      }
      const maxPeriod = Math.max(cur.早, cur.中, cur.晚, 1);
      const periodLabel: Record<string, string> = { 早: '早上', 中: '中午', 晚: '晚上' };
      const byPeriod = ['早', '中', '晚'].map((k) => ({
        key: k,
        label: periodLabel[k],
        count: cur[k],
        // 条长相对本周最大桶，最大桶恒为 100%
        pct: Math.round((cur[k] / maxPeriod) * 100),
        top: cur[k] > 0 && cur[k] === maxPeriod,
      }));
      const topKey = ['早', '中', '晚'].reduce((a, b) => (cur[b] > cur[a] ? b : a), '早');
      const diff = cur[topKey] - prev[topKey];
      const insightCompare =
        cur[topKey] > 0
          ? diff > 0
            ? `${periodLabel[topKey]}比上周多漏了 ${diff} 次`
            : diff < 0
              ? `${periodLabel[topKey]}比上周少漏了 ${-diff} 次`
              : `${periodLabel[topKey]}和上周一样`
          : '';
      const topPeriodLabel = cur[topKey] > 0 ? `最容易漏的是${periodLabel[topKey]}` : '';

      // 按药聚合：哪种药漏得最多
      const medMap: Record<string, number> = {};
      (d.missList || []).forEach((m: any) => {
        medMap[m.medicine_name] = (medMap[m.medicine_name] || 0) + 1;
      });
      const byMed = Object.keys(medMap)
        .map((name) => ({ name, count: medMap[name] }))
        .sort((a, b) => b.count - a.count)
        .slice(0, 3);

      this.setData({
        loadError: false,
        week: start,
        rangeText: start ? `${shortDate(start)} - ${shortDate(addDays(start, 6))}` : '',
        rate: d.rate || 0,
        missCount: (d.missList || []).length,
        manualCount: d.manualCorrectCount || 0,
        byPeriod,
        byMed,
        insightCompare,
        topPeriodLabel,
        misses: (d.missList || []).map((m: any) => ({
          date: shortDate(m.date),
          week: weekdayOf(m.date),
          time: `${`${Math.floor(m.slot / 60)}`.padStart(2, '0')}:${`${m.slot % 60}`.padStart(2, '0')}`,
          name: m.medicine_name,
          patient: needWho ? m.patient : '',
          dose: m.dose_text,
          // 患者名 + 剂量拼成一句，**只拼非空的**：
          // ② 家庭只有 1 位服药人时不说人名（说了是废话）；
          // ⑤ 任一为空就只显示另一个 —— 原来的三元写法在 dose_text 为空时会留下半个「爸爸 ·」。
          who: [needWho ? m.patient || '' : '', m.dose_text || ''].filter((x) => !!x).join(' · '),
        })),
        days: start
          ? Array.from({ length: 7 }, (_, i) => {
              const date = addDays(start, i);
              const future = date > dateCN();
              return {
                key: date,
                label: weekdayOf(date).replace('周', ''),
                miss: !future && missDates.indexOf(date) >= 0,
                future,
              };
            })
          : [],
      });
    } catch (e: any) {
      // 原来没有 try/catch：请求一失败页面就是 0% + "这周没有漏服记录"，把人骗了
      logClient('warn', `report.week 没取到 ${(e && e.code) || ''} ${(e && e.msg) || ''}`.trim(), {
        scene: 'report_load',
        domain: 'schedule',
        action: 'week',
        code: (e && e.code) || 0,
      });
      this.setData({ loadError: true });
    }
  },

  /**
   * P1-6：导出给医生看 —— 跳到真正的「近 30 天用药报告」单页（可存图/转发），
   * 不再调 user.export 那个全量数据包（两者语义完全不同，JSON 给不了医生）。
   */
  onExport() {
    wx.navigateTo({
      url: '/packageReport/pages/export-report/index',
      fail: () => wx.showToast({ title: '页面打开失败，稍后再试', icon: 'none' }),
    });
  },
});
