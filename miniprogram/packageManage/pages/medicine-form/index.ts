/**
 * packageManage/pages/medicine-form —— P22–P24 三步建计划
 * 每步 1 个问题 + 1 个主按钮 + 默认值预填；模板卡让默认路径 0 次时间选择器操作。
 * 第 4 个药品被 1004 拦下时弹 UnlockNudge，关闭不清空表单（draft 已落缓存）。
 */
import { bootstrap, refreshToday } from '../../../store/user';
import { get } from '../../../store/index';
import { call, ApiError, get as apiGet } from '../../../utils/request';
import { requestOne } from '../../../utils/subscribe';
import { unlockByAd, PRODUCT } from '../../../utils/ad';
import { report } from '../../../utils/report';
import { timeToMinute } from '../../../utils/time';

const TEMPLATES = [
  { key: 'm3', name: '早中晚', desc: '三次', times: ['08:00', '12:30', '19:00'] },
  { key: 'm2', name: '早晚', desc: '两次', times: ['08:00', '19:00'] },
  { key: 'm1', name: '每天一次', desc: '一次', times: ['08:00'] },
];

Page({
  data: {
    step: 1,
    mid: '',
    uid: '',
    patients: [] as any[],
    name: '',
    spec: '',
    dose: '1片',
    doses: ['半片', '1片', '2片', '1.5片'],
    /** P1-2：什么时候吃（写进药品备注，打卡卡片会显示） */
    note: '',
    noteOptions: ['饭前', '随餐', '饭后', '睡前'],
    times: [{ t: '08:00', d: '1片' }] as { t: string; d: string }[],
    template: 'm1',
    templates: TEMPLATES,
    cycle: 'long_term',
    common: [] as any[],
    sugg: [] as any[],
    saved: false,
    saving: false,
    showNudge: false,
    showSubscribe: false,
    stepHint: '约 40 秒',
  },

  async onLoad(q: Record<string, string>) {
    await bootstrap().catch(() => {});
    const s = get();
    const fid = s.family.fid;
    // 「常用药」= 本家庭引用次数 Top5（不足 5 条由服务端用平台预设补位）。
    // 失败时退回旧的公开预设端点：小程序可能跑在还没部署新接口的后端上，
    // 那时候 chips 不能整块消失 —— 它是 P22 唯一免打字的入口。
    const common: any = await apiGet('medicine', 'suggest', { fid }, { silent: true })
      .catch(() => apiGet('medicine', 'common', {}, { silent: true }))
      .catch(() => ({ items: [] }));
    this.setData({
      patients: s.family.patients,
      uid: q.uid || (s.family.patients[0] ? s.family.patients[0].uid : ''),
      common: (common && common.items) || [],
    });
    if (q.mid) await this.loadMedicine(q.mid);
    report('setup_start', { fid: s.family.fid });
  },

  onUnload() {
    if (this._sgTimer) clearTimeout(this._sgTimer);
  },

  async loadMedicine(mid: string) {
    const d: any = await apiGet('medicine', 'list', { fid: get().family.fid }, { silent: true });
    const m = (d.items || []).find((x: any) => x.mid === mid);
    if (!m) return;
    this.setData({
      mid,
      uid: m.uid,
      name: m.name,
      spec: m.spec,
      dose: m.default_dose,
      times: (m.slots && m.slots.length ? m.slots : [{ t: '08:00', d: m.default_dose }]).map((sl: any) => ({
        t: sl.t,
        d: sl.d || m.default_dose,
      })),
      cycle: m.cycle_type || 'long_term',
      note: m.note || '',
    });
  },

  onBack() {
    if (this.data.step > 1) {
      this.setData({ step: this.data.step - 1 });
      wx.pageScrollTo({ scrollTop: 0, duration: 0 });
    } else wx.navigateBack();
  },

  onNext() {
    // 一次点击只能推进一步。onNext 每次都重读 this.data.step，而 setData 之后
    // this.data 立刻就是新值 —— 只要处理函数被派发两次（原生 tap 冒泡 + 组件自定义
    // tap），点一下"下一步"就会连跳两步，甚至第 2 步刚渲染就被第二次调用当成第 3 步
    // 执行 onSave，表现为"还没选时间就自动保存了"。
    const now = Date.now();
    if (now - (this._navAt || 0) < 350) return;
    this._navAt = now;
    const step = this.data.step;
    if (step === 1) this.next1();
    else if (step === 2) this.next2();
    else this.onSave();
  },

  /* ── Step1 ── */
  onName(e: any) {
    const name = e.detail.value;
    this.setData({ name });
    this.queueSuggest(name);
  },
  onSpec(e: any) {
    this.setData({ spec: e.detail.value });
  },
  onPickPatient(e: any) {
    this.setData({ uid: e.currentTarget.dataset.uid });
  },
  onPickCommon(e: any) {
    const item = this.data.common[e.currentTarget.dataset.i];
    this.setData({ name: item.name, spec: item.spec || '', dose: item.default_dose || this.data.dose, sugg: [], step: 2 });
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
    report('setup_step', { step: 1, ms: 0 });
  },

  /* ── 药名联想（本家庭用药史，按引用次数排序） ── */
  queueSuggest(kw: string) {
    if (this._sgTimer) clearTimeout(this._sgTimer);
    const q = (kw || '').trim();
    if (!q) {
      // 清空输入立刻收起，不等 debounce —— 否则列表悬在空白输入框下面像个 bug
      if (this.data.sugg.length) this.setData({ sugg: [] });
      return;
    }
    // 220ms：一个汉字打完到下一个字通常 >200ms，太短会把每个字都打一次请求
    this._sgTimer = setTimeout(() => this.loadSuggest(q), 220);
  },
  async loadSuggest(q: string) {
    const fid = get().family.fid;
    if (!fid) return;
    const r: any = await apiGet('medicine', 'suggest', { fid, q }, { silent: true }).catch(() => ({ items: [] }));
    // 回包是异步的：用户可能已经改字或翻到第 2 步，只认"当前输入仍等于这次请求的 q"的结果，
    // 否则慢网下会看到上一次的列表盖在现在的输入上。
    if (this.data.step !== 1) return;
    if ((this.data.name || '').trim() !== q) return;
    const items = ((r && r.items) || []).filter((x: any) => x.name !== q);
    this.setData({ sugg: items });
  },
  onPickSuggest(e: any) {
    const item = this.data.sugg[e.currentTarget.dataset.i];
    if (!item) return;
    this.setData({ name: item.name, spec: item.spec || '', dose: item.default_dose || this.data.dose, sugg: [], step: 2 });
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
    report('setup_step', { step: 1, ms: 0 });
  },
  next1() {
    if (!(this.data.name || '').trim()) {
      wx.showToast({ title: '先填药名', icon: 'none' });
      return;
    }
    if (!this.data.uid) {
      wx.showToast({ title: '先选一个服药人', icon: 'none' });
      return;
    }
    this.setData({ step: 2 });
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
    report('setup_step', { step: 1, ms: 0 });
  },

  /* ── Step2 ── */
  onPickDose(e: any) {
    const dose = e.currentTarget.dataset.dose;
    const times = this.data.times.map((x) => ({ ...x, d: dose }));
    this.setData({ dose, times });
  },
  onDoseInput(e: any) {
    this.setData({ dose: e.detail.value });
  },
  /** P1-2：什么时候吃（快捷 chips，点一下即选中；再点取消） */
  onPickNote(e: any) {
    const note = e.currentTarget.dataset.note;
    this.setData({ note: this.data.note === note ? '' : note });
  },
  onNoteInput(e: any) {
    this.setData({ note: e.detail.value });
  },
  next2() {
    this.setData({ step: 3 });
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
    report('setup_step', { step: 2, ms: 0 });
  },

  /* ── Step3 ── */
  onPickTemplate(e: any) {
    const tpl = TEMPLATES[e.currentTarget.dataset.i];
    const times = tpl.times.map((t) => {
      const exist = this.data.times.find((x) => x.t === t);
      return { t, d: exist ? exist.d : this.data.dose };
    });
    this.setData({ template: tpl.key, times });
  },
  onTime(e: any) {
    const i = e.currentTarget.dataset.i;
    const times = this.data.times.slice();
    times[i] = { ...times[i], t: e.detail.value };
    this.setData({ times, template: '' });
  },
  onSlotDose(e: any) {
    const i = e.currentTarget.dataset.i;
    const times = this.data.times.slice();
    times[i] = { ...times[i], d: e.detail.value };
    this.setData({ times });
  },
  addTime() {
    const times = this.data.times.concat([{ t: '20:00', d: this.data.dose }]);
    this.setData({ times, template: '' });
  },
  removeTime(e: any) {
    const i = e.currentTarget.dataset.i;
    if (this.data.times.length <= 1) {
      wx.showToast({ title: '至少留一个时间点', icon: 'none' });
      return;
    }
    const times = this.data.times.slice();
    times.splice(i, 1);
    this.setData({ times, template: '' });
  },
  onCycle(e: any) {
    this.setData({ cycle: e.currentTarget.dataset.cycle });
  },

  async onSave() {
    // 只有停在第 3 步、且没保存成功过才允许落库：防止任何重复派发在别的步骤
    // 用默认时间（08:00）把计划直接存下来。
    if (this.data.step !== 3 || this.data.saved || this.data.saving) return;
    // 建/改药与建计划都要 admin|member，老人端 role=elder 必然 1003
    if (get().family.role === 'elder') {
      wx.showToast({ title: '让子女在手机上修改', icon: 'none' });
      return;
    }
    this.setData({ saving: true });
    const s = get();
    const slots = this.data.times.map((x) => ({
      t: x.t,
      d: x.d || this.data.dose,
      slot_key: `s${timeToMinute(x.t)}`,
    }));
    try {
      let mid = this.data.mid;
      if (!mid) {
        const r: any = await call('medicine', 'create', {
          fid: s.family.fid,
          uid: this.data.uid,
          name: (this.data.name || '').trim(),
          spec: this.data.spec,
          default_dose: this.data.dose,
          note: this.data.note,
        });
        mid = r.mid;
      } else {
        await call('medicine', 'update', {
          fid: s.family.fid,
          uid: this.data.uid,
          mid,
          name: (this.data.name || '').trim(),
          spec: this.data.spec,
          default_dose: this.data.dose,
          note: this.data.note,
        });
      }
      await call('plan', this.data.mid ? 'update' : 'create', {
        mid,
        slots,
        cycle_type: this.data.cycle,
        startDate: s.today.date,
      });
      // 改计划后必须整树重拉：旧 dose_id 不能复用
      await refreshToday();
      this.setData({ saving: false, saved: true, showSubscribe: true, mid });
      report('setup_complete', { med_count: 1, duration: 0 });
    } catch (e: any) {
      this.setData({ saving: false });
      if (e instanceof ApiError && e.code === 1004) {
        this.setData({ showNudge: true });
        report('med_limit_hit', { count: 4, limit: s.user.config.freeMedLimit });
        return;
      }
      wx.showToast({ title: (e && e.msg) || '保存失败，请重试', icon: 'none' });
    }
  },

  /* ── 保存成功后的订阅引导 ── */
  onSubscribe() {
    report('subscribe_request', { scene: 'medicine_form', tmpl: 'CHECKIN', quota_before: get().user.quota.CHECKIN });
    requestOne('CHECKIN', 'medicine_form').then((res) => {
      if (res.unconfigured) {
        wx.showToast({ title: '提醒模板待配置', icon: 'none' });
        this.done();
        return;
      }
      wx.showToast({ title: res.accepted ? '到点会提醒你' : '没开启也能在小程序里看到', icon: 'none' });
      setTimeout(() => this.done(), 800);
    });
  },
  onSubscribeLater() {
    this.done();
  },
  done() {
    wx.navigateBack();
  },

  /* ── 名额拦截 ── */
  onNudgeUnlock() {
    unlockByAd(PRODUCT.MED_SLOT, async () => {
      this.setData({ showNudge: false });
      // 权益已落库，但 store 里的 entitlement.credits 还是旧值：先重拉一次，
      // 否则保存成功后回到药品页，名额文案仍写着"用完了"。
      await bootstrap().catch(() => {});
      this.onSave();
    });
  },
  onNudgeClose() {
    this.setData({ showNudge: false }); // 关闭不清空表单
  },
});
