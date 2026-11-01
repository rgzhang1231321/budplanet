/**
 * utils/logger.ts —— 客户端日志上报（进后端 app_logs，source=client）
 *
 * 存在的理由：模拟器/真机上"点了没反应"这类问题，用户没法描述、开发者看不到控制台。
 * 上报到服务端后，一条 SQL 就能看到哪一页、什么错误、什么时候。
 *
 * 三条纪律：
 * 1. 上报失败绝不再触发上报 —— 否则网络一断就自己把自己递归打死；
 * 2. 同一条 message 每分钟最多一次 —— onError 循环抛错时不会把库刷爆；
 * 3. 只传错误本身，不传业务数据（药名、剂量、家人称呼）—— 日志库不该变成健康档案。
 */
import { apiBase, useMock } from '../config/index';
import { getToken } from './auth';

type Level = 'error' | 'warn' | 'info';

const seen: Record<string, number> = {};
const WINDOW_MS = 60_000;

export function logClient(level: Level, message: string, extra: Record<string, any> = {}, stack?: string) {
  try {
    const msg = String(message || 'unknown').slice(0, 1000);
    const key = `${level}|${msg.slice(0, 80)}`;
    const now = Date.now();
    if (seen[key] && now - seen[key] < WINDOW_MS) return;
    seen[key] = now;

    // mock 模式下后端不一定在，报上去只会让控制台多一条 fail
    if (useMock()) return;

    let page = '';
    try {
      const pages = getCurrentPages();
      const cur = pages[pages.length - 1] as any;
      page = (cur && cur.route) || '';
    } catch (e) {
      /* 启动早期没有页面栈 */
    }

    let system = '';
    try {
      const info: any = wx.getSystemInfoSync();
      system = `${info.platform || ''} ${info.system || ''} ${info.version || ''}`.trim();
    } catch (e) {
      /* 忽略 */
    }

    wx.request({
      url: `${apiBase()}/api/log/client`,
      method: 'POST',
      timeout: 5000,
      header: {
        'content-type': 'application/json',
        Authorization: `Bearer ${getToken() || ''}`,
      },
      data: {
        level,
        message: msg,
        stack: stack ? String(stack).slice(0, 4000) : '',
        page,
        scene: extra.scene || '',
        appVersion: '',
        system,
        ts: now,
        extra,
      },
      // 关键：这里必须是空的，不能再调 logClient
      fail: () => {},
    });
  } catch (e) {
    /* 上报本身不能抛，业务链路优先 */
  }
}

/** 把 Error 对象拆成 message + stack 上报。 */
export function logError(err: any, scene: string, extra: Record<string, any> = {}) {
  const msg =
    (err && (err.message || err.errMsg)) ||
    (typeof err === 'string' ? err : '') ||
    'unknown error';
  const stack = err && err.stack ? String(err.stack) : '';
  logClient('error', msg, { ...extra, scene }, stack);
}
