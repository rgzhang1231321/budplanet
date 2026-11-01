/**
 * utils/queue.ts —— 写操作离线队列（打卡主链路永不阻塞）
 * 出队规则（契约 §七）：applied 里无论 changed 真假都出队；rejected 仅 1005/1003 留队。
 * 留队也不是无限留：重试超过 MAX_RETRY 就移出队列并明确提示「这条没记上」——
 * 一条注定失败的任务若永远留在队列里，就是每 1.2 秒重投一次的死循环（白耗电、白耗流量）。
 */
import { SUBMIT_BATCH_MAX } from '../config/index';
import type { DoseStatus } from '../types/api';
import { patchRow, emit } from '../store/index';
import { call } from './request';
import { logClient } from './logger';
import { report } from './report';
import { dateCN } from './time';

const K = 'q_write_v1';
const MAX_RETRY = 5;

export interface QueueTask {
  pid: string;
  date: string;
  slot: number;
  /** 本次写入的意图状态：只可能是 taken / skipped（pending 是"还没点"，不是一次写入）。
   *  可空只因兼容旧版本队列里的老条目，新写入一律带。 */
  status?: DoseStatus;
  /** 旧版本队列（v1 早期只存 kind）的兼容字段，新写入同时写它，读取时以 status 为准 */
  kind?: 'take' | 'skip';
  reason?: string;
  retry: number;
  at: number;
}

/** 入队参数：status 必填，避免"补记 / 跳过"在重放时被悄悄改成已服 */
export interface EnqueueInput {
  pid: string;
  date: string;
  slot: number;
  status: DoseStatus;
  reason?: string;
}

const keyOf = (t: { pid: string; date: string; slot: number }) => `${t.pid}_${t.date}_${t.slot}`;

/** 意图状态：新任务读 status，老任务（只存 kind）按 kind 兜底 */
function intentOf(t: QueueTask): DoseStatus {
  if (t.status === 'taken' || t.status === 'skipped' || t.status === 'missed') return t.status;
  return t.kind === 'skip' ? 'skipped' : 'taken';
}

const kindOf = (status: DoseStatus): 'take' | 'skip' => (status === 'skipped' ? 'skip' : 'take');

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

let inflight: Promise<void> | null = null;

export function enqueue(task: EnqueueInput) {
  // 防呆：pending 不是可提交状态（服务端必 1005 拒），塞进队列只会变成死任务
  if (task.status === 'pending') {
    logClient('warn', '队列拒绝了 pending 意图', { scene: 'queue_enqueue', slot: task.slot });
    return;
  }

  const q = read();
  const key = keyOf(task);
  const i = q.findIndex((t) => keyOf(t) === key);
  const next: QueueTask = {
    pid: task.pid,
    date: task.date,
    slot: task.slot,
    status: task.status,
    kind: kindOf(task.status),
    reason: task.reason,
    retry: 0,
    at: Date.now(),
  };

  if (i >= 0) {
    const prev = intentOf(q[i]);
    // 同意图：保持原样（不重置重试次数，否则又是一次死循环）
    if (prev === task.status) return;
    // 新意图覆盖旧意图：用户最后点的那个才是他的意思（先记已服、改主意标跳过，不能只剩已服）
    q[i] = next;
    logClient('info', `队列意图被覆盖 ${prev}->${task.status}`, { scene: 'queue_override', slot: task.slot });
  } else {
    q.push(next);
  }

  write(q);
  flush();
}

/** 放弃任务（重试超上限）：出队 + 落日志 + 明确提示，绝不让它继续 1.2 秒一次地重投 */
function drop(tasks: QueueTask[]) {
  if (!tasks.length) return;
  const keys = tasks.map(keyOf);
  logClient('error', `队列放弃 ${tasks.length} 条写入（重试超上限）`, { scene: 'queue_drop', slots: keys });
  report('checkin_dropped', { n: tasks.length });
  keys.forEach((k) => patchRow(k, { syncing: false })); // 别让顶部一直挂"正在同步"
  // 老人端文案红线：不出现"失败 / 错误"
  wx.showToast({ title: '这条没记上，请重新点一次', icon: 'none' });
}

