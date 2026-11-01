Component({
  options: { addGlobalClass: true },
  properties: {
    rate: { type: Number, value: 0 },
    size: { type: Number, value: 320 },
    label: { type: String, value: '本周服药完成率' },
    delta: { type: String, value: '' },
    deltaDown: { type: Boolean, value: false },
  },
});
