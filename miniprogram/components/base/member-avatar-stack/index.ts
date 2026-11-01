/** MemberAvatarStack —— 「家人看见了」的视觉证明（48rpx，最多 3 个依次点亮） */
Component({
  options: { addGlobalClass: true },
  properties: {
    members: { type: Array, value: [] },
    lit: { type: Boolean, value: true },
    max: { type: Number, value: 3 },
  },
  data: { list: [] as any[], more: 0 },
  observers: {
    'members, max'(members: any[], max: number) {
      const arr = (members || []).slice(0, max).map((m: any) => ({
        key: m.openid || m.nick,
        initial: (m.nick || '家').slice(0, 1),
      }));
      this.setData({ list: arr, more: Math.max(0, (members || []).length - max) });
    },
  },
});
