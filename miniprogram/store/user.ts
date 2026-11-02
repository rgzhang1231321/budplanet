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

/** 「我自己吃药」这条路上，本家还没有"我"的服药人档案时怎么办。
 *
 *  以前在这里 `patient.create` 补建一条「我」—— 判据写的是 `patients.length > 0`，
 *  于是家里只要已经有一位服药人（爸爸/妈妈/媳妇），这条**永远不建**，"我"也就永远绑不上；
 *  现在判据换成"有没有 self_openid == 我 的档案"，与本家患者总数无关。
 *
 *  ⚠️ 但**这里不再建档案**：建 + 绑由紧接着的 `family.join(role:'patient')` 一次完成
 *  （服务端：已绑给我的就复用，没有就新建一条「我」再绑）。为什么不让前端建：
 *  前端 create 出来的是"没有 self_openid 的裸患者"，后端接着 join 时仍找不到"绑给我的那条"，
 *  会再建一条 → 服药人列表里出现两条「我」。**同一条档案只能有一个创建点。**
 *  本函数现在只做一件事：没有本人档案时留一条日志，方便线上定位走到哪条分支。
 */
export async function ensureSelfPatient(): Promise<void> {
  const s = state();
  if (!s.family.fid) return;
  const hasSelf = s.family.patients.some((p) => !!p.selfOpenid && p.selfOpenid === s.user.openid);
  if (hasSelf) return;
  logClient('info', '本家还没有「我」的服药人档案，交给 family.join 建并绑定', {
    scene: 'bind_self',
    domain: 'patient',
    action: 'create',
    code: 0,
  });
}

/** 「我自己吃药」：绑定 self_openid，服务端随后按 selfUids 过滤今日。
 *
 *  为什么要放过 member：`family.join` 现在**只负责"进这个家"，不改已有成员的角色** ——
 *  历史上 `roleIntent='patient'` 会把非 admin 成员降级成 elder，那个分支已删
 *  （见后端 `FamilyHandlers.FamilyJoinHandler` 的 summary，改角色只能走
 *  `family.update_role`）。所以"帮着管家的子女"和"自己也要吃药的人"不是互斥身份，
 *  member 自绑随之开放（路线图决策 6 / 审计 §13.4.1）。旧判据 `role !== 'admin'`
 *  的唯一理由"避免 member 被降级"已经不存在，再拿它卡人就是凭一条过时注释把能力关着。
 *
 *  为什么 elder 仍然跳过：elder 是经 P42 邀请链路以 patient 意图进来的，服务端在那条
 *  join 上已经把 `self_openid` / `self_patient_id` 绑好，再调一次是无谓往返。
 *  判据写成"elder 才跳"而不是"admin/member 才放"，是为了将来新增第四种角色时
 *  默认走向绑定，而不是默认被静默丢掉一次能力。
 *
 *  判据用 role 而不是 inviteToken：`family.info` 的 `invite_token` 只对 admin 下发，
 *  令牌只是"我是 admin"的代理变量，而**历史家庭可能令牌为空**（后端 FamilyJoinPolicy
 *  明确把空令牌视为合法状态），拿令牌判会把"admin 但没令牌"的人一起挡掉，绑定就静默丢了。
 *
 *  ⚠️ 两条 skip 都**不许静默**：正常路径不该走到这里（建家人是 admin、邀请路径根本不进
 *  本函数），一旦 skip 就说明有真问题（家庭信息没加载到 / 权限被改），必须留痕。 */
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
  if (s.family.role === 'elder') {
    logClient('warn', 'bindSelf 跳过：elder 的 self 已由邀请链路绑定', {
      scene: 'bind_self',
      domain: 'family',
      action: 'join',
      code: 0,
    });
    return { skipped: true, reason: 'elder_no_self_bind' };
  }
  const res: any = await call('family', 'join', {
    fid: s.family.fid,
    ivt: s.family.inviteToken,
    role,
    // ⛔ 不许再传 uid。这里曾传 `patients[0].uid`，即"家里第一位服药人"——他多半是爸爸。
    // 后端拿到就把他绑成"我"，于是客户的周报/历史全变成「我的记录」。不传 uid 时由服务端
    // 按"已经绑给我的那条 → 没有就新建一条「我」"处理，永远不会绑到别人身上。
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
