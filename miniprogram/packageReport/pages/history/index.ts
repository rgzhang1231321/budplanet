/** packageReport/pages/history —— 历史明细（schedule.today 支持任意 date，按天回放） */
import { bootstrap } from '../../../store/user';
import { get, patientOf, pureElderView } from '../../../store/index';
import { get as apiGet } from '../../../utils/request';
import { addDays, dateCN, humanDate } from '../../../utils/time';

Page({
  data: {
    groups: [] as any[],
    loading: true,
    isEmpty: false,
    /** 纯服药人视角（role=elder）才叫「我的记录」：只看自己的行，且不展示家庭汇总 */
    pureElder: false,
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
    // 数据视角判 role（契约 §4.1）：admin/member 看全家，role=elder 只回放自己的行（family.selfUids）。
    // 不再用 mode 判 —— "自己也吃药又管全家"的 admin 会被自己的 mode 裁掉爸妈的记录。
    const pureElder = pureElderView();
    const selfUids = s.family.selfUids.length
      ? s.family.selfUids
      : s.family.patients.slice(0, 1).map((p) => p.uid);
    // 家庭有 ≥2 位服药人时每行标"这是谁的药"；只有 1 位就是废话，纯服药人也不显示（含自己）
    const needWho = !pureElder && s.family.patients.length >= 2;
    const aliasOf = (pid: string) => {
      const p = patientOf(pid);
      return (p && (p.alias || p.name)) || '';
    };
    for (let i = 0; i < 7; i++) {
      const date = addDays(today, -i);
      try {
        const d: any = await apiGet('schedule', 'today', { fid: s.family.fid, date }, { silent: true });
        let rows = d.rows || [];
        if (pureElder) rows = rows.filter((r: any) => selfUids.indexOf(r.pid) >= 0);
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
            who: needWho ? aliasOf(r.pid) : '',
          })),
        });
      } catch (e) {
        /* 单天失败跳过 */
      }
    }
    this.setData({ groups, loading: false, isEmpty: groups.length === 0, pureElder });
  },
});
