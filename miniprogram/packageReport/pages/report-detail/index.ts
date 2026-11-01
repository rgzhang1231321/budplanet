/** packageReport/pages/report-detail —— P31 详细报告（激励视频 D 解锁后进入） */
import { bootstrap } from '../../../store/user';
import { get } from '../../../store/index';
import { call, get as apiGet } from '../../../utils/request';
import { addDays, dateCN, shortDate, weekdayOf } from '../../../utils/time';

Page({
  data: {
    week: '',
    rangeText: '',
    rate: 0,
    missCount: 0,
    manualCount: 0,
    misses: [] as any[],
    days: [] as any[],
  },

  async onLoad(q: Record<string, string>) {
    await bootstrap().catch(() => {});
    await this.load(q.week);
  },

  async load(week: string) {
    const s = get();
    const d: any = await apiGet('schedule', 'week', { fid: s.family.fid, weekStart: week }, { silent: true });
    const start = week || d.weekStart || '';
    const missDates = (d.missList || []).map((m: any) => m.date);
    this.setData({
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
