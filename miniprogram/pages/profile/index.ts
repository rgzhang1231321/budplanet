/** pages/profile —— P40 我的（双模切换、家庭、额度、合规入口） */
import { bootstrap, setMode } from '../../store/user';
import { get, isElder, on } from '../../store/index';
import { call } from '../../utils/request';
import { unlockByAd } from '../../utils/ad';
import { APP_VERSION } from '../../config/index';

Page({
  data: {
    elder: false,
    nickname: '我',
    initial: '我',
    familyName: '',
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
    this.setData({
      elder: isElder(),
      nickname: s.user.nickname || '我',
      initial: (s.user.nickname || '我').slice(0, 1),
      familyName: s.family.name || '我家',
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
    unlockByAd('med_slot', (d: any) => {
      this.setData({ showNudge: false, credits: (d && d.credits) || this.data.credits + 1 });
      wx.showToast({ title: '名额已到账', icon: 'none' });
    });
  },

  onNudgeClose() {
    this.setData({ showNudge: false });
  },

  go(e: any) {
    wx.navigateTo({ url: e.currentTarget.dataset.url });
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
    wx.showModal({
      title: '注销并删除全部数据',
      content: '将永久删除你的账号、药品与服药记录，家人的历史记录会去除你的身份关联。此操作不可恢复。',
      confirmText: '确认注销',
      confirmColor: '#E0402F',
      success: (r) => {
        if (!r.confirm) return;
        call('user', 'erase', { confirm: true })
          .then(() => {
            wx.showToast({ title: '已删除', icon: 'none' });
            setTimeout(() => wx.reLaunch({ url: '/pages/launch/index' }), 700);
          })
          .catch(() => wx.showToast({ title: '稍后再试', icon: 'none' }));
      },
    });
  },
});
