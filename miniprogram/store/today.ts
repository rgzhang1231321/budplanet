/** store/today.ts —— 打卡（乐观更新 + 离线队列 + 静默续订） */
import type { DoseRow, DoseStatus } from '../types/api';
import { call } from '../utils/request';
import { enqueue } from '../utils/queue';
import { silentRenew } from '../utils/subscribe';
import { markCheckinDone } from '../utils/ad';
import { report } from '../utils/report';
import { dateCN } from '../utils/time';
import { emit, get as state, patchRow, set } from './index';

interface WriteTask {
  pid: string;
  date: string;
  slot: number;
  status: DoseStatus;
  skip_reason?: string;
}

/** 撤销窗口内暂缓上行：契约只允许 taken→missed/skipped 单向改判，没有 taken→pending，
 *  所以 3 秒撤销窗口内先不发请求，撤销即"没发生过"，比事后改判干净。 */
let holdTimer: any = null;
let heldRows: DoseRow[] = [];

async function submit(tasks: WriteTask[]) {
  try {
    const d: any = await call('checkin', 'submit', { items: tasks });
    (d.applied || []).forEach((a: any) => {
      patchRow(`${a.pid}_${a.date}_${a.slot}`, { syncing: false });
    });
    const rejected = d.rejected || [];
    if (rejected.length) {
      rejected.forEach((r: any) => report('checkin_rejected', { code: r.code, n: 1 }));
      rejected
        .filter((r: any) => r.code === 1005 || r.code === 1003)
        .forEach((r: any) => {
          enqueue({ pid: r.pid, date: r.date, slot: r.slot, kind: 'take' });
          patchRow(`${r.pid}_${r.date}_${r.slot}`, { syncing: true });
        });
      wx.showToast({ title: rejected[0].msg || '有 1 条没记上', icon: 'none' });
    }
    if (d.quota) set('user.quota', d.quota);
    set('today.lastSyncAt', Date.now());
    emit('today.synced');
  } catch (e: any) {
    // 网络失败：入队重试，状态不回退（辅助写入失败不阻断主链路）
    tasks.forEach((t) =>
      enqueue({
        pid: t.pid,
        date: t.date,
        slot: t.slot,
        kind: t.status === 'skipped' ? 'skip' : 'take',
        reason: t.skip_reason,
      }),
    );
    emit('today.offline');
  }
}

function commit(list: DoseRow[]) {
  const date = dateCN();
  submit(list.map((r) => ({ pid: r.pid, date, slot: r.slot, status: 'taken' as DoseStatus })));
}

/** 打卡：点击即终态（防连点，不等网络），同步态交给顶部细条 */
export function take(list: DoseRow[], deferMs = 0) {
  const now = Date.now();
  list.forEach((r) => patchRow(r.dose_id, { status: 'taken', taken_at: now, syncing: true }));
  emit('today.rows');
  markCheckinDone();
  report('checkin_success', { n: list.length, batch: list.length > 1 ? 1 : 0 });
  if (deferMs > 0) {
    heldRows = list;
    holdTimer = setTimeout(() => {
      holdTimer = null;
      const l = heldRows;
      heldRows = [];
      commit(l);
    }, deferMs);
  } else {
    commit(list);
  }
  silentRenew('CHECKIN'); // S2 才生效；未勾选则无任何副作用
}

/** 离开页面 / 窗口结束：立刻上行 */
export function flushHeld() {
  if (!holdTimer) return;
  clearTimeout(holdTimer);
  holdTimer = null;
  const l = heldRows;
  heldRows = [];
  commit(l);
}

/** 撤销：仅撤销窗口内有效（未上行） */
export function undoTake(r: DoseRow): boolean {
  if (!holdTimer) return false;
  clearTimeout(holdTimer);
  holdTimer = null;
  heldRows = [];
  patchRow(r.dose_id, { status: 'pending', taken_at: 0, syncing: false });
  emit('today.rows');
  report('checkin_undo', { ms: 0 });
  return true;
}

export function skip(r: DoseRow, reason: string) {
  patchRow(r.dose_id, { status: 'skipped', taken_at: 0, syncing: true });
  emit('today.rows');
  report('checkin_skip', { reason });
  submit([{ pid: r.pid, date: dateCN(), slot: r.slot, status: 'skipped', skip_reason: reason }]);
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
