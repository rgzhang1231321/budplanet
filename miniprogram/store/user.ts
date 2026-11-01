/** store/user.ts —— 登录、家庭、模式、免责留痕 */
import type { Mode } from '../types/api';
import { login } from '../utils/auth';
import { call, get } from '../utils/request';
import { applyRemoteConfig } from '../utils/ad';
import { applyMode } from '../utils/theme';
import { dateCN } from '../utils/time';
import { logClient } from '../utils/logger';
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
  // 失败必须把 booting 清空，否则整个会话都被这一个失败的 promise 钉死：
  // 后续每次 bootstrap() 都拿到同一个 rejected 结果，网络恢复了也不会重登 → 全站空数据。
  // 只清自己这一次（booting === p），避免把后来者刚发起的那次覆盖掉。
  const p = booting;
  p.catch(() => {
    if (booting === p) booting = null;
  });
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
  set('today.loading', true);
  try {
    const d: any = await get('schedule', 'today', { fid: s.family.fid, date }, { silent });
    const offset = (d.server_ts || Date.now()) - Date.now();
    set('user.serverOffset', offset);
    set('today.date', date);
    // 每行打上"属于哪一天"：23:59 打开、00:00 点击时，dateCN() 已经是新的一天，
    // 直接用它会把这顿药写到明天的记录里（契约 rows[] 没有 date，只能前端自己记住）。
    set(
      'today.rows',
      (d.rows || []).map((r: any) => ({ ...r, localDate: date })),
    );
    set('today.recentView', d.recentView || null);
    set('today.lastSyncAt', Date.now());
  } catch (e: any) {
    // 读失败不清空已有数据（离线仍可看今日 + 打卡），但必须留痕：
    // 线上"今天一片空白"如果没有这条日志，就完全不知道是超时、1009 还是没权限。
    logClient('warn', `schedule.today 没取到 ${(e && e.code) || ''} ${(e && e.msg) || ''}`.trim(), {
      scene: 'today_load',
      domain: 'schedule',
      action: 'today',
      code: (e && e.code) || 0,
    });
  } finally {
    set('today.loading', false);
  }
}

/** 模式切换：mode 只决定渲染；role 决定权限，二者解耦 */
export function setMode(m: Mode | null) {
  set('user.modeOverride', m);
  applyMode(mode());
}

/** 「我自己吃药」：绑定 self_openid，服务端随后按 selfUids 过滤今日。
 *  ⚠️ `family.info` 的 `invite_token` 现在只对 admin 下发（member/elder 收到 null），
 *  而 `family.join` 在带 fid 时会校验 ivt —— 空串必被 1005「邀请码无效」顶回。
 *  所以拿不到码时直接跳过：邀请落地页那次 `join(role='patient')` 已经在服务端把
 *  `member.role=elder` / `self_patient_id` 绑好了，这里再调一次纯属重复，还会把
 *  一次已经成功的加入变成"邀请码无效"的红字。 */
export async function bindSelf(role: 'patient' | 'guardian'): Promise<any> {
  const s = state();
  if (!s.family.inviteToken) return { skipped: true, reason: 'no_invite_token' };
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