/**
 * 冲队列。已有一轮在跑时返回那一轮的 promise ——
 * 页面「重试」必须等真的跑完才能判断结果，早退会让它在"还在发"时就宣布没上去。
 */
export function flush(): Promise<void> {
  if (inflight) return inflight;
  if (!read().length) return Promise.resolve();
  inflight = run().finally(() => {
    inflight = null;
  });
  return inflight;
}

async function run(): Promise<void> {
  const q = read();
  if (!q.length) return;

  const batch = q.slice(0, SUBMIT_BATCH_MAX);
  const batchKeys = new Set(batch.map(keyOf));
  let retryDelay = 0;

  try {
    const d: any = await call('checkin', 'submit', {
      items: batch.map((t) => ({
        pid: t.pid,
        date: t.date,
        slot: t.slot,
        status: intentOf(t),
        skip_reason: t.reason,
      })),
    });

    const doneKeys = new Set((d.applied || []).map((a: any) => keyOf(a)));
    const rejected: any[] = d.rejected || [];
    const rejByKey = new Map<string, any>(rejected.map((r: any) => [keyOf(r), r]));

    const keep: QueueTask[] = [];
    const gone: QueueTask[] = [];
    read().forEach((t) => {
      const key = keyOf(t);
      if (!batchKeys.has(key)) {
        keep.push(t);                              // 不在本批：原样保留，一条都不许丢
        return;
      }
      if (doneKeys.has(key)) return;               // applied：changed 真假都出队
      const rej = rejByKey.get(key);
      if (!rej) {
        keep.push(t);                              // 既没 applied 也没 rejected：留队等下次
        return;
      }
      if (rej.code !== 1005 && rej.code !== 1003) return; // 其它错误码按契约出队
      const retry = t.retry + 1;
      if (retry > MAX_RETRY) {
        gone.push({ ...t, retry });
        return;
      }
      keep.push({ ...t, retry });                  // 仍留队，但记一次重试
    });

    write(keep);
    drop(gone);

    // 重放成功要把行上的"同步中"摘掉并通知页面：
    // 不 emit 的话打卡页会一直挂着「正在发送给家人…」，用户以为没同步上（重放的结果没人接住）。
    if (doneKeys.size) {
      doneKeys.forEach((k: any) => patchRow(String(k), { syncing: false }));
      emit('today.synced');
    }

    if (rejected.length) report('checkin_rejected', { n: rejected.length, code: rejected[0].code });
  } catch (e) {
    // 网络失败：只有本批每条 +1 次重试，超上限的移出队列（不再无限重投）
    const keep: QueueTask[] = [];
    const gone: QueueTask[] = [];
    read().forEach((t) => {
      if (!batchKeys.has(keyOf(t))) {
        keep.push(t);
        return;
      }
      const retry = t.retry + 1;
      if (retry > MAX_RETRY) {
        gone.push({ ...t, retry });
        return;
      }
      keep.push({ ...t, retry, at: Date.now() });
    });
    write(keep);
    drop(gone);

    // 指数退避 1s / 3s / 9s / 27s。
    // 取本批里重试次数最多的那条：只看 batch[0] 会让"第一条刚入队、后面几条已经失败 5 次"
    // 的批次一直按 1 秒重投，退避形同虚设。
    const r = Math.min(
      batch.reduce((max, t) => Math.max(max, t.retry), 0),
      4,
    );
    retryDelay = Math.min(1000 * Math.pow(3, r), 27000);
  }

  if (read().length) setTimeout(flush, retryDelay || 1200);
}

/** 今日是否有未同步项 */
export function dirtyToday(): boolean {
  const d = dateCN();
  return read().some((t) => t.date === d);
}
