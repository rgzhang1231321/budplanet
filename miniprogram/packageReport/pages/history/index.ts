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
    /** 纯服药人 + 还没有绑上自己的服药人：空态要说"让家人帮你绑"，不能说"还没有记录" */
    needBind: false,
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
    // 数据视角判 role（契约 §4.1）：admin/member 看全家，role=elder 只回放自己的行（family.selfUids）。
    // 不再用 mode 判 —— "自己也吃药又管全家"的 admin 会被自己的 mode 裁掉爸妈的记录。
    const pureElder = pureElderView();
    // ⛔ 不再用 `patients.slice(0, 1)` 兜底：没有自绑就是"还没绑上"，不是"第一位服药人就是我"
    // （2026-11-02 事故的前端同形：把爸爸的记录显示成"我的"）。
    const selfUids = s.family.selfUids;
    // 空态分两种：真没记录 vs 还没绑上自己的服药人（后者要引导找人绑，不是"等着打卡"）
    const needBind = pureElder && selfUids.length === 0;
    // 老后端的回落：本地档案里按 pid 查别名。新后端 r.patient 已经直接给出，这里只是兜底，
    // 因为 family.patients 是**缓存**，bootstrap 半失败时它为空、查谁都是空串。
    const aliasOf = (pid: string) => {
      const p = patientOf(pid);
      return (p && (p.alias || p.name)) || '';
    };

    // 先把 7 天的原始行攒齐，再统一决定要不要标"这是谁的"——
    // 判据必须来自**这些行本身**（出现了 ≥2 位服药人），不能来自 family.patients.length：
    // 2026-11-03 客户看到"只有已服/漏服、不知道是谁"，就是缓存没到位时该判据恒假、人名整片消失。
    const raw: { date: string; label: string; rows: any[] }[] = [];
    for (let i = 0; i < 7; i++) {
      const date = addDays(today, -i);
      try {
        const d: any = await apiGet('schedule', 'today', { fid: s.family.fid, date }, { silent: true });
        let rows = d.rows || [];
        if (pureElder) rows = rows.filter((r: any) => selfUids.indexOf(r.pid) >= 0);
        if (!rows.length) continue;
        raw.push({ date, label: i === 0 ? '今天' : humanDate(date), rows });
      } catch (e) {
        /* 单天失败跳过 */
      }
    }

    const seen: Record<string, 1> = {};
    raw.forEach((g) => g.rows.forEach((r: any) => { if (r.pid) seen[r.pid] = 1; }));
    // 只要不是「纯服药人看自己的记录」，每行都标"这是谁的"。
    // ⛔ 曾经用「≥2 位服药人才标」省视觉噪音 —— 那是个错的省法：家里只有一位服药人时
    // （最常见的起步状态）页面全是"药名 + 已服/漏服"，谁也没有主语，客户两次投诉的就是这个。
    // 一位服药人的家庭里多一个"爸爸"是冗余，但"不知道是谁"是事故。
    const needWho = !pureElder;
    // 只有一位服药人时标签用更轻的样式（不加底色块），避免整页重复同一个词还抢眼
    const onlyOne = Object.keys(seen).length < 2;

    const groups = raw.map((g) => ({
      date: g.date,
      label: g.label,
      taken: g.rows.filter((r: any) => r.status === 'taken').length,
      total: g.rows.length,
      rows: g.rows.map((r: any) => ({
        dose_id: r.dose_id,
        due_time: r.due_time,
        medicine_name: r.medicine_name,
        dose_text: r.dose_text,
        status: r.status,
        taken_at: r.taken_at,
        is_corrected: r.is_corrected,
        who: needWho ? (r.patient || aliasOf(r.pid)) : '',
        // 全家只有一位服药人：同一个名字会重复整页，用轻样式（无底色、弱化色）即可
        whoQuiet: onlyOne,
      })),
    }));
    this.setData({ groups, loading: false, isEmpty: groups.length === 0, pureElder, needBind });
  },
});
