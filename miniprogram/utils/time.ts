/**
 * utils/time.ts —— 东八区日期与时间
 * 禁令：全项目禁用 new Date().toISOString().slice(0,10)
 *      东八区 08:00 前会被算成"昨天"，打卡记错日、完成率算错。
 */

const CN_OFFSET = 8 * 3600 * 1000;

/** 东八区自然日 YYYY-MM-DD */
export function dateCN(ts: number = Date.now()): string {
  const d = new Date(ts + CN_OFFSET);
  const m = `${d.getUTCMonth() + 1}`.padStart(2, '0');
  const day = `${d.getUTCDate()}`.padStart(2, '0');
  return `${d.getUTCFullYear()}-${m}-${day}`;
}

/** 东八区当日分钟数 0–1439（与契约 slot 同义） */
export function minuteOfDay(ts: number = Date.now()): number {
  const d = new Date(ts + CN_OFFSET);
  return d.getUTCHours() * 60 + d.getUTCMinutes();
}

export function pad2(n: number): string {
  return `${n}`.padStart(2, '0');
}

/** 480 → "08:00" */
export function minuteToTime(minute: number): string {
  return `${pad2(Math.floor(minute / 60))}:${pad2(minute % 60)}`;
}

/** "08:00" → 480 */
export function timeToMinute(t: string): number {
  const [h, m] = t.split(':').map(Number);
  return (h || 0) * 60 + (m || 0);
}

/** 东八区某日 00:00 的 UTC 毫秒 */
export function dayStartTs(date: string): number {
  const [y, m, d] = date.split('-').map(Number);
  return Date.UTC(y, m - 1, d) - CN_OFFSET;
}

/** 东八区某日某 slot 的 UTC 毫秒 */
export function slotTs(date: string, slot: number): number {
  return dayStartTs(date) + slot * 60 * 1000;
}

/** 打卡时间戳 → "08:12" */
export function tsToTime(ts: number): string {
  if (!ts) return '';
  return minuteToTime(minuteOfDay(ts));
}

const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

export function weekdayOf(date: string): string {
  const [y, m, d] = date.split('-').map(Number);
  return WEEK[new Date(Date.UTC(y, m - 1, d)).getUTCDay()];
}

/** "10月30日 周五" */
export function humanDate(date: string): string {
  const [, m, d] = date.split('-').map(Number);
  return `${m}月${d}日 ${weekdayOf(date)}`;
}

/** "8月14日" */
export function shortDate(date: string): string {
  const [, m, d] = date.split('-').map(Number);
  return `${m}月${d}日`;
}

/** 相对当日偏移 n 天的东八区日期 */
export function addDays(date: string, n: number): string {
  return dateCN(dayStartTs(date) + n * 86400000 + 3600 * 1000);
}

/** 本周一（东八区） */
export function weekStartOf(date: string = dateCN()): string {
  const [y, m, d] = date.split('-').map(Number);
  const wd = new Date(Date.UTC(y, m - 1, d)).getUTCDay(); // 0=周日
  const diff = wd === 0 ? -6 : 1 - wd;
  return addDays(date, diff);
}

/** 偏差分钟数（正数=晚于计划） */
export function diffMinutes(a: number, b: number): number {
  return Math.round((a - b) / 60000);
}

/** 人话化偏差：62 → "1 小时 2 分钟" */
export function humanGap(min: number): string {
  const m = Math.abs(min);
  if (m < 60) return `${m} 分钟`;
  const h = Math.floor(m / 60);
  const rest = m % 60;
  return rest ? `${h} 小时 ${rest} 分钟` : `${h} 小时`;
}

/** 倒计时 mm:ss */
export function countdown(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  return `${pad2(Math.floor(total / 60))}:${pad2(total % 60)}`;
}

/** "早上/上午/中午/下午/晚上" + 时间 */
export function partOfDay(minute: number): string {
  if (minute < 5 * 60) return '凌晨';
  if (minute < 9 * 60) return '早上';
  if (minute < 11 * 60 + 30) return '上午';
  if (minute < 13 * 60) return '中午';
  if (minute < 18 * 60) return '下午';
  return '晚上';
}
