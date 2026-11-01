/**
 * utils/ad.ts —— 广告频控 + 激励视频单例
 * 铁律：打卡主流程（提醒 → 打卡按钮 → 成功动效）0 广告；开关只读后端下发的 config。
 */
import { AD_UNITS } from '../config/index';
import { call } from './request';
import { report } from './report';
import { dateCN, minuteOfDay, timeToMinute } from './time';

/** 安静时段的**唯一默认值**：读不到 config.quietHours 时才用它（默认值与契约 §三 一致）。
 *  别在页面里再写一份 —— 三处各写一个时间，改一处就会出现「设置页说 22:00，实际 20:00 就屏蔽」。 */
export const DEFAULT_QUIET_HOURS = { start: '22:00', end: '07:00' };

/**
 * 激励视频 product_id —— **必须与后端 EntitlementService 的常量逐字一致（大写）**。
 * 曾经页面里直接写 'med_slot'，后端按 'MED_SLOT' 统计 credits：看完视频 granted=true，
 * 名额却永远是 0，第 4 个药依旧 1004、解锁弹框再弹一次。统一走这里，别再手写字符串。
 * 本地 unlockLog 的 kind 仍是小写（unlocks 页按小写分组），在 grant 里转换。
 */
export const PRODUCT = {
  MED_SLOT: 'MED_SLOT',
  WEEK_REPORT: 'WEEK_REPORT',
} as const;

const CFG = {
  coolSec: 30,
  cDayMax: 3,
  aDayMax: 6,
  bDayMax: 8,
  bElderDayMax: 4,
  dDayMax: 2,
  quietHours: DEFAULT_QUIET_HOURS,
  afterCheckinMin: 10,
  /** 插屏（子女端）默认开：adOn 一关就都不弹；adSwitch.interstitial=false 可单独关掉插屏 */
  interstitial: true,
  elderInterstitial: false,
  adOn: false,
};

let cfg = { ...CFG };
let lastCheckinAt = 0;

export function applyRemoteConfig(c: any) {
  if (!c) return;
  const q = c.quietHours;
  cfg = {
    ...CFG,
    ...c,
    coolSec: c.interstitialCool !== undefined ? c.interstitialCool : CFG.coolSec,
    cDayMax: c.interstitialDayMax !== undefined ? c.interstitialDayMax : CFG.cDayMax,
    elderInterstitial: !!(c.adSwitch && c.adSwitch.elderInterstitial),
    interstitial: c.adSwitch && c.adSwitch.interstitial !== undefined ? !!c.adSwitch.interstitial : CFG.interstitial,
    adOn: c.adOn !== undefined ? c.adOn : CFG.adOn,
    quietHours: q && q.start && q.end ? { start: q.start, end: q.end } : DEFAULT_QUIET_HOURS,
  };
  // 本地强制关：控制台执行 wx.setStorageSync('ad_off', true)，仅本机生效。
  // 用途是联调/演示时不想看到广告，不用去改数据库 —— 线上总开关始终以 app_config.adOn 为准。
  if (localAdOff()) cfg.adOn = false;
}

function localAdOff(): boolean {
  try {
    return wx.getStorageSync('ad_off') === true;
  } catch (e) {
    return false;
  }
}

/** 安静时段（23:00-07:00 这类跨零点区间要按"或"判）。设置页文案与插屏屏蔽共用这一个来源。 */
export function quietHours(): { start: string; end: string } {
  const q = cfg.quietHours;
  return q && q.start && q.end ? { start: q.start, end: q.end } : DEFAULT_QUIET_HOURS;
}

/** 设置页展示用："22:00 - 07:00" */
export function quietHoursText(): string {
  const q = quietHours();
  return `${q.start} - ${q.end}`;
}

function inQuietHours(): boolean {
  const q = quietHours();
  const sm = timeToMinute(q.start);
  const em = timeToMinute(q.end);
  const now = minuteOfDay();
  return sm <= em ? now >= sm && now < em : now >= sm || now < em;
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

/** 插屏 C：冷却 + 日上限 + 时段屏蔽 + 打卡后 10 分钟不弹。
 *  老人端默认不弹：只有 config.adSwitch.elderInterstitial 明确打开才允许（契约 §三 默认 false）。 */
export function showC(mode: string): Promise<boolean> {
  const s = st();
  // 判据是"这一端允许不允许弹"，不是"函数传了哪个字符串"——
  // 调用点写死 'guardian' 时，老人端也会弹出来（传参是调用方的自由，闸门必须在本函数内）。
  const modeOk = mode === 'elder' ? cfg.elderInterstitial : cfg.interstitial;
  const ok =
    cfg.adOn &&
    modeOk &&
    !inQuietHours() &&
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
        wx.showToast({ title: '名额还没到账，再试一次', icon: 'none' });
        return;
      }
      report('reward_grant', { product: p.productId });
      // 后端 product_id 是大写，unlockLog 的 kind 用小写（unlocks 页按小写分组展示）
      rememberUnlock(p.productId.toLowerCase());
      p.onGranted(d);
    })
    .catch((e: any) => wx.showToast({ title: (e && e.msg) || '网络不好，稍后再试', icon: 'none' }));
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

/** 是否正式版（release）。取不到环境信息时按正式版处理 —— 宁可演示分支不出现，也不能让正式包白送权益。 */
function isRelease(): boolean {
  try {
    const info: any = (wx as any).getAccountInfoSync();
    const v = info && info.miniProgram && info.miniProgram.envVersion;
    return v ? v === 'release' : true;
  } catch (e) {
    return true;
  }
}

/**
 * 激励视频解锁（D/E 共用）。
 * 演示分支（不发视频直接给权益）**只在开发版/体验版**出现；正式版广告位未开通或未填充时
 * 一律不发权益 —— 否则"点一下模拟看完"等于不看完也能解锁，既是商业损失也是审核风险。
 */
export function unlockByAd(productId: string, onGranted: (d: any) => void) {
  if (!AD_UNITS.d) {
    if (isRelease()) {
      wx.showToast({ title: '广告暂时没有，稍后再试', icon: 'none' });
      return;
    }
    wx.showModal({
      title: '广告位未开通',
      content: '当前为开发/体验环境：跳过视频直接发放权益，便于验收解锁流程。正式版不会这样。',
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
          wx.showToast({ title: '广告还没加载出来，稍后再试', icon: 'none' });
        }),
    );
}
