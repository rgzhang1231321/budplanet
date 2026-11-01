/** utils/theme.ts —— 模式落地的 API 部分（CSS 变量改不到 page 节点，只能走 API） */
import type { Mode } from '../types/api';

export function applyMode(mode: Mode) {
  const elder = mode === 'elder';
  try {
    wx.setBackgroundColor({
      backgroundColor: elder ? '#FFFFFF' : '#F4F6F8',
      backgroundColorTop: elder ? '#FFFFFF' : '#F4F6F8',
      backgroundColorBottom: elder ? '#FFFFFF' : '#F4F6F8',
    });
    wx.setNavigationBarColor({
      frontColor: '#000000',
      backgroundColor: elder ? '#FFFFFF' : '#F4F6F8',
    });
  } catch (e) {
    /* 部分基础库不支持，忽略 */
  }
}

/** 读系统字号（getSystemInfo 已废弃，用新 API + 降级） */
export function systemFontSize(): number {
  try {
    const info = (wx as any).getAppBaseInfo ? (wx as any).getAppBaseInfo() : wx.getSystemInfoSync();
    return info.fontSizeSetting || 16;
  } catch (e) {
    return 16;
  }
}

let capsuleRight = 0;

/** 右上角胶囊位置：自绘导航必须避让，否则「齿轮 / ＋」会被胶囊盖住 */
export function navRightPad(): number {
  if (capsuleRight) return capsuleRight;
  try {
    const rect = wx.getMenuButtonBoundingClientRect();
    const win = wx.getWindowInfo ? wx.getWindowInfo() : wx.getSystemInfoSync();
    capsuleRight = Math.round(win.windowWidth - rect.left + 8);
  } catch (e) {
    capsuleRight = 100;
  }
  return capsuleRight;
}

export function statusBarHeight(): number {
  try {
    const win = wx.getWindowInfo ? (wx.getWindowInfo() as any) : wx.getSystemInfoSync();
    return win.statusBarHeight || 20;
  } catch (e) {
    return 20;
  }
}
