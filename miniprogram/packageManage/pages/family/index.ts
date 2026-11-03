/** packageManage/pages/family —— P41 邀请家人 / 成员管理 */
import { bootstrap, loadFamily, resetSession } from '../../../store/user';
import { get } from '../../../store/index';
import { call } from '../../../utils/request';
import { report } from '../../../utils/report';
import { requestOne } from '../../../utils/subscribe';

/** P1-4：邀请发出后本地留痕，用于「已发出邀请 · 等待加入」占位（48h 后变「再发一次」） */
const INV_KEY = 'invite_pending_v1';

function inviteState(fid: string): { at: number; memberCount: number } | null {
  try {
    const all = wx.getStorageSync(INV_KEY) || {};
    return all[fid] || null;
  } catch (e) {
    return null;
  }
}

function markInvited(fid: string, memberCount: number) {
  if (!fid) return;
  try {
    const all = wx.getStorageSync(INV_KEY) || {};
    all[fid] = { at: Date.now(), memberCount };
    wx.setStorageSync(INV_KEY, all);
  } catch (e) {
    /* ignore */
  }
}

Page({
  data: {
    name: '',
    token: '',
    members: [] as any[],
    isAdmin: false,
    canLeave: false,
    path: '',
    showMissedCta: false,
    missedLater: false,
    /** P1-4：已发出邀请、对方还没加入 */
    pendingInvite: false,
    pendingText: '',
    pendingResend: false,
    /**
     * 「这次邀请的是谁」——分享卡上带 `&uid=`，对方首次加入时服务端认领这条档案。
     * 不带这个参数的话，爸爸点进来只会得到一条新建的空「我」，而药都挂在
     * 子女先前建的「爸爸」那条上 —— 他看到的就是空药单，家里还多一条重复档案。
     */
    inviteChips: [] as any[],
    inviteUid: '',
    inviteName: '',
    /**
     * 多位可认领却还没点选 = 此刻不能转发。
     * 微信的 `open-type="share"` 拦不住点击（点了就直接拉起转发面板），
     * 所以只能在渲染层把按钮换成普通 button：不点人，转发入口根本不存在。
     */
    needPick: false,
  },

  async onShow() {
    await bootstrap().catch(() => {});
    // bootstrap 命中缓存时不会重拉 family.info：刚被邀请进来的人、刚改过的角色都会看不见，
    // 而这一页的整个内容就是"谁在这个家里"，必须每次进来都取最新。
    await loadFamily().catch(() => {});
    this.render();
  },

  render() {
    const s = get();
    const roleMap: Record<string, string> = { admin: '管理员', member: '成员', elder: '服药人' };
    const isAdmin = s.family.role === 'admin';
    const quota = (s.user.quota || {}) as any;
    // P1-4：邀请发出后成员数没涨 = 对方还没进来。48h 后把文案换成「再发一次」。
    const inv = inviteState(s.family.fid);
    const stillPending = !!inv && s.family.members.length <= inv.memberCount;
    const waitedDays = inv ? Math.floor((Date.now() - inv.at) / 86400000) : 0;
    const patients = s.family.patients || [];
    // 只列「还没人绑」的档案：服务端认领时只认 selfOpenid 为空的那条。
    // 把已绑定的（妈妈已加入那条、子女自己吃药那条）列进选项 = 点中必然认领失败，
    // 服务端退化成新建一条空的「我」—— 正是这轮要修的空药单 + 多一条档案，而界面看不出错。
    const inviteChips = patients
      .filter((p: any) => !p.selfOpenid)
      .map((p: any) => ({ uid: p.uid, name: p.alias || p.name || '家人' }));
    // 点名规则：家里只有一位可认领的服药人 → 自动带（子女一秒都不用多花）；多位 → 不猜，让管理员点一下。
    // "不猜"这条是硬约束：猜错就是把妈妈的档案发给爸爸，两条绑定全乱。
    const picked = inviteChips.find((c: any) => c.uid === this.data.inviteUid) || (inviteChips.length === 1 ? inviteChips[0] : null);
    const invitePath = (uid: string) =>
      isAdmin && s.family.inviteToken
        ? `/pages/launch/index?fid=${s.family.fid}&invite=${s.family.inviteToken}&role=patient${uid ? `&uid=${uid}` : ''}`
        : '/pages/launch/index';
    this.setData({
      name: s.family.name || '我家',
      token: s.family.inviteToken,
      isAdmin,
      pendingInvite: isAdmin && stillPending,
      pendingText: waitedDays >= 2 ? `邀请已发出 ${waitedDays} 天，还没进来` : '邀请已发出，等家人加入',
      pendingResend: waitedDays >= 2,
      canLeave: s.family.members.length > 1,
      inviteChips,
      inviteUid: picked ? picked.uid : '',
      inviteName: picked ? picked.name : '',
      // 还没点名且有多位候选 → 把转发按钮换成「先选这次邀请谁」。
      // 零候选（家里所有档案都绑好了）不拦：这时没有可认领的人，新人正常新建就是对的。
      needPick: !picked && inviteChips.length > 1,
      // 非管理员没有邀请码（family.info 只对 admin 下发），拼出来的 `invite=` 是空串，
      // 落地页会当成"没有邀请"走普通欢迎页 —— 与其发一张假邀请卡，不如分享普通入口。
      // 带上 fid：落地页判"你已经在里面了"用的是 fid（比邀请码可靠）—— 邀请码只对
      // admin 下发，家里其它成员点开这张卡时手里没有码，只能靠 fid 认出"这就是我的家"。
      path: invitePath(picked ? picked.uid : ''),
      // MISSED 的接收方是家庭管理员（子女），所以只给管理员看这一张卡；
      // 家里还没有服药人时不显示——"谁没吃药"这件事此刻还不存在。
      showMissedCta:
        !this.data.missedLater && isAdmin && (quota.MISSED || 0) <= 0 && (s.family.patients || []).length > 0,
      members: s.family.members.map((m) => {
        // 该成员若是「服药人」，找绑定到他的 patient，带上大字版开关状态
        const bound = (s.family.patients || []).find((p) => p.selfOpenid && p.selfOpenid === m.openid);
        // 家内称呼优先：小程序拿不到微信昵称，`nick` 对绝大多数成员都是"家人"
        const displayName = (m as any).label || m.nick || '家人';
        // 副信息必须说清"他是谁 + 他负责哪条药单"，否则一堆「家人」还是分不出来
        const parts = [roleMap[m.role] || '成员'];
        if (bound) parts.push(`药单：${bound.alias || bound.name || '未命名'}`);
        return {
          openid: m.openid,
          nick: m.nick,
          label: (m as any).label || '',
          displayName,
          initial: displayName.slice(0, 1),
          subText: parts.join(' · '),
          role: m.role,
          roleText: roleMap[m.role] || '成员',
          isMe: m.openid === s.user.openid,
          // 改称呼 / 绑定服药人是管理员的事；自己那行也能改（给自己起个"小明"）
          canManage: isAdmin,
          // v1.7：服药人可设「大字版」默认；只有管理员能改
          isPatient: m.role === 'elder',
          patientUid: bound ? bound.uid : '',
          boundUid: bound ? bound.uid : '',
          boundName: bound ? bound.alias || bound.name || '未命名' : '',
          largeMode: !!bound && bound.display_mode === 'large',
          canSetMode: isAdmin && !!bound && m.openid !== s.user.openid,
        };
      }),
    });
  },

  /**
   * 「这次邀请的是谁」：家里有两位以上服药人时必须显式点一位。
   * 分享卡上的 uid 决定对方首次加入时认领哪条档案 —— 猜错就是把妈妈的档案发给爸爸，
   * 两条绑定一起乱（所以宁可不发，也不默认选中第一位）。
   */
  onPickInvite(e: any) {
    const uid = e.currentTarget.dataset.uid || '';
    if (!uid || uid === this.data.inviteUid) return;
    const chip = (this.data.inviteChips || []).find((c: any) => c.uid === uid);
    if (!chip) return;
    const s = get();
    const path = s.family.inviteToken
      ? `/pages/launch/index?fid=${s.family.fid}&invite=${s.family.inviteToken}&role=patient&uid=${uid}`
      : '/pages/launch/index';
    // 点完立刻放开转发（needPick: false）—— 选人这个动作本身就是"可以发了"的信号
    this.setData({ inviteUid: uid, inviteName: chip.name, path, needPick: false });
    report('invite_target_pick', {});
  },

  /**
   * 多位候选但没点人时按了转发位 → 只提醒，不弹窗。
   * 这里不能弹 showModal：用户在"发邀请"这条主路径上，被打断一次就可能直接退出页面；
   * 一行 toast 足够把下一步说清。
   */
  onPickHint() {
    wx.showToast({ title: '先选这次邀请谁', icon: 'none' });
  },

  /** 复制邀请码（主邀请路径）：家人拿到码后在小程序首页「我有邀请码」里粘贴。
   *  比分享链接稳 —— 链接在小程序外可能点不开，码是一段纯文本，微信/短信随便发。 */
  onCopyCode() {
    const code = (this.data.token || '').trim();
    if (!code) {
      wx.showToast({ title: '邀请码还没加载出来', icon: 'none' });
      return;
    }
    wx.setClipboardData({
      data: code,
      success: () => {
        const s = get();
        report('invite_code_copy', { fid: s.family.fid });
        // P1-4：复制即视为"已发出邀请"，成员数没涨就显示待加入占位
        markInvited(s.family.fid, s.family.members.length);
        this.setData({ pendingInvite: true, pendingText: '邀请已发出，等家人加入', pendingResend: false });
        wx.showToast({ title: '邀请码已复制', icon: 'none' });
      },
    });
  },

  /** P1-4：再发一次（复制并重置计时） */
  onResend() {
    this.onCopyCode();
  },

  /** v1.7：子女给服药人开/关大字版默认（老人本机手动切换优先，这里只改默认值） */  async onToggleLarge(e: any) {
    const uid = e.currentTarget.dataset.uid;
    const checked = !!e.detail.value;
    if (!uid) return;
    const s = get();
    try {
      await call('patient', 'set_mode', { fid: s.family.fid, uid, mode: checked ? 'large' : 'normal' });
      await loadFamily();
      this.render();
      wx.showToast({ title: checked ? '已设为大字版' : '已设为普通版', icon: 'none' });
    } catch (err: any) {
      wx.showToast({ title: (err && err.msg) || '设置失败，稍后再试', icon: 'none' });
      this.render(); // 回滚开关
    }
  },

  /** 开关点击不要冒泡到行上的角色菜单 */
  noop() {},

  onReset() {
    if (!this.data.isAdmin) return;
    wx.showModal({
      title: '重发邀请码',
      content: '旧的邀请卡片会失效，已经加入的家人不受影响。',
      success: async (r) => {
        if (!r.confirm) return;
        try {
          const d: any = await call('family', 'reset_invite', { fid: get().family.fid });
          await loadFamily();
          this.render();
          wx.showToast({ title: `新邀请码 ${d.ivt}`, icon: 'none' });
        } catch (e) {
          wx.showToast({ title: '稍后再试', icon: 'none' });
        }
      },
    });
  },

  onShareAppMessage() {
    const s = get();
    report('share_click', { channel: 'wechat', from: 'invite' });
    // 还没点名（多位可认领）却转发了 —— 只可能是走了右上角「···」菜单，微信拦不住它。
    // 这样发出去的卡不带 uid，本质是"没说清邀谁"的邀请卡：不该以邀请卡的形式存在，
    // 所以降级成**普通入口**（家人看到普通欢迎页，不会加入这个家，也就不会多出一条空档案）。
    // 配套：这一档**不能** markInvited —— 卡里没有邀请码，家人根本不可能通过它进来，
    // 记成"已发出邀请"会让页面挂上「等待加入」的假状态，比不记更糟。
    if (this.data.needPick) {
      return { title: '一起盯着爸妈吃药', path: '/pages/launch/index' };
    }
    markInvited(s.family.fid, s.family.members.length);
    return {
      title: '一起盯着爸妈吃药',
      path: this.data.path,
    };
  },

  /** MISSED 授权（漏服告警，发给管理员）。
   *  合规 §6.3 原写「P42 加入家庭时请求」，但 join 是异步的 —— await 完就出了 TAP 同步栈，
   *  requestSubscribeMessage 必失败（不报错、只是框不弹）。所以挪到管理员进家庭页这张卡上，
   *  这是一个干净的独立 TAP。 */
  onSubscribeMissed() {
    report('subscribe_request', { scene: 'family', tmpl: 'MISSED', quota_before: get().user.quota.MISSED });
    requestOne('MISSED', 'family').then((res) => {
      this.setData({ showMissedCta: false });
      if (res.unconfigured) return;
      wx.showToast({ title: res.accepted ? '漏服时第一时间提醒你' : '没开启也能在药单里看到', icon: 'none' });
    });
  },

  onSubscribeMissedLater() {
    // 点了「先不用」本次进页面就不再提 —— 反复弹只会把人推去关总开关
    this.setData({ showMissedCta: false, missedLater: true });
  },

  /**
   * 管理员点成员那行 → 先选要做什么。
   * 三件事挤在一个 modal 里说不清，所以用原生 ActionSheet 分派：
   *   ① 改称呼（家庭成员分不清谁是谁的直接解法）
   *   ② 绑定服药人（把"这个人"和"这条药单"对起来，也是绑错后的救济口）
   *   ③ 管理员权限（原有能力，只对别人开放 —— 自降会被后端拦，不如别让点了再报错）
   */
  onMemberTap(e: any) {
    const openid = e.currentTarget.dataset.openid;
    const m = (this.data.members as any[]).find((x: any) => x.openid === openid);
    if (!m || !this.data.isAdmin) return;

    const actions: string[] = ['改称呼'];
    // 「我」这一行不给绑定入口：这里是"把某条药单指给某位家人"，而给自己绑别人的档案
    // 正是 2026-11-02 事故（爸爸的药变成"我的药"）的形状。自己要吃药走首页「我自己吃药」，
    // 那条路会复用属于本人的档案、没有就新建「我」，不会碰到别人。
    if (!m.isMe) actions.push(m.boundUid ? '改绑服药人' : '绑定服药人');
    if (!m.isMe) actions.push(m.role === 'admin' ? '取消管理员' : '设为管理员');

    wx.showActionSheet({
      itemList: actions,
      success: (r) => {
        if (r.tapIndex === 0) this.onRenameMember(m);
        else if (r.tapIndex === 1) this.onBindMember(m);
        else this.onSetRole(m);
      },
    });
  },

  /** 改称呼：原生可输入弹窗，一个框足够，别再叠一层自绘对话框 */
  onRenameMember(m: any) {
    wx.showModal({
      title: `怎么称呼${m.isMe ? '你自己' : ' TA'}`,
      editable: true,
      placeholderText: '比如：爸爸、姐姐、二叔',
      content: m.label || '',
      confirmText: '保存',
      success: async (r) => {
        if (!r.confirm) return;
        const label = (r.content || '').trim();
        if (label === m.label) return;
        try {
          await call('family', 'set_member_name', {
            fid: get().family.fid,
            openid: m.openid,
            label,
          });
          await loadFamily();
          this.render();
          wx.showToast({ title: label ? `已称呼为${label}` : '已恢复默认称呼', icon: 'none' });
        } catch (err: any) {
          // 1005 的具体原因（超长 / 内容不合规）已由 request 层 toast
        }
      },
    });
  },

  /**
   * 绑定服药人：把"这个人"和"这条药单"绑在一起。
   * 语义必须说清 —— 绑定后对方打开小程序看到的是**自己的**药单，不是全家看板。
   * 已绑给别人的档案不在客户端拦：服务端会 1005 并点名"已经绑给谁了"，
   * 单一真相源留在后端，前端别自己再判一遍（判错了就是静默覆盖别人的绑定）。
   */
  onBindMember(m: any) {
    // family.info 只下发 active 档案，归档的不会出现在这里
    const patients = get().family.patients || [];
    if (!patients.length) {
      wx.showToast({ title: '还没有服药档案，先去加一位', icon: 'none' });
      return;
    }
    const members = get().family.members || [];
    const nameOfOpenid = (openid: string) => {
      const mm: any = members.find((x: any) => x.openid === openid);
      return mm ? mm.label || mm.nick || '家人' : '家人';
    };
    const nameOf = (p: any) => p.alias || p.name || '未命名';

    // 未绑定的排前面：要绑的时候先看到能绑的。
    // ActionSheet 上限 6 项：已绑定时给「解除绑定」留一格，所以档案最多占 5 格。
    // 家里有 6 位以上服药人是极端情况，真遇到时"先解绑一条再加"仍能走通。
    const sorted = [...patients].sort((a: any, b: any) => (a.selfOpenid ? 1 : 0) - (b.selfOpenid ? 1 : 0));
    const slots = 6 - (m.boundUid ? 1 : 0);
    const shownPatients = sorted.slice(0, slots);
    const items = shownPatients.map((p: any) => {
      if (!p.selfOpenid) return nameOf(p);
      if (p.selfOpenid === m.openid) return `${nameOf(p)}（当前）`;
      return `${nameOf(p)}（已绑给${nameOfOpenid(p.selfOpenid)}）`;
    });
    if (m.boundUid) items.push('解除绑定');

    wx.showActionSheet({
      itemList: items,
      success: (r) => {
        if (items[r.tapIndex] === '解除绑定') {
          this.confirmUnbind(m);
          return;
        }
        const patient = shownPatients[r.tapIndex];
        if (!patient) return;
        if (patient.selfOpenid === m.openid) {
          wx.showToast({ title: '已经绑的就是这条', icon: 'none' });
          return;
        }
        this.confirmBind(m, patient);
      },
    });
  },

  confirmBind(m: any, patient: any) {
    const pname = patient.alias || patient.name || '未命名';
    wx.showModal({
      title: `把「${pname}」交给${m.displayName}？`,
      content: `绑定后这条药单就算${m.displayName}本人在吃，TA 打开小程序看到的是自己的药单，到点提醒也发给 TA。TA 原来绑的档案会自动解除，记录都留着。`,
      confirmText: '确认绑定',
      success: async (r) => {
        if (!r.confirm) return;
        try {
          await call('family', 'bind_member', {
            fid: get().family.fid,
            openid: m.openid,
            uid: patient.uid,
          });
          await loadFamily();
          this.render();
          wx.showToast({ title: '已绑定', icon: 'none' });
        } catch (err: any) {
          // 1005「已经绑给 XX 了」由 request 层 toast
        }
      },
    });
  },

  confirmUnbind(m: any) {
    wx.showModal({
      title: `解除${m.displayName}的药单？`,
      content: '解除后 TA 变回普通家人界面。药单和服药记录都不会删，家里其他人照常看。',
      confirmText: '解除',
      confirmColor: '#E0402F',
      success: async (r) => {
        if (!r.confirm) return;
        try {
          await call('family', 'bind_member', {
            fid: get().family.fid,
            openid: m.openid,
            uid: '',
          });
          await loadFamily();
          this.render();
          wx.showToast({ title: '已解除绑定', icon: 'none' });
        } catch (err: any) {
          /* 原因已由 request 层 toast */
        }
      },
    });
  },

  /** 角色：只做「管理员 / 成员」两档。
   *  elder 是"加入时选了我要自己吃药"的产物，不该在这里手动来回切 —— 切了会和他绑没绑服药人对不上。
   *  用 showModal 而不是 showActionSheet：这里必须说清"改了会怎样"。 */
  onSetRole(m: any) {
    if (!m || !m.canManage || m.isMe) return;

    const toAdmin = m.role !== 'admin';
    wx.showModal({
      title: m.displayName,
      content: toAdmin
        ? '设为管理员后，TA 也能加药、改时间、改漏服记录。'
        : '取消管理员后，TA 只能看药单和催吃药，改不了任何东西。',
      // confirmText 上限 4 字：写"设为管理员"整个弹窗会 fail，且不报错
      confirmText: toAdmin ? '确认授权' : '确认取消',
      confirmColor: toAdmin ? '#2E6BE6' : '#E0402F',
      success: async (r) => {
        if (!r.confirm) return;
        try {
          await call('family', 'update_role', {
            fid: get().family.fid,
            openid: m.openid,
            role: toAdmin ? 'admin' : 'member',
          });
          await loadFamily();
          this.render();
          wx.showToast({ title: toAdmin ? '已设为管理员' : '已取消管理员', icon: 'none' });
        } catch (err) {
          // 1005 的具体原因（比如"至少要留一个管理员"）已由 request 层 toast，不重复弹
        }
      },
    });
  },

  onLeave() {
    wx.showModal({
      title: '退出这个家庭',
      content: '退出后你就看不到这家的药单了。已经记录的服药历史会留着，家里其他人照常看。',
      confirmText: '退出',
      confirmColor: '#E0402F',
      success: async (r) => {
        if (!r.confirm) return;
        try {
          await call('family', 'leave', { fid: get().family.fid });
          // 退出后本地还留着这个家的 fid 与家庭列表：不清场的话，launch 的早退逻辑
          // 会把人直接送回首页，首页再拿旧 fid 请求 → 1002 → 反复重进，页面一直闪。
          resetSession();
          wx.showToast({ title: '已退出', icon: 'none' });
          // 不能留在本页：这里的数据已经不属于你了，交给启动流程重新决定去哪
          setTimeout(() => wx.reLaunch({ url: '/pages/launch/index' }), 600);
        } catch (e) {
          // 1005 的具体原因（比如"家庭里只有你"）已由 request 层 toast，这里不重复弹
        }
      },
    });
  },
});
