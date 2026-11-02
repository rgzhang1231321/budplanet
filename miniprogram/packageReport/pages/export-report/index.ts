/**
 * packageReport/pages/export-report —— P1-6 复诊报告（近 30 天）
 * 合规红线：不含诊断 / 建议 / 「医嘱」；完成率标注为「用户记录的统计」，不是依从性评估。
 * 数据来自 report.export（v1.6），前端只负责排版与存图。
 */
import { bootstrap } from '../../../store/user';
import { get } from '../../../store/index';
import { call } from '../../../utils/request';
import { logClient } from '../../../utils/logger';
import { report } from '../../../utils/report';

Page({
  data: {
    patientName: '家人',
    rangeText: '',
    rate: 0,
    missCount: 0,
    medCount: 0,
    meds: [] as any[],
    loadError: false,
    saving: false,
  },

  async onLoad() {
    await bootstrap().catch(() => {});
    await this.load();
  },

  async load() {
    const s = get();
    try {
      const d: any = await call('report', 'export', { fid: s.family.fid, days: 30 });
      const p = (s.family.patients || [])[0];
      this.setData({
        loadError: false,
        patientName: (p && p.alias) || '家人',
        rangeText: d && d.range ? `${d.range.start} - ${d.range.end}` : '',
        rate: (d && d.rate) || 0,
        missCount: (d && d.missCount) || 0,
        medCount: (d && d.medCount) || 0,
        meds: (d && d.meds) || [],
      });
    } catch (e: any) {
      logClient('warn', `report.export 没取到 ${(e && e.code) || ''} ${(e && e.msg) || ''}`.trim(), {
        scene: 'report_export',
        domain: 'report',
        action: 'export',
        code: (e && e.code) || 0,
      });
      this.setData({ loadError: true });
    }
  },

  onRetry() {
    this.setData({ loadError: false });
    this.load();
  },

  onShareAppMessage() {
    return {
      title: `${this.data.patientName}近 30 天用药记录`,
      path: '/pages/launch/index?from=report',
    };
  },

  /** 保存为图片：把报告画到离屏 canvas，再存相册。画的是同一份数据，不是截图。 */
  onSaveImage() {
    if (this.data.saving || this.data.loadError) return;
    this.setData({ saving: true });
    const that = this;
    const q = wx.createSelectorQuery();
    q.select('#reportCanvas')
      .fields({ node: true, size: true })
      .exec((res) => {
        try {
          const canvas = (res[0] && res[0].node) as any;
          if (!canvas) throw new Error('canvas not ready');
          const ctx = canvas.getContext('2d');
          const dpr = (wx.getWindowInfo ? wx.getWindowInfo().pixelRatio : 2) || 2;
          const W = 750;
          const H = 1060;
          canvas.width = W * dpr;
          canvas.height = H * dpr;
          ctx.scale(dpr, dpr);
          that.drawReport(ctx, W, H);
          wx.canvasToTempFilePath({
            canvas,
            success: (r) => {
              wx.saveImageToPhotosAlbum({
                filePath: r.tempFilePath,
                success: () => {
                  that.setData({ saving: false });
                  wx.showToast({ title: '已存到相册', icon: 'success' });
                },
                fail: () => {
                  that.setData({ saving: false });
                  wx.showModal({
                    title: '需要相册权限',
                    content: '在设置里允许「保存到相册」后，再点一次保存。',
                    confirmText: '去设置',
                    success: (m) => {
                      if (m.confirm) wx.openSetting({});
                    },
                  });
                },
              });
            },
            fail: () => {
              that.setData({ saving: false });
              wx.showToast({ title: '生成图片失败，稍后再试', icon: 'none' });
            },
          });
        } catch (err) {
          that.setData({ saving: false });
          wx.showToast({ title: '生成图片失败，稍后再试', icon: 'none' });
        }
      });
  },

  drawReport(ctx: any, W: number, H: number) {
    const d = this.data;
    const pad = 48;
    // 底
    ctx.fillStyle = '#FFFFFF';
    ctx.fillRect(0, 0, W, H);
    // 顶栏色条
    ctx.fillStyle = '#2E6BE6';
    ctx.fillRect(0, 0, W, 8);

    let y = 64;
    ctx.textBaseline = 'top';
    ctx.fillStyle = '#8A94A0';
    ctx.font = '22px sans-serif';
    ctx.fillText('替你记着吃药 · 用药记录', pad, y);

    y += 40;
    ctx.fillStyle = '#1A1F26';
    ctx.font = 'bold 34px sans-serif';
    ctx.fillText(`${d.patientName} · 近 30 天用药记录`, pad, y);

    y += 48;
    ctx.fillStyle = '#5C6B7A';
    ctx.font = '24px sans-serif';
    ctx.fillText(d.rangeText, pad, y);

    // 三格数据
    y += 56;
    const boxW = (W - pad * 2 - 24) / 3;
    const stats = [
      { n: `${d.rate}%`, l: '完成率' },
      { n: `${d.missCount}`, l: '漏服次数' },
      { n: `${d.medCount}`, l: '在服药品' },
    ];
    stats.forEach((s, i) => {
      const x = pad + i * (boxW + 12);
      ctx.fillStyle = '#F7F9FB';
      this.roundRect(ctx, x, y, boxW, 96, 10);
      ctx.fillStyle = '#1A1F26';
      ctx.font = 'bold 34px sans-serif';
      ctx.fillText(s.n, x + 18, y + 18);
      ctx.fillStyle = '#8A94A0';
      ctx.font = '20px sans-serif';
      ctx.fillText(s.l, x + 18, y + 62);
    });

    // 表格
    y += 132;
    ctx.fillStyle = '#8A94A0';
    ctx.font = '22px sans-serif';
    ctx.fillText('药品', pad, y);
    ctx.fillText('用法', pad + 300, y);
    ctx.textAlign = 'right';
    ctx.fillText('完成率', W - pad, y);
    ctx.textAlign = 'left';
    y += 34;
    ctx.strokeStyle = '#E8EDF2';
    ctx.beginPath();
    ctx.moveTo(pad, y);
    ctx.lineTo(W - pad, y);
    ctx.stroke();

    const maxRows = 8;
    const rows = d.meds.slice(0, maxRows);
    rows.forEach((m: any) => {
      y += 16;
      ctx.fillStyle = '#1A1F26';
      ctx.font = '24px sans-serif';
      this.clipText(ctx, m.name || '', pad, y, 280);
      ctx.fillStyle = '#5C6B7A';
      ctx.font = '22px sans-serif';
      this.clipText(ctx, m.usage || '', pad + 300, y, W - pad - (pad + 300) - 90);
      ctx.fillStyle = '#1A1F26';
      ctx.font = 'bold 24px sans-serif';
      ctx.textAlign = 'right';
      ctx.fillText(`${m.rate}%`, W - pad, y);
      ctx.textAlign = 'left';
      y += 30;
      ctx.strokeStyle = '#F2F5F8';
      ctx.beginPath();
      ctx.moveTo(pad, y);
      ctx.lineTo(W - pad, y);
      ctx.stroke();
    });

    // 底部合规声明
    const footY = H - 96;
    ctx.strokeStyle = '#E8EDF2';
    ctx.beginPath();
    ctx.moveTo(pad, footY - 16);
    ctx.lineTo(W - pad, footY - 16);
    ctx.stroke();
    ctx.fillStyle = '#8A94A0';
    ctx.font = '20px sans-serif';
    ctx.fillText('本报告由用户自行记录生成，不含任何诊断或用药建议。', pad, footY);
    ctx.fillText(`数据范围：${d.rangeText}`, pad, footY + 30);
  },

  roundRect(ctx: any, x: number, y: number, w: number, h: number, r: number) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
    ctx.fill();
  },

  /** 超长文本截断加省略号（canvas 不会自动换行） */
  clipText(ctx: any, text: string, x: number, y: number, maxW: number) {
    let s = text || '';
    if (ctx.measureText(s).width <= maxW) {
      ctx.fillText(s, x, y);
      return;
    }
    while (s.length > 1 && ctx.measureText(s + '…').width > maxW) s = s.slice(0, -1);
    ctx.fillText(s + '…', x, y);
  },
});
