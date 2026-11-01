/**
 * utils/queue.ts —— 写操作离线队列（打卡主链路永不阻塞）
 * 出队规则（契约 §七）：applied 里无论 changed 真假都出队；rejected 仅 1005/1003 留队。
 */
import { SUBMIT_BATCH_MAX } from '../config/index';
import { call } from './request';
import { report } from './report';
import { dateCN } from './time';

const K = 'q_write_v1';
const MAX_RETRY = 5;

export interface QueueTask {
  pid: string;
  date: string;
  slot: number;
  kind: 'take' | 'skip';
  reason?: string;
  retry: number;
  at: number;
}

function read(): QueueTask[] {
  try {
    return wx.getStorageSync(K) || [];
  } catch (e) {
    return [];
  }
}

function write(q: QueueTask[]) {
  try {
    wx.setStorageSync(K, q);
  } catch (e) {
    /* ignore */
  }
}

export function pendingCount(): number {
  return read().length;
}

let flushing = false;

export function enqueue(task: Omit<QueueTask, 'retry' | 'at'>) {
  const q = read();
  const key = `${task.pid}_${task.date}_${task.slot}`;
  if (q.some((t) => `${t.pid}_${t.date}_${t.slot}` === key)) return;
  q.push({ ...task, retry: 0, at: Date.now() });
  write(q);
  flush();
}

export async function flush(): Promise<void> {
  if (flushing) return;
  const q = read();
  if (!q.length) return;
  flushing = true;
  const batch = q.slice(0, SUBMIT_BATCH_MAX);
  try {
    const d: any = await call('checkin', 'submit', {
      items: batch.map((t) => ({
        pid: t.pid,
        date: t.date,
        slot: t.slot,
        status: t.kind === 'take' ? 'taken' : 'skipped',
        skip_reason: t.reason,
      })),
    });
    const done = new Set((d.applied || []).map((a: any) => `${a.pid}_${a.date}_${a.slot}`));
    const stay = (d.rejected || []).filter((r: any) => r.code === 1005 || r.code === 1003);
    write(
      read().filter((t) => {
        const key = `${t.pid}_${t.date}_${t.slot}`;
        if (done.has(key)) return false;
        const rej = stay.find((r: any) => `${r.pid}_${r.date}_${r.slot}` === key);
        return !!rej;
      }),
    );
    if ((d.rejected || []).length) report('checkin_rejected', { n: d.rejected.length, code: d.rejected[0].code });
  } catch (e) {
    write(
      read().map((t) => {
        if (`${t.pid}_${t.date}_${t.slot}` === `${batch[0].pid}_${batch[0].date}_${batch[0].slot}`) {
          return { ...t, retry: Math.min(t.retry + 1, MAX_RETRY) };
        }
        return t;
      }),
    );
    // 指数退避 1s / 3s / 9s / 27s
    const r = Math.min(batch[0].retry, 4);
    const delay = Math.min(1000 * Math.pow(3, r), 27000);
    setTimeout(() => {
      flushing = false;
      flush();
    }, delay);
    return;
  } finally {
    flushing = false;
  }
  if (read().length) setTimeout(flush, 1200);
}

/** 今日是否有未同步项 */
export function dirtyToday(): boolean {
  const d = dateCN();
  return read().some((t) => t.date === d);
}
