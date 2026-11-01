import { APP_VERSION } from '../../../config/index';

Page({
  data: { version: APP_VERSION },

  goLegal(e: any) {
    wx.navigateTo({ url: `/pages/legal/index?type=${e.currentTarget.dataset.type}` });
  },

  onCopy() {
    wx.setClipboardData({
      data: 'support@example.com',
      success: () => wx.showToast({ title: '邮箱已复制', icon: 'none' }),
    });
  },

  onShareAppMessage() {
    return { title: '给爸妈做个吃药提醒，一天三次，点一下就好', path: '/pages/launch/index' };
  },
});
