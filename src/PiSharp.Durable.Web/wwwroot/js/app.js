// PiSharp Durable Web: the network page. The hub's stream drives it: `network` frames carry the tree's HTML and every device's status;
// the selected agent's `view` frames carry its transcript items and panels as rendered fragments, and the device's `doc` frames its App
// State. Selecting an agent changes the URL (/d/{device}/s/{session}/a/{agent}), the hub's UI state (so a reload or a second tab opens
// on it), the stream's subscriptions and the inspector's panels. Every write goes through the hub's proxy to the device.
import { $, $$, api, deviceApi, fragment, uiState, toast, toastError, initCommon, showPane, restorePanels, savePanels, tokens, count, cost, escapeHtml, uuid, modal } from './core.js';
import { HubStream } from './stream.js';
import { applyItems, swapHtml, keepPinned } from './swap.js';
import { Tree } from './tree.js';
import { Dialogs, providerName } from './dialogs.js';
import { Composer } from './composer.js';
import { StateEditor } from './state-editor.js';
import { initContextBar } from './context-bar.js';

const enc = encodeURIComponent;
const STATUS_WORDS = { online: 'online', offline: 'offline', checking: 'checking', unauthorized: 'rejecting its token', mismatch: 'answering as another device', incompatible: 'on another API version' };

class App {
  constructor() {
    initCommon();
    restorePanels();
    this.devices = [];
    this.selection = { device: null, session: null, agent: null };
    this.live = {};
    this.offlineShown = null;
    this.subs = [];
    this.stream = new HubStream();
    this.tree = new Tree({ onSelect: (selection) => this.select(selection) });
    this.dialogs = new Dialogs(this);
    this.composer = new Composer(this);
    this.stateEditor = new StateEditor($('[data-state-tab]'), { send: (body) => this.agentApi().post('/state', body) });
    this.contextBar = { setUsed() {} };
    this.bind();
    this.stream.status = (status) => this.connection(status);
    this.stream.on('network', (frame) => this.network(frame));
    this.stream.start();
    window.addEventListener('popstate', () => this.select(this.fromPath(), { push: false }));
    $('#insPanes').addEventListener('pane', (e) => this.onTab(e.detail));
    const root = $('#app');
    const initial = { device: root.dataset.device || null, session: root.dataset.session || null, agent: root.dataset.agent ? Number(root.dataset.agent) : null };
    if (initial.device) this.select(initial, { push: false });
    else uiState.get('selection').then((saved) => {
      if (saved && saved.device && !this.selection.device) this.select(saved, { replace: true });
      else this.render();
    });
  }

  // ─── Selection ────────────────────────────────────────────────────────
  fromPath() {
    const m = /^\/d\/([^/]+)(?:\/s\/([^/]+)(?:\/a\/(\d+))?)?/.exec(location.pathname);
    if (!m) return { device: null, session: null, agent: null };
    return { device: decodeURIComponent(m[1]), session: m[2] ? decodeURIComponent(m[2]) : null, agent: m[2] ? Number(m[3] || 1) : null };
  }

  path(s) {
    if (!s.device) return '/';
    if (!s.session) return `/d/${enc(s.device)}`;
    return `/d/${enc(s.device)}/s/${enc(s.session)}/a/${s.agent || 1}`;
  }

  key(s = this.selection) {
    if (!s.device) return null;
    if (!s.session) return s.device;
    return `${s.device}/s/${s.session}/a/${s.agent || 1}`;
  }

  select(selection, { push = true, replace = false } = {}) {
    const next = { device: selection.device || null, session: selection.session || null, agent: selection.session ? Number(selection.agent || 1) : null };
    const same = next.device === this.selection.device && next.session === this.selection.session && next.agent === this.selection.agent;
    this.selection = next;
    const url = this.path(next);
    if (replace || (!push && location.pathname !== url)) history.replaceState(null, '', url);
    else if (push && !same && location.pathname !== url) history.pushState(null, '', url);
    uiState.set('selection', next);
    this.tree.select(this.key(next));
    if (!same) {
      this.resetAgentView();
      this.offlineShown = null;
    }
    this.render();
    this.subscribe();
    if (!same && this.agentReady()) this.loadPanels();
  }

