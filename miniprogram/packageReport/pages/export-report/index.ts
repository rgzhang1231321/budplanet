/**
 * packageReport/pages/export-report —— P1-6 复诊报告（近 30 天）
 * 合规红线：不含诊断 / 建议 / 「医嘱」；完成率标注为「用户记录的统计」，不是依从性评估。
 * 数据来自 report.export（v1.6），前端只负责排版与存图。
 */
import { bootstrap } from '../../../store/user';
import { get } from '../../../store/index';
import { call } from '../../../utils/request';
import { logClient, logError } from '../../../utils/logger';
import { report } from '../../../utils/report';

/** 存图用的画布逻辑尺寸（CSS px），实际像素 = 该值 × dpr */
const CANVAS_W = 750;
const CANVAS_H = 1060;
/** 节点查询兜底超时：拿不到节点就立刻走失败分支，绝不让按钮悬着 */
const TO_NODE = 2500;
/** 生成临时文件超时：正常 <500ms，超时说明这条路径在当前环境不通，换下一条 */
const TO_EXPORT = 3000;
/** 写相册超时：开发者工具里回调不返回（环境限制），快速收尾；真机要留时间给系统权限弹窗 */
const TO_ALBUM_DEVTOOLS = 1500;
const TO_ALBUM_DEVICE = 15000;

/**
 * 给异步调用加超时兜底。
 * 为它存在的理由：wx.saveImageToPhotosAlbum 在开发者工具里实测既不 success 也不 fail
 * （其它 wx 异步在个别基础库上也可能这样）——只要一路 await 下去，saving 就永远复位不了，
 * 按钮永久卡死。所以每个 wx 异步都必须能"到点自己 reject"。
 */
function withTimeout<T>(p: Promise<T>, ms: number, label: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      reject(new Error(`${label}超时(${ms}ms)`));
    }, ms);
    p.then(
      (v) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        resolve(v);
      },
      (e) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        reject(e);
      },
    );
  });
}

