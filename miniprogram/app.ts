import { bootstrap, resetSession } from './store/user';
import { setSessionLostHandler } from './utils/auth';
import { primeSubscribeState } from './utils/subscribe';
import { flush } from './utils/queue';
import { report } from './utils/report';
import { logClient, logError } from './utils/logger';
import { dateCN } from './utils/time';
import { USE_MOCK } from './config/index';
import { mockGrant, mockReset } from './mock/index';

App({
  globalData: {
    launchQuery: {} as Record<string, string>,
    coldStart: true,
    lastShowAt: 0,
    today: dateCN(),
    /** 仅 mock 模式挂载：演示/联调时可在控制台发名额、清假数据 */
    __mock: USE_MOCK ? { grant: mockGrant, reset: mockReset } : null,
  },

  onLaunch(query: Record<string, any>) {
    this.globalData.launchQuery = query || {};
    // 1002（不是家庭成员 / 账号数据被清）时由请求层回调，把登录态与内存态一起清掉
    setSessionLostHandler(resetSession);
    // 预热订阅授权缓存：TAP 内不能 await，只能提前拿
    primeSubscribeState();
    report('app_launch', { scene: (query && query.scene) || '', cold: 1 });
    bootstrap().catch(() => {
      /* 登录失败不阻断：本地缓存仍可看今日、打卡走离线队列 */
    });
  },

  onShow() {
    this.globalData.lastShowAt = Date.now();
    flush();
    const today = dateCN();
    if (today !== this.globalData.today) {
      this.globalData.today = today;
      bootstrap(true).catch(() => {});
    }
  },

  onHide() {
    flush();
  },

  onError(msg: string) {
    // 埋点只留截断后的摘要（PrivacyFilter 会拦非 ASCII 与长文本）；原文进 app_logs 才可排查
    report('error_js', { msg: String(msg || '').slice(0, 100) });
    logClient('error', String(msg || 'unknown'), { scene: 'app_onerror' });
  },

  onUnhandledRejection(res: any) {
    const reason = res && res.reason;
    logError(reason, 'app_rejection');
    report('error_rejection', {
      msg: String((reason && (reason.message || reason.errMsg)) || '').slice(0, 100),
    });
  },

  onPageNotFound(res: any) {
    logClient('warn', `page not found: ${(res && res.path) || ''}`, { scene: 'page_not_found' });
  },
});
