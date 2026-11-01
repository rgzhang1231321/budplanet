Component({
  options: { addGlobalClass: true },
  properties: {
    icon: { type: String, value: 'pill' },
    title: { type: String, value: '' },
    desc: { type: String, value: '' },
    actionText: { type: String, value: '' },
  },
  methods: {
    onAction() {
      this.triggerEvent('action');
    },
  },
});
