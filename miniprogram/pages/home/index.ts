/**
 * pages/home —— P10 家庭看板 / E10 老人端今日（同一路由，mode 决定渲染）
 * 身份（isSelf = 我本人也是服药人）与字号（isElder = 大字版）是两件事：
 * 服药人默认也用普通版，字号由他手动切；但不能让他看全家的药单、也不能给他红字漏服告警。
 */
import { bootstrap, refreshToday } from '../../store/user';
import { get, groups, isElder, isSelf, on, overview, visibleRows } from '../../store/index';
import { correct, skip, take } from '../../store/today';
import { humanDate, humanGap, diffMinutes, tsToTime } from '../../utils/time';
import { statusBarHeight } from '../../utils/theme';
import { report } from '../../utils/report';

Page({
  data: {
    statusBar: 20,
    elder: false,
    familyName: '我家',
    dateText: '',
    ov: { taken: 0, total: 0, rate: 0, missed: 0, pending: 0 },
    perText: '',
    groups: [] as any[],
    elderRows: [] as any[],
    missedText: '',
    showMissed: false,
    recentText: '',
    hasPatients: true,
    hasRows: true,
    /** role === 'admin' 才有改记录的权限（role 管权限，mode 只管渲染，两者不要混用） */
    isAdmin: false,
    adOn: false,
    refreshAt: '',
  },

  onLoad() {
    this.setData({ statusBar: statusBarHeight() });
    this._off = on(() => this.render());
  },

  onUnload() {
    if (this._off) this._off();
  },

  async onShow() {
    const tb = typeof this.getTabBar === 'function' ? (this.getTabBar() as any) : null;
    if (tb && tb.sync) tb.sync(0);
    await bootstrap().catch(() => {});
    await refreshToday();
    this.render();
    report('board_view', { fid: get().family.fid, patient_n: get().family.patients.length });
  },

  async onPullDownRefresh() {
    await refreshToday(false);
    this.render();
    wx.stopPullDownRefresh();
  },

  render() {
    const s = get();
    const elder = isElder();
    const ov = overview();
    const gs = groups();
    const rows = visibleRows();
    const perText = gs.map((g) => `${g.patient.alias} ${g.taken}/${g.total}`).join(' · ');

    // 漏服强提示：只给子女端（老人看到红色告警的第一反应是焦虑而不是行动）
    const firstMiss = rows.find((r) => r.status === 'missed');
    let missedText = '';
    if (firstMiss) {
      const p = s.family.patients.find((x) => x.uid === firstMiss.pid);
      missedText = `${(p && p.alias) || ''}今天 ${firstMiss.due_time} 的 ${firstMiss.medicine_name} 还没吃`;
    }

    const rv = s.today.recentView;
    this.setData({
      elder,
      familyName: s.family.name || '我家',
      dateText: `今天 ${humanDate(s.today.date)}`,
      ov,
      perText,
      groups: gs.map((g) => ({
        uid: g.patient.uid,
        alias: g.patient.alias,
        initial: (g.patient.alias || '家').slice(0, 1),
        taken: g.taken,
        total: g.total,
        rows: g.rows,
      })),
      elderRows: rows,
      missedText,
      showMissed: !!firstMiss && !isSelf(),
      recentText: isSelf() && rv ? `${rv.alias}昨天看过` : '',
      hasPatients: s.family.patients.length > 0,
      hasRows: rows.length > 0,
      isAdmin: s.family.role === 'admin',
      adOn: !!s.user.config.adOn,
      refreshAt: s.today.lastSyncAt ? tsToTime(s.today.lastSyncAt) : '',
    });
  },

  /* ── 交互 ── */

  onRowTap(e: any) {
    const row = e.detail.row;
    // 服药人（即使开着普通版）直接进打卡页：他改不了记录，弹 ActionSheet 只会给一条错误路
    if (isSelf()) {
      wx.navigateTo({ url: `/pages/checkin/index?dose=${row.dose_id}` });
      return;
    }
    this.openActionSheet(row);
  },

  onMenu(e: any) {
    this.openActionSheet(e.detail.row);
  },

  /** 改状态必须两步：ActionSheet → Modal 二次确认（一点就改会污染用药史） */
  openActionSheet(row: any) {
    const s = get();
    // 权限判 role，不判 mode：mode 只决定渲染哪套版式。
    // member 也能进这个菜单的话，改一次就被服务端 1003 顶回首页（utils/request.ts handleError 会 reLaunch），
    // 用户只会看到"点一下、页面跳走了"，不知道为什么。
    if (s.family.role !== 'admin') {
      wx.showToast({ title: '记录由家庭管理员修改', icon: 'none' });
      return;
    }
    // 通道要按契约 §7 分开走，混用必报错：
    //   taken → missed/skipped  = checkin.correct
    //   pending → skipped       = checkin.submit（correct 的 to_status 不收 skipped 以外还会被状态机拒）
    //   missed(系统判定) → taken = checkin.submit（走 correct 会被标成"人工改判"，周报里单列、不计完成率）
    let items: string[] = [];
    if (row.status === 'taken') items = ['改为漏服', '改为跳过'];
    else if (row.status === 'missed' && !row.is_corrected) items = ['改为已服'];
    else if (row.status === 'pending') items = ['改为跳过'];
    else items = ['这一条已不能改'];
    items = items.concat(['取消']);

    wx.showActionSheet({
      itemList: items,
      success: (r) => {
        const pick = items[r.tapIndex];
        if (pick === '这一条已不能改' || pick === '取消') return;
        const to: 'taken' | 'missed' | 'skipped' =
          pick === '改为已服' ? 'taken' : pick === '改为漏服' ? 'missed' : 'skipped';
        wx.showModal({
          title: '确认修改',
          content: `把 ${row.due_time} 的 ${row.medicine_name} 改成「${to === 'taken' ? '已服' : to === 'missed' ? '漏服' : '跳过'}」？这会记入用药史。`,
          confirmText: '确认修改',
          success: async (m) => {
            if (!m.confirm) return;
            try {
              if (to === 'taken') {
                const res = await take([row], 0);
                if (res.offline) {
                  wx.showToast({ title: '已记在本机，会自动重试', icon: 'none' });
                } else if (!res.ok) {
                  wx.showToast({ title: '这条没记上，往上拉一下再试', icon: 'none' });
                  return;
                }
              } else if (to === 'skipped' && row.status === 'pending') {
                const res = await skip(row, '家人代记');
                if (res.offline) {
                  wx.showToast({ title: '已记在本机，会自动重试', icon: 'none' });
                } else if (!res.ok) {
                  wx.showToast({ title: '这条没记上，往上拉一下再试', icon: 'none' });
                  return;
                }
              } else {
                await correct(row, to);
              }
              wx.showToast({ title: '已修改', icon: 'none' });
            } catch (e: any) {
              wx.showToast({ title: (e && e.msg) || '改不动，稍后再试', icon: 'none' });
            }
          },
        });
      },
    });
  },

  onGoCheckin() {
    wx.navigateTo({ url: '/pages/checkin/index' });
  },

  onMissedBar() {
    const row = visibleRows().find((r) => r.status === 'missed');
    if (row) wx.navigateTo({ url: `/pages/checkin/index?dose=${row.dose_id}` });
  },

  onAdd() {
    wx.navigateTo({ url: '/packageManage/pages/medicine-form/index' });
  },

  onSettings() {
    wx.navigateTo({ url: '/packageSettings/pages/settings/index' });
  },

  onAddPatient() {
    wx.navigateTo({ url: '/packageManage/pages/patient/index' });
  },

  onEmptyAdd() {
    if (!this.data.hasPatients) this.onAddPatient();
    else this.onAdd();
  },
});
