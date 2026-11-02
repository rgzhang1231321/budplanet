/**
 * packageSettings/pages/settings —— P43 / E43
 * 老人端 6 行平铺大字（不做分组列表）；子女端分组列表。
 * 订阅授权：一次只申请 1 个模板，必须用户 TAP 触发。
 */
import { bootstrap, setMode, loadQuota } from '../../../store/user';
import { get, isElder, on, set } from '../../../store/index';
import { requestOne, isMainSwitchOn } from '../../../utils/subscribe';
import { quietHoursText, DEFAULT_QUIET_HOURS } from '../../../utils/ad';
import { report } from '../../../utils/report';
import { navTo } from '../../../utils/nav';

Page({
  data: {
    /** 初值取本地偏好（不是 false）：wxml 用 wx:if 整块换版式，
     *  初值错了第一帧就是另一套 DOM，接不住 onLoad 里的同步 render。 */
    elder: isElder(),
    sound: true,
    quiet: true,
    /** 安静时段文案：只认 config.quietHours（读不到用 utils/ad 里那唯一一份默认值），不在这里写死 */
    quietText: `${DEFAULT_QUIET_HOURS.start} - ${DEFAULT_QUIET_HOURS.end}`,
    reduceMotion: false,
    modeLabel: '普通版',
    quota: 0,
    quotaText: '',
  },

  onLoad() {
    // 首帧必须同步出图。elder 只取决于本地 modeOverride（storage 里的偏好），
    // 不依赖登录/网络；放到 onShow 的 await 之后才 setData，第一次渲染就会落到
    // data 里的 elder:false —— 大字版用户先看到一帧小字版，再闪成大字版。
    // 分包页每次 navigateTo 都是新实例，所以这个闪是必现的（tab 页只冷启一次）。
    this.render();
    this._off = on(() => this.render());
  },
  onUnload() {
    if (this._off) this._off();
  },

  async onShow() {
    await bootstrap().catch(() => {});
    await loadQuota();
    this.render();
  },

  render() {
    const s = get();
    const q = s.user.quota.CHECKIN;
    this.setData({
      elder: isElder(),
      sound: s.user.sound,
      quiet: s.user.quiet,
      quietText: quietHoursText(),
      reduceMotion: s.user.reduceMotion,
      modeLabel: isElder() ? '大字版' : '普通版',
      quota: q,
      quotaText: q > 0 ? `还能提醒 ${q} 次` : '提醒额度用完了，点一下补上',
    });
  },

  onSound(e: any) {
    set('user.sound', e.detail.value);
  },
  onQuiet(e: any) {
    set('user.quiet', e.detail.value);
  },
  onReduceMotion(e: any) {
    set('user.reduceMotion', e.detail.value);
  },

  onToggleMode() {
    const next = isElder() ? 'guardian' : 'elder';
    setMode(next);
    wx.showToast({ title: next === 'elder' ? '已切到大字版' : '已切回普通版', icon: 'none' });
    setTimeout(() => wx.reLaunch({ url: '/pages/home/index' }), 500);
  },

  /** 订阅授权：必须在 TAP 同步栈内调用 */
  onSubscribe() {
    report('subscribe_request', { scene: 'settings', tmpl: 'CHECKIN', quota_before: this.data.quota });
    if (!isMainSwitchOn()) {
      wx.showModal({
        title: '提醒总开关关着',
        content: '打开「接收订阅消息」后才能收到到点提醒。',
        confirmText: '去打开',
        success: (r) => {
          if (r.confirm) wx.openSetting({});
        },
      });
      return;
    }
    requestOne('CHECKIN', 'settings').then((res) => {
      if (res.unconfigured) {
        wx.showToast({ title: '提醒模板待配置', icon: 'none' });
        return;
      }
      if (res.accepted) {
        wx.showToast({ title: '提醒已开启', icon: 'success' });
        this.render();
      } else if (res.needSetting) {
        wx.showModal({
          title: '需要你点一下同意',
          content: '你之前拒绝过提醒。可以到设置里重新允许，我们不会再弹一次。',
          confirmText: '去设置',
          success: (r) => {
            if (r.confirm) wx.openSetting({});
          },
        });
      } else {
        wx.showToast({ title: '没开启也能在打开时看到今日用药', icon: 'none' });
      }
    });
  },

  go(e: any) {
    navTo(e.currentTarget.dataset.url);
  },

  onCall() {
    wx.showModal({
      title: '联系客服',
      content: '在小程序「关于」页点「联系客服」，我们会在微信里直接回你。',
      showCancel: false,
    });
  },
});
