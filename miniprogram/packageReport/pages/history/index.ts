/** packageReport/pages/history —— 历史明细（schedule.today 支持任意 date，按天回放） */
import { bootstrap } from '../../../store/user';
import { get } from '../../../store/index';
import { get as apiGet } from '../../../utils/request';
import { addDays, dateCN, humanDate } from '../../../utils/time';

Page({
  data: {
    groups: [] as any[],
    loading: true,
    isEmpty: false,
  },

  onLoad() {
    this.load();
  },

  async onPullDownRefresh() {
    await this.load();
    wx.stopPullDownRefresh();
  },

  async load() {
    await bootstrap().catch(() => {});
    const s = get();
    const today = dateCN();
    const groups: any[] = [];
    for (let i = 0; i < 7; i++) {
      const date = addDays(today, -i);
      try {
        const d: any = await apiGet('schedule', 'today', { fid: s.family.fid, date }, { silent: true });
        const rows = d.rows || [];
        if (!rows.length) continue;
        groups.push({
          date,
          label: i === 0 ? '今天' : humanDate(date),
          taken: rows.filter((r: any) => r.status === 'taken').length,
          total: rows.length,
          rows: rows.map((r: any) => ({
            dose_id: r.dose_id,
            due_time: r.due_time,
            medicine_name: r.medicine_name,
            dose_text: r.dose_text,
            status: r.status,
            taken_at: r.taken_at,
            is_corrected: r.is_corrected,
          })),
        });
      } catch (e) {
        /* 单天失败跳过 */
      }
    }
    this.setData({ groups, loading: false, isEmpty: groups.length === 0 });
  },
});