  device(id = this.selection.device) {
    return this.devices.find((d) => d.id === id) || null;
  }

  session() {
    return this.device()?.tree?.sessions?.find((s) => s.id === this.selection.session) || null;
  }

  agents() {
    return (this.live.agents && this.live.agents.length ? this.live.agents : this.session()?.agents) || [];
  }

  agentName(id = this.selection.agent) {
    return this.agents().find((a) => a.id === id)?.name || (id === 1 ? 'Main' : `Agent #${id}`);
  }

  agentReady() {
    const d = this.device();
    return !!(d && d.status === 'online' && this.selection.session && this.selection.agent);
  }

  agentApi() {
    const { device, session, agent } = this.selection;
    const api = deviceApi(device);
    const base = `/sessions/${enc(session)}/agents/${agent}`;
    return {
      get: (p = '') => api.get(base + p),
      post: (p, body, options) => api.post(base + p, body, options),
      patch: (p, body, options) => api.patch(base + p, body, options),
      del: (p) => api.del(base + p),
    };
  }

  subscribe() {
    for (const sub of this.subs) this.stream.off(sub);
    this.subs = [];
    const { device, session, agent } = this.selection;
    const d = this.device();
    if (device && d && d.status === 'online') {
      const deviceKey = `${device}/device?docs=app.shared-state`;
      this.stream.on(deviceKey, (frame) => this.deviceFrame(frame));
      this.subs.push(deviceKey);
      if (session && agent) {
        const agentKey = `${device}/agent/${enc(session)}/${agent}`;
        this.stream.on(agentKey, (frame) => this.agentFrame(frame));
        this.subs.push(agentKey);
      }
    }
    this.stream.subscribe(this.subs);
  }

  // ─── Network ──────────────────────────────────────────────────────────
  network(frame) {
    const before = this.device();
    this.devices = frame.devices || [];
    this.tree.render(frame.html);
    this.tree.select(this.key());
    const online = this.devices.filter((d) => d.status === 'online').length;
    $$('[data-online-summary]').forEach((el) => { el.textContent = this.devices.length ? `${online} of ${this.devices.length} online` : 'No devices'; });
    const after = this.device();
    if (this.selection.session && after?.status === 'online' && after.tree && !after.tree.sessions.some((s) => s.id === this.selection.session)) {
      this.toast('That session is no longer on the device.');
      this.select({ device: this.selection.device, session: null, agent: null }, { replace: true });
      return;
    }
    const cameOnline = after?.status === 'online' && before?.status !== 'online';
    this.render();
    if (cameOnline) {
      this.subscribe();
      if (this.agentReady()) this.loadPanels();
    }
  }

  render() {
    const d = this.device();
    const ready = this.agentReady();
    $$('[data-needs-agent]').forEach((el) => { el.hidden = !ready; });
    const offline = !!(d && d.status !== 'online');
    $('#offlineCenter').hidden = !offline;
    $('#welcome').hidden = ready || offline;
    $$('[data-welcome="devices"]').forEach((el) => { el.hidden = this.devices.length === 0; });
    $$('[data-welcome="nodevices"]').forEach((el) => { el.hidden = this.devices.length > 0; });
    $$('[data-action="new-agent"]').forEach((el) => { if (!el.closest('.tree-row')) el.disabled = !ready; });
    if (offline && this.offlineShown !== d.id + d.status + (d.error || '')) {
      this.offlineShown = d.id + d.status + (d.error || '');
      fragment(`/ui/offline/${enc(d.id)}`).then((html) => { $('#offlineCenter').innerHTML = html; }).catch(() => {});
    }
    $$('[data-device-name]').forEach((el) => { el.textContent = d?.id || ''; });
    if (ready) this.renderHead();
    document.title = ready ? `${this.agentName()} · ${this.session()?.name || ''} · PiSharp Durable` : 'Network · PiSharp Durable';
  }

