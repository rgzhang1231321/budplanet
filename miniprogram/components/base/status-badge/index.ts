/**
 * StatusBadge —— 四态三重编码：颜色 + 图标形状 + 中文标签
 * status 字面量直接来自后端枚举，组件内零映射、不得出现 late。
 */
const MAP: Record<string, { glyph: string; label: string }> = {
  taken: { glyph: '✓', label: '已服' },
  pending: { glyph: '', label: '待服' },
  missed: { glyph: '!', label: '漏服' },
  skipped: { glyph: '—', label: '跳过' },
};

Component({
  options: { addGlobalClass: true },
  properties: {
    status: { type: String, value: 'pending' },
    /** elder 走大号（64rpx），由页面根节点变量控制 */
    big: { type: Boolean, value: false },
  },
  data: { glyph: '', label: '待服' },
  observers: {
    status(v: string) {
      const m = MAP[v] || MAP.pending;
      this.setData({ glyph: m.glyph, label: m.label });
    },
  },
  lifetimes: {
    attached() {
      const m = MAP[this.data.status] || MAP.pending;
      this.setData({ glyph: m.glyph, label: m.label });
    },
  },
});
