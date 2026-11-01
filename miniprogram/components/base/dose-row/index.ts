/**
 * DoseRow —— 用药行（5 槽位）
 * 计划时间 · 药名剂量 · 状态徽标 · 打卡时间戳（仅子女端）· 「⋯」菜单（仅管理员）
 * 挤压规则：徽标固定宽不收缩；剂量永不省略（安全信息）；只有药名可让位。
 */
import { diffMinutes, humanGap, tsToTime } from '../../../utils/time';

Component({
  options: { addGlobalClass: true },
  properties: {
    row: { type: Object, value: {} },
    admin: { type: Boolean, value: false },
    /** 子女端才展示打卡时间戳与异常标注 */
    showTaken: { type: Boolean, value: true },
    clickable: { type: Boolean, value: true },
    /** 老人端大行高（168rpx）+ 药名允许换行 2 行 */
    large: { type: Boolean, value: false },
    /** 老人端把「漏服」降级为「待服」：漏服对老人只有一种表达（不显示红色告警） */
    soft: { type: Boolean, value: false },
  },
  data: {
    takenText: '',
    subText: '',
    subWarn: false,
    badgeStatus: 'pending',
  },
  observers: {
    'row.**': function (row: any) {
      if (!row || !row.dose_id) return;
      const isSoft = this.data.soft;
      const taken = row.status === 'taken' && row.taken_at;
      let subText = '';
      let subWarn = false;
      if (taken) {
        const gap = diffMinutes(row.taken_at, row.due_ts);
        const dawn = new Date(row.taken_at + 8 * 3600e3).getUTCHours() < 5;
        if (Math.abs(gap) > 120 || dawn) {
          subText = `⚠ ${tsToTime(row.taken_at)} 打卡 · 比计划${gap >= 0 ? '晚' : '早'} ${humanGap(gap)}`;
          subWarn = true;
        } else {
          subText = `${tsToTime(row.taken_at)} 打卡`;
        }
      } else if (row.status === 'missed') {
        if (isSoft) {
          subText = '过了时间了，现在补记也算';
        } else {
          subText = `超时 ${humanGap(diffMinutes(Date.now(), row.due_ts))} 未打卡`;
          subWarn = true;
        }
      } else if (row.status === 'skipped') {
        subText = '今天不吃了';
      } else if (row.status === 'pending' && Date.now() - row.due_ts > 45 * 60000) {
        subText = '过了时间了，现在补记也算';
      }
      const badgeStatus = isSoft && row.status === 'missed' ? 'pending' : row.status;
      this.setData({ takenText: taken ? tsToTime(row.taken_at) : '', subText, subWarn, badgeStatus });
    },
    soft(v: boolean) {
      const status = (this.data.row || {}).status;
      this.setData({ badgeStatus: v && status === 'missed' ? 'pending' : status });
    },
  },
  methods: {
    onTap() {
      if (!this.data.clickable) return;
      this.triggerEvent('tap', { row: this.data.row });
    },
    onMenu(e: any) {
      this.triggerEvent('menu', { row: this.data.row });
    },
  },
});
