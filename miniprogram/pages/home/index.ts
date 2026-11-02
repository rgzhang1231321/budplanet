/**
 * pages/home —— P10 家庭看板 / E10 老人端今日（同一路由，mode 决定渲染）
 * 身份（isSelf = 我本人也是服药人）与字号（isElder = 大字版）是两件事：
 * 服药人默认也用普通版，字号由他手动切；但不能让他看全家的药单、也不能给他红字漏服告警。
 */
import { bootstrap, refreshToday, switchFamily } from '../../store/user';
import { get, groups, isElder, isSelf, on, overview, visibleRows } from '../../store/index';
import { correct, skip, take } from '../../store/today';
import { dateCN, dayStartTs, humanDate, humanGap, diffMinutes, tsToTime } from '../../utils/time';
import { statusBarHeight } from '../../utils/theme';
import { AD_UNITS } from '../../config/index';
import { report } from '../../utils/report';
import { navTo } from '../../utils/nav';

/** P0-5：「已联系」是本地痕迹，不改服务端状态 —— 联系过不等于吃了。按 dose_id 记到当天。 */
const ACK_KEY = 'missed_ack_v1';
/** P0-3：设置清单可关闭，3 天后重新出现（留存提示，不是永久骚扰）。 */
const SETUP_KEY = 'setup_dismiss_v1';
const SETUP_RESHOW_MS = 3 * 24 * 3600 * 1000;

function ackMap(): Record<string, number> {
  try {
    return wx.getStorageSync(ACK_KEY) || {};
  } catch (e) {
    return {};
  }
}

/** 「XX 今天/昨天/N 天前看过」——回执时间不能写死成"昨天"，那是编的 */
function relativeDay(at: number): string {
  const today = dateCN();
  const d = dateCN(at);
  if (!at || d >= today) return '今天';
  const diff = Math.round((dayStartTs(today) - dayStartTs(d)) / 86400000);
  return diff === 1 ? '昨天' : `${diff} 天前`;
}

