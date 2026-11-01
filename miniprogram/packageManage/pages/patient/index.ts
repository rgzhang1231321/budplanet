/** packageManage/pages/patient —— P25 服药人管理 */
import { bootstrap, loadFamily, refreshToday } from '../../../store/user';
import { get } from '../../../store/index';
import { call, get as apiGet } from '../../../utils/request';

Page({
  data: {
    list: [] as any[],
    showForm: false,
    name: '',
    alias: '',
    editingUid: '',
    saving: false,
    limitText: '',
  },

  async onShow() {
    await bootstrap().catch(() => {});
    await this.load();
  },

  async load() {
    const s = get();
    try {
      const d: any = await apiGet('patient', 'list', { fid: s.family.fid }, { silent: true });
      const meds: any = await apiGet('medicine', 'list', { fid: s.family.fid }, { silent: true });
      const list = (d.patients || []).map((p: any) => ({
        uid: p.uid,
        name: p.name,
        alias: p.alias,
        initial: (p.alias || p.name || '家').slice(0, 1),
        count: (meds.items || []).filter((m: any) => m.uid === p.uid).length,
        isSelf: !!p.selfOpenid && p.selfOpenid === s.user.openid,
      }));
      this.setData({ list, limitText: `本产品不限服药人数，多人协同免费` });
    } catch (e) {
      /* ignore */
    }
  },

  onAdd() {
    this.setData({ showForm: true, name: '', alias: '', editingUid: '' });
  },

  onOpen(e: any) {
    const p = this.data.list.find((x: any) => x.uid === e.currentTarget.dataset.uid);
    if (!p) return;
    this.setData({ showForm: true, name: p.name, alias: p.alias, editingUid: p.uid });
  },

  onName(e: any) {
    this.setData({ name: e.detail.value });
  },
  onAlias(e: any) {
    this.setData({ alias: e.detail.value });
  },
  onClose() {
    this.setData({ showForm: false });
  },

  async onSave() {
    const name = (this.data.name || '').trim();
    if (!name) {
      wx.showToast({ title: '填一下称呼', icon: 'none' });
      return;
    }
    if (this.data.saving) return;
    this.setData({ saving: true });
    const s = get();
    // 冷启没拉到 family.info（真机连不上后端时就是这样）会带空 fid 去请求，
    // 后端只会回"缺少 fid"，用户看到的却是一句没头没尾的失败。先拦在这里说人话。
    if (!s.family.fid) {
      this.setData({ saving: false });
      wx.showToast({ title: '家庭信息没加载到，检查网络后重进本页', icon: 'none' });
      return;
    }
    try {
      if (this.data.editingUid) {
        await call('patient', 'update', {
          fid: s.family.fid,
          uid: this.data.editingUid,
          name,
          alias: (this.data.alias || name).trim(),
        });
      } else {
        await call('patient', 'create', {
          fid: s.family.fid,
          name,
          alias: (this.data.alias || name).trim(),
        });
      }
      await loadFamily();
      await refreshToday();
      await this.load();
      this.setData({ showForm: false, saving: false });
      wx.showToast({ title: '已保存', icon: 'success' });
    } catch (e: any) {
      this.setData({ saving: false });
      wx.showToast({ title: (e && e.msg) || '保存失败', icon: 'none' });
    }
  },

  onArchive(e: any) {
    const uid = e.currentTarget.dataset.uid;
    const p = this.data.list.find((x: any) => x.uid === uid);
    wx.showModal({
      title: '删除服药人',
      content: `删除「${(p && p.alias) || ''}」后，他的药品与提醒也会一起移除。历史记录保留。`,
      confirmText: '删除',
      confirmColor: '#E0402F',
      success: async (r) => {
        if (!r.confirm) return;
        const s = get();
        try {
          await call('patient', 'archive', { fid: s.family.fid, uid });
          await loadFamily();
          await this.load();
          this.setData({ showForm: false });
        } catch (e2: any) {
          wx.showToast({ title: (e2 && e2.msg) || '删不掉', icon: 'none' });
        }
      },
    });
  },
});
