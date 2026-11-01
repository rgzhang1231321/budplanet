/**
 * pages/launch —— 启动分发（P00 授权 / P01 双模选择 / P42 被邀请落地）
 * 判据只有一条：URL 里有没有 fid + ivt。
 *   有  → P42 落地页：首屏零弹窗，点身份按钮后才弹免责确认，确认才 family.join
 *   没有 → P00：首屏即弹免责确认（搜一搜/首页/扫码进来的新用户走这条）
 */
import { bootstrap, bindSelf, agree, loadFamily } from '../../store/user';
import { get, set } from '../../store/index';
import { call, get as apiGet } from '../../utils/request';
import { report } from '../../utils/report';
import { statusBarHeight } from '../../utils/theme';

Page({
  data: {
    statusBar: 20,
    step: 'welcome' as 'welcome' | 'mode' | 'invite',
    showDisclaim: false,
    disclaimElder: false,
    pending: '' as '' | 'mode' | 'join-patient' | 'join-guardian',
    invite: { fid: '', ivt: '' },
    meds: [] as { time: string; name: string; dose: string }[],
    medMore: 0,
    familyName: '我家',
    joining: false,
  },

  async onLoad(q: Record<string, string>) {
    const ivt = q.ivt || q.invite || '';
    this.setData({ statusBar: statusBarHeight() });
    wx.setNavigationBarTitle({ title: '替你记着吃药' });
    try {
      await bootstrap();
    } catch (e) {
      /* 登录失败也允许先看落地页 */
    }
    const s = get();
    const hasInvite = !!ivt;
    if (hasInvite) {
      this.setData({ step: 'invite', invite: { fid: q.fid || s.family.fid, ivt }, familyName: s.family.name || '我家' });
      this.loadInvitePreview();
      report('app_launch', { from: 'invite' });
    } else {
      report('app_launch', { from: q.from || 'search' });
      if (!s.user.disclaimerAgreed) {
        // 例外路径：首屏即弹（子女规格，此时 mode 未定）
        this.setData({ showDisclaim: true, disclaimElder: false, pending: '' });
        report('disclaimer_shown', { trigger: 'first_screen', from: q.from || 'search' });
      }
    }
  },

  /** 落地页药单：最多 2 条 + 「等 N 种药」（限量防超屏） */
  async loadInvitePreview() {
    try {
      const s = get();
      const pid = s.family.patients[0] ? s.family.patients[0].uid : '';
      const d: any = await apiGet('medicine', 'list', { fid: s.family.fid, uid: pid }, { silent: true });
      const items = d.items || [];
      const meds: { time: string; name: string; dose: string }[] = [];
      items.forEach((m: any) => {
        (m.slots || []).forEach((sl: any) => {
          if (meds.length < 2) meds.push({ time: sl.t, name: m.name, dose: sl.d || m.default_dose });
        });
      });
      const total = items.reduce((n: number, m: any) => n + (m.slots || []).length, 0);
      this.setData({ meds, medMore: Math.max(0, total - meds.length) });
    } catch (e) {
      /* 预览失败不阻断加入 */
    }
  },

  onStart() {
    const s = get();
    if (!s.user.disclaimerAgreed) {
      this.setData({ showDisclaim: true, disclaimElder: false, pending: 'mode' });
      report('disclaimer_shown', { trigger: 'after_btn', from: 'welcome' });
      return;
    }
    this.setData({ step: 'mode' });
  },

  /** 手动输入邀请码：家人把码用微信/短信发过来时不依赖分享链接。
   *  校验只做形状（IVT + 8 位十六进制），真假由 family.join 判定 —— 这里没有预览接口，
   *  提前查一次等于把"加入"提前发生，反而会让选身份那步失效。 */
  onManualInvite() {
    wx.showModal({
      title: '输入邀请码',
      editable: true,
      placeholderText: '形如 IVT1A2B3C4D',
      confirmText: '加入',
      success: (r) => {
        if (!r.confirm) return;
        const code = (r.content || '').replace(/\s/g, '').toUpperCase();
        if (!/^IVT[0-9A-F]{8}$/.test(code)) {
          wx.showToast({ title: '邀请码是 IVT 开头的 11 位', icon: 'none' });
          return;
        }
        report('invite_code_manual', {});
        // fid 交给服务端按邀请码反查：拿到码的人本来就不该知道 fid
        this.setData({ step: 'mode', invite: { fid: '', ivt: code } });
      },
    });
  },

  /** P42：点「我自己吃药」——确认框用老人规格（规格跟着他点的按钮走） */
  onJoinAsPatient() {
    this.setData({ showDisclaim: true, disclaimElder: true, pending: 'join-patient' });
    report('disclaimer_shown', { trigger: 'after_btn', from: 'invite', role: 'patient' });
  },

  onJoinAsGuardian() {
    this.setData({ showDisclaim: true, disclaimElder: false, pending: 'join-guardian' });
    report('disclaimer_shown', { trigger: 'after_btn', from: 'invite', role: 'guardian' });
  },

  async onDisclaimConfirm() {
    const delay = 1200;
    this.setData({ showDisclaim: false });
    report('disclaimer_ack', { delay_ms: delay, from: this.data.step, trigger: this.data.pending ? 'after_btn' : 'first_screen' });
    try {
      await agree('disclaimer');
      await agree('privacy');
    } catch (e) {
      /* 留痕失败不阻断（主链路优先） */
    }
    const p = this.data.pending;
    this.setData({ pending: '' });
    if (p === 'mode') this.setData({ step: 'mode' });
    if (p === 'join-patient') this.join('patient');
    if (p === 'join-guardian') this.join('guardian');
  },

  async join(role: 'patient' | 'guardian') {
    if (this.data.joining) return;
    this.setData({ joining: true });
    try {
      if (this.data.invite.ivt) {
        const res: any = await call('family', 'join', {
          // 手动输码时 fid 为空，服务端按邀请码反查家庭
          fid: this.data.invite.fid,
          ivt: this.data.invite.ivt,
          role,
        });
        // 加入成功必须把当前家庭切过去：否则手动输码的人会被 loadFamily 带回"第一个家庭"
        if (res && res.fid) set('family.fid', res.fid);
        await loadFamily();
      }
      if (role === 'patient') await bindSelf('patient');
      report('invite_accepted', { role, ms: 0 });
      wx.reLaunch({ url: '/pages/home/index' });
    } catch (e: any) {
      wx.showToast({ title: (e && e.msg) || '加入失败，请重试', icon: 'none' });
      this.setData({ joining: false });
    }
  },

  /** P01 双模选择 */
  onPickGuardian() {
    this.join('guardian');
  },
  onPickElder() {
    this.join('patient');
  },

  onClose() {
    wx.reLaunch({ url: '/pages/home/index' });
  },

  onShareAppMessage() {
    const s = get();
    return {
      title: '一起盯着爸妈吃药',
      path: `/pages/launch/index?invite=${s.family.inviteToken}&role=patient`,
    };
  },
});
