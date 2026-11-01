/**
 * pages/checkin —— P11+P12 同页两态（★生死页）
 * 为什么不拆两页：动效是「按钮收缩 → 绿环绘制」连续过渡，跨页会白屏一帧。
 * 铁律：pending 分支 0 广告 0 弹窗；点击即终态（不等网络）；老人端无返回箭头、无撤销。
 */
import { bootstrap, refreshToday } from '../../store/user';
import { get, isElder, isSelf, mode, on, visibleRows } from '../../store/index';
import { flushHeld, skip, snooze, take, undoTake } from '../../store/today';
import { countdown, humanGap, diffMinutes, partOfDay } from '../../utils/time';
import { report } from '../../utils/report';

Page({
  data: {
    elder: false,
    viewState: 'pending' as 'pending' | 'taken' | 'missed' | 'empty',
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
    syncShow: false,
    syncState: 'sending' as 'sending' | 'error',
    showCountdown: false,
    countdownText: '',
    whoText: '',
    canMakeup: false,
    missTitle: '',
    missWhy: '',
  },

  onLoad(q: Record<string, string>) {
    this._doseId = q.dose || '';
    this._off = on((_s, changed) => {
      if (changed === 'today.synced') {
        this.setData({ syncShow: false });
      } else if (changed === 'today.offline') {
        this.setData({ syncShow: true, syncState: 'error' });
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

  /** 选当前要打的卡：优先 pending（同分钟合并为批量），否则回看最近一条已服 */
  build() {
    const s = get();
    const rows = visibleRows();
    if (!rows.length) {
      this.setData({ elder: isElder(), viewState: 'empty', whoText: this.who() });
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
      }
    }
    if (!list.length) {
      // 漏服项仍可打卡：系统判 missed 但人确实吃了，是最高频的假阳性（契约允许 missed→taken）
      const pend = rows
        // 判据是"本人就是服药人"，不是"开着大字版"。
        .filter((r) => r.status === 'pending' || (isSelf() && r.status === 'missed' && !r.is_corrected))
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
    const known =
      members.length && members[0].nick ? `${members[0].nick}已经知道了` : '家人已经知道了';

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

  takenSub(list: any[]) {
    const names = list.map((r) => `${r.due_time} ${r.medicine_name} ${r.dose_text}`).join(' · ');
    return `${names} · 已通知家人`;
  },

  /* ── 打卡 ── */
  onTake() {
    if (this.data.viewState !== 'pending') return; // 只有待打卡视图能打卡（终态锁死防连点）
    // 与 build() 同源：能不能记这一条的判据是"本人就是服药人"(isSelf)，不是"开着大字版"(elder)
    const list = (this._pendingAll || []).filter(
      (r: any) => r.status === 'pending' || (isSelf() && r.status === 'missed' && !r.is_corrected),
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

  onRetrySync() {
    this.setData({ syncState: 'sending' });
    wx.showToast({ title: '正在重试', icon: 'none' });
  },

  goHome() {
    flushHeld();
    wx.reLaunch({ url: '/pages/home/index' });
  },

  onSettings() {
    wx.navigateTo({ url: '/packageSettings/pages/settings/index' });
  },

  onShareAppMessage() {
    const s = get();
    const r = (this._pendingAll || [])[0] || {};
    const alias = this.who().split(' · ')[0] || '爸';
    return {
      title: `${alias}今天按时吃药了`,
      path: `/pages/launch/index?from=checkin&fid=${s.family.fid}&ivt=${s.family.inviteToken}`,
      success: () => report('share_click', { channel: 'wechat', from: 'checkin' }),
    };
  },

  onShareTap() {
    report('share_click', { channel: 'button', from: 'checkin' });
    this.setData({ canUndo: false }); // 已发进家庭群又撤销是最坏错乱
  },
});
