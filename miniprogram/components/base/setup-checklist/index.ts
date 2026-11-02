/**
 * setup-checklist —— 首次设置清单（P0-3）
 * 三行：① 添加服药人 ② 添加第一个药 ③ 把邀请码发给家人。
 * 原先加完药、关掉订阅引导后，主路径上再没有任何指向邀请的动作，
 * 而 PRD 漏斗（设置完成 → 邀请至少 1 人 ≥ 50%）正卡在这一步。
 * 每一行只在自己是"下一步"时可点，避免用户点已完成项。
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    title: { type: String, value: '还差一步就能开始' },
    hasPatient: { type: Boolean, value: false },
    hasMed: { type: Boolean, value: false },
    invited: { type: Boolean, value: false },
  },
  data: {
    step1Done: false,
    step2Done: false,
    step3Done: false,
    /** 当前该做哪一步：1/2/3，全做完为 0 */
    active: 3,
  },
  observers: {
    'hasPatient, hasMed, invited': function (hasPatient: boolean, hasMed: boolean, invited: boolean) {
      const active = !hasPatient ? 1 : !hasMed ? 2 : !invited ? 3 : 0;
      this.setData({ step1Done: hasPatient, step2Done: hasMed, step3Done: invited, active });
    },
  },
  methods: {
    onRow1() {
      if (!this.data.step1Done) this.triggerEvent('addPatient');
    },
    onRow2() {
      if (this.data.step1Done && !this.data.step2Done) this.triggerEvent('addMed');
    },
    onRow3() {
      if (this.data.step2Done && !this.data.step3Done) this.triggerEvent('invite');
    },
    onClose() {
      this.triggerEvent('close');
    },
  },
});
