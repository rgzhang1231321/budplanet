/** 全局配置：广告位、模板 ID、接口地址、缓存版本 */

/**
 * 自建 .NET 10 API 地址（本地开发端口 5080）
 *
 * 为什么不能只写 127.0.0.1：开发者工具里它就等于这台电脑，
 * 但「预览」跑在手机上时，127.0.0.1 指的是手机自己 —— 请求必然连不上，
 * 而页面只会弹一句「保存失败」，看起来像后端坏了。
 * 所以真机走本机局域网地址（手机与电脑必须同一 Wi-Fi）。
 *
 * 换电脑 / 换 Wi-Fi / IP 变了：改 DEV_LAN_HOST；
 * 或在调试器控制台临时覆盖（每次请求都读，改完下一次请求就生效）：
 *   wx.setStorageSync('api_base', 'http://192.168.x.x:5080')
 * 清掉覆盖：wx.removeStorageSync('api_base')
 */
const DEV_LAN_HOST = '192.168.3.9'; // 本机 ipconfig 里的 IPv4
const DEV_PORT = 5080;

// platform 只在启动时取一次（getSystemInfoSync 比 getStorageSync 重）
let platform = '';
try {
  platform = (wx.getSystemInfoSync() as any).platform || '';
} catch (e) {
  /* ignore */
}

/**
 * 当前请求该用哪个地址。每次请求都调用 —— 这样控制台改 `api_base` 不用重编译。
 * 开发者工具默认 127.0.0.1；真机（预览/真机调试）默认走局域网 IP。
 */
export function apiBase(): string {
  try {
    const override = wx.getStorageSync('api_base');
    if (typeof override === 'string' && override) return override.replace(/\/+$/, '');
  } catch (e) {
    /* ignore */
  }
  return platform === 'devtools'
    ? `http://127.0.0.1:${DEV_PORT}`
    : `http://${DEV_LAN_HOST}:${DEV_PORT}`;
}

// 后端已可用（server/，端口 5080）。默认连真后端；
// 需要设计稿口径的假数据时，控制台执行 wx.setStorageSync('use_mock', true) 即可整站切回 mock。
export const USE_MOCK = false;

/** 运行时开关：默认读 USE_MOCK，控制台 wx.setStorageSync('use_mock', false) 可切真后端 */
export function useMock(): boolean {
  try {
    const v = wx.getStorageSync('use_mock');
    if (v === true || v === false) return v;
  } catch (e) {
    /* ignore */
  }
  return USE_MOCK;
}

// 广告位：未开通流量主时留空，AdSlot 会自动折叠为 0 高，不破坏布局
export const AD_UNITS = {
  a: '',
  b: '',
  bElder: '',
  c: '',
  d: '',
};

// 订阅消息模板：已按微信后台「功能 → 订阅消息 → 我的模板」实际选用的三套填写，
// 与 db/migrations/V004__set_subscribe_template_ids.sql、后端 PushTaskBuilder.TemplateData 一一对应。
// 换模板时三处一起改：本文件 + V00N 新脚本 + TemplateData（字段序号也变）。
export const TMPL_IDS: Record<'CHECKIN' | 'MISSED' | 'WEEKLY', string> = {
  CHECKIN: 'goqfpenKaS9IILIzItZUpt1SaNjZ5XcHgHqIEbitS_w', // 药剂更换提醒 10596（服药剂量 short_thing7 / 服药时间 time6）
  MISSED: 'Dv3W1DnoL-bxD5GRf8NJ3aC35z0DoqkOCrUw2QQDEaQ', // 工作任务待办通知 49925（处理人 thing18 / 时间 time3 / 办理事项 thing14）
  WEEKLY: 'Ip4U864D5NGg6gG4ITj5CN9kMSxLysQNZpEq70va8Rs', // 周报更新提醒 1542（统计时间 time5 / 备注 thing6）
};

export const APP_VERSION = '1.0.0';
export const CACHE_VERSION = 'v1';

// 打卡单批上限（契约：超限 1005）
export const SUBMIT_BATCH_MAX = 8;
