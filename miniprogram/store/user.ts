/** store/user.ts —— 登录、家庭、模式、免责留痕 */
import type { Mode } from '../types/api';
import { login } from '../utils/auth';
import { call, get } from '../utils/request';
import { applyRemoteConfig } from '../utils/ad';
import { applyMode } from '../utils/theme';
import { dateCN } from '../utils/time';
import { emit, get as state, mode, patch, set, loadCachedToday } from './index';

let booting: Promise<void> | null = null;

export function bootstrap(force = false): Promise<void> {
  if (booting && !force) return booting;
  booting = (async () => {
    const s = state();
    loadCachedToday();
    const res: any = await login();
    patch('user', {
      openid: res.openid,
      nickname: (res.user && res.user.nickname) || '我',
      avatar: (res.user && res.user.avatar) || '',
      disclaimerAgreed: !!(res.user && res.user.disclaimer_ack_at),
      privacyAgreed: !!(res.user && res.user.privacy_ack_at),
      reduceMotion: !!(res.user && res.user.setting && res.user.setting.reduceMotion),
      sound: !!(res.user && res.user.setting ? res.user.setting.sound : true),
      quiet: !!(res.user && res.user.setting ? res.user.setting.quiet : true),
      config: res.config,
      entitlement: res.entitlement,
    });
    applyRemoteConfig(res.config);
    set('family.all', res.families || []);
    // 额度必须冷启就拉一次：所有"额度≤0 才显示授权卡"的判断都读 user.quota，
    // 而它的初值是 {0,0,0} —— 不拉就等于判据恒真，授权完再进页面卡片又冒出来。
    await Promise.all([loadFamily(), loadQuota()]);
    set('ready', true);
    applyMode(mode());
    await refreshToday();
  })();
  return booting;
}

export async function loadFamily() {
  const s = state();
  // 冷启没有当前家庭时，用 login 下发的第一个家庭（真接口才暴露这个坑：mock 里 fid 是写死的）
  const fid = s.family.fid || (s.family.all[0] ? s.family.all[0].fid : '');
  if (!fid) return;

  const info: any = await get('family', 'info', { fid }, { silent: true });
  patch('family', {
    fid: info.fid,
    name: info.name,
    role: info.role,
    mode: info.mode,
    selfUids: info.selfUids || [],
    patients: info.patients || [],
    members: info.members || [],
    inviteToken: info.invite_token || '',
  });
}

/** 今日数据：老人端与子女端同一接口（schedule.today 一次给全 today 树） */
export async function refreshToday(silent = true) {
  const s = state();
  if (!s.family.fid) return;
  const date = dateCN();
  try {
    const d: any = await get('schedule', 'today', { fid: s.family.fid, date }, { silent });
    const offset = (d.server_ts || Date.now()) - Date.now();
    set('user.serverOffset', offset);
    set('today.date', date);
    set('today.rows', d.rows || []);
    set('today.recentView', d.recentView || null);
    set('today.lastSyncAt', Date.now());
  } catch (e) {
    /* 读失败不清空已有数据（离线仍可看今日 + 打卡） */
  }
}

/** 模式切换：mode 只决定渲染；role 决定权限，二者解耦 */
export function setMode(m: Mode | null) {
  set('user.modeOverride', m);
  applyMode(mode());
}

/** 「我自己吃药」：绑定 self_openid，服务端随后按 selfUids 过滤今日 */
export async function bindSelf(role: 'patient' | 'guardian'): Promise<any> {
  const s = state();
  const res: any = await call('family', 'join', {
    fid: s.family.fid,
    ivt: s.family.inviteToken,
    role,
    uid: s.family.patients[0] ? s.family.patients[0].uid : undefined,
  });
  await loadFamily();
  setMode(null);
  emit('family');
  return res;
}

export async function agree(kind: 'disclaimer' | 'privacy') {
  if (kind === 'disclaimer') {
    await call('user', 'agree_disclaimer', {});
    set('user.disclaimerAgreed', true);
  } else {
    await call('user', 'agree_privacy', {});
    set('user.privacyAgreed', true);
  }
}

export async function loadQuota() {
  try {
    const d: any = await get('notify', 'quota', {}, { silent: true });
    set('user.quota', d.quota);
  } catch (e) {
    /* ignore */
  }
}
