/** store/today.ts —— 打卡（乐观更新 + 离线队列 + 静默续订） */
import type { DoseRow, DoseStatus } from '../types/api';
import { call } from '../utils/request';
import { enqueue } from '../utils/queue';
import { silentRenew } from '../utils/subscribe';
import { markCheckinDone } from '../utils/ad';
import { report } from '../utils/report';
import { dateCN } from '../utils/time';
import { emit, get as state, patchRow, set } from './index';

/** 一次写入的意图。status 必须是可提交状态（taken / skipped），永不传 pending。 */
interface WriteTask {
  pid: string;
  date: string;
  slot: number;
  status: DoseStatus;
  skip_reason?: string;
}

/** 点击前的真实状态：撤销 / 被服务端拒绝时按它回滚，不许一律变成 pending。 */
interface RowOrigin {
  dose_id: string;
  status: DoseStatus;
  taken_at: number;
}

export interface SubmitOutcome {
  /** 服务端确认写入成功（至少一条 applied、且本条没被拒） */
  ok: boolean;
  /** 断网：已入队等重放，前端只能显示"记在本机"，不能算成功 */
  offline: boolean;
  applied: any[];
  rejected: any[];
}

const keyOf = (r: { pid: string; date: string; slot: number }) => `${r.pid}_${r.date}_${r.slot}`;

/** 撤销窗口内暂缓上行：契约只允许 taken→missed/skipped 单向改判，没有 taken→pending，
 *  所以 3 秒撤销窗口内先不发请求，撤销即"没发生过"，比事后改判干净。 */
let holdTimer: any = null;
let heldRows: DoseRow[] = [];
let heldOrigins: RowOrigin[] = [];

async function submit(tasks: WriteTask[], origins: RowOrigin[] = []): Promise<SubmitOutcome> {
  try {
    const d: any = await call('checkin', 'submit', { items: tasks });

    // 以服务端返回的真实状态回填本地：绿勾只能来自服务端确认，
    // 本地乐观值只是"点了"，不是"记上了"。
    (d.applied || []).forEach((a: any) => {
      const status = (a.status || 'taken') as DoseStatus;
      patchRow(keyOf(a), {
        status,
        taken_at: status === 'taken' ? a.ts || Date.now() : 0,
        syncing: false,
      });
    });

    const rejected: any[] = d.rejected || [];
    rejected.forEach((r: any) => {
      const key = keyOf(r);
      const origin = origins.find((o) => o.dose_id === key);
      if (origin) {
        // 服务端拒了：回滚到点击前的真实状态（原状态是 missed 就还显示 missed）
        patchRow(key, { status: origin.status, taken_at: origin.taken_at, syncing: false });
      } else {
        // 不知道原状态就不改状态，只解除同步态
        patchRow(key, { syncing: false });
      }
      report('checkin_rejected', { code: r.code, n: 1 });
    });

    // 契约 §七：只有 1005/1003 留队重放，且重放必须带本次的意图状态
    rejected
      .filter((r: any) => r.code === 1005 || r.code === 1003)
      .forEach((r: any) => {
        const t = tasks.find((x) => keyOf(x) === keyOf(r));
        enqueue({
          pid: r.pid,
          date: r.date,
          slot: r.slot,
          status: t ? t.status : 'taken',
          reason: t && t.skip_reason,
        });
        patchRow(keyOf(r), { syncing: true });
      });

    if (rejected.length) wx.showToast({ title: rejected[0].msg || '有 1 条没记上', icon: 'none' });
    if (d.quota) set('user.quota', d.quota);
    set('today.lastSyncAt', Date.now());
    emit('today.synced');

    return { ok: rejected.length === 0 && (d.applied || []).length > 0, offline: false, applied: d.applied || [], rejected };
  } catch (e: any) {
    // 网络失败：带着本次意图入队重试，状态不回退（辅助写入失败不阻断主链路）
    tasks.forEach((t) =>
      enqueue({ pid: t.pid, date: t.date, slot: t.slot, status: t.status, reason: t.skip_reason }),
    );
    emit('today.offline');
    return { ok: false, offline: true, applied: [], rejected: [] };
  }
}

function commit(list: DoseRow[], origins: RowOrigin[] = []): Promise<SubmitOutcome> {
  const date = dateCN();
  return submit(
    list.map((r) => ({ pid: r.pid, date, slot: r.slot, status: 'taken' as DoseStatus })),
    origins,
  );
}

/** 打卡：点击即终态（防连点，不等网络），同步态交给顶部细条。
 *  返回的 outcome 只给"补记"这类必须等服务端确认才显示的入口用。 */
