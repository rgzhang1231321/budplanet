/**
 * packageSettings/pages/unlocks —— P44 解锁记录（原会员页已删除）
 * 产品内无任何付费出口：这里只展示"看视频换来的东西"，全页无价格、无支付入口、无"会员/升级"字样。
 * 空态不是死胡同：说明能解锁什么 + 给一个去周报的下一步。
 */
import { bootstrap } from '../../../store/user';
import { get } from '../../../store/index';
import { unlockLog } from '../../../utils/ad';
import { shortDate } from '../../../utils/time';

const TITLE: Record<string, { title: string; desc: string; icon: string }> = {
  med_slot: { title: '额外药品名额', desc: '永久有效，按名额累加', icon: 'pill' },
  week_report: { title: '周报详细漏服分析', desc: '含每次漏服的具体时间与趋势', icon: 'report' },
  export: { title: '复诊报告导出', desc: '单次生成，永久留存', icon: 'doc' },
};

Page({
  data: {
    count: 0,
    credits: 0,
    medUnlocks: [] as any[],
    reportUnlocks: [] as any[],
    adOn: false,
    isEmpty: true,
  },

  async onShow() {
    await bootstrap().catch(() => {});
    const s = get();
    const log = unlockLog();
    const map = (kind: string) =>
      log
        .filter((x) => x.kind === kind)
        .map((x) => ({
          id: x.id,
          title: (TITLE[kind] || TITLE.med_slot).title,
          desc: (TITLE[kind] || TITLE.med_slot).desc,
          icon: (TITLE[kind] || TITLE.med_slot).icon,
          date: shortDate(new Date(x.at + 8 * 3600e3).toISOString().slice(0, 10)),
        }));
    this.setData({
      count: log.length,
      credits: s.user.entitlement.credits || 0,
      medUnlocks: map('med_slot'),
      reportUnlocks: map('week_report'),
      adOn: !!s.user.config.adOn,
      isEmpty: log.length === 0,
    });
  },

  onGo() {
    wx.switchTab({ url: '/pages/weekly/index' });
  },
});
