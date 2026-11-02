/**
 * packageSettings/pages/quota —— P0-2 提醒额度
 * 微信一次性订阅：授权一次 = 发一条。额度归零后后端直接跳过推送，前端收不到任何错误，
 * 用户会以为"我设了提醒"，实际第二天起就收不到。这一页把 CHECKIN / MISSED / WEEKLY
 * 三个模板的额度、说明、补授权入口放在一起，让"静默失效"变成可见、可补。
 */
import { bootstrap, loadQuota } from '../../../store/user';
import { get } from '../../../store/index';
import { requestOne } from '../../../utils/subscribe';
import { report } from '../../../utils/report';

type TmplKey = 'CHECKIN' | 'MISSED' | 'WEEKLY';

Page({
  data: {
    checkin: 0,
    rows: [] as any[],
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

  onTopupCheckin() {
    this.onTopup({ currentTarget: { dataset: { key: 'CHECKIN' } } });
  },
});
