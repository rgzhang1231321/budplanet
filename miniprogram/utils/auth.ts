/** utils/auth.ts —— 登录态存取与静默续期（身份只从 JWT 取，前端永不传 openid） */
import { apiBase, useMock } from '../config/index';
import { mockCall } from '../mock/index';

const TK = 'jwt_v1';
const RTK = 'jwt_refresh_v1';

export function getToken(): string {
  try {
    return wx.getStorageSync(TK) || '';
  } catch (e) {
    return '';
  }
}

export function setToken(token: string, refresh?: string) {
  try {
    wx.setStorageSync(TK, token);
    if (refresh) wx.setStorageSync(RTK, refresh);
  } catch (e) {
    /* ignore */
  }
}

export function clearToken() {
  try {
    wx.removeStorageSync(TK);
    wx.removeStorageSync(RTK);
  } catch (e) {
    /* ignore */
  }
}

function code2session(): Promise<string> {
  return new Promise((resolve) => {
    wx.login({
      success: (r) => resolve(r.code || 'mock_code'),
      fail: () => resolve('mock_code'),
    });
  });
}

/** 冷启必调；返回 user.login 的完整出参 */
export async function login(): Promise<any> {
  const code = await code2session();
  let env: { code: number; data: any; msg: string };
  if (useMock()) {
    env = await mockCall('user', 'login', { code });
  } else {
    env = await new Promise<any>((resolve, reject) => {
      wx.request({
        url: `${apiBase()}/api/user/login`,
        method: 'POST',
        data: { code },
        success: (r) => resolve(r.data),
        fail: reject,
      });
    });
  }
  if (env.code !== 0) throw new Error(env.msg || '登录失败');
  setToken(env.data.token || 'mock_token', env.data.refreshToken || '');
  return env.data;
}

/** 1001 时静默续期：重新 code2session，打卡链路不阻塞 */
export async function refreshSession(): Promise<void> {
  clearToken();
  await login();
}
