/**
 * store/index.ts —— 60 行自研 store（不引入状态管理库）
 * 纪律：页面只读 store.get()，只经 actions 改；onLoad 里 sub 一个监听在 onUnload 注销。
 */
import type { AppConfig, DoseRow, FamilyListItemDto, Member, Mode, Patient, Role } from '../types/api';
import { dateCN } from '../utils/time';

export interface AppState {
  ready: boolean;
  user: {
    openid: string;
    nickname: string;
    avatar: string;
    serverOffset: number;
    modeOverride: Mode | null;
    disclaimerAgreed: boolean;
    privacyAgreed: boolean;
    reduceMotion: boolean;
    sound: boolean;
    quiet: boolean;
    config: AppConfig;
    entitlement: { memberLevel: string; credits: number };
    quota: { CHECKIN: number; MISSED: number; WEEKLY: number };
  };
  family: {
    /** 我加入的全部家庭（login/family.list 下发），冷启拿它选默认 fid */
    all: FamilyListItemDto[];
    fid: string;
    name: string;
    role: Role;
    mode: Mode;
    selfUids: string[];
    patients: Patient[];
    members: Member[];
    inviteToken: string;
  };
  today: {
    date: string;
    rows: DoseRow[];
    recentView: { alias: string; at: number } | null;
    lastSyncAt: number;
    loading: boolean;
  };
  ad: { elderPreview: boolean };
}

const PREF = 'app_pref_v1';
const TODAY = 'app_today_v1';

function prefs(): any {
  try {
    return wx.getStorageSync(PREF) || {};
  } catch (e) {
    return {};
  }
}

export const state: AppState = {
  ready: false,
  user: {
    openid: '',
    nickname: '',
    avatar: '',
    serverOffset: 0,
    modeOverride: prefs().modeOverride || null,
    disclaimerAgreed: !!prefs().disclaimerAgreed,
    privacyAgreed: !!prefs().privacyAgreed,
    reduceMotion: !!prefs().reduceMotion,
    sound: prefs().sound !== false,
    quiet: prefs().quiet !== false,
    config: {
      adOn: false,
      adSwitch: { interstitial: false, elderInterstitial: false },
      interstitialCool: 30,
      interstitialDayMax: 3,
      freeMedLimit: 3,
      freePatientLimit: 1,
      tmplIds: { CHECKIN: '', MISSED: '', WEEKLY: '' },
      quietHours: { start: '22:00', end: '07:00' },
      missedThresholdMin: 60,
      snoozeDeadlineMin: 45,
      notice: '',
    },
    entitlement: { memberLevel: 'free', credits: 0 },
    quota: { CHECKIN: 0, MISSED: 0, WEEKLY: 0 },
  },
  family: {
    all: [],
    fid: '',
    name: '',
    role: 'member',
    mode: 'guardian',
    selfUids: [],
    patients: [],
    members: [],
    inviteToken: '',
  },
  today: { date: dateCN(), rows: [], recentView: null, lastSyncAt: 0, loading: false },
  ad: { elderPreview: false },
};

type Listener = (s: AppState, changed: string) => void;
const listeners: Listener[] = [];

export function on(fn: Listener): () => void {
  listeners.push(fn);
  return () => {
    const i = listeners.indexOf(fn);
    if (i >= 0) listeners.splice(i, 1);
  };
}

export function emit(changed = '') {
  listeners.forEach((fn) => {
    try {
      fn(state, changed);
    } catch (e) {
      /* 单个页面监听异常不影响其它页面 */
    }
  });
}

export function get(): AppState {
  return state;
}

function setByPath(path: string, value: any) {
  const keys = path.split('.');
  let obj: any = state;
  for (let i = 0; i < keys.length - 1; i++) obj = obj[keys[i]];
  obj[keys[keys.length - 1]] = value;
}

export function set(path: string, value: any) {
  setByPath(path, value);
  persist();
  emit(path);
}

export function patch(path: string, obj: Record<string, any>) {
  const keys = path.split('.');
  let target: any = state;
  for (const k of keys) target = target[k];
  Object.assign(target, obj);
  persist();
  emit(path);
}

function persist() {
  try {
    wx.setStorageSync(PREF, {
      modeOverride: state.user.modeOverride,
      disclaimerAgreed: state.user.disclaimerAgreed,
      privacyAgreed: state.user.privacyAgreed,
      reduceMotion: state.user.reduceMotion,
      sound: state.user.sound,
      quiet: state.user.quiet,
    });
    if (state.today.date === dateCN()) {
      wx.setStorageSync(TODAY, {
        date: state.today.date,
        rows: state.today.rows,
        recentView: state.today.recentView,
      });
    }
  } catch (e) {
    /* ignore */
  }
}

/** 冷启秒开：读上次缓存直出（打卡页 1.2s 指标的本地底座） */
export function loadCachedToday() {
  try {
    const c = wx.getStorageSync(TODAY);
    if (c && c.date === dateCN() && Array.isArray(c.rows) && c.rows.length) {
      state.today.rows = c.rows;
      state.today.recentView = c.recentView || null;
      state.today.date = c.date;
      return true;
    }
  } catch (e) {
    /* ignore */
  }
  return false;
}

