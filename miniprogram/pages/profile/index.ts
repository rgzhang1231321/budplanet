/** pages/profile —— P40 我的（双模切换、家庭、额度、合规入口） */
import { bootstrap, resetSession, setMode, switchFamily } from '../../store/user';
import { get, isElder, on } from '../../store/index';
import { call } from '../../utils/request';
import { unlockByAd, PRODUCT } from '../../utils/ad';
import { navTo } from '../../utils/nav';
import { APP_VERSION } from '../../config/index';

Page({
  data: {
    elder: false,
    nickname: '我',
    initial: '我',
    familyName: '',
    /** P1-3：家庭数量与"当前：张家 · 共 2 个家"文案 */
    familyCount: 0,
    familySub: '',
    roleText: '',
    credits: 0,
    medCount: 0,
    limit: 3,
    showNudge: false,
    version: APP_VERSION,
  },

  onLoad() {
    this._off = on(() => this.render());
  },

  onUnload() {
    if (this._off) this._off();
  },

  async onShow() {
    const tb = typeof this.getTabBar === 'function' ? (this.getTabBar() as any) : null;
    if (tb && tb.sync) tb.sync(3);
    await bootstrap().catch(() => {});
    this.render();
  },

  render() {
    const s = get();
    const roleMap: Record<string, string> = { admin: '管理员', member: '家庭成员', elder: '服药人' };
    const familyCount = (s.family.all || []).length;
    const familyName = s.family.name || '我家';
    this.setData({
      elder: isElder(),
      nickname: s.user.nickname || '我',
      initial: (s.user.nickname || '我').slice(0, 1),
      familyName,
      familyCount,
      familySub: familyCount > 1 ? `${familyName} · 共 ${familyCount} 个家` : familyName,
      roleText: `${roleMap[s.family.role] || '家庭成员'} · ${s.family.members.length} 人在这个家`,
      credits: s.user.entitlement.credits || 0,
      limit: s.user.config.freeMedLimit,
    });
  },

  /** 界面模式：切到老人端后 page 底色走 API，字号靠 .p-root.elder 覆写 */
  onToggleMode(e: any) {
    const on = e.detail.value;
    if (!on && isElder()) {
      wx.showModal({
        title: '切回普通版？',
        content: '大字版更适合老人使用，切回后字号会变小。',
        confirmText: '切回', // 微信 confirmText 上限 4 字，超了整个弹窗会 fail 静默不显示
        success: (r) => {
          if (r.confirm) {
            setMode('guardian');
            setTimeout(() => wx.reLaunch({ url: '/pages/home/index' }), 200);
          }
        },
        fail: () => {},
      });
      return;
    }
    setMode(on ? 'elder' : 'guardian');
    setTimeout(() => wx.reLaunch({ url: '/pages/home/index' }), 200);
  },

  onUnlockMore() {
    this.setData({ showNudge: true });
  },

  onNudgeUnlock() {
    unlockByAd(PRODUCT.MED_SLOT, (d: any) => {
      this.setData({ showNudge: false, credits: (d && d.credits) || this.data.credits + 1 });
      wx.showToast({ title: '名额已到账', icon: 'none' });
    });
  },

  onNudgeClose() {
    this.setData({ showNudge: false });
  },

  /** P1-3：多家庭切换（>1 个家才可点） */
  onSwitchFamily() {
    const s = get();
    const list = s.family.all || [];
    if (list.length <= 1) {
      wx.showToast({ title: '只加入了 1 个家庭', icon: 'none' });
      return;
    }
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

  go(e: any) {
    navTo(e.currentTarget.dataset.url);
  },

  onExport() {
    call('user', 'export', {})
      .then((d: any) => {
        // 链接必须能复制出去：showModal 正文不能长按选中，只把 URL 打在弹窗里等于给不了用户。
        // 用 complete 而不是 success：某些环境下 setClipboardData 既不回 success 也不回 fail，
        // 只挂 success 会导致"点了没反应"，那比复制失败更难排查。
        let copied = false;
        wx.setClipboardData({
          data: d.fileUrl,
          success: () => {
            copied = true;
          },
          complete: () => {
            wx.showModal({
              title: '导出我的数据',
              content: copied
                ? '下载链接已复制，24 小时内有效。在浏览器里打开即可下载你的全部用药记录。'
                : `链接 24 小时内有效：\n${d.fileUrl}`,
              showCancel: false,
              confirmText: '知道了',
            });
          },
        });
      })
      .catch(() => wx.showToast({ title: '稍后再试', icon: 'none' }));
  },

  onErase() {
    const s = get();
    // 家里只剩自己时，注销会把家庭变成"没有成员的孤儿"：药品/记录还在库里，
    // 但没有任何账号能再打开，等于永久失联。文案必须和"家人还在"分开说，
    // 否则用户以为只是自己退出，回头发现整个家的药单再也进不去。
    const alone = (s.family.members || []).length <= 1;
    wx.showModal({
      title: '注销并删除全部数据',
      content: alone
        ? '你是这个家唯一的成员。注销后这个家的药品、服药记录、周报将没有任何账号能再打开，也无法恢复。确定继续？'
        : '将永久删除你的账号，家人仍可继续查看这个家的药单与记录。此操作不可恢复。',
      confirmText: '确认注销',
      confirmColor: '#E0402F',
      success: (r) => {
        if (!r.confirm) return;
        call('user', 'erase', { confirm: true })
          .then(() => {
            // 账号已经没了，但本地 token / family 列表 / bootstrap 缓存都还在：
            // 直接跳启动页会被 launch 的「已有家庭 → 回首页」早退逻辑送回首页，
            // 首页再拿旧 fid 发请求 → 全部 1002 → 反复 reLaunch，页面一直闪。
            // 必须先清场，让 launch 真的重新登录。
            resetSession();
            wx.showToast({ title: '已删除', icon: 'none' });
            setTimeout(() => wx.reLaunch({ url: '/pages/launch/index' }), 700);
          })
          .catch(() => wx.showToast({ title: '稍后再试', icon: 'none' }));
      },
    });
  },
});
