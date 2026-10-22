// Decision 0011 (run W2, WU7): the chat pages' behaviour (Views/Chat). The new chat page creates the chat with its first message; a chat page
// follows its agent over the tab's one stream (Web.Common's MuxStream, key chat/{session}/{agent}) and applies the server-rendered `view`
// frames with swap.js. Every command is a JSON POST under /api/chats; creates, sends, forks, edits and compacts carry a request id, which a
// retry reuses, so nothing is sent twice. A 409 (the chat is busy, or another tab decided first) is a toast, never a silent change. Markup
// comes only from the server's partials; text set here goes through textContent.
import { MuxStream } from '/_content/PiSharp.Web.Common/js/stream.js';
import { applyItems, swapHtml, keepPinned } from '/_content/PiSharp.Web.Common/js/swap.js';
import { showToast, confirmAction, providerModelValue } from './shell.js';

const HOST_LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh'];

function newRequestId() {
  if (window.crypto?.randomUUID) return window.crypto.randomUUID();
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

/** POSTs JSON; retries a network failure (same body, so the same request id) up to `attempts` times. Resolves {ok, status, data}. */
async function post(path, body = {}, { attempts = 3 } = {}) {
  let lastError;
  for (let attempt = 0; attempt < attempts; attempt++) {
    try {
      const response = await fetch(path, {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      let data = null;
      try { data = await response.json(); } catch { data = null; }
      return { ok: response.ok, status: response.status, data };
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, 400 * (attempt + 1)));
    }
  }
  return { ok: false, status: 0, data: { error: { code: 'offline', message: lastError?.message || 'The app could not be reached.' } } };
}

/** Shows a failed command as a toast; returns true when the result was ok. */
function report(result, what) {
  if (result.ok) return true;
  const error = result.data?.error ?? {};
  const tone = result.status === 409 ? 'warn' : 'danger';
  const title = error.code === 'busy' ? 'The chat is busy'
    : error.code === 'already_decided' ? 'Already decided'
      : `${what} failed`;
  showToast({ title, body: error.message || `The app answered ${result.status}.`, tone });
  return false;
}

function hideModal(el) {
  if (el && window.bootstrap) window.bootstrap.Modal.getOrCreateInstance(el).hide();
}

function showModal(el) {
  if (el && window.bootstrap) window.bootstrap.Modal.getOrCreateInstance(el).show();
}

/** Fills a thinking select with the host's levels of a model (space-separated data-thinking), keeping the choice when it still fits. */
function fillThinking(select, levels) {
  if (!select) return;
  const supported = (levels || []).filter((level) => HOST_LEVELS.includes(level));
  const list = supported.length ? supported : ['off'];
  const current = select.value;
  select.replaceChildren(...list.map((level) => {
    const option = document.createElement('option');
    option.value = level;
    option.textContent = `Thinking: ${level}`;
    return option;
  }));
  select.value = list.includes(current) ? current : (list.includes('medium') ? 'medium' : list[list.length - 1]);
}

/** Rebuilds the sidebar's chat list from a `chats` frame (titles as text). */
function updateSidebar(frame, activeId) {
  const host = document.querySelector('[data-recent-chats]');
  if (!host || !Array.isArray(frame.items)) return;
  const label = host.querySelector('.app-nav-label');
  const nodes = frame.items.map((chat) => {
    const link = document.createElement('a');
    link.className = 'chat-link';
    link.href = `/chat/${encodeURIComponent(chat.id)}`;
    link.dataset.chat = chat.id;
    if (chat.id === activeId) {
      link.classList.add('active');
      link.setAttribute('aria-current', 'page');
    }
    if (chat.running) {
      link.classList.add('d-flex', 'align-items-center', 'gap-2');
      link.dataset.running = '';
      const title = document.createElement('span');
      title.className = 'text-truncate';
      title.textContent = chat.title;
      const dot = document.createElement('span');
      dot.className = 'status-dot live ms-auto';
      dot.setAttribute('aria-hidden', 'true');
      const sr = document.createElement('span');
      sr.className = 'visually-hidden';
      sr.textContent = ', running';
      link.append(title, dot, sr);
    } else {
      link.textContent = chat.title;
    }
    return link;
  });
  if (nodes.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'recent-empty';
    empty.textContent = 'No chats yet';
    nodes.push(empty);
  }
  host.replaceChildren(...(label ? [label] : []), ...nodes);
}

/** Enter sends, Shift+Enter is a new line, Alt+Enter queues a follow-up, Esc stops a run. */
function wireComposerKeys(input, { send, followUp, stop }) {
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      if (event.altKey && followUp) followUp(); else send();
    } else if (event.key === 'Escape' && stop) {
      stop();
    }
  });
}