/* ── selectors（两页必须同源，禁止各算各的） ── */

/** 生效模式：mode 管渲染、role 管权限，二者解耦。
 *  优先级：本机手动切换（modeOverride）> 子女给"我这位服药人"设的默认（patient.display_mode）> 普通版。
 *  子女/成员（family.mode !== 'elder'）不继承任何服药人的默认字号，否则会被爸妈的 large 带跑。 */
export function mode(): Mode {
  if (state.user.modeOverride) return state.user.modeOverride;
  if (state.family.mode === 'elder') {
    const self = state.family.patients.find((p) => state.family.selfUids.indexOf(p.uid) >= 0);
    if (self && self.display_mode === 'large') return 'elder';
  }
  return 'guardian';
}

/**
 * 能看全家数据 = role 为 admin / member。**这是"数据视角"，判据只能是 role。**
 * 为什么不看 mode：mode 只决定渲染（契约 §4.1），它由"有没有自绑服药人"派生。
 * 契约同时写明 role='admin' + mode='elder'（自己也吃药、同时管全家）是常见组合 ——
 * 拿 mode 判数据视角会把这类人的爸妈直接裁出视野（本项目真实事故：
 * 子女点过「我自己吃药」→ 被绑成家里第一位服药人 → 周报/历史/看板全变成"我的"）。
 */
export function seesWholeFamily(): boolean {
  return state.family.role !== 'elder';
}

/** 纯服药人视角 = role 为 elder：只看自己的药，也不能有"这是谁的药"这类他人信息。 */
export function pureElderView(): boolean {
  return state.family.role === 'elder';
}

/**
 * 我本人也是服药人（服务端按 patients.self_openid 派生，等于"我有 selfUids"）。
 * ⚠️ 只用于**权限**判断（我能不能给自己补记、要不要接广告），不要拿它判"能看多少数据"——
 * 那属于数据视角，用上面的 seesWholeFamily() / pureElderView()。
 * 后端同名字段 MemberGuard.IsElder / Membership.Mode 语义相同，两边不要各改各的。
 */
export function isSelf(): boolean {
  return state.family.mode === 'elder';
}

/** 大字版界面。只决定字号/版式，不代表身份。 */
export function isElder(): boolean {
  return mode() === 'elder';
}

/** 纯服药人只看「我的今日」；admin/member 看全家。无绑定患者时退化为第一位（演示态） */
export function visibleRows(): DoseRow[] {
  const rows = state.today.rows;
  if (seesWholeFamily()) return rows;
  const self = state.family.selfUids.length ? state.family.selfUids : state.family.patients.slice(0, 1).map((p) => p.uid);
  if (!self.length) return rows;
  return rows.filter((r) => self.indexOf(r.pid) >= 0);
}

export function patientOf(pid: string): Patient | undefined {
  return state.family.patients.find((p) => p.uid === pid);
}

export interface Overview {
  taken: number;
  total: number;
  rate: number;
  missed: number;
  pending: number;
}

export function overview(): Overview {
  const rows = visibleRows();
  const total = rows.length;
  const taken = rows.filter((r) => r.status === 'taken').length;
  const missed = rows.filter((r) => r.status === 'missed').length;
  const pending = rows.filter((r) => r.status === 'pending').length;
  return { taken, total, rate: total ? Math.round((taken / total) * 100) : 0, missed, pending };
}

export function groups(): { patient: Patient; rows: DoseRow[]; taken: number; total: number }[] {
  const rows = visibleRows();
  const list: { patient: Patient; rows: DoseRow[]; taken: number; total: number }[] = [];
  state.family.patients.forEach((p) => {
    const rs = rows.filter((r) => r.pid === p.uid).sort((a, b) => a.slot - b.slot);
    if (!rs.length) return;
    list.push({ patient: p, rows: rs, taken: rs.filter((r) => r.status === 'taken').length, total: rs.length });
  });
  return list;
}

/** 补丁单行（setData 只传路径 diff 的底座） */
export function patchRow(doseId: string, next: Partial<DoseRow>) {
  const i = state.today.rows.findIndex((r) => r.dose_id === doseId);
  if (i < 0) return -1;
  Object.assign(state.today.rows[i], next);
  persist();
  return i;
}

/**
 * 会话被服务端否认（1002 不是家庭成员 / 账号数据被清）时把内存态清回初值。
 * 不清的话旧 fid/旧今日数据会继续渲染，用户看到的是"有内容但每点一下都报错"。
 * 只清"身份相关"的字段：免责/隐私同意、字号偏好这类本地偏好留着，别让用户重看一遍协议。
 */
export function resetState() {
  state.ready = false;
  state.user.openid = '';
  state.user.entitlement = { memberLevel: 'free', credits: 0 };
  state.user.quota = { CHECKIN: 0, MISSED: 0, WEEKLY: 0 };
  state.family = {
    all: [],
    fid: '',
    name: '',
    role: 'member',
    mode: 'guardian',
    selfUids: [],
    patients: [],
    members: [],
    inviteToken: '',
  };
  state.today = { date: dateCN(), rows: [], recentView: null, lastSyncAt: 0, loading: false };
  try {
    wx.removeStorageSync(TODAY);
  } catch (e) {
    /* ignore */
  }
  emit('reset');
}
