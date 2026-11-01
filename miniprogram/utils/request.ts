/**
 * utils/request.ts —— 统一请求层（替代云开发的 wx.cloud.callFunction）
 * 纪律：页面里禁止裸写 wx.request；钉死 15s 超时；1001 静默续期后重放一次；
 *      打卡链路不得被登录态阻塞（离线队列在 utils/queue.ts 兜底）。
 */
import { apiBase, useMock } from '../config/index';
import { mockCall } from '../mock/index';
import { getToken, notifySessionLost, refreshSession } from './auth';
import { logClient } from './logger';

export interface CallOptions {
  method?: 'GET' | 'POST';
  silent?: boolean;
  /** 1001 续期后只重放一次的标记，由本模块内部设置，调用方不要传 */
  replayed?: boolean;
  /** 1009 读操作重试过一次的标记，由本模块内部设置，调用方不要传 */
  retried?: boolean;
}

export class ApiError extends Error {
  code: number;
  /**
   * 后端原样下发的 msg。**页面统一读 `e.msg`**（几十处 `(e && e.msg) || '保存失败'`），
   * 只把它塞进 Error.message 的话，e.msg 恒为 undefined —— 1002/1003/1005
   * 这些带人话的错误全被吞成没头没尾的"保存失败"，线上根本分不清是越权、
   * 参数错还是后端没起。两个字段都留，别只留一个。
   */
  msg: string;
  constructor(code: number, msg: string) {
    super(msg);
    this.code = code;
    this.msg = msg;
  }
}

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

/**
 * 1002「不是这个家庭的成员」= 本地登录态/家庭态和服务端对不上（被移出家庭、家庭被删、
 * 服务端数据被清）。原来的处理是 toast + reLaunch 首页 —— 这是**死循环**：
 * 首页 onShow 一次并发 3 个请求，全部 1002，又 3 次 reLaunch，页面反复重建，
 * 用户看到的就是"一直在闪、白屏"。
 *
 * 现在：3 秒内只处理一次（并发失败合并成一次），跳启动页重新登录；
 * 60 秒内超过 3 次说明重登也救不回来，直接停手只提示一次，绝不再跳。
 */
let lastResetAt = 0;
let resetWindowAt = 0;
let resetCount = 0;

function handleSessionLost(msg: string, silent: boolean) {
  const now = Date.now();
  if (now - lastResetAt < 3000) return;
  lastResetAt = now;

  if (now - resetWindowAt > 60000) {
    resetWindowAt = now;
    resetCount = 0;
  }
  resetCount += 1;
  if (resetCount > 3) {
    if (resetCount === 4) {
      logClient('error', '会话重置熔断：60s 内连续 1002 超过 3 次', { scene: 'session_lost' });
      wx.showToast({ title: '账号数据异常，请退出小程序重新打开', icon: 'none' });
    }
    return;
  }

  if (!silent) wx.showToast({ title: msg || '登录已失效，请重新进入', icon: 'none' });
  notifySessionLost();
  // 跳启动页而不是首页：首页 onShow 会立刻再发一轮请求，跳回去等于原地打转
  setTimeout(() => wx.reLaunch({ url: '/pages/launch/index' }), 800);
}

function handleError(code: number, msg: string, silent: boolean) {
  if (code === 1002) {
    handleSessionLost(msg, silent);
    return;
  }
  // 1003 = 是成员、但没有这个操作的权限。踢回首页毫无意义（身份没变，回去照样没权限），
  // 只提示。真正该拦的入口在页面上提前拦（老人端点管理功能）。
  if (code === 1003) {
    if (!silent) wx.showToast({ title: msg || '没有权限', icon: 'none' });
    return;
  }
  if (code === 1005 && !silent) {
    wx.showToast({ title: msg || '填写有误', icon: 'none' });
  }
  // 1004 由调用方弹 UnlockNudge；1009 由调用方决定重试或入队
}

export async function call<T = any>(domain: string, action: string, data: Record<string, any> = {}, opts: CallOptions = {}): Promise<T> {
  const method = opts.method || 'POST';
  const env = await raw(domain, action, data, method);
  if (env.code === 0) return env.data as T;

  // 1001 只续期重放一次：用入参标记传染「已重放过」，不用模块变量复位——
  // 模块变量在递归里会被复位成 false，重放再失败就无限重登，把登录接口打爆。
  if (env.code === 1001) {
    if (!opts.replayed) {
      let renewed = false;
      try {
        await refreshSession();
        renewed = true;
      } catch (e) {
        renewed = false; // 续期失败不再重试，落到下面抛错
      }
      if (renewed) return call<T>(domain, action, data, { ...opts, replayed: true });
    }
    if (!opts.silent) wx.showToast({ title: '登录已过期，请重新进入', icon: 'none' });
    logClient('warn', `${domain}.${action} → code=1001 续期后仍未登录`, {
      scene: 'api_error',
      domain,
      action,
      code: 1001,
    });
    throw new ApiError(1001, env.msg || '登录已过期，请重新进入');
  }
  // 契约 §一：1009「写操作入队 / **读操作重试 1 次**」。
  // 只重试 GET：写操作（submit/correct/unlock…）重投等于重复提交，宁可交给队列按幂等键去处理。
  if (env.code === 1009 && method === 'GET' && !opts.retried) {
    logClient('warn', `${domain}.${action} → code=1009 读接口重试一次`, {
      scene: 'api_retry',
      domain,
      action,
      code: 1009,
    });
    return call<T>(domain, action, data, { ...opts, retried: true });
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
