/**
 * utils/nav.ts —— 统一导航（P2-9）
 * 裸写 wx.navigateTo 时，分包加载失败 / 页面不存在是**静默失败**，用户只看到"点了没反应"。
 * 所有页面跳转都走这里，失败给一句人话。
 */
export function navTo(url: string) {
  wx.navigateTo({
    url,
    fail: () => wx.showToast({ title: '页面打开失败，稍后再试', icon: 'none' }),
  });
}

/** tabBar 页必须用 switchTab（navigateTo 到 tab 页会失败） */
export function switchTab(url: string) {
  wx.switchTab({
    url,
    fail: () => wx.showToast({ title: '页面打开失败，稍后再试', icon: 'none' }),
  });
}
