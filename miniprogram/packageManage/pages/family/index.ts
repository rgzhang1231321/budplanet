/** packageManage/pages/family —— P41 邀请家人 / 成员管理 */
import { bootstrap, loadFamily, resetSession } from '../../../store/user';
import { get } from '../../../store/index';
import { call } from '../../../utils/request';
import { report } from '../../../utils/report';
import { requestOne } from '../../../utils/subscribe';

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
    this.setData({
      name: s.family.name || '我家',
      token: s.family.inviteToken,
      isAdmin,
      canLeave: s.family.members.length > 1,
      // 非管理员没有邀请码（family.info 只对 admin 下发），拼出来的 `invite=` 是空串，
      // 落地页会当成"没有邀请"走普通欢迎页 —— 与其发一张假邀请卡，不如分享普通入口。
      // 带上 fid：落地页判"你已经在里面了"用的是 fid（比邀请码可靠）—— 邀请码只对
      // admin 下发，家里其它成员点开这张卡时手里没有码，只能靠 fid 认出"这就是我的家"。
      path:
        isAdmin && s.family.inviteToken
          ? `/pages/launch/index?fid=${s.family.fid}&invite=${s.family.inviteToken}&role=patient`
          : '/pages/launch/index',
      // MISSED 的接收方是家庭管理员（子女），所以只给管理员看这一张卡；
      // 家里还没有服药人时不显示——"谁没吃药"这件事此刻还不存在。
      showMissedCta:
        !this.data.missedLater && isAdmin && (quota.MISSED || 0) <= 0 && (s.family.patients || []).length > 0,
      members: s.family.members.map((m) => ({
        openid: m.openid,
        nick: m.nick,
        initial: (m.nick || '家').slice(0, 1),
        role: m.role,
        roleText: roleMap[m.role] || '成员',
        isMe: m.openid === s.user.openid,
        // 只有管理员能改别人的角色；自己那行不给入口（自降会被后端拦，不如别让点了再报错）
        canManage: isAdmin && m.openid !== s.user.openid,
      })),
    });
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
        report('invite_code_copy', { fid: get().family.fid });
        wx.showToast({ title: '邀请码已复制', icon: 'none' });
      },
    });
  },

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
    report('share_click', { channel: 'wechat', from: 'invite' });
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

  /** 管理员点别人那行 → 调角色。只做「管理员 / 成员」两档：
   *  elder 是"加入时选了我要自己吃药"的产物，不该在这里手动来回切 —— 切了会和他绑没绑服药人对不上。
   *  用 showModal 而不是 showActionSheet：只有一个选项时 ActionSheet 太重，而且这里必须说清"改了会怎样"。 */
  onMemberTap(e: any) {
    const openid = e.currentTarget.dataset.openid;
    const m = this.data.members.find((x: any) => x.openid === openid);
    if (!m || !m.canManage) return;

    const toAdmin = m.role !== 'admin';
    wx.showModal({
      title: m.nick,
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
            openid,
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
