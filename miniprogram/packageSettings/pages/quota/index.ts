/**
 * packageSettings/pages/quota —— P0-2 提醒额度
 * 微信一次性订阅：授权一次 = 发一条。额度归零后后端直接跳过推送，前端收不到任何错误，
 * 用户会以为"我设了提醒"，实际第二天起就收不到。这一页把 CHECKIN / MISSED / WEEKLY
 * 三个模板的额度、说明、补授权入口放在一起，让"静默失效"变成可见、可补。
 */
import { bootstrap, loadQuota } from '../../../store/user';
import { get, on } from '../../../store/index';
import { requestOne, requestMany } from '../../../utils/subscribe';
import { report } from '../../../utils/report';

type TmplKey = 'CHECKIN' | 'MISSED' | 'WEEKLY';

const LABEL: Record<TmplKey, string> = {
  CHECKIN: '到点提醒',
  MISSED: '漏服提醒',
  WEEKLY: '每周周报',
};

Page({
  data: {
    checkin: 0,
    rows: [] as any[],
  },

  /**
   * 本页**必须**订阅 store，不能只在 TAP 回调里 render 一次。
   * 原因：一次「一键补充」= 3 个独立 notify.ack、3 份独立回包，回包顺序不确定；
   * 谁先回来谁把 user.quota 覆盖一次。只在点完那一刻 render，读到的是点之前的快照，
   * 页面就停在旧数字上（真机反馈：「补了三种，只有到点提醒的次数涨了」）。
   */
  onLoad() {
    (this as any)._off = on((_s, changed) => {
      if (changed === 'user.quota' || changed === 'reset') this.render();
    });
  },

  onUnload() {
    const off = (this as any)._off;
    if (typeof off === 'function') off();
    (this as any)._off = null;
  },

  async onShow() {
    await bootstrap().catch(() => {});
    await loadQuota();
    this.render();
  },

  render() {
    const q = get().user.quota;
    const mk = (key: TmplKey, name: string, desc: string) => {
      const n = q[key] || 0;
      return {
        key,
        name,
        desc,
        n,
        text: n > 0 ? `剩 ${n} 次` : '已用完',
        tone: n <= 0 ? 'bad' : n <= 1 ? 'warn' : 'ok',
      };
    };
    this.setData({
      checkin: q.CHECKIN || 0,
      rows: [
        mk('CHECKIN', '到点提醒', '到点微信提醒你吃药'),
        mk('MISSED', '漏服提醒', '超时没打卡，告诉家人'),
        mk('WEEKLY', '每周周报', '每周日一份用药小结'),
      ],
    });
  },

  /** 每个模板单独补授权：requestSubscribeMessage 必须在 TAP 同步栈内调用 */
  onTopup(e: any) {
    const key = e.currentTarget.dataset.key as TmplKey;
    if (!key) return;
    report('subscribe_request', { scene: 'quota', tmpl: key, quota_before: (get().user.quota as any)[key] || 0 });
    requestOne(key, 'quota').then((res) => {
      if (res.unconfigured) {
        wx.showToast({ title: '提醒模板待配置', icon: 'none' });
        return;
      }
      wx.showToast({
        title: res.accepted ? '已补充 1 条' : '没开启也能在小程序里看到',
        icon: 'none',
      });
      // requestOne 内部已把 ack 回传的 quota 写回 store，这里重渲染即可
      this.render();
    });
  },

  /**
   * 一键补三种：微信一次弹窗最多 3 个模板，勾中的每项各 +1 条。
   * 主角是"补额度"，用户在这页的意图就是补，没必要让他一条一条点三次。
   */
  async onTopupAll() {
    report('subscribe_request', { scene: 'quota_all', tmpl: 'ALL', quota_before: get().user.quota });
    // 注意：requestMany 会同步发起 wx.requestSubscribeMessage（保住 TAP 手势），
    // 所以这行之前不能有 await，否则微信判非用户手势、直接 fail。
    const res = await requestMany(['CHECKIN', 'MISSED', 'WEEKLY'], 'quota_all');
    if (res.unconfigured) {
      wx.showToast({ title: '提醒模板待配置', icon: 'none' });
      return;
    }
    const n = res.accepted.length;
    wx.showToast({
      title: n === 3 ? '三种提醒各补 1 条' : n > 0 ? `已补充 ${res.accepted.map((k) => LABEL[k]).join('、')}` : '没开启也能在小程序里看到',
      icon: 'none',
    });
    // 等三个 ack 都落库，再拉一次服务端真相：中途某个 ack 失败、或回包乱序
    // 把 quota 覆盖成较旧的快照，都以这次 GET 为准。store 订阅会顺带重渲染。
    await res.settled;
    await loadQuota();
    this.render();
  },
});
