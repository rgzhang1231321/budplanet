/**
 * utils/request.ts —— 统一请求层（替代云开发的 wx.cloud.callFunction）
 * 纪律：页面里禁止裸写 wx.request；钉死 15s 超时；1001 静默续期后重放一次；
 *      打卡链路不得被登录态阻塞（离线队列在 utils/queue.ts 兜底）。
 */
import { apiBase, useMock } from '../config/index';
import { mockCall } from '../mock/index';
import { getToken, refreshSession } from './auth';
import { logClient } from './logger';

export interface CallOptions {
  method?: 'GET' | 'POST';
  silent?: boolean;
}

export class ApiError extends Error {
  code: number;
  constructor(code: number, msg: string) {
    super(msg);
    this.code = code;
  }
}

let retriedOnce = false;

function qs(data: Record<string, any>): string {
  const parts = Object.keys(data || {})
    .filter((k) => data[k] !== undefined && data[k] !== null && data[k] !== '')
    .map((k) => `${encodeURIComponent(k)}=${encodeURIComponent(data[k])}`);
  return parts.length ? `?${parts.join('&')}` : '';
}

async function raw(domain: string, action: string, data: Record<string, any>, method: 'GET' | 'POST') {
  if (useMock()) return mockCall(domain, action, data);
  const base = apiBase();
  return new Promise<{ code: number; data: any; msg: string }>((resolve, reject) => {
    wx.request({
      url: `${base}/api/${domain}/${action}${method === 'GET' ? qs(data) : ''}`,
      method,
      timeout: 15000,
      header: {
        Authorization: `Bearer ${getToken() || ''}`,
        'content-type': 'application/json',
      },
      data: method === 'GET' ? undefined : data,
      success: (r) => resolve(r.data as any),
      fail: (err) => {
        // 网络层失败（超时/域名未配置/后端没起/真机用的是 127.0.0.1）：这类问题最容易被当成"按钮没反应"
        const reason = (err && err.errMsg) || 'unknown';
        logClient('error', `请求失败 ${domain}.${action} @${base}: ${reason}`, {
          scene: 'request_fail',
          domain,
          action,
        });
        // 必须包成 ApiError 抛出：页面只 toast e.msg，直接抛原始对象会让所有网络问题
        // 都显示成"保存失败"，分不清是后端没起、地址不对还是手机不在同一网络。
        reject(new ApiError(-1, `连不上服务器（${base}）`));
      },
    });
  });
}

function handleError(code: number, msg: string, silent: boolean) {
  if (code === 1002 || code === 1003) {
    wx.showToast({ title: msg || '请重新进入', icon: 'none' });
    setTimeout(() => wx.reLaunch({ url: '/pages/home/index' }), 600);
  } else if (code === 1005 && !silent) {
    wx.showToast({ title: msg || '填写有误', icon: 'none' });
  }
  // 1004 由调用方弹 UnlockNudge；1009 由调用方决定重试或入队
}

export async function call<T = any>(domain: string, action: string, data: Record<string, any> = {}, opts: CallOptions = {}): Promise<T> {
  const method = opts.method || 'POST';
  const env = await raw(domain, action, data, method);
  if (env.code === 0) return env.data as T;

  if (env.code === 1001 && !retriedOnce) {
    retriedOnce = true;
    try {
      await refreshSession();
      retriedOnce = false;
      return await call<T>(domain, action, data, opts);
    } catch (e) {
      retriedOnce = false;
    }
  }
  handleError(env.code, env.msg, !!opts.silent);
  // 业务失败也落库：code=1005「点了没反应」、1004「名额不够」都从这条线出来。
  // 用 warn 而不是 error —— 它是被预期处理的业务结果，不是系统故障。1001 已在上面续期，不报。
  logClient('warn', `${domain}.${action} → code=${env.code} ${env.msg || ''}`.trim(), {
    scene: 'api_error',
    domain,
    action,
    code: env.code,
  });
  throw new ApiError(env.code, env.msg || '请求失败');
}

/** 读接口便捷封装：GET + query */
export function get<T = any>(domain: string, action: string, query: Record<string, any> = {}, opts: CallOptions = {}) {
  return call<T>(domain, action, query, { ...opts, method: 'GET' });
}