// ─── New chat ────────────────────────────────────────────────────────────

function initNewChat(root) {
  const form = root.querySelector('[data-composer]');
  if (!form) return;
  const input = form.querySelector('[data-composer-input]');
  const thinking = form.querySelector('[data-thinking-select]');
  const tier = form.querySelector('[data-speed-select]');
  let requestId = null;
  let sending = false;

  async function send() {
    const text = input.value;
    if (!text.trim() || sending) return;
    sending = true;
    requestId ??= newRequestId();
    form.querySelector('[data-send]')?.setAttribute('disabled', '');
    const result = await post('/api/chats', {
      requestId,
      text,
      provider: root.dataset.provider || null,
      model: root.dataset.model || null,
      thinking: thinking?.value || null,
      tier: tier?.value || null,
    });
    sending = false;
    form.querySelector('[data-send]')?.removeAttribute('disabled');
    if (!report(result, 'Starting the chat')) {
      if (result.status !== 0) requestId = null;
      return;
    }
    window.location.assign(result.data.url);
  }

  form.addEventListener('submit', (event) => { event.preventDefault(); send(); });
  wireComposerKeys(input, { send });
  input.focus();

  const picker = document.querySelector('[data-model-picker]');
  picker?.addEventListener('submit', (event) => {
    event.preventDefault();
    const value = providerModelValue(picker.querySelector('[data-provider-model]'));
    if (!value?.provider || !value?.model) return;
    root.dataset.provider = value.provider;
    root.dataset.model = value.model;
    const select = picker.querySelector(`[data-provider="${CSS.escape(value.provider)}"] [data-model-select]`);
    const option = select?.selectedOptions?.[0];
    const name = document.querySelector('[data-model-name]');
    if (name) name.textContent = option?.textContent ?? value.model;
    const provider = picker.querySelector('[data-provider-select]')?.selectedOptions?.[0];
    const providerName = document.querySelector('[data-provider-name]');
    if (providerName) providerName.textContent = provider?.textContent ?? value.provider;
    fillThinking(thinking, value.thinking);
    hideModal(picker.closest('.modal'));
    input.focus();
  });
}

// ─── Chat ────────────────────────────────────────────────────────────────