  renderHead() {
    const d = this.device();
    const session = this.session();
    const agents = this.agents();
    const byId = new Map(agents.map((a) => [a.id, a]));
    const line = [];
    for (let id = this.selection.agent, guard = 0; id != null && guard < 50; guard++) {
      const a = byId.get(id);
      if (!a) break;
      line.unshift(a);
      id = a.parentId ?? (a.kind === 'subagent' ? a.ownerAgentId : null);
    }
    const icon = (a) => a.kind === 'fork' ? 'bi-signpost-split' : a.kind === 'subagent' ? 'bi-arrow-return-right' : 'bi-chat-square';
    const crumbs = [
      `<button class="crumb" type="button" title="Device ${escapeHtml(d.id)}${d.info ? ' (' + escapeHtml(d.info.hostname) + ')' : ''}" data-crumb-device><i class="bi ${platformIcon(d)}"></i><span class="visually-hidden">device </span><span class="crumb-text-device">${escapeHtml(d.id)}</span></button>`,
      `<i class="bi bi-chevron-right crumb-sep"></i>`,
      `<button class="crumb" type="button" title="Workspace ${escapeHtml(session?.cwd || '')}" data-crumb-agent="1"><i class="bi bi-folder2-open"></i><span class="visually-hidden">workspace </span>${escapeHtml(session?.name || '')}</button>`,
    ];
    line.forEach((a, i) => {
      crumbs.push(`<i class="bi bi-chevron-right crumb-sep"></i>`);
      const kind = a.kind === 'fork' ? 'fork' : a.kind === 'subagent' ? 'subagent' : 'agent';
      crumbs.push(`<button class="crumb" type="button" title="${a.kind === 'fork' ? `Fork of agent #${a.parentId} at entry #${a.forkedAt}` : a.kind === 'subagent' ? 'Subagent' : 'Root agent'}" data-crumb-agent="${a.id}"${i === line.length - 1 ? ' aria-current="page"' : ''}><i class="bi ${icon(a)}"></i><span class="visually-hidden">${kind} </span>${escapeHtml(a.name)}</button>`);
    });
    if (!line.length) crumbs.push(`<i class="bi bi-chevron-right crumb-sep"></i>`, `<button class="crumb" type="button" aria-current="page"><i class="bi bi-chat-square"></i>${escapeHtml(this.agentName())}</button>`);
    $('[data-crumbs]').innerHTML = crumbs.join('');
    const current = byId.get(this.selection.agent);
    const source = $('[data-agent-source]');
    source.hidden = !current || current.kind === 'root';
    if (current?.kind === 'fork') source.textContent = `Forked from ${byId.get(current.parentId)?.name || 'agent #' + current.parentId} at entry #${current.forkedAt}. App State stays shared.`;
    else if (current?.kind === 'subagent') source.textContent = `A subagent owned by ${byId.get(current.ownerAgentId)?.name || 'agent #' + current.ownerAgentId}. It answers its owner's task.`;
    const path = (a) => {
      const names = [];
      for (let x = a, guard = 0; x && guard < 50; guard++) { names.unshift(x.name); x = byId.get(x.parentId ?? (x.kind === 'subagent' ? x.ownerAgentId : -1)); }
      return names.length > 1 ? `<span class="muted">${names.slice(0, -1).map(escapeHtml).join(' › ')} ›</span> ${escapeHtml(names.at(-1))}` : escapeHtml(names[0]);
    };
    const describe = (a) => a.kind === 'fork' ? `Forked at entry #${a.forkedAt}` : a.kind === 'subagent' ? `Owned by ${escapeHtml(byId.get(a.ownerAgentId)?.name || '#' + a.ownerAgentId)}` : 'Root agent';
    $('[data-agent-picker]').innerHTML = `<h6 class="dropdown-header">Agents in ${escapeHtml(session?.name || 'this session')}</h6>` + agents.map((a) =>
      `<a class="dropdown-item" href="${this.path({ ...this.selection, agent: a.id })}" data-nav><i class="bi ${a.id === this.selection.agent ? 'bi-check2' : icon(a)} mt-1"></i><span><span class="ap-path d-block">${path(a)}</span><span class="ap-src">${describe(a)}${a.busy ? ' · working' : ''}</span></span></a>`).join('');
  }

  // ─── Agent view ───────────────────────────────────────────────────────
  resetAgentView() {
    $('#thread').replaceChildren();
    $$('[data-live-panel]').forEach((el) => { el.innerHTML = ''; });
    $('[data-connecting]').hidden = false;
    $('#chatScroll').hidden = true;
    $('[data-empty-hero]').hidden = true;
    $('[data-working]').hidden = true;
    this.live = {};
    this.applyState({});
    this.hideBanner();
  }

  agentFrame(frame) {
    if (frame.type === 'view') {
      const scroller = $('#chatScroll');
      const firstPaint = scroller.hidden;
      $('[data-connecting]').hidden = true;
      scroller.hidden = false;
      $('[data-status="disconnected"]').hidden = true;
      let jobs = false;
      keepPinned(scroller, () => {
        applyItems($('#thread'), frame.items, frame.reset);
        jobs = (frame.items.upsert || []).some((u) => u.html.includes('job-card'));
        for (const [name, html] of Object.entries(frame.panels || {})) {
          const panel = $(`[data-live-panel="${name}"]`);
          if (panel) swapHtml(panel, html);
        }
        $('[data-empty-hero]').hidden = $('#thread').children.length > 0;
        this.applyState(frame.state || {});
      });
      if (firstPaint) scroller.scrollTop = scroller.scrollHeight;
      if (jobs) this.jobsChanged();
      this.renderHead();
    } else if (frame.type === 'disconnected') {
      $('[data-status="disconnected"]').hidden = false;
    } else if (frame.type === 'fault') {
      const code = frame.error?.code;
      this.toast(frame.error?.message || 'The agent stream failed.', 'error');
      if (code === 'not_found') this.select({ device: this.selection.device, session: this.selection.agent === 1 ? null : this.selection.session, agent: 1 }, { replace: true });
    }
  }

  applyState(s) {
    this.live = { ...this.live, ...s };
    const busy = !!s.busy;
    $('[data-status="working"]').hidden = !busy;
    $('[data-status="waiting"]').hidden = !(s.waiting > 0);
    $('[data-action="interrupt"]').disabled = !busy;
    $('[data-working]').hidden = !(s.running && !s.generating && !(s.waiting > 0));
    $$('[data-task-count]').forEach((el) => { el.hidden = !s.tasks; el.textContent = String(s.tasks || 0); });
    const activity = $('[data-activity-count]');
    if (activity) activity.textContent = s.tasks ? `${s.tasks} active` : 'Nothing running';
    $$('[data-live-status]').forEach((el) => {
      const which = el.getAttribute('data-live-status');
      el.hidden = which === 'working' ? !busy : which === 'waiting' ? !(s.waiting > 0) : busy || s.waiting > 0;
    });
    const me = (s.agents || []).find((a) => a.id === this.selection.agent);
    const bg = $('[data-live-background]');
    if (bg) { bg.hidden = !me?.background; bg.textContent = `${me?.background || 0} background`; }
    const u = s.usage || {};
    const stats = { tokens: tokens(u.tokens), cost: cost(u.cost), input: count(u.input), output: count(u.output), cacheRead: count(u.cacheRead), cacheWrite: count(u.cacheWrite),
      inputCost: cost(u.inputCost), outputCost: cost(u.outputCost), cacheReadCost: cost(u.cacheReadCost), cacheWriteCost: cost(u.cacheWriteCost) };
    for (const [name, text] of Object.entries(stats)) $$(`[data-stat="${name}"]`).forEach((el) => { el.textContent = text; });
    const tab = $('[data-session-tab]');
    if (tab) {
      const windowSize = Number(tab.getAttribute('data-window')) || 0;
      const used = Number(s.contextTokens) || 0;
      $('[data-ctx-used]', tab).textContent = tokens(used);
      $('[data-ctx-pct]', tab).textContent = windowSize ? Math.round(used / windowSize * 100) + '%' : '';
      $('[data-ctx-compacting]', tab).hidden = !s.compacting;
      const failure = $('[data-ctx-failure]', tab);
      failure.hidden = !s.compactionFailure;
      failure.textContent = s.compactionFailure ? `The last compaction failed: ${s.compactionFailure}` : '';
      this.contextBar.setUsed(used);
    }
    if (s.retry) this.showBanner(`<strong>Retrying.</strong> ${escapeHtml(s.retry.error)}`);
  }

  // ─── Panels ───────────────────────────────────────────────────────────
  panelPath(name) {
    const { device, session, agent } = this.selection;
    return name === 'runtime' ? `/ui/${enc(device)}/${enc(session)}/runtime` : `/ui/${enc(device)}/${enc(session)}/${agent}/${name}`;
  }

  loadPanels() {
    this.reloadPanels(['session', 'tools', 'extend', 'jobs']);
    const runtime = $('[data-runtime]');
    if (runtime) runtime.open = false;
  }

  async reloadPanels(names) {
    const key = this.key();
    await Promise.all(names.map(async (name) => {
      const container = $(`[data-panel="${name}"]`);
      if (!container) return;
      try {
        const html = await fragment(this.panelPath(name));
        if (key !== this.key()) return;
        swapHtml(container, html);
        if (name === 'session') this.sessionTabLoaded();
      } catch (error) {
        if (key === this.key()) container.innerHTML = `<div class="ins-section fs-7 text-danger">Could not load: ${escapeHtml(error.message)}</div>`;
      }
    }));
  }

  sessionTabLoaded() {
    const { device, session } = this.selection;
    this.contextBar = initContextBar($('[data-session-tab] .ctx-bar'), {
      save: (body) => deviceApi(device).patch(`/sessions/${enc(session)}/compaction`, body),
    });
    this.applyState(this.live);
  }

  jobsChanged() {
    clearTimeout(this.jobsTimer);
    this.jobsTimer = setTimeout(() => this.reloadPanels(['jobs']), 400);
  }

  onTab(name) {
    if (name === 'ins-tasks' && this.agentReady()) this.reloadPanels(['jobs']);
  }

  openTab(name) {
    showPane($('#insPanes'), name);
    $('#workspace').classList.remove('ins-collapsed');
    savePanels();
    if (window.innerWidth < 1200) window.bootstrap.Offcanvas.getOrCreateInstance($('#inspector')).show();
  }

  deviceFrame(frame) {
    if (frame.type === 'doc' && frame.kind === 'app.shared-state') this.stateEditor.update(frame.value, frame.revision);
  }

  // ─── Actions ──────────────────────────────────────────────────────────
  bind() {
    document.addEventListener('click', (e) => {
      const nav = e.target.closest('a[data-nav]');
      if (nav && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
        e.preventDefault();
        const url = new URL(nav.href, location.href);
        const m = /^\/d\/([^/]+)\/s\/([^/]+)\/a\/(\d+)/.exec(url.pathname);
        if (m) this.select({ device: decodeURIComponent(m[1]), session: decodeURIComponent(m[2]), agent: Number(m[3]) });
        return;
      }
      const crumb = e.target.closest('[data-crumb-agent], [data-crumb-device]');
      if (crumb) {
        if (crumb.hasAttribute('data-crumb-device')) this.select({ device: this.selection.device });
        else this.select({ ...this.selection, agent: Number(crumb.getAttribute('data-crumb-agent')) });
        return;
      }
      const fill = e.target.closest('[data-fill-composer]');
      if (fill) { this.composer.fill(fill.getAttribute('data-fill-composer')); return; }
      const scroll = e.target.closest('[data-scroll-to]');
      if (scroll) {
        e.preventDefault();
        document.querySelector(scroll.getAttribute('data-scroll-to'))?.scrollIntoView({ behavior: 'smooth', block: 'center' });
        return;
      }
      if (e.target.closest('[data-dismiss-banner]')) { this.hideBanner(); return; }
      const action = e.target.closest('[data-action]');
      if (action && !action.disabled) this.action(action.getAttribute('data-action'), action, e);
    });
    document.addEventListener('change', (e) => {
      const control = e.target.closest('[data-action-change]');
      if (control) this.change(control.getAttribute('data-action-change'), control);
    });
    $('[data-runtime]')?.addEventListener('toggle', (e) => { if (e.target.open) this.reloadPanels(['runtime']); });
    $('#cmp-auto')?.addEventListener('change', (e) => this.compaction({ enabled: e.target.checked }));
    $('#cmp-keep')?.addEventListener('change', (e) => this.compaction({ keepRecentTokens: Number(e.target.value) }));
    document.addEventListener('keydown', (e) => {
      const mac = /Mac/.test(navigator.platform);
      const combo = mac ? (e.ctrlKey && !e.altKey && !e.metaKey) : (e.altKey && e.shiftKey && !e.ctrlKey);
      if (combo && !e.repeat && !document.querySelector('.modal.show')) {
        const ws = $('#workspace');
        if (e.code === 'KeyN') { e.preventDefault(); ws.classList.toggle('tree-collapsed'); savePanels(); }
        if (e.code === 'KeyT') { e.preventDefault(); ws.classList.toggle('ins-collapsed'); savePanels(); }
      }
      if (e.key === 'Escape' && !e.repeat && !document.querySelector('.modal.show') && !this.composer.menuOpen && this.live.busy && this.agentReady()) {
        e.preventDefault();
        this.interrupt();
      }
    });
  }

  async action(name, el) {
    const data = el.dataset;
    const { device, session } = this.selection;
    try {
      switch (name) {
        case 'new-session': this.dialogs.newSession(data.device); break;
        case 'new-agent': await this.newAgent('', data.device, data.session); break;
        case 'check-device': {
          const result = await api('POST', `/api/devices/${enc(data.device)}/check`);
          this.toast(`${data.device} is ${STATUS_WORDS[result?.status] || result?.status}.`, result?.status === 'online' ? 'ok' : 'error');
          break;
        }
        case 'forget-identity':
          await api('POST', `/api/devices/${enc(data.device)}/forget-identity`);
          this.toast(`${data.device}'s identity forgotten. Checking again.`);
          break;
        case 'rename-session':
          this.dialogs.prompt({
            title: 'Rename session', label: 'Session name', value: data.name || '', submit: 'Save name',
            onSubmit: async (value) => { await deviceApi(data.device).patch(`/sessions/${enc(data.session)}`, { name: value }); this.toast('Name saved.'); this.reloadPanels(['session']); },
          });
          break;
        case 'rename-agent': {
          const target = data.agent ? { device: data.device, session: data.session, agent: Number(data.agent) } : this.selection;
          const current = data.name || this.agentName(target.agent);
          this.dialogs.prompt({
            title: 'Rename agent', label: 'Agent name', value: current, submit: 'Save name', hint: "A name can't be only digits, and only agent #1 can be called main.",
            onSubmit: async (value) => { await deviceApi(target.device).patch(`/sessions/${enc(target.session)}/agents/${target.agent}`, { name: value }); this.toast('Name saved.'); },
          });
          break;
        }
        case 'archive-session':
          this.dialogs.prompt({
            title: `Archive ${data.name}?`, text: `Stops all of its work and moves its files to archive/ on ${data.device}. Nothing is deleted.`, submit: 'Archive session', danger: true,
            onSubmit: async () => {
              await deviceApi(data.device).del(`/sessions/${enc(data.session)}`);
              this.toast(`${data.name} archived on ${data.device}.`);
              if (this.selection.session === data.session) this.select({ device: data.device });
            },
          });
          break;
        case 'fork': {
          const entry = Number(data.entry);
          this.dialogs.prompt({
            title: 'Fork conversation', text: `${this.agentName()} · entry #${entry}. The fork keeps the conversation up to that entry; both agents carry on separately. App State stays shared.`,
            label: 'Fork name (optional)', placeholder: 'Alternative approach', submit: 'Create fork',
            onSubmit: (value) => this.fork(entry, value),
          });
          break;
        }
        case 'copy-entry': {
          const text = [...el.closest('article').querySelectorAll('.msg-content')].map((c) => c.innerText).join('\n\n');
          await navigator.clipboard?.writeText(text);
          this.toast('Copied.');
          break;
        }
        case 'approve-job': {
          const approved = data.approved === 'true';
          await this.agentApi().post(`/jobs/${data.task}/approval`, { approved });
          this.toast(approved ? 'Approved. The job continues from its checkpoint.' : 'Declined. Nothing is published.');
          this.jobsChanged();
          break;
        }
        case 'stop-task':
          await deviceApi(device).post(`/sessions/${enc(session)}/tasks/${data.task}/abort`);
          this.toast('Stopped.');
          this.jobsChanged();
          break;
        case 'stop-all':
          await this.agentApi().post('/abort', { background: true });
          this.toast(`Every task of ${this.agentName()} stopped, including background jobs.`);
          break;
        case 'run-job':
          await this.agentApi().post('/jobs', { job: data.job });
          this.toast(`${data.title} started. It runs in the chat.`);
          this.jobsChanged();
          break;
        case 'withdraw':
          await this.agentApi().del(`/inbox/${data.submission}`);
          this.toast('Withdrawn. The device will not place it.');
          break;
        case 'interrupt': await this.interrupt(); break;
        case 'send': await this.composer.submit(); break;
        case 'answer-questions': await this.answer(el.closest('form'), false); break;
        case 'decline-questions': await this.answer(el.closest('form'), true); break;
        case 'review-plan': {
          const form = el.closest('form');
          const approved = data.approved === 'true';
          await this.agentApi().post(`/interactions/${form.dataset.task}`, { approved, feedback: approved ? undefined : form.querySelector('textarea')?.value || undefined });
          this.toast(approved ? 'Plan approved. The agent continues.' : 'Plan sent back with your feedback.');
          this.reloadPanels(['session', 'tools']);
          break;
        }
        case 'pick-model': this.dialogs.modelPicker(); break;
        case 'show-path': {
          const tab = $('[data-session-tab]');
          this.dialogs.path(`${tab.getAttribute('data-session-name')} on ${device}`, tab.getAttribute('data-cwd'));
          break;
        }
        case 'compaction-details': this.compactionDetails(); break;
        case 'open-agent': this.select({ ...this.selection, agent: Number(data.agent) }); break;
      }
    } catch (error) {
      this.toastError(error);
    }
  }

  async change(name, control) {
    try {
      switch (name) {
        case 'thinking': await this.setThinking(control.value); break;
        case 'plan-mode': {
          const result = await this.agentApi().patch('/plan-mode', { enabled: control.checked });
          this.toast(result.enabled ? 'Plan mode on. The agent plans before it changes anything.' : 'Plan mode off.');
          this.reloadPanels(['tools']);
          break;
        }
        case 'read-only': {
          const config = await this.agentApi().patch('/config', { readOnly: control.checked });
          this.toast(config.readOnly ? 'Read only on. Tools that write are disabled.' : 'Read only off. Every tool is back.');
          this.reloadPanels(['tools']);
          break;
        }
        case 'extension': {
          await this.agentApi().patch('/extensions', { name: control.dataset.extension, enabled: control.checked });
          this.toast(`${control.getAttribute('aria-label')} ${control.checked ? 'on' : 'off'} for ${this.agentName()}.`);
          this.reloadPanels(['extend', 'tools']);
          break;
        }
        case 'prompt': {
          await this.agentApi().patch('/extensions', { prompt: control.dataset.prompt, enabled: control.checked });
          this.toast(`${control.dataset.prompt} ${control.checked ? 'checked' : 'unchecked'}.`);
          break;
        }
      }
    } catch (error) {
      this.toastError(error);
      if (control.type === 'checkbox') control.checked = !control.checked;
    }
  }

  async answer(form, decline) {
    const task = form.dataset.task;
    if (decline) {
      await this.agentApi().post(`/interactions/${task}`, { decline: true });
      this.toast(`Question declined. ${this.agentName()} continues without an answer.`);
      return;
    }
    const answers = [...form.querySelectorAll('fieldset[data-question]')].map((set) => {
      const checked = [...set.querySelectorAll('input.btn-check:checked')];
      const other = checked.find((i) => i.hasAttribute('data-other'));
      return {
        selected: checked.filter((i) => !i.hasAttribute('data-other')).map((i) => i.value),
        other: other ? set.querySelector('[data-other-text]').value.trim() || undefined : undefined,
      };
    });
    await this.agentApi().post(`/interactions/${task}`, { answers });
    this.toast(`Answer sent. ${this.agentName()} continues.`);
  }

  async interrupt() {
    await this.agentApi().post('/abort', {});
    this.toast('Foreground work interrupted. Background jobs continue.');
  }

  async newAgent(name, deviceId, sessionId) {
    const device = deviceId || this.selection.device;
    const session = sessionId || this.selection.session;
    if (!device || !session) return;
    const agent = await deviceApi(device).post(`/sessions/${enc(session)}/agents`, name ? { name } : {}, { idempotencyKey: uuid() });
    this.toast(`New agent ${agent.name}. Same session, same shared App State.`);
    this.select({ device, session, agent: agent.id });
  }

  async fork(entry, name) {
    const fork = await this.agentApi().post('/fork', name ? { entryId: entry, name } : { entryId: entry }, { idempotencyKey: uuid() });
    this.toast(`Forked at entry #${entry}. App State stays shared.`);
    this.select({ ...this.selection, agent: fork.id });
  }

  async renameAgent(name) {
    await this.agentApi().patch('', { name });
    this.toast('Name saved.');
  }

  async setThinking(level) {
    const config = await this.agentApi().patch('/config', { thinkingLevel: level });
    const select = $('#thinkingLevel');
    if (select) select.value = config.thinkingLevel;
    this.toast(`Thinking ${config.thinkingLevel}. Used on the next turn.`);
  }

  lastAnswerEntry() {
    const answers = $$('#thread > article.msg-assistant[data-entry]').filter((a) => a.dataset.entry);
    return answers.length ? Number(answers.at(-1).dataset.entry) : null;
  }

  compactionDetails() {
    const tab = $('[data-session-tab]');
    if (!tab) return;
    const windowSize = Number(tab.dataset.window);
    const wait = windowSize - Number(tab.dataset.reserve);
    const background = Number(tab.dataset.background);
    $('#cmp-auto').checked = tab.dataset.enabled === 'true';
    $('#cmp-keep').value = tab.dataset.keep;
    $('[data-cmp-auto]').textContent = background > 0 ? tokens(wait - background) : 'off';
    $('[data-cmp-wait]').textContent = tokens(wait);
    $('[data-cmp-window]').textContent = tokens(windowSize);
    modal('modal-compaction').show();
  }

  async compaction(body) {
    try {
      await deviceApi(this.selection.device).patch(`/sessions/${enc(this.selection.session)}/compaction`, body);
      this.toast(body.enabled === undefined ? 'Compaction settings saved for this session.' : body.enabled ? 'Automatic compaction on.' : 'Automatic compaction off.');
      this.reloadPanels(['session']);
    } catch (error) {
      this.toastError(error);
    }
  }

  // ─── Chrome ───────────────────────────────────────────────────────────
  connection(status) {
    $$('[data-connection]').forEach((el) => {
      el.textContent = status === 'connected' ? 'Connected' : 'Reconnecting';
      el.setAttribute('data-tone', status === 'connected' ? 'ok' : 'warn');
    });
  }

  showBanner(html) {
    const banner = $('#errorBanner');
    banner.querySelector('[data-banner-text]').innerHTML = html;
    banner.hidden = false;
  }

  hideBanner() {
    $('#errorBanner').hidden = true;
  }

  toast(text, tone) { toast(text, tone); }

  toastError(error) { toastError(error); }
}

function platformIcon(device) {
  const p = device?.info?.platform || '';
  return p.startsWith('win') ? 'bi-windows' : p.startsWith('darwin') ? 'bi-apple' : p.startsWith('linux') ? 'bi-ubuntu' : 'bi-hdd-rack';
}

window.durableApp = new App();
export { providerName };