function errText(e: any): string {
  return (e && (e.message || e.errMsg)) || String(e || '');
}

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

  /**
   * 保存为图片：把报告画到 canvas，再存相册。画的是同一份数据，不是截图。
   *
   * 为什么每一步都要超时：实测（开发者工具）wx.saveImageToPhotosAlbum 既不 success 也不 fail，
   * 一路 await 下去 saving 就永远复位不了，按钮永久卡死在不可点状态、页面也看不到任何提示。
   * 现在的顺序是「离屏画布优先 → 节点查询兜底」，每步都有超时，
   * 且 try/catch/finally 单一出口复位 saving：成功、失败、超时都不会静默。
   */
  async onSaveImage() {
    if (this.data.saving || this.data.loadError) return;
    this.setData({ saving: true });
    try {
      const filePath = await this.renderToTempFile();
      await this.saveToAlbum(filePath);
      this.safeToast('已存到相册', 'success');
    } catch (err: any) {
      if (err && err.isAlbumDenied) {
        this.askAlbumPermission();
      } else if (err && err.isAlbumTimeout && this.isDevtools()) {
        // 开发者工具不实现相册写入（回调不返回），图片本身已经生成成功，只说环境限制
        this.safeToast('图片已生成，开发者工具不支持存相册');
      } else if (err && err.isAlbumError) {
        logError(err, 'report_export_album', { domain: 'report', action: 'save_image' });
        this.safeToast('保存相册失败，请重试');
      } else {
        logError(err, 'report_export_canvas', { domain: 'report', action: 'save_image' });
        this.safeToast('生成图片失败，请重试');
      }
    } finally {
      // 唯一复位点：成功、失败、异常、超时都从这里出去
      if (!this.isUnloaded) this.setData({ saving: false });
    }
  },

  /** 画图 + 导出临时文件。离屏画布优先，不通再走节点查询。 */
  async renderToTempFile(): Promise<string> {
    const dpr = this.getDpr();
    const fails: string[] = [];
    // 1) 离屏画布：不等 DOM，是最稳的一条
    const off = this.createOffscreenCanvas(dpr);
    if (off) {
      try {
        return await withTimeout(this.exportFrom(off, dpr, 'offscreen'), TO_EXPORT, '离屏 canvas 导出');
      } catch (e) {
        fails.push(`offscreen: ${errText(e)}`);
      }
    }
    // 2) 兜底：页面里的 #reportCanvas 节点（查询本身带超时）
    try {
      const node = await this.queryCanvasNode();
      return await withTimeout(this.exportFrom(node, dpr, 'node'), TO_EXPORT * 2, '节点 canvas 导出');
    } catch (e) {
      fails.push(`node: ${errText(e)}`);
    }
    throw new Error(fails.join(' | ') || '没有可用画布');
  },

  /** 在指定画布上画报告并导出临时文件路径 */
  async exportFrom(canvas: any, dpr: number, path: string): Promise<string> {
    this.savePath = path;
    const pxW = CANVAS_W * dpr;
    const pxH = CANVAS_H * dpr;
    canvas.width = pxW;
    canvas.height = pxH;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('canvas 2d context 不可用');
    // 同一个画布可能被复用，先清掉上一次的缩放，避免越画越大
    if (typeof ctx.setTransform === 'function') ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.scale(dpr, dpr);
    this.drawReport(ctx, CANVAS_W, CANVAS_H);
    return await this.toTempFilePath(canvas, pxW, pxH);
  },

  /** 建离屏画布；拿不到就返回 null（交给节点兜底） */
  createOffscreenCanvas(dpr: number): any {
    try {
      const create = (wx as any).createOffscreenCanvas;
      if (typeof create !== 'function') return null;
      const c = create.call(wx, { type: '2d', width: CANVAS_W * dpr, height: CANVAS_H * dpr });
      return c && typeof c.getContext === 'function' ? c : null;
    } catch (e) {
      return null;
    }
  },

  /** 查 canvas 节点，带超时：回调不进来（工具里会直接抛）也必须能 reject，否则按钮会悬死 */
  queryCanvasNode(): Promise<any> {
    return new Promise((resolve, reject) => {
      let settled = false;
      const finish = (fn: () => void) => {
        if (settled) return;
        settled = true;
        if (this.nodeTimer) {
          clearTimeout(this.nodeTimer);
          this.nodeTimer = 0;
        }
        fn();
      };
      this.nodeTimer = setTimeout(() => finish(() => reject(new Error('canvas 节点查询超时'))), TO_NODE);
      try {
        wx.createSelectorQuery()
          .select('#reportCanvas')
          .fields({ node: true, size: true } as any)
          .exec((res: any[]) => {
            finish(() => {
              const node = res && res[0] && res[0].node;
              if (!node) {
                reject(new Error('canvas 节点不存在'));
                return;
              }
              resolve(node);
            });
          });
      } catch (e) {
        // 查询对象在个别环境（工具自动化上下文）会直接抛，这里必须当成"这条路径失败"收掉
        finish(() => reject(e));
      }
    });
  },

  /** 导出临时文件：显式给输出像素，不依赖默认值 */
  toTempFilePath(canvas: any, pxW: number, pxH: number): Promise<string> {
    return new Promise((resolve, reject) => {
      wx.canvasToTempFilePath({
        canvas,
        // 采集区域用 canvas 默认（=canvas.width/height），只显式指定输出像素：
        // 2d canvas 与旧 canvas 的 width/height 语义不同，显式传容易裁掉一块
        destWidth: pxW,
        destHeight: pxH,
        success: (r: any) => {
          const p = r && r.tempFilePath;
          if (p) resolve(p);
          else reject(new Error('canvasToTempFilePath 未返回 tempFilePath'));
        },
        fail: (e: any) => reject(new Error((e && e.errMsg) || 'canvasToTempFilePath 失败')),
      } as any);
    });
  },

  /** 存相册；失败统一包成 isAlbumError，超时包成 isAlbumTimeout，权限类单独标记 */
  async saveToAlbum(filePath: string): Promise<void> {
    const dev = this.isDevtools();
    try {
      await withTimeout(this.albumWrite(filePath), dev ? TO_ALBUM_DEVTOOLS : TO_ALBUM_DEVICE, '写入相册');
    } catch (e: any) {
      // 不是相册自己 fail 出来的（即超时：回调没返回），单独标记
      if (e && !e.isAlbumError) e.isAlbumTimeout = true;
      throw e;
    }
  },

  albumWrite(filePath: string): Promise<void> {
    return new Promise((resolve, reject) => {
      wx.saveImageToPhotosAlbum({
        filePath,
        success: () => resolve(),
        fail: (e: any) => {
          const msg = (e && e.errMsg) || 'saveImageToPhotosAlbum 失败';
          const err: any = new Error(msg);
          err.isAlbumError = true;
          err.isAlbumDenied = /auth|deny|denied|authorize|permission/i.test(msg);
          reject(err);
        },
      });
    });
  },

  /** 相册权限被拒：引导去设置（保留原有分支） */
  askAlbumPermission() {
    if (this.isUnloaded) return;
    wx.showModal({
      title: '需要相册权限',
      content: '在设置里允许「保存到相册」后，再点一次保存。',
      confirmText: '去设置',
      success: (m) => {
        if (m.confirm) wx.openSetting({});
      },
    });
  },

  /** 页面已销毁就别再弹 toast（会报 setData/界面调用失败） */
  safeToast(title: string, icon: any = 'none') {
    if (this.isUnloaded) return;
    wx.showToast({ title, icon });
  },

  /** 当前是否在开发者工具里跑（工具对相册写入/节点查询的支持与真机不同） */
  isDevtools(): boolean {
    if (this._isDevtools !== undefined) return this._isDevtools;
    let dev = false;
    try {
      const info: any = typeof wx.getDeviceInfo === 'function' ? wx.getDeviceInfo() : wx.getSystemInfoSync();
      dev = !!info && info.platform === 'devtools';
    } catch (e) {
      dev = false;
    }
    this._isDevtools = dev;
    return dev;
  },

  getDpr(): number {
    let d = 2;
    try {
      if (typeof wx.getWindowInfo === 'function') d = wx.getWindowInfo().pixelRatio || 2;
    } catch (e) {
      d = 2;
    }
    // 上限 3：再大的画布只涨内存不涨清晰度
    return d > 3 ? 3 : d;
  },

  onUnload() {
    this.isUnloaded = true;
    // 不留悬挂定时器：页面销毁后再回调会 setData 报错
    if (this.nodeTimer) {
      clearTimeout(this.nodeTimer);
      this.nodeTimer = 0;
    }
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
