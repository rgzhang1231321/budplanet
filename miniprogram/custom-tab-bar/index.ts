/**
 * custom-tab-bar —— 自绘 tabBar
 * 为什么不用原生 + hideTabBar：会"先出现再消失"闪一下，老人会去点，点进药品管理就迷路。
 * elder 模式根本不渲染，零闪烁。
 */
import { get, isElder, on } from '../store/index';

const TABS = [
  { path: '/pages/home/index', text: '今日', icon: 'home' },
  { path: '/pages/medicine/index', text: '药品', icon: 'pill' },
  { path: '/pages/weekly/index', text: '周报', icon: 'report' },
  { path: '/pages/profile/index', text: '我的', icon: 'me' },
];

Component({
  options: { addGlobalClass: true },
  data: { selected: 0, tabs: TABS, hidden: false },
  lifetimes: {
    attached() {
      this.setData({ hidden: isElder() });
      this._off = on(() => {
        const h = isElder();
        if (h !== this.data.hidden) this.setData({ hidden: h });
      });
    },
    detached() {
      if (this._off) this._off();
    },
  },
  methods: {
    onTap(e: any) {
      const i = e.currentTarget.dataset.index;
      if (i === this.data.selected) return;
      wx.switchTab({ url: TABS[i].path });
      this.setData({ selected: i });
    },
    /** 页面 onShow 调用：同步选中态 */
    sync(i: number) {
      this.setData({ selected: i, hidden: isElder() });
    },
  },
});
