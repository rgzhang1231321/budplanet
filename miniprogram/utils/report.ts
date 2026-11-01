/**
 * utils/report.ts —— 埋点攒批（10 条或 5 秒一次）
 * 合规：参数绝不出现药品名 / 疾病名 / 剂量文本，只允许 dose_id / patient_id / 枚举 / fid。
 */
import { call } from './request';

interface Event {
  name: string;
  ts: number;
  params: Record<string, any>;
}

let buf: Event[] = [];
let timer: any = null;

const BANNED = /(药|片|mg|μg|胶囊|糖尿病|高血压)/i;

function sanitize(params: Record<string, any>): Record<string, any> {
  const out: Record<string, any> = {};
  Object.keys(params || {}).forEach((k) => {
    const v = params[k];
    if (typeof v === 'string' && BANNED.test(v)) return; // 健康信息一律丢弃
    if (typeof v === 'string' && v.length > 100) out[k] = v.slice(0, 100);
    else out[k] = v;
  });
  return out;
}

function flush() {
  if (!buf.length) return;
  const events = buf.splice(0, 10);
  call('report', 'batch', { events }, { silent: true }).catch(() => {
    /* 埋点失败静默，绝不影响主链路 */
  });
  if (buf.length) schedule();
}

function schedule() {
  if (timer) return;
  timer = setTimeout(() => {
    timer = null;
    flush();
  }, 5000);
}

export function report(name: string, params: Record<string, any> = {}) {
  buf.push({ name, ts: Date.now(), params: sanitize(params) });
  if (buf.length >= 10) flush();
  else schedule();
}
