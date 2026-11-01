/** packageReport/pages/report-detail —— P31 详细报告（激励视频 D 解锁后进入） */
import { bootstrap } from '../../../store/user';
import { get, isSelf } from '../../../store/index';
import { call, get as apiGet } from '../../../utils/request';
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
    /** 读失败：不能停在一片空白的 0%，那看起来像"这周真的一次没漏" */
    loadError: false,
  },

  async onLoad(q: Record<string, string>) {
    await bootstrap().catch(() => {});
    // 这页是"看视频换来的东西"，也是分包页 —— 不能只靠入口不给进，URL 直开必须自己校验。
    // ① 服药人本人不给看（老人误点/被诱导点都没有意义，且周报是发给子女的）
    if (isSelf()) {
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
      this.setData({
        loadError: false,
        week: start,
        rangeText: start ? `${shortDate(start)} - ${shortDate(addDays(start, 6))}` : '',
        rate: d.rate || 0,
        missCount: (d.missList || []).length,
        manualCount: d.manualCorrectCount || 0,
        misses: (d.missList || []).map((m: any) => ({
          date: shortDate(m.date),
          week: weekdayOf(m.date),
          time: `${`${Math.floor(m.slot / 60)}`.padStart(2, '0')}:${`${m.slot % 60}`.padStart(2, '0')}`,
          name: m.medicine_name,
          patient: m.patient,
          dose: m.dose_text,
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

  onExport() {
    call('user', 'export', {})
      .then((d: any) => {
        // 复制到剪贴板而不是塞进弹窗正文：正文不能长按选中，用户拿不走这条链接
        let copied = false;
        wx.setClipboardData({
          data: d.fileUrl,
          success: () => {
            copied = true;
          },
          // complete 兜底：只挂 success 时，某些环境会"点了没反应"
          complete: () => {
            wx.showModal({
              title: '导出给医生看',
              content: copied ? '下载链接已复制，24 小时内有效。在浏览器里打开即可下载。' : `链接 24 小时内有效：\n${d.fileUrl}`,
              showCancel: false,
              confirmText: '知道了',
            });
          },
        });
      })
      .catch(() => wx.showToast({ title: '稍后再试', icon: 'none' }));
  },
});
