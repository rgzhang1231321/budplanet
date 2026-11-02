/**
 * pages/launch —— 启动分发（P01 双模选择 / P42 被邀请落地）
 * 判据只有一条：URL 里有没有 fid + ivt。
 *   有  → P42 落地页：首屏零弹窗，点身份按钮后才弹免责确认，确认才 family.join
 *          例外：邀请指向的家就是我已经在的家 → 直接说"你已经在这个家里了"，不给"加入"按钮
 *   没有 → P01：首屏即双模选择（搜一搜/首页/扫码进来的新用户走这条）；
 *              点身份按钮后才弹免责确认，确认才 join —— 确认前不发任何请求、不写任何数据
 *         例外：已同意免责 + 已有家庭的老用户 → 直接进首页，不再走"选身份"
 *
 * 原 P00 欢迎页按产品路线图 §五 决策 3（方案 c）退出首屏：它只承担"演示产品长什么样"，
 * 而搜一搜/扫码进来的人要的是立刻选身份；邀请链路本来就有 P42 的整页老人规格兜底。
 * 它底部的法务入口、「只做提醒与记录」注记与「我有邀请码」随之搬到 P01 ——
 * 合规红线（首屏必须可直达《隐私政策》《用户协议》）不随 P00 一起消失。
 */
import { bootstrap, bindSelf, agree, loadFamily, createFamily, ensureSelfPatient } from '../../store/user';
import { get, inviteUidForShare, set } from '../../store/index';
import { call } from '../../utils/request';
import { report } from '../../utils/report';
import { statusBarHeight } from '../../utils/theme';

