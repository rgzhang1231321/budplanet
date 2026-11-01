import { navRightPad, statusBarHeight } from '../../../utils/theme';

Component({
  options: { multipleSlots: true, addGlobalClass: true },
  properties: {
    title: { type: String, value: '' },
    subtitle: { type: String, value: '' },
    back: { type: Boolean, value: false },
    /** 不留右侧胶囊位（无右侧动作时仍留位，保证标题不被胶囊压住） */
    reserveCapsule: { type: Boolean, value: true },
    bg: { type: String, value: '' },
  },
  data: {
    statusBar: 20,
    rightPad: 100,
  },
  lifetimes: {
    attached() {
      this.setData({
        statusBar: statusBarHeight(),
        rightPad: this.data.reserveCapsule ? navRightPad() : 8,
      });
    },
  },
  methods: {
    goBack() {
      const pages = getCurrentPages();
      if (pages.length > 1) wx.navigateBack();
      else wx.reLaunch({ url: '/pages/home/index' });
    },
  },
});