function initChat(root) {
  const chatId = root.dataset.chat;
  const agentId = Number(root.dataset.agent);
  const base = `/api/chats/${encodeURIComponent(chatId)}`;
  const thread = root.querySelector('[data-thread]');
  const scroller = root.querySelector('[data-chat-scroll]');
  const form = root.querySelector('[data-composer]');
  const input = form.querySelector('[data-composer-input]');
  const thinking = form.querySelector('[data-thinking-select]');
  const tier = form.querySelector('[data-speed-select]');
  const running = form.querySelector('[data-running-controls]');
  const sendButton = form.querySelector('[data-send]');
  const statusChip = document.querySelector('[data-stream-status]');
  let busy = root.dataset.busy === 'true';
  let pendingText = null;
  let pendingId = null;

  function setBusy(value) {
    busy = value;
    root.dataset.busy = value ? 'true' : 'false';
    if (running) running.hidden = !value;
    if (sendButton) sendButton.hidden = value;
  }
  setBusy(busy);
  scroller.scrollTop = scroller.scrollHeight;

  function info(name, value) {
    const el = root.querySelector(`[data-info="${name}"]`);
    if (el && value !== undefined && value !== null) el.textContent = typeof value === 'number' ? value.toLocaleString() : String(value);
  }

  function onView(frame) {
    if (frame.type === 'view') {
      keepPinned(scroller, () => applyItems(thread, frame.items || {}, frame.reset));
      for (const [name, html] of Object.entries(frame.panels || {})) {
        const panel = root.querySelector(`[data-panel="${CSS.escape(name)}"]`);
        if (panel) swapHtml(panel, html);
      }
      const state = frame.state || {};
      setBusy(Boolean(state.busy));
      info('thinking', state.thinking);
      info('tier', state.tier || 'default');
      info('input', state.usage?.input);
      info('output', state.usage?.output);
      info('cacheRead', state.usage?.cacheRead);
      if (state.contextTokens) info('contextTokens', Math.round(state.contextTokens));
      if (statusChip) statusChip.hidden = true;
    } else if (frame.type === 'disconnected') {
      if (statusChip) statusChip.hidden = false;
    } else if (frame.type === 'fault') {
      showToast({ title: 'This chat stopped streaming', body: frame.error?.message ?? '', tone: 'danger' });
    }
  }

  const stream = new MuxStream();
  stream.status = (status) => { if (statusChip) statusChip.hidden = status === 'connected'; };
  stream.on(root.dataset.streamKey, onView);
  stream.on('chats', (frame) => updateSidebar(frame, chatId));
  stream.start();
  stream.subscribe([root.dataset.streamKey, 'chats']);

  async function send({ steer = false } = {}) {
    const text = input.value;
    if (!text.trim()) return;
    if (pendingText !== text) {
      pendingText = text;
      pendingId = newRequestId();
    }
    const result = await post(`${base}/messages`, { requestId: pendingId, text, agentId, steer });
    if (!report(result, 'Sending')) return;
    pendingText = null;
    pendingId = null;
    input.value = '';
    input.focus();
  }

  async function stop() {
    if (!busy) return;
    report(await post(`${base}/abort`, { agentId }), 'Stopping');
  }

  form.addEventListener('submit', (event) => { event.preventDefault(); send({ steer: busy }); });
  wireComposerKeys(input, { send: () => send({ steer: busy }), followUp: () => send({ steer: false }), stop });
  form.addEventListener('click', (event) => {
    const action = event.target.closest('[data-action]')?.dataset.action;
    if (action === 'follow-up') send({ steer: false });
    else if (action === 'steer') send({ steer: true });
    else if (action === 'stop') stop();
  });

  async function change(body, select, previous) {
    const result = await post(`${base}/model`, body);
    if (!report(result, 'Changing the model')) {
      if (select) select.value = previous;
      return null;
    }
    return result.data;
  }
  for (const select of [thinking, tier]) {
    if (!select) continue;
    let previous = select.value;
    select.addEventListener('focus', () => { previous = select.value; });
    select.addEventListener('change', async () => {
      const body = select === thinking ? { thinking: select.value } : { tier: select.value };
      if (await change(body, select, previous)) previous = select.value;
    });
  }

  const picker = document.querySelector('[data-model-picker]');
  picker?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const value = providerModelValue(picker.querySelector('[data-provider-model]'));
    if (!value?.provider || !value?.model) return;
    const done = await change({ provider: value.provider, model: value.model, thinking: thinking?.value }, null, null);
    if (!done) return;
    hideModal(picker.closest('.modal'));
    window.location.reload();
  });

  // Delegated actions on rendered fragments (transcript items, the pending panel, the queue) and the top bar.
  document.addEventListener('click', async (event) => {
    const target = event.target.closest('[data-action]');
    if (!target || form.contains(target)) return;
    const action = target.dataset.action;
    if (action === 'approve') {
      const card = target.closest('[data-approval]');
      const result = await post(`${base}/approvals/${card.dataset.approval}`, { decision: target.dataset.decision });
      if (report(result, 'Deciding')) card.querySelectorAll('button').forEach((button) => button.setAttribute('disabled', ''));
    } else if (action === 'approval-details') {
      const card = target.closest('[data-approval]');
      const modal = document.getElementById('modal-tool-approval');
      const approvalForm = modal.querySelector('[data-approval-form]');
      approvalForm.elements.taskId.value = card.dataset.approval;
      approvalForm.elements.reason.value = '';
      modal.querySelector('[data-approval-tool]').textContent = card.dataset.tool;
      modal.querySelector('[data-approval-input]').textContent = card.querySelector('[data-approval-detail]')?.textContent ?? '';
      showModal(modal);
    } else if (action === 'answer-questions' || action === 'decline-questions') {
      const pending = target.closest('[data-pending]');
      const body = { agentId: Number(pending.dataset.agent) };
      if (action === 'decline-questions') body.decline = true;
      else {
        body.answers = [...pending.querySelectorAll('fieldset[data-question]')].map((fieldset) => {
          const checked = [...fieldset.querySelectorAll('input.btn-check:checked')];
          const other = checked.some((box) => box.hasAttribute('data-other'))
            ? fieldset.querySelector('[data-other-text]')?.value ?? '' : null;
          return { selected: checked.filter((box) => !box.hasAttribute('data-other')).map((box) => box.value), other };
        });
      }
      report(await post(`${base}/questions/${pending.dataset.task}`, body), 'Answering');
    } else if (action === 'review-plan') {
      const pending = target.closest('[data-pending]');
      report(await post(`${base}/plans/${pending.dataset.task}`, {
        agentId: Number(pending.dataset.agent),
        approved: target.dataset.approved === 'true',
        feedback: pending.querySelector('textarea[name="feedback"]')?.value || null,
      }), 'Reviewing the plan');
    } else if (action === 'withdraw') {
      report(await post(`${base}/withdraw`, { agentId, submissionId: Number(target.dataset.submission) }), 'Removing');
    } else if (action === 'fork') {
      const result = await post(`${base}/fork`, { requestId: newRequestId(), entryId: Number(target.dataset.entry), agentId });
      if (report(result, 'Forking')) window.location.assign(result.data.url);
    } else if (action === 'edit') {
      const article = target.closest('[data-entry]');
      const modal = document.getElementById('modal-edit-message');
      const editForm = modal.querySelector('[data-edit-form]');
      editForm.elements.entryId.value = target.dataset.entry;
      editForm.elements.text.value = article.querySelector('[data-entry-text]')?.textContent ?? '';
      editForm.dataset.requestId = newRequestId();
      showModal(modal);
    } else if (action === 'copy-entry') {
      const article = target.closest('[data-key]');
      const text = [...article.querySelectorAll('[data-entry-text]')].map((el) => el.innerText).join('\n\n');
      try {
        await navigator.clipboard.writeText(text);
        showToast({ title: 'Copied', tone: 'ok', delay: 2000 });
      } catch {
        showToast({ title: 'Copy failed', body: 'The browser did not allow it.', tone: 'warn' });
      }
    } else if (action === 'archive') {
      const ok = await confirmAction({
        title: 'Delete this chat?',
        body: 'Its run stops and it leaves the sidebar. The session moves to the app\'s archive; nothing is erased.',
        ok: 'Delete',
      });
      if (!ok) return;
      if (report(await post(`${base}/archive`, {}), 'Deleting')) window.location.assign('/chat/new');
    } else if (action === 'toggle-info') {
      root.classList.toggle('info-collapsed');
    }
  });

  document.querySelector('[data-approval-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const approvalForm = event.currentTarget;
    const decision = event.submitter?.dataset.decision ?? 'allow';
    const result = await post(`${base}/approvals/${approvalForm.elements.taskId.value}`, {
      decision,
      reason: decision === 'deny' ? approvalForm.elements.reason.value || null : null,
    });
    if (report(result, 'Deciding')) hideModal(approvalForm.closest('.modal'));
  });

  document.querySelector('[data-edit-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const editForm = event.currentTarget;
    const result = await post(`${base}/edit`, {
      requestId: editForm.dataset.requestId || newRequestId(),
      entryId: Number(editForm.elements.entryId.value),
      text: editForm.elements.text.value,
      agentId,
    });
    if (report(result, 'Editing')) window.location.assign(result.data.url);
  });

  document.querySelector('[data-rename-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const renameForm = event.currentTarget;
    const result = await post(`${base}/rename`, { title: renameForm.elements.title.value });
    if (!report(result, 'Renaming')) return;
    const title = result.data.title;
    document.querySelector('[data-chat-title]').textContent = title;
    document.title = `${title} · PiSharp Web Agent`;
    hideModal(renameForm.closest('.modal'));
  });

  document.querySelector('[data-compact-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const compactForm = event.currentTarget;
    compactForm.dataset.requestId ||= newRequestId();
    const result = await post(`${base}/compact`, {
      requestId: compactForm.dataset.requestId,
      agentId,
      instructions: compactForm.elements.instructions.value || null,
    });
    if (!report(result, 'Compacting')) return;
    delete compactForm.dataset.requestId;
    hideModal(compactForm.closest('.modal'));
    showToast({ title: 'Compacting', body: 'A context note will appear in the chat.', tone: 'info' });
  });
}

const chat = document.querySelector('.chat-layout[data-chat]');
const fresh = document.querySelector('.chat-layout[data-chat-new]');
if (chat) initChat(chat);
else if (fresh) {
  initNewChat(fresh);
  const stream = new MuxStream();
  stream.on('chats', (frame) => updateSidebar(frame, null));
  stream.start();
  stream.subscribe(['chats']);
}
