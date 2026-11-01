/**
 * utils/ad.ts —— 广告频控 + 激励视频单例
 * 铁律：打卡主流程（提醒 → 打卡按钮 → 成功动效）0 广告；开关只读后端下发的 config。
 */
import { AD_UNITS, USE_MOCK } from '../config/index';
import { call } from './request';
import { report } from './report';
import { dateCN } from './time';

const CFG = {
  coolSec: 30,
  cDayMax: 3,
  aDayMax: 6,
  bDayMax: 8,
  bElderDayMax: 4,
  dDayMax: 2,
  quiet: [20, 7],
  afterCheckinMin: 10,
  elderInterstitial: false,
  adOn: false,
};

let cfg = { ...CFG };
let lastCheckinAt = 0;

export function applyRemoteConfig(c: any) {
  if (!c) return;
  cfg = {
    ...CFG,
    ...c,
    coolSec: c.interstitialCool !== undefined ? c.interstitialCool : CFG.coolSec,
    cDayMax: c.interstitialDayMax !== undefined ? c.interstitialDayMax : CFG.cDayMax,
    elderInterstitial: !!(c.adSwitch && c.adSwitch.elderInterstitial),
    adOn: c.adOn !== undefined ? c.adOn : CFG.adOn,
  };
}

export const markCheckinDone = () => {
  lastCheckinAt = Date.now();
};
export const isAdOn = () => cfg.adOn;

const K = 'ad_state_v1';
interface AdState {
  dayCN: string;
  a: number;
  b: number;
  c: number;
  d: number;
  cAt: number;
}

function st(): AdState {
  let s: AdState | null = null;
  try {
    s = wx.getStorageSync(K) || null;
  } catch (e) {
    s = null;
  }
  const today = dateCN();
  if (!s || s.dayCN !== today) return { dayCN: today, a: 0, b: 0, c: 0, d: 0, cAt: 0 };
  return s;
}

function inc(k: 'a' | 'b' | 'c' | 'd') {
  const s = st();
  s[k] = (s[k] || 0) + 1;
  if (k === 'c') s.cAt = Date.now();
  try {
    wx.setStorageSync(K, s);
  } catch (e) {
    /* ignore */
  }
}

export const allowA = () => cfg.adOn && (st().a || 0) < cfg.aDayMax;
export const allowB = (mode: string) => cfg.adOn && (st().b || 0) < (mode === 'elder' ? cfg.bElderDayMax : cfg.bDayMax);
export const allowD = () => cfg.adOn && (st().d || 0) < cfg.dDayMax;
export function markShown(k: 'a' | 'b' | 'c' | 'd') {
  inc(k);
}

let interstitial: any = null;

/** 插屏 C：仅子女端、冷却 + 日上限 + 时段屏蔽 + 打卡后 10 分钟不弹 */
export function showC(mode: string): Promise<boolean> {
  const s = st();
  const h = new Date(Date.now() + 8 * 3600e3).getUTCHours();
  const quiet = h >= cfg.quiet[0] || h < cfg.quiet[1];
  const ok =
    cfg.adOn &&
    mode === 'guardian' &&
    !quiet &&
    (s.c || 0) < cfg.cDayMax &&
    Date.now() - (s.cAt || 0) > cfg.coolSec * 1000 &&
    Date.now() - lastCheckinAt > cfg.afterCheckinMin * 60000 &&
    !!AD_UNITS.c;
  if (!ok) return Promise.resolve(false);
  if (!interstitial) {
    interstitial = wx.createInterstitialAd({ adUnitId: AD_UNITS.c });
    interstitial.onError(() => {
      interstitial = null;
    });
  }
  inc('c');
  return Promise.resolve(interstitial.show())
    .then(() => true)
    .catch(() => false);
}

let rewardAd: any = null;
let pending: { productId: string; onGranted: (d: any) => void } | null = null;

function ensureReward() {
  if (rewardAd || !AD_UNITS.d) return rewardAd;
  rewardAd = wx.createRewardedVideoAd({ adUnitId: AD_UNITS.d, multiton: false });
  rewardAd.onClose((res: any) => {
    const p = pending;
    pending = null;
    if (res && res.isEnded) grant(p);
    else wx.showToast({ title: '视频没看完，名额还没到账', icon: 'none' });
  });
  rewardAd.onError(() => report('ad_error', { unit: 'd', code: 'reward' }));
  return rewardAd;
}

function grant(p: { productId: string; onGranted: (d: any) => void } | null) {
  if (!p) return;
  inc('d');
  call('ad', 'unlock', { product_id: p.productId, ad_trace: `${Date.now()}` })
    .then((d: any) => {
      if (!d || !d.granted) {
        wx.showToast({ title: '解锁失败，请重试', icon: 'none' });
        return;
      }
      report('reward_grant', { product: p.productId });
      rememberUnlock(p.productId);
      p.onGranted(d);
    })
    .catch(() => wx.showToast({ title: '网络不好，稍后再试', icon: 'none' }));
}

/** 解锁记录本地留痕（P44 展示用；权益本身存后端，前端只渲染 granted） */
const UL = 'unlock_log_v1';
function rememberUnlock(productId: string) {
  try {
    const list = wx.getStorageSync(UL) || [];
    list.unshift({
      id: `${productId}_${Date.now()}`,
      kind: productId,
      at: Date.now(),
    });
    wx.setStorageSync(UL, list.slice(0, 50));
  } catch (e) {
    /* ignore */
  }
}

export function unlockLog(): { id: string; kind: string; at: number }[] {
  try {
    return wx.getStorageSync(UL) || [];
  } catch (e) {
    return [];
  }
}

/** 激励视频解锁（D/E 共用）；未开通广告位时走演示分支，保证流程可验收 */
export function unlockByAd(productId: string, onGranted: (d: any) => void) {
  if (!AD_UNITS.d) {
    wx.showModal({
      title: '广告位未开通',
      content: USE_MOCK ? '当前为演示环境：跳过视频直接发放权益，便于验收解锁流程。' : '当前位置还没有广告填充，稍后再试。',
      confirmText: '模拟看完',
      cancelText: '取消',
      success: (r) => {
        if (r.confirm) grant({ productId, onGranted });
      },
    });
    return;
  }
  pending = { productId, onGranted };
  const ad = ensureReward();
  ad.load()
    .then(() => ad.show())
    .catch(() =>
      ad
        .load()
        .then(() => ad.show())
        .catch(() => {
          pending = null;
          wx.showToast({ title: '广告加载失败，先看简版', icon: 'none' });
        }),
    );
}
