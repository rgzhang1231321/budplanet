/** store/user.ts —— 登录、家庭、模式、免责留痕 */
import type { Mode, Role } from '../types/api';
import { clearToken, login } from '../utils/auth';
import { call, get } from '../utils/request';
import { applyRemoteConfig } from '../utils/ad';
import { applyMode } from '../utils/theme';
import { dateCN } from '../utils/time';
import { logClient } from '../utils/logger';
import { emit, get as state, mode, patch, set, loadCachedToday, resetState } from './index';

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

/**
 * 会话失效清场：清 token + 清内存态 + 让下一次 bootstrap 真的重新登录。
 * `booting` 必须置空 —— 它缓存的是上一次的 promise，不清的话 bootstrap() 永远返回旧结果，
 * 重登不会发生，页面就一直停在"每点一下都报 1002"的状态。
 */
export function resetSession() {
  clearToken();
  booting = null;
  resetState();
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

/**
 * 多家庭切换（P1-3）：管自己父母 + 配偶父母两个家的用户，原先永远只能看到第一个家。
 * 改 fid → 重拉 family.info（角色/成员/服药人都变）→ 重拉今日 → 广播，首页据此重渲染。
 */
export async function switchFamily(fid: string): Promise<void> {
  const s = state();
  if (!fid || fid === s.family.fid) return;
  set('family.fid', fid);
  await loadFamily();
  await refreshToday(false);
  emit('family');
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

/** family.create —— 全仓唯一的建家调用点（P01 选完身份、又没有任何邀请码时走这里）。
 *  产品上"自己建一个家"和"加入别人家"是两条路，不要把它藏进 family.join 的语义里：
 *  join 的入参是 fid + ivt，它天然假设"这个家已经存在"。 */
export async function createFamily(name = '我家'): Promise<string> {
  const res: any = await call('family', 'create', { name });
  const role: Role = (res.role as Role) || 'admin';
  // 必须先钉住 fid 再 loadFamily：loadFamily 在 family.fid 为空时会退回去读 all[0]，
  // 那读的是旧家庭（或者什么都没有），刚建的家就白建了。
  set('family.fid', res.fid);
  set('family.role', role);
  const all = state().family.all || [];
  if (!all.some((f) => f.fid === res.fid)) {
    set('family.all', all.concat([{ fid: res.fid, name, role }]));
  }
  await loadFamily();
  return res.fid;
}

/** 刚建好的家里一个服药人都没有：先把"我"建成服药人，后面的 bindSelf 才有地方可绑。
 *  不建的话 family.join(role=patient) 找不到 target，绑定是**静默失败** ——
 *  老人会落在子女版界面，也看不到「我的今日」。 */
export async function ensureSelfPatient(name: string): Promise<void> {
  const s = state();
  if (!s.family.fid || s.family.patients.length > 0) return;
  const alias = (name || '我').slice(0, 20);
  await call('patient', 'create', { fid: s.family.fid, name: alias, alias });
  await loadFamily();
}

/** 「我自己吃药」：绑定 self_openid，服务端随后按 selfUids 过滤今日。
 *  ⚠️ **非 admin 不重复调 join**：`family.info` 的 `invite_token` 只对 admin 下发，
 *  所以「没有码」实际等于「我不是管理员」。少一次无用往返，也避免
 *  `roleIntent='patient'` 把 member 补成 elder。这道判断是团队口径，别删。
 *  判据用 role 而不是 inviteToken：令牌只是"我是 admin"的代理变量，
 *  而**历史家庭可能令牌为空**（后端 FamilyJoinPolicy 明确把空令牌视为合法状态），
 *  拿令牌判会把"admin 但没令牌"的人一起挡掉，绑定就静默丢了。
 *
 *  ⚠️ 但**不许静默跳过**：正常路径不该走到这里（建家人是 admin、邀请路径根本不进本函数），
 *  一旦 skip 就说明有真问题（家庭信息没加载到 / 权限被改），必须留痕。 */
export async function bindSelf(role: 'patient' | 'guardian'): Promise<any> {
  const s = state();
  if (!s.family.fid) {
    // 连"往哪个家绑"都不知道：请求出去只会换回一句 1005「缺少 fid」
    logClient('warn', 'bindSelf 跳过：family.fid 为空', {
      scene: 'bind_self',
      domain: 'family',
      action: 'join',
      code: 0,
    });
    return { skipped: true, reason: 'no_family' };
  }
  if (s.family.role !== 'admin') {
    logClient('warn', 'bindSelf 跳过：当前角色不是 admin', {
      scene: 'bind_self',
      domain: 'family',
      action: 'join',
      code: 0,
    });
    return { skipped: true, reason: 'not_admin' };
  }
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