Page({
  data: {
    statusBar: 20,
    elder: false,
    familyName: '我家',
    /** P1-3：我加入的全部家庭，>1 时标题可下拉切换 */
    families: [] as any[],
    dateText: '',
    ov: { taken: 0, total: 0, rate: 0, missed: 0, pending: 0 },
    perText: '',
    groups: [] as any[],
    elderRows: [] as any[],
    missedText: '',
    showMissed: false,
    /** P0-5：红条点过「已联系」后降级为一行灰字，当天不再展开 */
    missedAcked: false,
    missedAckedText: '',
    /** P0-2：提醒额度耗尽时首页顶部出琥珀细条 */
    quotaEmpty: false,
    quotaDismissed: false,
    /** P0-3：首设清单（添加服药人 / 添加第一个药 / 邀请家人） */
    showSetup: false,
    setupHasPatient: false,
    setupHasMed: false,
    setupInvited: false,
    recentText: '',
    hasPatients: true,
    hasRows: true,
    /** 今日数据还在路上（rows 为空 + loading）：别抢在它到达前说"还没有添加药品" */
    loading: false,
    /** 能不能改记录：契约 §7.8 是 admin/member。role 管权限，mode 只管渲染，两者不要混用 */
    canCorrect: false,
    adOn: false,
    adUnitB: AD_UNITS.b,
    adUnitBElder: AD_UNITS.bElder,
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
    try {
      await bootstrap();
    } catch (e) {
      // 登录/家庭拿不到就别再发今日请求了：只会再报一次同样的错，
      // 把一次失败放大成"连续报错 + 页面反复重进"。
      this.render();
      return;
    }
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
    let missedAckedText = '';
    if (firstMiss) {
      const p = s.family.patients.find((x) => x.uid === firstMiss.pid);
      const alias = (p && p.alias) || '';
      missedText = `${alias}今天 ${firstMiss.due_time} 的 ${firstMiss.medicine_name} 还没吃`;
      missedAckedText = `${firstMiss.due_time} 那条已联系过`;
    }
    // P0-5：「已联系」当天有效。联系过不等于吃了，只把红条降级成灰字，不改状态。
    const acks = ackMap();
    const acked = !!firstMiss && !!acks[firstMiss.dose_id] && dateCN(acks[firstMiss.dose_id]) === dateCN();
    const childView = !isSelf();

    // P0-2：CHECKIN 额度为 0 = 明天起收不到到点提醒。这是"产品静默失效"，必须让它可见。
    const quotaEmpty = (s.user.quota.CHECKIN || 0) <= 0;
    let quotaDismissed = false;
    try {
      const d = wx.getStorageSync('quota_bar_dismiss_v1');
      quotaDismissed = !!d && dateCN(d) === dateCN();
    } catch (e) {
      quotaDismissed = false;
    }

    // P0-3：首设清单。成员 > 1 视为「邀请已生效」，整卡消失；关闭后 3 天再出现。
    const invited = s.family.members.length > 1;
    const hasPatient = s.family.patients.length > 0;
    const hasMed = rows.length > 0;
    let setupDismissed = false;
    try {
      const at = wx.getStorageSync(SETUP_KEY);
      setupDismissed = !!at && Date.now() - at < SETUP_RESHOW_MS;
    } catch (e) {
      setupDismissed = false;
    }
    // 只有"还没邀请到人"且至少有服药人时才出清单（纯新家 + 邀请未完成）
    const showSetup = childView && !invited && hasPatient && !setupDismissed;

    const rv = s.today.recentView;
    const base = {
      elder,
      familyName: s.family.name || '我家',
      families: s.family.all || [],
      dateText: `今天 ${humanDate(s.today.date)}`,
      ov,
      perText,
      missedText,
      missedAcked: !!firstMiss && childView && acked,
      missedAckedText,
      showMissed: !!firstMiss && childView && !acked,
      quotaEmpty,
      quotaDismissed,
      showSetup,
      setupHasPatient: hasPatient,
      setupHasMed: hasMed,
      setupInvited: invited,
      recentText: isSelf() && rv && rv.at ? `${rv.alias}${relativeDay(rv.at)}看过` : '',
      hasPatients: s.family.patients.length > 0,
      hasRows: rows.length > 0,
      // 首屏：数据没到时 rows 也是空的，与"真的没排药"必须分开，否则老人会以为今天不用吃药
      loading: s.today.loading && rows.length === 0,
      // 契约 §7.8 + 后端 CheckinHandlers.cs:178 的 AssertMemberAsync(..., "admin", "member")：
      // member 服务端本来就放行。这里曾写成只认 admin，后果不是报错，而是 member 的改判能力
      // 被前端静默关掉 —— 同一个家庭里的第二位子女看得到「⋯」以外的一切，却改不了任何一条记录。
      canCorrect: s.family.role === 'admin' || s.family.role === 'member',
      adOn: !!s.user.config.adOn,
      refreshAt: s.today.lastSyncAt ? tsToTime(s.today.lastSyncAt) : '',
    };
    // 同一批行只传一份：大字版读 elderRows，子女端读 groups[].rows。
    // 两个都塞等于把家里所有人的今日剂量传两遍（人多时单次 setData 直接翻倍）。
    if (elder) {
      this.setData({ ...base, groups: [], elderRows: rows });
    } else {
      this.setData({
        ...base,
        groups: gs.map((g) => ({
          uid: g.patient.uid,
          alias: g.patient.alias,
          initial: (g.patient.alias || '家').slice(0, 1),
          taken: g.taken,
          total: g.total,
          rows: g.rows,
        })),
        elderRows: [],
      });
    }
  },

  /* ── 交互 ── */

  onRowTap(e: any) {
    const row = e.detail.row;
    // 服药人（即使开着普通版）直接进打卡页：他改不了记录，弹 ActionSheet 只会给一条错误路
    if (isSelf()) {
      navTo(`/pages/checkin/index?dose=${row.dose_id}`);
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
    // 契约 §7.8：admin/member 可改，elder 一律不可（服务端 AssertMemberAsync 会 1003，
    // utils/request.ts handleError 会 reLaunch，用户只会看到"点一下、页面跳走了"，不知道为什么）。
    // 所以入口只对 elder 收；这一道也只是纵深防御——正常路径 dose-row 根本不渲染「⋯」。
    if (s.family.role !== 'admin' && s.family.role !== 'member') {
      wx.showToast({ title: '记录由家人修改', icon: 'none' });
      return;
    }
    // 通道要按契约 §7 分开走，混用必报错：
    //   taken → missed/skipped  = checkin.correct
    //   pending → skipped       = checkin.submit（correct 的 to_status 不收 skipped 以外还会被状态机拒）
    //   missed(系统判定) → taken = checkin.submit（走 correct 会被标成"人工改判"，周报里单列、不计完成率）
    let items: string[] = [];
    if (row.status === 'taken') items = ['改为漏服', '改为跳过'];
    else if (row.status === 'missed' && !row.is_corrected) items = ['改为已服'];
    // 老人打电话说「我吃了，那个按钮我不会点」：pending 必须能记成已服。
    // 契约 §7 允许 pending→taken（走 checkin.submit）；原先只给「改为跳过」，
    // 会把一次已服记成跳过，周报完成率被拉低且语义全错。
    else if (row.status === 'pending') items = ['替他记上（已服）', '改为跳过'];
    // 一条都改不了就别弹菜单：弹出来只有一个点不动的选项，用户还得再点一次取消才退得出去。
    // 已跳过、以及已被人工改判过的漏服，都落到这里。
    if (!items.length) {
      wx.showToast({ title: '这一条已不能改', icon: 'none' });
      return;
    }
    // 不要再往 itemList 里塞「取消」：showActionSheet 底部自带系统取消项，
    // 自己加一条就会出现两个取消（点自己加的那条还会走 success，被误当成业务项）。
    // 用户点系统取消时走 fail(errMsg 含 cancel)，不进 success，所以这里不用处理。

    wx.showActionSheet({
      itemList: items,
      success: (r) => {
        const pick = items[r.tapIndex];
        const isProxy = pick === '替他记上（已服）';
        const to: 'taken' | 'missed' | 'skipped' =
          pick === '改为已服' || isProxy ? 'taken' : pick === '改为漏服' ? 'missed' : 'skipped';
        const p = get().family.patients.find((x) => x.uid === row.pid);
        const alias = (p && p.alias) || '家人';
        const label = to === 'taken' ? '已服' : to === 'missed' ? '漏服' : '跳过';
        wx.showModal({
          title: isProxy ? '替他记一次？' : '确认修改',
          // 代记必须说清「这是替谁记的」：子女不能悄悄替老人刷完成率。
          content: isProxy
            ? `把 ${row.due_time} 的 ${row.medicine_name} 记为「已服」？这是代 ${alias} 记的，会记入用药史。`
            : `把 ${row.due_time} 的 ${row.medicine_name} 改成「${label}」？这会记入用药史。`,
          confirmText: isProxy ? '确认已服' : '确认修改',
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

  /** 去打卡：带上最早一条待服的 dose，打卡页才能算出「还没到点」的时间语义（P0-4） */
  onGoCheckin() {
    const pend = visibleRows()
      .filter((r) => r.status === 'pending')
      .sort((a, b) => a.slot - b.slot)[0];
    navTo(pend ? `/pages/checkin/index?dose=${pend.dose_id}` : '/pages/checkin/index');
  },

  /** P0-5：标记「已联系」——本地当天有效，只把红条降级为灰字，不改服务端状态 */
  onAckMissed() {
    const row = visibleRows().find((r) => r.status === 'missed');
    if (!row) return;
    try {
      const m = ackMap();
      m[row.dose_id] = Date.now();
      wx.setStorageSync(ACK_KEY, m);
    } catch (e) {
      /* ignore */
    }
    this.render();
  },

  /** P0-2：去补充提醒额度 */
  onQuotaGo() {
    navTo('/packageSettings/pages/quota/index');
  },

  /** P0-2：当天关闭额度细条（24h 内不再出现） */
  onQuotaClose() {
    try {
      wx.setStorageSync('quota_bar_dismiss_v1', Date.now());
    } catch (e) {
      /* ignore */
    }
    this.setData({ quotaDismissed: true });
  },

  /** P0-3：设置清单 → 邀请家人（复用家庭组页，那里有邀请码与转发） */
  onInvite() {
    navTo('/packageManage/pages/family/index');
  },

  /** P0-3：关闭设置清单，3 天后重新出现 */
  onSetupClose() {
    try {
      wx.setStorageSync(SETUP_KEY, Date.now());
    } catch (e) {
      /* ignore */
    }
    this.setData({ showSetup: false });
  },

  onMissedBar() {
    const row = visibleRows().find((r) => r.status === 'missed');
    if (row) navTo(`/pages/checkin/index?dose=${row.dose_id}`);
  },

  /** P1-3：切换家庭（标题下拉）。只有 >1 个家时才给入口。 */
  onSwitchFamily() {
    const s = get();
    const list = s.family.all || [];
    if (list.length <= 1) return;
    wx.showActionSheet({
      itemList: list.map((f) => `${f.name}${f.fid === s.family.fid ? '（当前）' : ''}`),
      success: async (r) => {
        const target = list[r.tapIndex];
        if (!target || target.fid === s.family.fid) return;
        try {
          await switchFamily(target.fid);
          this.render();
          wx.showToast({ title: `已切到${target.name}`, icon: 'none' });
        } catch (e: any) {
          wx.showToast({ title: (e && e.msg) || '切换失败，稍后再试', icon: 'none' });
        }
      },
    });
  },

  onAdd() {
    navTo('/packageManage/pages/medicine-form/index');
  },

  onSettings() {
    navTo('/packageSettings/pages/settings/index');
  },

  onAddPatient() {
    navTo('/packageManage/pages/patient/index');
  },

  onEmptyAdd() {
    if (!this.data.hasPatients) this.onAddPatient();
    else this.onAdd();
  },
});
