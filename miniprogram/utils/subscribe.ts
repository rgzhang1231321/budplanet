/**
 * utils/subscribe.ts —— 订阅授权四态状态机
 * 铁律：requestSubscribeMessage 必须在用户 TAP 的同步栈内调用（前置 await 必失败）；
 *      一次申请 1 个模板（打卡等日常场景），额度页可一次弹 3 个（微信上限）；
 *      无论接受还是拒绝都要 notify.ack 上报（否则授权率永远 100%）。
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

export interface MultiSubscribeResult {
  /** 用户勾选了哪几个模板（每个 = +1 条额度） */
  accepted: TmplKey[];
  /** 没勾 / 被拒的模板 */
  rejected: TmplKey[];
  unconfigured?: boolean;
}

/**
 * 一次弹窗补多种提醒（额度页专用）。
 *
 * 微信允许一次 requestSubscribeMessage 传**最多 3 个模板**，用户在同一个弹窗里逐项勾选，
 * 勾中的每项各 +1 条额度 —— 所以「补充提醒」不必一条一条点三次。
 *
 * ⚠️ 与 requestOne 一样必须在 TAP 同步栈内调用：本函数第一件事就是 requestSubscribeMessage，
 *    中间不能有 await，否则微信判定非用户手势、直接 fail。
 * ⚠️ 每个模板要单独 ack：服务端 quota 是按 tmpl_key 一行一行存的，合成一次会上报错。
 */
export function requestMany(tmplKeys: TmplKey[], scene: string): Promise<MultiSubscribeResult> {
  const keys = tmplKeys.filter((k) => !!TMPL_IDS[k]);
  if (!keys.length) {
    report('subscribe_result', { scene, tmpl: tmplKeys.join(','), err: 'unconfigured' });
    return Promise.resolve({ accepted: [], rejected: [], unconfigured: true });
  }
  return new Promise((resolve) => {
    wx.requestSubscribeMessage({
      tmplIds: keys.map((k) => TMPL_IDS[k]),
      success(res: any) {
        const accepted = keys.filter((k) => res[TMPL_IDS[k]] === 'accept');
        const rejected = keys.filter((k) => accepted.indexOf(k) < 0);
        keys.forEach((k) => {
          const ok = accepted.indexOf(k) >= 0;
          report('subscribe_result', { scene, tmpl: k, accepted: ok, ban: res[TMPL_IDS[k]] === 'ban' });
          if (ok) alwaysCache[k] = true;
          // 逐模板 ack：服务端一个 tmpl_key 一行，拒授权也要报，否则授权率恒为 100%
          call('notify', 'ack', { tmpl_key: k, accepted: ok ? [k] : [], requested: 1 })
            .then((d: any) => {
              if (d && d.quota) set('user.quota', d.quota);
            })
            .catch(() => {});
        });
        resolve({ accepted, rejected });
      },
      fail(err: any) {
        const off = /ban|20004|auth den/i.test(err.errMsg || '');
        keys.forEach((k) => {
          report('subscribe_result', { scene, tmpl: k, err: off ? 'main_off' : 'fail' });
          call('notify', 'ack', { tmpl_key: k, accepted: [], requested: 1 }).catch(() => {});
        });
        resolve({ accepted: [], rejected: keys });
      },
    });
  });
}

/** S2 静默续订：仅当已勾选「总是保持」时，在打卡 TAP 内调用 —— 无弹窗，不违反设计铁律 */export function silentRenew(tmplKey: TmplKey) {
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
