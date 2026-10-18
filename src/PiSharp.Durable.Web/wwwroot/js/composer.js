// PiSharp Durable Web: the composer. Enter sends, Shift+Enter breaks the line; "When busy" chooses follow-up, steer or reject. Each message
// keeps one request id until the device admits it, so a retry after a lost response never sends twice. Typing "/" opens the slash menu:
// /compact /reset /tasks /new /fork /name /model /thinking /help (arrows, Tab and Enter pick).
import { $, $$, uuid } from './core.js';

const LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh'];

export class Composer {
  constructor(app) {
    this.app = app;
    this.box = $('#composerInput');
    this.menu = $('#slashMenu');
    this.whenBusy = $('#whenBusy');
    this.items = $$('.slash-item', this.menu);
    this.active = 0;
    this.dismissed = false;
    this.box.addEventListener('input', () => { this.active = 0; this.update(); });
    this.box.addEventListener('keydown', (e) => this.keydown(e));
    this.items.forEach((item) => item.addEventListener('mousedown', (e) => { e.preventDefault(); this.pick(item); }));
  }

  shown() { return this.items.filter((i) => !i.hidden); }

  update() {
    const match = /^\/(\S*)$/.exec(this.box.value);
    if (!match) this.dismissed = false;
    if (!match || this.dismissed) { this.menu.hidden = true; return; }
    this.items.forEach((i) => { i.hidden = !i.getAttribute('data-cmd').startsWith(match[1]); });
    const list = this.shown();
    this.menu.hidden = list.length === 0;
    this.active = Math.min(this.active, Math.max(0, list.length - 1));
    list.forEach((i, n) => { i.classList.toggle('active', n === this.active); i.setAttribute('aria-selected', n === this.active ? 'true' : 'false'); });
  }

  get menuOpen() { return !this.menu.hidden; }

  keydown(e) {
    if (this.menu.hidden) {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); this.submit(); }
      return;
    }
    const list = this.shown();
    if (e.key === 'ArrowDown') { e.preventDefault(); this.active = (this.active + 1) % list.length; this.update(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); this.active = (this.active - 1 + list.length) % list.length; this.update(); }
    else if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); this.pick(list[this.active]); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); this.dismissed = true; this.menu.hidden = true; }
  }

  pick(item) {
    const cmd = item.getAttribute('data-cmd');
    if (item.hasAttribute('data-needs-arg')) { this.box.value = `/${cmd} `; this.menu.hidden = true; this.box.focus(); return; }
    this.box.value = '';
    this.menu.hidden = true;
    this.command(cmd, '');
  }

  fill(text) {
    this.box.value = text;
    this.box.focus();
    this.update();
  }

  /** Sends the box's text, or runs its slash command. */
  async submit() {
    const text = this.box.value.trim();
    if (!text) return;
    const slash = /^\/(\w+)(?:\s+([\s\S]*))?$/.exec(text);
    if (slash && this.items.some((i) => i.getAttribute('data-cmd') === slash[1])) {
      this.box.value = '';
      this.update();
      await this.command(slash[1], (slash[2] || '').trim());
      return;
    }
    await this.send(text);
  }

  async send(text) {
    const { device, session, agent } = this.app.selection;
    if (!device) return;
    const pendingKey = `pisharp-durable-request:${device}/${session}/${agent}`;
    let pending = null;
    try { pending = JSON.parse(sessionStorage.getItem(pendingKey) || 'null'); } catch { /* storage unavailable */ }
    const requestId = pending && pending.content === text ? pending.requestId : uuid();
    try { sessionStorage.setItem(pendingKey, JSON.stringify({ content: text, requestId })); } catch { /* storage unavailable */ }
    const whenBusy = this.whenBusy.value;
    try {
      await this.app.agentApi().post('/messages', { content: text, whenBusy, requestId });
      try { sessionStorage.removeItem(pendingKey); } catch { /* storage unavailable */ }
      this.box.value = '';
      this.app.hideBanner();
    } catch (error) {
      if (error.code === 'busy') {
        try { sessionStorage.removeItem(pendingKey); } catch { /* storage unavailable */ }
        this.app.showBanner(`<strong>${this.app.agentName()} is busy.</strong> Reject if busy refused your message (409 busy). Choose Follow-up or Steer to queue it instead.`);
      } else {
        this.app.toastError(error);
      }
    }
  }

  async command(cmd, arg) {
    const app = this.app;
    try {
      switch (cmd) {
        case 'compact': {
          await app.agentApi().post('/compact', arg ? { instructions: arg } : {});
          app.toast('Compaction requested. /tasks shows its outcome.');
          break;
        }
        case 'reset':
          await app.agentApi().post('/reset', arg ? { note: arg } : {});
          app.toast('New context started.');
          break;
        case 'tasks':
          app.openTab('ins-tasks');
          break;
        case 'new':
          await app.newAgent(arg);
          break;
        case 'fork': {
          const parts = arg.split(/\s+/).filter(Boolean);
          const entry = parts[0] && /^#?\d+$/.test(parts[0]) ? Number(parts.shift().replace('#', '')) : app.lastAnswerEntry();
          if (!entry) { app.toast('Nothing to fork yet.', 'error'); break; }
          await app.fork(entry, parts.join(' '));
          break;
        }
        case 'name':
          if (!arg) { app.toast('Usage: /name <name>', 'error'); break; }
          await app.renameAgent(arg);
          break;
        case 'model':
          app.dialogs.modelPicker();
          break;
        case 'thinking':
          if (!LEVELS.includes(arg)) { app.toast(`Usage: /thinking ${LEVELS.join('|')}`, 'error'); break; }
          await app.setThinking(arg);
          break;
        case 'help':
          window.bootstrap.Modal.getOrCreateInstance(document.getElementById('modal-help')).show();
          break;
      }
    } catch (error) {
      app.toastError(error);
    }
  }
}
