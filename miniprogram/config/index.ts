/** 全局配置：广告位、模板 ID、接口地址、缓存版本 */

/**
 * 自建 .NET 10 API 地址（本地开发端口 5080）
 *
 * 开发者工具默认连本机 127.0.0.1:5080；真机（预览 / 真机调试）默认走腾讯云
 * HTTPS 服务（PROD_API_BASE），这样手机才能实地测试功能，且不必和电脑同 Wi-Fi。
 *
 * 想临时连本地后端联调（改了后端还没部署），在调试器控制台覆盖即可
 * （每次请求都读，改完下一次请求就生效）：
 *   wx.setStorageSync('api_base', 'http://192.168.x.x:5080')
 * 清掉覆盖：wx.removeStorageSync('api_base')
 */
const DEV_PORT = 5080;

/**
 * 正式环境地址（腾讯云 CVM + 腾讯云 TrustAsia 证书）。
 * 现在「真机 / 正式版」都默认走这里（手机实地测试就靠它）；开发者工具仍连本地。
 * request 合法域名已登记 https://api.apiyun.net，真机请求不会被拦。
 * 想让真机临时连本地后端，控制台覆盖：wx.setStorageSync('api_base', 'http://192.168.x.x:5080')
 */
const PROD_API_BASE = 'https://api.apiyun.net';

/** develop 开发版 / trial 体验版 / release 正式版 */
function envVersion(): string {
  try {
    const info: any = (wx as any).getAccountInfoSync();
    return (info && info.miniProgram && info.miniProgram.envVersion) || 'develop';
  } catch (e) {
    return 'develop';
  }
}

// platform 只在启动时取一次（getSystemInfoSync 比 getStorageSync 重）
let platform = '';
try {
  platform = (wx.getSystemInfoSync() as any).platform || '';
} catch (e) {
  /* ignore */
}

/**
 * 当前请求该用哪个地址。每次请求都调用 —— 这样控制台改 `api_base` 不用重编译。
 * 开发者工具默认 127.0.0.1:5080（本地后端）；真机（预览/真机调试）默认走云端 HTTPS。
 */
export function apiBase(): string {
  try {
    const override = wx.getStorageSync('api_base');
    if (typeof override === 'string' && override) return override.replace(/\/+$/, '');
  } catch (e) {
    /* ignore */
  }
  if (envVersion() === 'release') return PROD_API_BASE;
  return platform === 'devtools'
    ? `http://127.0.0.1:${DEV_PORT}`
    : PROD_API_BASE;
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

/**
 * ⚠️⚠️ 广告位 unitId：**必须去微信公众平台开流量主、建好广告位后填进来** ⚠️⚠️
 *
 * 位置：mp.weixin.qq.com → 流量主 → 广告位管理 → 新建广告位，拿到的 16 位 unitId 填在下面。
 *   a      = 打卡成功页底部 banner
 *   b      = 看板/药品/周报/解锁记录页 banner（子女端）
 *   bElder = 老人端首页 banner（单独一个位，便于老人端单独关停）
 *   c      = 插屏（子女端，返回药品列表时）
 *   d      = 激励视频（看 15 秒换权益）
 *
 * 留空 = 全站不展示广告（AdSlot 静默折叠为 0 高，不会留白块），`ad_show` 埋点也不会触发。
 * 所以「config.adOn 配了但一个广告都不出来」的第一嫌疑就是这里还是空串 —— 不是开关没生效。
 * 未开通流量主时**不要**为了"看起来有广告"去填别人的 unitId：unitId 与 appid 不匹配会直接加载失败。
 *
 * ── 广告的两个开关：出广告必须【两个都满足】────────────────────
 *  ① 总开关：数据库 app_config.runtime.adOn（后端下发，不发版即可改）
 *  ② 广告位：下面这五个 unitId（要开流量主才会有）
 *
 * 当前状态：adOn = false（V007 关闭），unitId 全空 → 广告全线关闭。
 * 想开启时按顺序做：
 *   1. 微信公众平台开流量主 → 建广告位 → 把 unitId 填进下面（哪个位先用就先填哪个，
 *      不必一次性填全；没填的位自动静默折叠，不会留白块）
 *   2. 新建 db/migrations/V00N__ads_on.sql 把 adOn 写回 true（**追加新脚本，不要改 V007**）
 *   3. bash db/apply.sh 生效
 * 只想临时试一下：控制台执行 wx.setStorageSync('ad_off', true) 可本地强制全关（仅本机生效）。
 */
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
//
// ⚠️⚠️ 上线前必须换成语义匹配的模板（服药提醒 / 漏服提醒 / 用药周报）⚠️⚠️
// 现在这三个 ID 是从后台模板库里挑的"字段结构最接近"的现成模板，其中 MISSED 挂的是
// 「工作任务待办通知」—— 给子女发的漏服告警长成一条待办任务，语义对不上。
// 微信《小程序运营规范》2.5.21 第 3 条：下发"与用户订阅时所理解的服务或内容严重不符"的
// 消息属违规，处理方式是删除模板或封禁订阅消息接口（等于提醒功能整体停摆）。
// 这不是前端代码能修的问题：模板 ID 必须在微信后台重新申请，审核通过后回来改这里。
// 前端只此一处持有模板 ID（utils/subscribe.ts 只读 TMPL_IDS），改这里即可，勿在页面里写死。
export const TMPL_IDS: Record<'CHECKIN' | 'MISSED' | 'WEEKLY', string> = {
  CHECKIN: 'goqfpenKaS9IILIzItZUpt1SaNjZ5XcHgHqIEbitS_w', // 药剂更换提醒 10596（服药剂量 short_thing7 / 服药时间 time6）
  MISSED: 'Dv3W1DnoL-bxD5GRf8NJ3aC35z0DoqkOCrUw2QQDEaQ', // 工作任务待办通知 49925（处理人 thing18 / 时间 time3 / 办理事项 thing14）
  WEEKLY: 'Ip4U864D5NGg6gG4ITj5CN9kMSxLysQNZpEq70va8Rs', // 周报更新提醒 1542（统计时间 time5 / 备注 thing6）
};

export const APP_VERSION = '1.0.0';
export const CACHE_VERSION = 'v1';

/**
 * ⚠️ 上线前必须替换：客服邮箱（客户尚未提供真实邮箱，此处留空）。
 *
 * 为什么留空不写 support@example.com：example.com 是假地址，用户点了复制、发出去的邮件
 * 永远没有回音，比"没有邮箱"更糟（隐私政策 §八 要求联系邮箱是法定必备要素）。
 * 留空时「关于」页只显示微信客服会话入口（button open-type="contact"，不需要邮箱）。
 * 客户给出真实邮箱后填进来，页面会自动多出一行「客服邮箱 + 复制」。
 */
export const SUPPORT_EMAIL = '';

// 打卡单批上限（契约：超限 1005）
export const SUBMIT_BATCH_MAX = 8;
