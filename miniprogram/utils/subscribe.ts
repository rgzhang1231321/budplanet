/**
 * utils/subscribe.ts —— 订阅授权四态状态机
 * 铁律：requestSubscribeMessage 必须在用户 TAP 的同步栈内调用（前置 await 必失败）；
 *      一次只申请 1 个模板；无论接受还是拒绝都要 notify.ack 上报（否则授权率永远 100%）。
 */
import { TMPL_IDS } from '../config/index';
import { call } from './request';
import { report } from './report';
import { set } from '../store/index';

type TmplKey = 'CHECKIN' | 'MISSED' | 'WEEKLY';
const ORDER: TmplKey[] = ['CHECKIN', 'MISSED', 'WEEKLY'];

let alwaysCache: Record<string, boolean> = {};
let mainSwitch = true;

/** app.onLaunch 调用：预热 getSetting 结果，避免 TAP 内 await 丢上下文 */
export function primeSubscribeState(): Promise<void> {
  return new Promise((resolve) => {
    wx.getSetting({
      withSubscriptions: true,
      success: (s: any) => {
        const item = (s.subscriptionsSetting && s.subscriptionsSetting.itemSettings) || {};
        alwaysCache = {};
        ORDER.forEach((k) => {
          alwaysCache[k] = !!TMPL_IDS[k] && item[TMPL_IDS[k]] === 'accept';
        });
        mainSwitch = (s.subscriptionsSetting || {}).mainSwitch !== false;
        resolve();
      },
      fail: () => resolve(),
    });
  });
}

export const isAlwaysAccept = (k: TmplKey) => !!alwaysCache[k];
export const isMainSwitchOn = () => mainSwitch;

export interface SubscribeResult {
  accepted: boolean;
  needSetting: boolean;
  unconfigured?: boolean;
  quota?: any;
}

/** 唯一授权入口。必须在 TAP 回调里同步调用。 */
export function requestOne(tmplKey: TmplKey, scene: string): Promise<SubscribeResult> {
  const tmplId = TMPL_IDS[tmplKey];
  if (!tmplId) {
    // 模板未配置：不假装成功，也不弹无效系统框
    report('subscribe_result', { scene, tmpl: tmplKey, err: 'unconfigured' });
    return Promise.resolve({ accepted: false, needSetting: false, unconfigured: true });
  }
  return new Promise((resolve) => {
    wx.requestSubscribeMessage({
      tmplIds: [tmplId],
      success(res: any) {
        const accepted = res[tmplId] === 'accept';
        const ban = res[tmplId] === 'ban';
        report('subscribe_result', { scene, tmpl: tmplKey, accepted, ban });
        if (accepted) alwaysCache[tmplKey] = true;
        call('notify', 'ack', {
          tmpl_key: tmplKey,
          accepted: accepted ? [tmplKey] : [],
          requested: 1,
        })
          .then((d: any) => {
            // 服务端回传的三池额度直接写回 store：不然这次授权后要等下次冷启，
            // 页面上「额度≤0」的判据才变准。
            if (d && d.quota) set('user.quota', d.quota);
            resolve({ accepted, needSetting: !accepted && ban, quota: d && d.quota });
          })
          .catch(() => resolve({ accepted, needSetting: !accepted && ban }));
      },
      fail(err: any) {
        const off = /ban|20004|auth den/i.test(err.errMsg || '');
        report('subscribe_result', { scene, tmpl: tmplKey, err: off ? 'main_off' : 'fail' });
        call('notify', 'ack', { tmpl_key: tmplKey, accepted: [], requested: 1 }).catch(() => {});
        resolve({ accepted: false, needSetting: off });
      },
    });
  });
}

/** S2 静默续订：仅当已勾选「总是保持」时，在打卡 TAP 内调用 —— 无弹窗，不违反设计铁律 */
export function silentRenew(tmplKey: TmplKey) {
  if (!alwaysCache[tmplKey] || !TMPL_IDS[tmplKey]) return;
  wx.requestSubscribeMessage({
    tmplIds: [TMPL_IDS[tmplKey]],
    success: (res: any) => {
      if (res[TMPL_IDS[tmplKey]] === 'accept') {
        call('notify', 'ack', { tmpl_key: tmplKey, accepted: [tmplKey], requested: 1 }).catch(() => {});
      }
    },
    fail: () => {},
  });
}
