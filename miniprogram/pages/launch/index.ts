/**
 * pages/launch —— 启动分发（P00 授权 / P01 双模选择 / P42 被邀请落地）
 * 判据只有一条：URL 里有没有 fid + ivt。
 *   有  → P42 落地页：首屏零弹窗，点身份按钮后才弹免责确认，确认才 family.join
 *          例外：邀请指向的家就是我已经在的家 → 直接说"你已经在这个家里了"，不给"加入"按钮
 *   没有 → P00：首屏即弹免责确认（搜一搜/首页/扫码进来的新用户走这条）
 *         例外：已同意免责 + 已有家庭的老用户 → 直接进首页，不再走"开始使用 → 选身份"
 */
import { bootstrap, bindSelf, agree, loadFamily } from '../../store/user';
import { get, set } from '../../store/index';
import { call } from '../../utils/request';
import { report } from '../../utils/report';
import { statusBarHeight } from '../../utils/theme';

Page({
  data: {
    statusBar: 20,
    step: 'welcome' as 'welcome' | 'mode' | 'invite' | 'joined',
    showDisclaim: false,
    disclaimElder: false,
    pending: '' as '' | 'mode' | 'join-patient' | 'join-guardian',
    invite: { fid: '', ivt: '' },
    familyName: '',
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
      // 只认 URL 带来的 fid：邀请指向哪个家，就展示哪个家。
      // 绝不拿 s.family（我自己当前所在的家）顶替 —— 那会让已在家的人看到"自己的家"被当成邀请。
      const fid = q.fid || '';
      const joined = this.alreadyJoined(fid, ivt);
      this.setData({
        step: joined ? 'joined' : 'invite',
        invite: { fid, ivt },
        // 家名只知道自己的：邀请指向别人家时留空，页面用中性标题兜底，绝不拿自己的家名顶上
        familyName: joined ? s.family.name || '我家' : '',
      });
      report('app_launch', { from: 'invite' });
      return;
    }

    report('app_launch', { from: q.from || 'search' });
    // 老用户早退：登录成功 + 已同意免责 + 已有家庭 → 直接进首页。
    // 不加这一条，老人每次冷启都要点三次（开始使用 → 选身份 → 首页），而选身份还会再走一次 family.join。
    if (s.ready && s.user.disclaimerAgreed && (s.family.all || []).length > 0) {
      wx.reLaunch({ url: '/pages/home/index' });
      return;
    }
    if (!s.user.disclaimerAgreed) {
      // 例外路径：首屏即弹（子女规格，此时 mode 未定）
      this.setData({ showDisclaim: true, disclaimElder: false, pending: '' });
      report('disclaimer_shown', { trigger: 'first_screen', from: q.from || 'search' });
    }
  },

  /**
   * 这个邀请是不是指向"我已经在的家"。
   * 有 fid 就比 fid；只有邀请码（来自分享卡的最后一段）就比当前家庭的邀请码 ——
   * 已在家的人点开家庭群里的邀请卡，必须看到"你已经在里面了"，而不是让他再"加入"一次
   * （family.join 对 patient 意图会把 admin 降成 elder，重复加入不是无害空操作）。
   */
  alreadyJoined(fid: string, ivt: string): boolean {
    const s = get();
    if (fid) {
      if (s.family.all.some((f) => f.fid === fid)) return true;
      return !!s.family.fid && s.family.fid === fid;
    }
    return !!ivt && !!s.family.inviteToken && s.family.inviteToken === ivt;
  },

  onHome() {
    wx.reLaunch({ url: '/pages/home/index' });
  },

  onLegal(e: any) {
    wx.navigateTo({ url: `/pages/legal/index?type=${e.currentTarget.dataset.type}` });
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
      if (role === 'patient') {
        // 刚刚带着 ivt 的 join 已经在服务端把 role=elder / self_patient_id 绑好了，
        // 再调一次 bindSelf 等于重复加入；而 invite_token 现在只给 admin，
        // 非管理员此刻手上是空串 → 第二次 join 必被 1005「邀请码无效」顶回，
        // 把一次已经成功的加入变成报错页。所以只在"没走 invite join"时才补绑。
        if (!this.data.invite.ivt) await bindSelf('patient');
      }
      report('invite_accepted', { role, ms: 0 });
      wx.reLaunch({ url: '/pages/home/index' });
    } catch (e: any) {
      wx.showToast({ title: (e && e.msg) || '没加入成功，请再试一次', icon: 'none' });
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
    // 非管理员拿不到邀请码：不拼空的 `invite=`，否则落地页会退化成普通欢迎页，
    // 而分享文案说的是"一起盯着爸妈吃药"，对不上。分享普通入口即可。
    return {
      title: '一起盯着爸妈吃药',
      path: s.family.inviteToken
        ? `/pages/launch/index?invite=${s.family.inviteToken}&role=patient`
        : '/pages/launch/index',
    };
  },
});