Page({
  data: {
    statusBar: 20,
    /** 默认值就是首屏：没有邀请链路的新用户直接落在 P01，不再经过 P00 欢迎页 */
    step: 'mode' as 'mode' | 'invite' | 'joined',
    showDisclaim: false,
    disclaimElder: false,
    pending: '' as '' | 'join-patient' | 'join-guardian',
    /** 入口来源（搜一搜/扫码/分享卡/checkin/report）：免责类埋点的 from 只认它，不认 step */
    entryFrom: 'search',
    /**
     * uid = 邀请人在家庭页点名的"这位是谁"（`?…&uid=Pxxx`）。
     * 首次加入时服务端会**认领这条还没人绑的档案**，而不是新建一条空「我」——
     * 否则子女先建「爸爸」加好药、再邀请他，爸爸登录后看到的是一条没有任何药的新档案。
     * 手动输码没有这个信息，留空即可（服务端退化成新建）。
     */
    invite: { fid: '', ivt: '', uid: '' },
    familyName: '',
    joining: false,
  },

  async onLoad(q: Record<string, string>) {
    const ivt = q.ivt || q.invite || '';
    const from = q.from || 'search';
    this.setData({ statusBar: statusBarHeight(), entryFrom: from });
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
      const uid = q.uid || '';
      const joined = this.alreadyJoined(fid, ivt);
      this.setData({
        step: joined ? 'joined' : 'invite',
        entryFrom: 'invite',
        invite: { fid, ivt, uid },
        // 家名只知道自己的：邀请指向别人家时留空，页面用中性标题兜底，绝不拿自己的家名顶上
        familyName: joined ? s.family.name || '我家' : '',
      });
      report('app_launch', { from: 'invite' });
      return;
    }

    report('app_launch', { from });
    // 老用户早退：登录成功 + 已同意免责 + 已有家庭 → 直接进首页。
    // 不加这一条，老人每次冷启都要再点一次"选身份"，而选身份还会再走一次 family.join。
    if (s.ready && s.user.disclaimerAgreed && (s.family.all || []).length > 0) {
      wx.reLaunch({ url: '/pages/home/index' });
      return;
    }
    // 没有邀请链路 → 首屏就是 P01（data 里的默认 step），这里不必再 setData。
    // 免责确认故意不在这里弹：首屏弹等于把"选身份"挡住一步，方案 c 的全部意义就是省掉这一步；
    // 合规 §11.3 仍满足 —— 点身份按钮时才弹，那之前没有任何请求、没有任何写库。
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
        this.setData({ invite: { fid: '', ivt: code, uid: '' } });
      },
    });
  },

  /** P42：点「我自己吃药」——确认框用老人规格（规格跟着他点的按钮走） */
  onJoinAsPatient() {
    this.setData({ showDisclaim: true, disclaimElder: true, pending: 'join-patient' });
    report('disclaimer_shown', { trigger: 'after_btn', from: this.data.entryFrom, role: 'patient' });
  },

  onJoinAsGuardian() {
    this.setData({ showDisclaim: true, disclaimElder: false, pending: 'join-guardian' });
    report('disclaimer_shown', { trigger: 'after_btn', from: this.data.entryFrom, role: 'guardian' });
  },

  async onDisclaimConfirm() {
    const delay = 1200;
    this.setData({ showDisclaim: false });
    report('disclaimer_ack', { delay_ms: delay, from: this.data.entryFrom, trigger: 'after_btn' });
    try {
      await agree('disclaimer');
      await agree('privacy');
    } catch (e) {
      /* 留痕失败不阻断（主链路优先） */
    }
    const p = this.data.pending;
    this.setData({ pending: '' });
    if (p === 'join-patient') this.join('patient');
    if (p === 'join-guardian') this.join('guardian');
  },

  async join(role: 'patient' | 'guardian') {
    if (this.data.joining) return;
    // 既没有邀请码、也不在任何家庭里 → 全新用户：给他建一个自己的家。
    // 以前这里只 toast 一句"让家人把邀请码发给你"，等于把没有子女帮忙的人挡在门外；
    // 硬往下走则会 reLaunch 进一个空首页（药单空、加药还会 1005）。
    //
    // `family.fid` 为空有两种成因，必须分开处理，否则会把网络问题说成"得先加入家庭"：
    //   ① 真的不在任何家庭（ready=true 说明 login/loadFamily 都成功了）→ 建家
    //   ② 登录或 loadFamily 失败（onLoad 里对 bootstrap 是 catch 掉的）—— 他可能有家，
    //      此时建家也会因为没有登录态而失败，先说清楚是网络问题
    const s = get();
    const hasInvite = !!this.data.invite.ivt;
    if (!hasInvite && !s.family.fid && !s.ready) {
      wx.showToast({ title: '网络不太好，稍后再试一次', icon: 'none' });
      return;
    }
    const needCreate = !hasInvite && !s.family.fid;
    this.setData({ joining: true });
    try {
      if (needCreate) await createFamily();
      if (hasInvite) {
        const res: any = await call('family', 'join', {
          // 手动输码时 fid 为空，服务端按邀请码反查家庭
          fid: this.data.invite.fid,
          ivt: this.data.invite.ivt,
          // 分享卡带来的「这位是谁」：只有首次加入时服务端才认它（认领那条还没人绑的档案），
          // 已在家的人走 bindSelf 时同一个字段会被忽略 —— 那边认 uid 就会把爸爸绑成子女自己。
          uid: this.data.invite.uid || undefined,
          role,
        });
        // 加入成功必须把当前家庭切过去：否则手动输码的人会被 loadFamily 带回"第一个家庭"
        if (res && res.fid) set('family.fid', res.fid);
        await loadFamily();
      }
      if (role === 'patient' && !hasInvite) {
        // 带 ivt 的那次 join 已经在服务端把 role=elder / self_patient_id 绑好了，不走这里。
        // 自己吃药 + 不是被别人邀请进来的：建「我」这条档案 + 绑 self_openid 两件事
        // 都在 family.join(role=patient) 里一次做完（服务端幂等：已绑过就复用，不会重复建）。
        // 这里只留一条"还没有本人档案"的痕迹，不再由前端 patient.create —— 两处都建会出现两条「我」。
        await ensureSelfPatient();
        await bindSelf('patient');
      }
      // 建家是 P01 这条自助路径的完成，不是"被邀请加入"，别混进 invite_accepted 的漏斗
      if (needCreate) report('family_created', { fid: get().family.fid, role });
      else report('invite_accepted', { role, ms: 0 });
      wx.reLaunch({ url: '/pages/home/index' });
    } catch (e: any) {
      wx.showToast({ title: (e && e.msg) || '没弄好，请再试一次', icon: 'none' });
      this.setData({ joining: false });
    }
  },

  /** P01 双模选择：未同意免责就先弹确认，确认后才 join（确认前零请求、零写库） */
  onPickGuardian() {
    if (!get().user.disclaimerAgreed) {
      this.setData({ showDisclaim: true, disclaimElder: false, pending: 'join-guardian' });
      report('disclaimer_shown', { trigger: 'after_btn', from: this.data.entryFrom, role: 'guardian' });
      return;
    }
    this.join('guardian');
  },
  onPickElder() {
    if (!get().user.disclaimerAgreed) {
      // 规格跟着他点的按钮走：点「我自己吃药」的人就是要吃药的人，按老人规格弹
      this.setData({ showDisclaim: true, disclaimElder: true, pending: 'join-patient' });
      report('disclaimer_shown', { trigger: 'after_btn', from: this.data.entryFrom, role: 'patient' });
      return;
    }
    this.join('patient');
  },

  onClose() {
    wx.reLaunch({ url: '/pages/home/index' });
  },

  onShareAppMessage() {
    const s = get();
    // 非管理员拿不到邀请码：不拼空的 `invite=`，否则落地页会退化成普通欢迎页，
    // 而分享文案说的是"一起盯着爸妈吃药"，对不上。分享普通入口即可。
    // 落地页右上角顺手转发：同样自动带「唯一那位还没绑的服药人」，让家人点进来认领自己那条档案。
    // 多位/零位不猜；没有邀请码的卡是普通入口，不拼 uid。
    const uid = s.family.inviteToken ? inviteUidForShare() : '';
    return {
      title: '一起盯着爸妈吃药',
      // 带上 fid：`alreadyJoined` 靠 fid 判"这邀请是不是我已经在的家"。
      // 不带 fid 就只能回落到比 invite_token，而它只对 admin 下发 —— 家里的 member/elder
      // 点开这张卡会看到"加入"而不是"你已经在里面了"。
      path: s.family.inviteToken
        ? `/pages/launch/index?fid=${s.family.fid}&invite=${s.family.inviteToken}&role=patient${uid ? `&uid=${uid}` : ''}`
        : '/pages/launch/index',
    };
  },
});
