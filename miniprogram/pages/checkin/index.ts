/**
 * pages/checkin —— P11+P12 同页两态（★生死页）
 * 为什么不拆两页：动效是「按钮收缩 → 绿环绘制」连续过渡，跨页会白屏一帧。
 * 铁律：pending 分支 0 广告 0 弹窗；点击即终态（不等网络）；老人端无返回箭头、无撤销。
 */
import { bootstrap, refreshToday } from '../../store/user';
import { get, isElder, mode, on, pureElderView, visibleRows } from '../../store/index';
import { flushHeld, skip, snooze, take, undoTake } from '../../store/today';
import { flush, dirtyToday } from '../../utils/queue';
import { isAlwaysAccept, isMainSwitchOn } from '../../utils/subscribe';
import { countdown, humanGap, diffMinutes, partOfDay } from '../../utils/time';
import { AD_UNITS } from '../../config/index';
import { report } from '../../utils/report';
import { navTo } from '../../utils/nav';

Page({
  data: {
    elder: false,
    viewState: 'pending' as 'pending' | 'taken' | 'missed' | 'empty' | 'stale' | 'loading',
    /** 纯服药人 + 还没绑上自己的服药人：空态说"让家人帮你绑一下"，不能说"今天还没有安排" */
    needBind: false,
    list: [] as any[],
    folded: false,
    extra: 0,
    headText: '',
    progressText: '',
    showProgress: false,
    lastOne: false,
    batch: false,
    btnLabel: '我已服用',
    subText: '',
    knownText: '',
    members: [] as any[],
    canUndo: false,
    showShare: false,
    adOn: false,
    adUnitA: AD_UNITS.a,
    syncShow: false,
    syncState: 'sending' as 'sending' | 'error',
    showCountdown: false,
    countdownText: '',
    whoText: '',
    canMakeup: false,
    missTitle: '',
    missWhy: '',
    /** P0-4：还没到点（due_ts - now > 30min）—— 主按钮降级，避免老人提前服药 */
    early: false,
    earlyText: '',
    /** P1-7 / P0-4：下一次几点 + 今日汇总「今天 3 次，已吃 2 次」 */
    nextText: '',
    summaryText: '',
    /** P0-2：额度 ≤1 时在成功页预埋一句，别等第二天收不到提醒才发现 */
    quotaHint: '',
  },

  onLoad(q: Record<string, string>) {
    this._doseId = q.dose || '';
    this._off = on((_s, changed) => {
      if (changed === 'today.synced') {
        this.setData({ syncShow: false });
      } else if (changed === 'today.offline') {
        this.setData({ syncShow: true, syncState: 'error' });
      } else if (changed === 'today.dropped') {
        // 队列放弃（重试超上限）：store 已把行回滚到写入前，这里重渲染回可打卡态，
        // 不 rebuild 的话页面停在「已服」终态，用户既看不到真相也补不了记
        this.build();
      }
    });
    this.build();
    report('checkin_view', { from: q.from || 'direct', batch_size: 0 });
  },

  onShow() {
    if (!this._loaded) {
      this._loaded = true;
      bootstrap()
        .then(() => refreshToday())
        .then(() => this.build())
        .catch(() => {});
    } else {
      refreshToday().then(() => this.build());
    }
  },

  onUnload() {
    if (this._off) this._off();
    clearTimeout(this._undoTimer);
    clearTimeout(this._shareTimer);
    clearInterval(this._cdTimer);
    flushHeld(); // 撤销窗口内离开也立刻上行，不能丢打卡
  },

  onHide() {
    clearInterval(this._cdTimer);
  },

  /** 下拉刷新：`?dose=` 失效时这是最直接的一条出路（重拉今日，卡片按最新状态重算） */
  async onPullDownRefresh() {
    try {
      await bootstrap(true);
      await refreshToday(false);
    } catch (e) {
      /* 刷新失败保留当前画面，不把已有数据清掉 */
    }
    this.build();
    wx.stopPullDownRefresh();
  },

  /**
   * 行级编辑权限谓词：**纯服药人只能记自己的药，成员/管理员记全家的。**
   *
   * 必须和服务端 `checkin.submit` 的拒绝条件逐字对齐
   * （`m.Role === 'elder' && !selfUids.Contains(pid)` → 1003「只能记自己的药」）。
   * 以前这里用 `isSelf()`（= 我在这个家有没有自绑服药人）：
   * 「子女自己也吃降压药，同时管全家」这类人 isSelf 恒为 true，于是页面上把爸妈的漏服行
   * 也摆成"可补记"，点下去服务端一律 1003 —— 看得见、点不动，前端还没有一句提示解释。
   * 判 role 不判"有没有自绑"：后者是渲染维度（mode），拿它当权限用就是 §4.1 那条老坑。
   */
  editableFilter(): (r: any) => boolean {
    const pureElder = pureElderView();
    const self = get().family.selfUids || [];
    return (r: any) => !pureElder || self.indexOf(r.pid) >= 0;
  },

  /** 选当前要打的卡：优先 pending（同分钟合并为批量），否则回看最近一条已服 */
  build() {
    const s = get();
    const rows = visibleRows();
    const canEdit = this.editableFilter();
    // 纯服药人但 selfUids 为空 → 服务端按 role 裁后只会给空药单（不再下发全家的药）。
    // 这时的空态必须说"还没绑上"，说"今天还没有安排"会让老人以为真的不用吃药。
    const needBind = pureElderView() && (s.family.selfUids || []).length === 0;
    if (!rows.length) {
      // 带了 ?dose= 却今天一条都没有：说明这条确实不在了（改计划会生成新 dose_id），
      // 仍然要说"这条已失效"，不能退化成"今天都记好了"让人以为记过了。
      // 数据还在路上时更不能下结论：空 rows + loading 一律先出加载态，
      // 否则首屏会闪一句"今天都记好了"，老人看到就真的不吃了。
      const viewState = this._doseId ? 'stale' : s.today.loading ? 'loading' : 'empty';
      this.setData({ elder: isElder(), viewState, whoText: this.who(), needBind });
      return;
    }
    let list: any[] = [];
    let viewState: 'pending' | 'taken' | 'missed' = 'pending';

    if (this._doseId) {
      const hit = rows.find((r) => r.dose_id === this._doseId);
      if (hit) {
        if (hit.status === 'pending') list = rows.filter((r) => r.status === 'pending' && r.slot === hit.slot);
        else {
          // 只有 taken 才是「已服」；missed / skipped 必须按事实渲染，否则就是假成功
          list = [hit];
          viewState = hit.status === 'taken' ? 'taken' : 'missed';
        }
      } else {
        // 找不到就明说这条失效，绝不拿"最早一条 pending"顶替 ——
        // 用户以为在看自己点的那条，实际是另一条，打卡就打错了记录。
        this._pendingAll = [];
        report('checkin_dose_stale', {});
        this.setData({ elder: isElder(), viewState: 'stale' });
        return;
      }
    }
    if (!list.length) {
      // 漏服项仍可打卡：系统判 missed 但人确实吃了，是最高频的假阳性（契约允许 missed→taken）
      const pend = rows
        // 判据见 editableFilter()：按 role，不按"我有没有自绑服药人"
        .filter((r) => r.status === 'pending' || (canEdit(r) && r.status === 'missed' && !r.is_corrected))
        .sort((a, b) => a.slot - b.slot);
      if (pend.length) list = pend.filter((r) => r.slot === pend[0].slot);
      else {
        const taken = rows.filter((r) => r.status === 'taken');
        if (taken.length) {
          list = [taken[taken.length - 1]];
          viewState = 'taken';
        } else {
          // 一条已服都没有：末条只可能是 missed / skipped，按事实给漏服视图，不冒充成功
          list = [rows[rows.length - 1]];
          viewState = 'missed';
        }
      }
    }

    const first = list[0];
    this._pendingAll = list;
    const patient = s.family.patients.find((x) => x.uid === first.pid);
    const whoText = `${(patient && patient.alias) || ''} · ${s.family.name || '我家'}`;
    const batch = list.length > 1;
    const folded = list.length > 3;
    const shown = folded ? list.slice(0, 2) : list;
    const extra = folded ? list.length - 2 : 0;
    const showProgress = first.today_total > 1;
    const lastOne = first.today_index === first.today_total && first.today_total > 1;

    const members = s.family.members.filter((m) => m.openid !== s.user.openid).slice(0, 3);
    // 「家人已经知道了」不能无条件说：家人知不知道，取决于微信侧是否真的发得出一条消息。
    const reach = this.reachFamily();
    const nick = members.length && members[0].nick ? members[0].nick : '家人';
    const known = reach ? `${nick}会看到这条记录` : `${nick}打开小程序就能看到`;

    // P0-4：还没到点（due_ts - now > 30min）。老人 6:20 打开 8:00 的药，若照常显示「我已服用」，
    // 有提前服药风险（缓释片 / 降糖药尤其危险）。主按钮降级为「我提前吃了」。
    const earlyMs = first.due_ts - Date.now();
    const early = viewState === 'pending' && earlyMs > 30 * 60000;
    const earlyText = early ? humanGap(Math.ceil(earlyMs / 60000)) : '';
    // 下一次：同一天里排在当前之后的下一条（老人最常问「下一次几点」）
    const next = rows
      .filter((r) => r.slot > first.slot && r.status === 'pending')
      .sort((a, b) => a.slot - b.slot)[0];
    const nextText = next ? `下一次 ${next.due_time} · ${next.medicine_name}` : '';
    // P1-7：老人端首屏汇总「今天 3 次，已吃 2 次」——他不必逐行读药单才能回答"我早上吃了没"
    const todayTotal = rows.length;
    const todayTaken = rows.filter((r) => r.status === 'taken').length;
    const summaryText = `今天 ${todayTotal} 次，已吃 ${todayTaken} 次`;
    // P0-2：一次性订阅 1 次 = 1 条，额度见底时下一顿就收不到。成功页先埋一句，别等静默失效。
    const quotaHint = (s.user.quota.CHECKIN || 0) <= 1 ? '下次提醒需要你点一下「允许」' : '';

    this.setData({
      elder: isElder(),
      viewState,
      list: shown,
      folded,
      extra,
      batch,
      headText: `${partOfDay(first.slot)} ${first.due_time}`,
      progressText: showProgress ? `第 ${first.today_index} 次 · 共 ${first.today_total} 次` : '',
      showProgress,
      lastOne: lastOne && !batch,
      btnLabel: batch ? '这些我都吃了' : '我已服用',
      subText: this.takenSub(list),
      knownText: known,
      members,
      whoText,
      adOn: !!s.user.config.adOn,
      early,
      earlyText,
      nextText,
      summaryText,
      quotaHint,
    });

    if (viewState === 'taken') {
      this.setData({ showShare: true, canUndo: false, syncShow: false });
    } else if (viewState === 'missed') {
      // 契约 §7：missed（系统判定、未人工改判）→ taken 允许；skipped 与已改判的 missed 是终态。
      const r: any = list[0];
      const canMakeup = r.status === 'missed' && !r.is_corrected;
      this.setData({
        canMakeup,
        missTitle: r.status === 'missed' ? '这条已经漏服了' : '这条标了「不吃」',
        missWhy: canMakeup
          ? '过了时间还没打卡，系统记成了漏服。如果确实吃了，点下面补记。'
          : r.status === 'missed'
            ? '这条已经人工改判过，按规则不能再改。'
            : '当天标了跳过，按规则不能改成已服。',
        showShare: false,
        canUndo: false,
        syncShow: false,
      });
    }
  },

  who() {
    const s = get();
    const pid = (this._pendingAll && this._pendingAll[0] && this._pendingAll[0].pid) || '';
    const p = s.family.patients.find((x) => x.uid === pid);
    return `${(p && p.alias) || ''} · ${s.family.name || '我家'}`;
  },

  /**
   * 家人能不能收到消息：MISSED 是唯一发给家人的模板（漏服告警），
   * 额度为 0 或没勾「总是保持以上选择」时，后台一条都不会发。
   * 所以"已通知家人"这句话有没有资格说，全看这里。
   */
  reachFamily(): boolean {
    const q = get().user.quota;
    return !!q && (q.MISSED || 0) > 0 && isAlwaysAccept('MISSED') && isMainSwitchOn();
  },

  takenSub(list: any[]) {
    const names = list.map((r) => `${r.due_time} ${r.medicine_name} ${r.dose_text}`).join(' · ');
    // 绝不写「已通知家人」：打卡这一刻微信侧不会推送任何消息，
    // 有 MISSED 额度也只会在"判成漏服"时才发。两种说法都必须是真的。
    return this.reachFamily() ? `${names} · 已记下，家人会看到` : `${names} · 已记下`;
  },

  /* ── 打卡 ── */
  onTake() {
    if (this.data.viewState !== 'pending') return; // 只有待打卡视图能打卡（终态锁死防连点）
    // 与 build() 同源（editableFilter）：能不能记这一条判 role，不判"有没有自绑"、也不判大字版
    const canEdit = this.editableFilter();
    const list = (this._pendingAll || []).filter(
      (r: any) => r.status === 'pending' || (canEdit(r) && r.status === 'missed' && !r.is_corrected),
    );
    if (!list.length) {
      wx.showToast({ title: '这条已经记过了，不用重复点', icon: 'none' });
      return;
    }
    wx.vibrateShort({ type: 'heavy', fail: () => {} } as any);
    // 子女端保留 3 秒撤销：窗口内暂缓上行，撤销即"没发生过"（契约无 taken→pending）
    take(list, this.data.elder ? 0 : 3000).then((res) => {
      // 服务端明确拒绝（skipped 终态 / 已人工改判的 missed）：本地不能停留在"已服"，按真实状态重渲染
      if (!res.offline && !res.ok) {
        this.build();
        wx.showToast({ title: '这条没记上，请重新点一次', icon: 'none' });
      }
    });
    this.setData({ viewState: 'taken', syncShow: true, syncState: 'sending', canUndo: !this.data.elder });
    this._shareTimer = setTimeout(() => this.setData({ showShare: true }), 1200);
    if (!this.data.elder) {
      this._undoTimer = setTimeout(() => this.setData({ canUndo: false }), 3000);
    }
  },

  /** 漏服补记：走 checkin.submit(status=taken)，契约 §7 允许 missed(系统判定、未改判)→taken。
   *  skipped 与已人工改判的 missed 是终态，页面不提供本入口。
   *  绿勾只认服务端确认：被拒或断网都不许显示"已经记下了"。 */
  onMakeup() {
    const r = (this._pendingAll || [])[0];
    if (!r || r.status !== 'missed' || r.is_corrected) {
      wx.showToast({ title: '这条已经不能补记了', icon: 'none' });
      return;
    }
    take([r], 0).then((res) => {
      if (res.offline) {
        // 断网：本机先记下、队列自动重放，顶部挂同步细条，但不算服务端确认成功
        this.setData({ viewState: 'taken', syncShow: true, syncState: 'error' });
        wx.showToast({ title: '已记在本机，会自动重试', icon: 'none' });
        return;
      }
      // 以服务端返回的真实 status 渲染（拒绝时 store 已回滚到点击前的原状态）
      this.build();
      if (!res.ok) {
        wx.showToast({ title: '这条没记上，请重新点一次', icon: 'none' });
        return;
      }
      this.setData({ syncShow: false, canUndo: false, showShare: true, subText: this.takenSub([r]) });
    });
  },

  onUndo() {
    const list = this._pendingAll || [];
    const ok = undoTake(list); // 传整批：批量打卡时窗口内暂缓的是整批，不能只回滚第一条
    if (!ok) {
      this.setData({ canUndo: false });
      return;
    }
    clearTimeout(this._undoTimer);
    clearTimeout(this._shareTimer);
    // 回滚到的是点击前的原状态（可能是 missed），所以视图必须重算，不能一律当 pending
    this.build();
    this.setData({ canUndo: false, showShare: false, syncShow: false });
    wx.showToast({ title: '已撤销', icon: 'none' });
  },

  onSnooze() {
    const r = (this._pendingAll || [])[0];
    if (!r) return;
    if (r.snooze_count >= 2) {
      wx.showToast({ title: '已延后 2 次，请选择服用或跳过', icon: 'none' });
      return;
    }
    snooze(r).then((res) => {
      if (res.ok) {
        this.setData({ showCountdown: true, countdownText: countdown(15 * 60000) });
        this.startCountdown(Date.now() + 15 * 60000);
        wx.showToast({ title: '15 分钟后再提醒你', icon: 'none' });
      } else {
        // 额度/窗口不可用时绝不静默：原地大字提示
        wx.showToast({ title: res.msg || '这个点快到最后时间了，先记一下', icon: 'none' });
      }
    });
  },

  startCountdown(endAt: number) {
    clearInterval(this._cdTimer);
    const tick = () => {
      const left = endAt - Date.now();
      if (left <= 0) {
        clearInterval(this._cdTimer);
        this.setData({ showCountdown: false });
        this.build();
        return;
      }
      this.setData({ countdownText: countdown(left) });
    };
    tick();
    this._cdTimer = setInterval(tick, 1000);
  },

  onSkip() {
    const r = (this._pendingAll || [])[0];
    if (!r) return;
    // P2-8：老人端「今天不吃」收进二次确认 —— 与"老人端不让人做选择"的原则一致
    if (this.data.elder) {
      wx.showModal({
        title: '今天这次不吃了？',
        content: '记下之后，今天这次就不会再提醒。',
        confirmText: '不吃',
        success: (m) => {
          if (m.confirm) this.doSkip(r);
        },
      });
      return;
    }
    this.doSkip(r);
  },

  doSkip(r: any) {
    wx.showActionSheet({
      itemList: ['已吃过', '不舒服', '医生让停', '其他'],
      success: (res) => {
        const reason = ['已吃过', '不舒服', '医生让停', '其他'][res.tapIndex];
        skip(r, reason);
        wx.showToast({ title: '今天这个记下了', icon: 'none' });
        setTimeout(() => this.goHome(), 600);
      },
    });
  },

  /**
   * 真的重试，不是只换个文案：
   * 先把撤销窗口里暂缓的那批发出去，再冲一次离线队列；队列空了才算真同步上。
   */
  async onRetrySync() {
    this.setData({ syncState: 'sending' });
    const held = flushHeld();
    try {
      // 等暂缓的那批真发完，再冲队列；两步都结束才有资格判断结果
      if (held) await held;
      await flush();
    } catch (e) {
      /* 冲队列本身不抛业务错，这里只兜住极端情况 */
    }
    if (!dirtyToday()) {
      this.setData({ syncShow: false });
      wx.showToast({ title: '已经同步好了', icon: 'none' });
      return;
    }
    // 还是没上去：不重复弹同一句 toast，给一条能照着做的出路
    this.setData({ syncState: 'error' });
    wx.showModal({
      title: '还没能发出去',
      content: '检查一下手机网络，或走到有信号的地方再点「重试」。记录已经存在手机里，不会丢。',
      showCancel: false,
      confirmText: '知道了',
    });
  },

  goHome() {
    flushHeld();
    wx.reLaunch({ url: '/pages/home/index' });
  },

  onSettings() {
    navTo('/packageSettings/pages/settings/index');
  },

  onShareAppMessage() {
    const s = get();
    const r = (this._pendingAll || [])[0] || {};
    const alias = this.who().split(' · ')[0] || '爸';
    // invite_token 只对 admin 下发：拿不到码时不要拼 `ivt=`（空串会让落地页落到普通欢迎页，
    // 用户以为是邀请卡、点进去却发现要重新开始）。宁可分享成普通入口。
    const ivt = s.family.inviteToken;
    return {
      title: `${alias}今天按时吃药了`,
      path: ivt
        ? `/pages/launch/index?from=checkin&fid=${s.family.fid}&ivt=${ivt}`
        : '/pages/launch/index?from=checkin',
      success: () => report('share_click', { channel: 'wechat', from: 'checkin' }),
    };
  },

  onShareTap() {
    report('share_click', { channel: 'button', from: 'checkin' });
    this.setData({ canUndo: false }); // 已发进家庭群又撤销是最坏错乱
  },
});