export function take(list: DoseRow[], deferMs = 0): Promise<SubmitOutcome> {
  const now = Date.now();
  // 先记住点击前的真实状态：撤销要按原状态回滚（原状态是 missed 就不能变成"待服"）
  const origins: RowOrigin[] = list.map((r) => ({
    dose_id: r.dose_id,
    status: r.status,
    taken_at: r.taken_at || 0,
  }));

  list.forEach((r) => patchRow(r.dose_id, { status: 'taken', taken_at: now, syncing: true }));
  emit('today.rows');
  markCheckinDone();
  report('checkin_success', { n: list.length, batch: list.length > 1 ? 1 : 0 });
  silentRenew('CHECKIN'); // S2 才生效；未勾选则无任何副作用

  if (deferMs > 0) {
    heldRows = list;
    heldOrigins = origins;
    return new Promise((resolve) => {
      holdTimer = setTimeout(() => {
        holdTimer = null;
        const l = heldRows;
        const o = heldOrigins;
        heldRows = [];
        heldOrigins = [];
        resolve(commit(l, o));
      }, deferMs);
    });
  }

  return commit(list, origins);
}

/** 离开页面 / 窗口结束：立刻上行 */
export function flushHeld() {
  if (!holdTimer) return;
  clearTimeout(holdTimer);
  holdTimer = null;
  const l = heldRows;
  const o = heldOrigins;
  heldRows = [];
  heldOrigins = [];
  commit(l, o);
}

/** 撤销：仅撤销窗口内有效（未上行）。批量打卡时窗口内暂缓的是整批，
 *  必须整批回滚——只回滚一条会让其余行本地显示已服却永不上行（少记服药且无报错）。
 *  回滚目标 = 点击前的原状态；原状态不知道就只清定时器、不动状态（宁可显示保守，也不编一个"待服"）。 */
export function undoTake(list?: DoseRow[]): boolean {
  if (!holdTimer) return false;
  clearTimeout(holdTimer);
  holdTimer = null;
  // heldRows 就是"还没发出去"的确切集合，以它为准；入参只作兜底
  const rows = heldRows.length ? heldRows.slice() : (list || []).slice();
  const origins = heldOrigins.slice();
  heldRows = []; // 先清空：即便下面 patch 抛错，也不会有残留任务被 commit 误发
  heldOrigins = [];
  if (!rows.length) return false;

  let patched = 0;
  rows.forEach((r) => {
    const o = origins.find((x) => x.dose_id === r.dose_id);
    if (!o) return;
    patchRow(r.dose_id, { status: o.status, taken_at: o.taken_at, syncing: false });
    patched += 1;
  });

  emit('today.rows');
  report('checkin_undo', { n: patched });
  return patched > 0;
}

export function skip(r: DoseRow, reason: string): Promise<SubmitOutcome> {
  const origin: RowOrigin = { dose_id: r.dose_id, status: r.status, taken_at: r.taken_at || 0 };
  patchRow(r.dose_id, { status: 'skipped', taken_at: 0, syncing: true });
  emit('today.rows');
  report('checkin_skip', { reason });
  return submit([{ pid: r.pid, date: dateCN(), slot: r.slot, status: 'skipped', skip_reason: reason }], [origin]);
}

/** 稍后提醒：先续订（仅 S2 静默生效）再延后；失败时页面原地切大字倒计时卡片 */
export async function snooze(r: DoseRow): Promise<{ ok: boolean; due_ts?: number; snooze_count?: number; msg?: string }> {
  silentRenew('CHECKIN');
  try {
    const d: any = await call('checkin', 'snooze', { dose_id: r.dose_id });
    patchRow(r.dose_id, { snooze_count: d.snooze_count, due_ts: d.due_ts, status: 'pending' });
    emit('today.rows');
    report('snooze_click', { n: d.snooze_count });
    return { ok: true, due_ts: d.due_ts, snooze_count: d.snooze_count };
  } catch (e: any) {
    return { ok: false, msg: (e && e.msg) || '现在不能延后了' };
  }
}

/** 管理员改判：taken → missed/skipped（不可逆）；系统判的 missed → taken 允许 */
export async function correct(r: DoseRow, to: 'taken' | 'missed' | 'skipped') {
  const d: any = await call('checkin', 'correct', { dose_id: r.dose_id, to_status: to });
  patchRow(r.dose_id, { status: to, is_corrected: true, taken_at: to === 'taken' ? Date.now() : 0 });
  emit('today.rows');
  report('dose_correct', { from_status: r.status, to_status: to });
  return d;
}

export function rowsOf(date?: string): DoseRow[] {
  return state().today.rows;
}
