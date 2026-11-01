import { navRightPad, statusBarHeight } from '../../../utils/theme';

Component({
  options: { multipleSlots: true, addGlobalClass: true },
  properties: {
    title: { type: String, value: '' },
    subtitle: { type: String, value: '' },
    back: { type: Boolean, value: false },
    /** 为 true 时不执行默认 navigateBack，只抛 back 事件交给页面处理。
     *  多步表单需要「返回上一步」而不是退出页面时用它（medicine-form）。 */
    interceptBack: { type: Boolean, value: false },
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
      if (this.data.interceptBack) {
        this.triggerEvent('back');
        return;
      }
      const pages = getCurrentPages();
      if (pages.length > 1) wx.navigateBack();
      else wx.reLaunch({ url: '/pages/home/index' });
    },
  },
});
