import { APP_VERSION, SUPPORT_EMAIL } from '../../../config/index';

Page({
  data: {
    version: APP_VERSION,
    /** 空串 = 客户还没提供真实邮箱，页面只留微信客服会话入口，不展示假地址 */
    supportEmail: SUPPORT_EMAIL,
    contactText: SUPPORT_EMAIL ? '微信会话 / 邮件' : '微信会话',
  },

  goLegal(e: any) {
    wx.navigateTo({ url: `/pages/legal/index?type=${e.currentTarget.dataset.type}` });
  },

  onCopyEmail() {
    if (!this.data.supportEmail) return;
    wx.setClipboardData({
      data: this.data.supportEmail,
      success: () => wx.showToast({ title: '邮箱已复制', icon: 'none' }),
    });
  },

  onShareAppMessage() {
    return { title: '给爸妈做个吃药提醒，一天三次，点一下就好', path: '/pages/launch/index' };
  },
});
