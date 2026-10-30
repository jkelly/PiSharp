// Decision 0011 (run W2, WU7, WU8): the chat pages' behaviour (Views/Chat). The new chat page creates the chat with its first message; a chat
// page follows its agent over the tab's one stream (Web.Common's MuxStream, key chat/{session}/{agent}) and applies the server-rendered
// `view` frames with swap.js: the transcript items and the panels (pending, queue, notices, banners, info). Every command is a JSON POST
// under /api/chats; creates, sends, forks, clones, retries, edits and compacts carry a request id, which a retry reuses, so nothing is sent
// twice. A 409 (the chat is busy, or another tab decided first) is a toast, never a silent change. WU8 adds the mockups' states and dialogs:
// background (data-background while a run that was going when the tab opened goes on), reconnecting (data-stream, the neutral chip and the
// "paused while reconnecting" footer), the approval dialog (it reopens after a reload while its call still waits, and closes when another
// tab decides), the error details, Retry (a new branch from before the failed answer, with the chat's own model and account), the session
// tree (fork before or at any entry, switch branch), compaction, the model switch without a reload, the slash commands and the keys
// (Shift+Tab cycles the thinking level, Esc Esc opens the tree). Markup comes only from the server's partials; text set here goes through
// textContent. W3 (S5) adds the Spend Guard: the send estimate by Send follows the draft (debounced; the server counts the request, the
// draft only by its size) and the model; every send and new chat asks the guard first (spend: {check: true}), and when it would ask or
// block nothing is posted and modal-spend-confirm (S4's spend-confirm.js) opens: Proceed once or Cap output sends again with the same
// request id and the choice, Cancel or Choose a model… sends nothing and keeps the draft. A call the guard stopped inside a run shows
// Review…, which opens the stop's modal and continues the run after the user's choice. Nothing here switches a model or an account.
import { MuxStream } from '/_content/PiSharp.Web.Common/js/stream.js';
import { applyItems, swapHtml, keepPinned } from '/_content/PiSharp.Web.Common/js/swap.js';
import { showToast, confirmAction, providerModelValue } from './shell.js';
import { showSpendConfirm } from './spend-confirm.js';

const HOST_LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh'];
const APPROVAL_KEY = 'pisharp.chat.approval-dialog';
const PANELS = ['pending', 'queue', 'notices', 'banners', 'info'];

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

/** Opens modal-spend-confirm for the guard's answer to a send (`spend` of a 409, or a stopped call's review); resolves with what to send
 * again ({choice: 'proceed-once'} or {choice: 'cap-output', maxOutputTokens}), or null when nothing is to be sent. */
async function askSpend(spend, error) {
  if (!spend?.modal) {
    showToast({ title: 'Not sent', body: error?.message || spend?.sentence || 'This request is over a spend cap.', tone: 'warn' });
    return null;
  }
  const answer = await showSpendConfirm({ html: spend.modal, verdict: spend.verdict, code: spend.code, pane: spend.pane });
  if (answer?.choice === 'proceed-once') return { choice: 'proceed-once' };
  if (answer?.choice === 'cap-output' && answer.maxOutputTokens) return { choice: 'cap-output', maxOutputTokens: answer.maxOutputTokens };
  return null;
}

/** POSTs a send the Spend Guard checks first: when it would ask or block, nothing was posted, the confirm modal opens and the user's choice
 * goes with the same body (the same request id), so the message posts once. Resolves with the last result; `refused` when the user chose
 * not to send (a failure is a toast). */
async function postWithSpend(path, body, what, spend = { check: true }) {
  let current = spend;
  for (let round = 0; round < 4; round++) {
    const result = await post(path, { ...body, spend: current });
    if (result.status === 409 && result.data?.spend) {
      const choice = await askSpend(result.data.spend, result.data.error);
      if (!choice) return { ...result, refused: true };
      current = choice;
      continue;
    }
    report(result, what);
    return result;
  }
  return { ok: false, status: 409, data: null, refused: true };
}

/** Keeps the send estimate by Send current: asks the server about the draft (debounced) and swaps in its rendered answer. */
function spendEstimate(form, url, body) {
  const host = form.querySelector('[data-spend-estimate-host]');
  let timer = null;
  let asked = 0;
  async function refresh() {
    if (!host) return;
    clearTimeout(timer);
    timer = null;
    const mine = ++asked;
    const result = await post(url(), body(), { attempts: 1 });
    if (mine !== asked || !result.ok || typeof result.data?.html !== 'string') return;
    disposeTooltips(host);
    swapHtml(host, result.data.html);
  }
  return {
    refresh,
    schedule(delay = 400) {
      if (!host) return;
      clearTimeout(timer);
      timer = setTimeout(refresh, delay);
    },
  };
}

function modalOf(el) {
  return el && window.bootstrap ? window.bootstrap.Modal.getOrCreateInstance(el) : null;
}

function hideModal(el) { modalOf(el)?.hide(); }

function showModal(el) { modalOf(el)?.show(); }

function storage() {
  try { return window.sessionStorage; } catch { return null; }
}

function remember(key, value) {
  try {
    if (value === null) storage()?.removeItem(key);
    else storage()?.setItem(key, JSON.stringify(value));
  } catch { /* Private mode or blocked storage: nothing to remember. */ }
}

function recall(key) {
  try { return JSON.parse(storage()?.getItem(key) ?? 'null'); } catch { return null; }
}

// Tooltips are created on first hover or focus (delegated, so streamed fragments get them too) and disposed before their markup is replaced.
function wireTooltips() {
  const open = (event) => {
    const el = event.target.closest?.('[data-bs-toggle="tooltip"]');
    if (!el || !window.bootstrap || window.bootstrap.Tooltip.getInstance(el)) return;
    window.bootstrap.Tooltip.getOrCreateInstance(el).show();
  };
  document.addEventListener('mouseover', open);
  document.addEventListener('focusin', open);
}

function disposeTooltips(container) {
  if (!container || !window.bootstrap) return;
  container.querySelectorAll('[data-bs-toggle="tooltip"]').forEach((el) => window.bootstrap.Tooltip.getInstance(el)?.dispose());
}

/** Fills a thinking select with the host's levels of a model, keeping the choice when it still fits; a model that can't turn thinking off
 * shows "off" disabled, as the server renders it. */
function fillThinking(select, levels) {
  if (!select) return;
  const supported = (levels || []).filter((level) => HOST_LEVELS.includes(level));
  const list = supported.length ? supported : ['off'];
  const current = select.value;
  const options = [];
  if (!list.includes('off')) {
    const off = document.createElement('option');
    off.value = 'off';
    off.disabled = true;
    off.textContent = 'Thinking: off (unsupported)';
    options.push(off);
  }
  for (const level of list) {
    const option = document.createElement('option');
    option.value = level;
    option.textContent = `Thinking: ${level}`;
    options.push(option);
  }
  select.replaceChildren(...options);
  select.value = list.includes(current) ? current : (list.includes('medium') ? 'medium' : list[list.length - 1]);
}

/** Selects the next enabled thinking level (Shift+Tab) and announces the change to the select's own handler. */
function cycleThinking(select) {
  if (!select) return;
  const levels = [...select.options].filter((option) => !option.disabled).map((option) => option.value);
  if (levels.length < 2) return;
  select.value = levels[(levels.indexOf(select.value) + 1) % levels.length];
  select.dispatchEvent(new Event('change', { bubbles: true }));
}

/** Rebuilds the sidebar's chat list from a `chats` frame (titles as text): a running chat carries the live dot. */
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

/** Enter sends, Shift+Enter is a new line, Alt+Enter queues a follow-up, Esc stops a run, Esc Esc opens the session tree and Shift+Tab
 * cycles the thinking level. */
function wireComposerKeys(input, { send, followUp, stop, tree, thinking }) {
  let lastEscape = 0;
  input.addEventListener('keydown', (event) => {
    if (event.isComposing) return;
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      if (event.altKey && followUp) followUp(); else send();
    } else if (event.key === 'Tab' && event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && thinking) {
      event.preventDefault();
      cycleThinking(thinking);
    } else if (event.key === 'Escape') {
      const now = Date.now();
      if (tree && now - lastEscape < 500) {
        lastEscape = 0;
        tree();
      } else {
        lastEscape = now;
        stop?.();
      }
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
  let requestText = null;
  let sending = false;
  const estimate = spendEstimate(form, () => '/api/chats/spend',
    () => ({ provider: root.dataset.provider || null, model: root.dataset.model || null, text: input.value }));

  async function send() {
    const text = input.value;
    if (!text.trim() || sending) return;
    sending = true;
    if (requestText !== text) {
      requestText = text;
      requestId = newRequestId();
    }
    form.querySelector('[data-send]')?.setAttribute('disabled', '');
    const result = await postWithSpend('/api/chats', {
      requestId,
      text,
      provider: root.dataset.provider || null,
      model: root.dataset.model || null,
      thinking: thinking?.value || null,
      tier: tier?.value || null,
    }, 'Starting the chat');
    sending = false;
    form.querySelector('[data-send]')?.removeAttribute('disabled');
    if (!result.ok) {
      if (!result.refused && result.status !== 0) requestText = null;
      return;
    }
    window.location.assign(result.data.url);
  }

  form.addEventListener('submit', (event) => { event.preventDefault(); send(); });
  wireComposerKeys(input, { send, thinking });
  input.addEventListener('input', () => estimate.schedule());
  input.focus();

  root.addEventListener('click', (event) => {
    const suggestion = event.target.closest('[data-suggestion]');
    if (!suggestion) return;
    input.value = suggestion.dataset.suggestion;
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  });

  const picker = document.querySelector('[data-model-picker]');
  picker?.addEventListener('submit', (event) => {
    event.preventDefault();
    const value = providerModelValue(picker.querySelector('[data-provider-model]'));
    if (!value?.provider || !value?.model) return;
    root.dataset.provider = value.provider;
    root.dataset.model = value.model;
    showModelName(picker, value);
    fillThinking(thinking, value.thinking);
    hideModal(picker.closest('.modal'));
    estimate.refresh();
    input.focus();
  });
}

/** Shows the chosen model in the top bar's switch. */
function showModelName(picker, value) {
  const select = [...picker.querySelectorAll('[data-provider]')].find((pane) => pane.dataset.provider === value.provider)?.querySelector('[data-model-select]');
  const modelName = select?.selectedOptions?.[0]?.textContent ?? value.model;
  const providerName = picker.querySelector('[data-provider-select]')?.selectedOptions?.[0]?.textContent ?? value.provider;
  const name = document.querySelector('[data-model-name]');
  if (name) name.textContent = modelName;
  const sub = document.querySelector('[data-provider-name]');
  if (sub) sub.textContent = providerName;
  document.querySelector('[data-model-switch]')?.setAttribute('aria-label', `Change model (${modelName})`);
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
  const approvalModal = document.getElementById('modal-tool-approval');
  const treeModal = document.getElementById('modal-session-tree');
  const errorModal = document.getElementById('modal-error-details');
  const compactModal = document.getElementById('modal-compact');
  const retries = new Map();
  const continues = new Map();
  const estimate = spendEstimate(form, () => `${base}/spend`, () => ({ text: input.value, agentId }));
  let busy = root.dataset.busy === 'true';
  let pendingText = null;
  let pendingId = null;
  let firstView = true;
  let decidedHere = null;

  // The mockups' background state: the tab opened while a run was going, so the running answer says it kept running while you were away.
  if (busy) root.dataset.background = 'true';

  function setBusy(value) {
    busy = value;
    root.dataset.busy = value ? 'true' : 'false';
    if (!value) delete root.dataset.background;
    if (running) running.hidden = !value;
    if (sendButton) sendButton.hidden = value;
  }
  setBusy(busy);
  scroller.scrollTop = scroller.scrollHeight;

  function setStream(status) {
    root.dataset.stream = status;
    if (statusChip) statusChip.hidden = status !== 'reconnecting';
  }

  function onView(frame) {
    if (frame.type === 'view') {
      disposeTooltips(thread);
      keepPinned(scroller, () => applyItems(thread, frame.items || {}, frame.reset));
      for (const [name, html] of Object.entries(frame.panels || {})) {
        if (!PANELS.includes(name)) continue;
        const panel = document.querySelector(`[data-panel="${CSS.escape(name)}"]`);
        if (!panel) continue;
        disposeTooltips(panel);
        swapHtml(panel, html);
      }
      const state = frame.state || {};
      // A finished run changed the context the next request sends: the estimate follows.
      if (busy && !state.busy) estimate.schedule(250);
      setBusy(Boolean(state.busy));
      setStream('connected');
      if (firstView) {
        firstView = false;
        restoreApprovalDialog();
      }
      followApprovalDialog();
    } else if (frame.type === 'disconnected') {
      setStream('reconnecting');
    } else if (frame.type === 'fault') {
      showToast({ title: 'This chat stopped streaming', body: frame.error?.message ?? '', tone: 'danger' });
    }
  }

  const stream = new MuxStream();
  stream.status = (status) => setStream(status === 'connected' ? 'connected' : 'reconnecting');
  stream.on(root.dataset.streamKey, onView);
  stream.on('chats', (frame) => updateSidebar(frame, chatId));
  stream.start();
  stream.subscribe([root.dataset.streamKey, 'chats']);

  // ── Sending, commands and keys ──

  async function send({ steer = false } = {}) {
    const text = input.value;
    if (!text.trim()) return;
    if (await command(text.trim())) return;
    if (pendingText !== text) {
      pendingText = text;
      pendingId = newRequestId();
    }
    const result = await postWithSpend(`${base}/messages`, { requestId: pendingId, text, agentId, steer }, 'Sending');
    if (!result.ok) return;
    pendingText = null;
    pendingId = null;
    input.value = '';
    estimate.schedule(0);
    input.focus();
  }

  /** Review… on a call the Spend Guard stopped inside the run: its own modal (or none, when continuing now fits as it is), then the run
   * continues with "Continue." after the user's choice, once per stop (the same request id on a retry). */
  async function reviewStop(op, button) {
    if (!op) return;
    button?.setAttribute('disabled', '');
    try {
      let response = null;
      try {
        response = await fetch(`${base}/spend/stops/${encodeURIComponent(op)}`, { headers: { Accept: 'application/json' }, cache: 'no-store' });
      } catch {
        response = null;
      }
      let data = null;
      try { data = response ? await response.json() : null; } catch { data = null; }
      if (!response?.ok || !data) {
        report({ ok: false, status: response?.status ?? 0, data }, 'Reviewing the stopped request');
        return;
      }
      let spend = { check: true };
      if (!data.allowed) {
        const choice = await askSpend(data, null);
        if (!choice) return;
        spend = choice;
      }
      if (!continues.has(op)) continues.set(op, newRequestId());
      const result = await postWithSpend(`${base}/spend/continue`, { requestId: continues.get(op), op, agentId }, 'Continuing', spend);
      if (result.ok) showToast({ title: 'Continuing', body: 'The run goes on after your choice.', tone: 'info', delay: 3000 });
    } finally {
      button?.removeAttribute('disabled');
    }
  }

  /** Runs a typed session command (/compact [focus], /tree, /new, /clone) instead of sending it; returns whether it was one. */
  async function command(text) {
    const match = /^\/(compact|tree|new|clone)(?:\s+([\s\S]*))?$/.exec(text);
    if (!match) return false;
    input.value = '';
    if (match[1] === 'compact') {
      const focus = compactModal?.querySelector('textarea[name="instructions"]');
      if (focus) focus.value = match[2] ?? '';
      showModal(compactModal);
    } else if (match[1] === 'tree') showModal(treeModal);
    else if (match[1] === 'new') window.location.assign('/chat/new');
    else await clone();
    return true;
  }

  async function stop() {
    if (!busy) return;
    report(await post(`${base}/abort`, { agentId }), 'Stopping');
  }

  /** The newest entry of the shown branch, from the rendered items (a tool result's entry included). */
  function lastEntry() {
    const ids = [...thread.querySelectorAll('[data-entry]')].map((el) => Number(el.dataset.entry)).filter((id) => Number.isFinite(id) && id > 0);
    return ids.length ? Math.max(...ids) : Number(root.dataset.lastEntry) || null;
  }

  async function fork(entryId, branch = agentId) {
    const result = await post(`${base}/fork`, { requestId: newRequestId(), entryId, agentId: branch });
    if (report(result, 'Forking')) window.location.assign(result.data.url);
  }

  async function clone() {
    const entry = lastEntry();
    if (!entry) {
      showToast({ title: 'Nothing to clone yet', body: 'Send a message first.', tone: 'info' });
      return;
    }
    await fork(entry);
  }

  async function retry(entryId) {
    if (!retries.has(entryId)) retries.set(entryId, newRequestId());
    const result = await post(`${base}/retry`, { requestId: retries.get(entryId), entryId, agentId });
    if (report(result, 'Retrying')) window.location.assign(result.data.url);
  }

  form.addEventListener('submit', (event) => { event.preventDefault(); send({ steer: busy }); });
  input.addEventListener('input', () => estimate.schedule());
  wireComposerKeys(input, {
    send: () => send({ steer: busy }),
    followUp: () => send({ steer: false }),
    stop,
    tree: () => showModal(treeModal),
    thinking,
  });
  form.addEventListener('click', (event) => {
    const action = event.target.closest('[data-action]')?.dataset.action;
    if (action === 'follow-up') send({ steer: false });
    else if (action === 'steer') send({ steer: true });
    else if (action === 'stop') stop();
    else if (action === 'clone') clone();
  });

  // ── Model, thinking and speed ──

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
      if (await change(body, select, previous)) {
        previous = select.value;
        estimate.refresh();
      }
    });
  }

  const picker = document.querySelector('[data-model-picker]');
  picker?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const value = providerModelValue(picker.querySelector('[data-provider-model]'));
    if (!value?.provider || !value?.model) return;
    const requested = thinking?.value;
    const done = await change({ provider: value.provider, model: value.model, thinking: requested }, null, null);
    if (!done) return;
    hideModal(picker.closest('.modal'));
    showModelName(picker, value);
    fillThinking(thinking, value.thinking);
    if (thinking && done.thinking) thinking.value = done.thinking;
    root.dataset.provider = done.provider;
    estimate.refresh();
    const clamped = requested && done.thinking && requested !== done.thinking
      ? ` The thinking level was clamped from ${requested} to ${done.thinking}.` : '';
    showToast({ title: 'Model switched', body: `Now using ${done.provider}/${done.model}.${clamped}`, tone: 'info' });
  });

  // ── The approval dialog ──

  function fillApprovalDialog(card) {
    const approvalForm = approvalModal.querySelector('[data-approval-form]');
    approvalForm.elements.taskId.value = card.dataset.approval;
    approvalForm.elements.reason.value = '';
    approvalModal.querySelectorAll('[data-approval-tool]').forEach((el) => { el.textContent = card.dataset.tool; });
    const call = card.dataset.call ?? '';
    approvalModal.querySelector('[data-approval-call]').textContent = call.length > 14 ? `${call.slice(0, 13)}…` : call;
    const detail = card.querySelector('[data-approval-detail]');
    approvalModal.querySelector('[data-approval-input-label]').textContent = detail?.dataset.command === 'true' ? 'Command' : 'Arguments';
    approvalModal.querySelector('[data-approval-input]').textContent = detail?.textContent ?? '';
    approvalModal.querySelector('[data-approval-raw-view]').textContent = card.querySelector('[data-approval-raw]')?.textContent ?? '';
  }

  function openApprovalDialog(card) {
    if (!approvalModal || !card) return;
    fillApprovalDialog(card);
    decidedHere = null;
    remember(APPROVAL_KEY, { chat: chatId, task: card.dataset.approval });
    showModal(approvalModal);
  }

  /** After a reload: the dialog that was open comes back while its call still waits. */
  function restoreApprovalDialog() {
    const saved = recall(APPROVAL_KEY);
    if (!saved || saved.chat !== chatId) return;
    const card = document.querySelector(`[data-approval="${CSS.escape(String(saved.task))}"]`);
    if (card) openApprovalDialog(card);
    else remember(APPROVAL_KEY, null);
  }

  /** Another tab decided the call the open dialog shows: close it and say so. */
  function followApprovalDialog() {
    if (!approvalModal?.classList.contains('show')) return;
    const task = approvalModal.querySelector('[data-approval-form]').elements.taskId.value;
    if (document.querySelector(`[data-approval="${CSS.escape(task)}"]`)) return;
    hideModal(approvalModal);
    if (decidedHere !== task) showToast({ title: 'Already decided', body: 'This tool call was answered in another tab.', tone: 'info' });
  }

  approvalModal?.addEventListener('hidden.bs.modal', () => remember(APPROVAL_KEY, null));

  approvalModal?.querySelector('[data-approval-form]')?.addEventListener('submit', async (event) => {
    event.preventDefault();
    const approvalForm = event.currentTarget;
    const decision = event.submitter?.dataset.decision ?? 'allow';
    const task = approvalForm.elements.taskId.value;
    const result = await post(`${base}/approvals/${encodeURIComponent(task)}`, {
      decision,
      reason: decision === 'deny' ? approvalForm.elements.reason.value || null : null,
    });
    if (result.ok || result.status === 409 || result.status === 404) decidedHere = task;
    if (report(result, 'Deciding') || result.status === 409 || result.status === 404) hideModal(approvalModal);
  });

  // ── The error details dialog ──

  function openErrorDetails(detail) {
    if (!errorModal || !detail) return;
    const article = detail.closest('[data-entry]');
    const set = (name, text) => { errorModal.querySelector(`[data-error-field="${name}"]`).textContent = text; };
    errorModal.querySelector('[data-error-subtitle]').textContent =
      [detail.dataset.errorModel, detail.dataset.errorTime].filter(Boolean).join(' · ');
    set('stop', detail.dataset.errorStop || 'error');
    set('attempts', detail.dataset.errorAttempts || '1');
    set('message', detail.dataset.errorMessage || 'The provider request failed.');
    const again = errorModal.querySelector('[data-error-retry]');
    again.dataset.entry = article?.dataset.entry ?? '';
    again.hidden = !article?.dataset.entry;
    showModal(errorModal);
  }

  async function copyText(text, what) {
    try {
      await navigator.clipboard.writeText(text);
      showToast({ title: `${what} copied`, tone: 'ok', delay: 2000 });
    } catch {
      showToast({ title: 'Copy failed', body: 'The browser did not allow it.', tone: 'warn' });
    }
  }

  // ── The session tree ──

  const treeHost = treeModal?.querySelector('[data-tree-host]');
  const treeSwitch = treeModal?.querySelector('[data-action="tree-switch"]');
  let selectedNode = null;

  treeModal?.addEventListener('show.bs.modal', async () => {
    selectedNode = null;
    if (treeSwitch) treeSwitch.disabled = true;
    let response;
    try {
      response = await fetch(`/chat/${encodeURIComponent(chatId)}/tree?agent=${agentId}`, { headers: { Accept: 'text/html' } });
    } catch {
      response = null;
    }
    if (!response?.ok) {
      const message = document.createElement('p');
      message.className = 'text-secondary fs-7 mb-0';
      message.textContent = 'The tree could not be loaded. Close it and try again.';
      treeHost.replaceChildren(message);
      return;
    }
    swapHtml(treeHost, await response.text());
    treeModal.querySelector('[data-tree-footer]').textContent = treeHost.querySelector('[data-tree]')?.dataset.summary ?? '';
    const current = treeHost.querySelector('.tree-node.current');
    if (current) {
      selectNode(current);
      current.scrollIntoView({ block: 'nearest' });
    }
  });

  function selectNode(node) {
    treeHost.querySelectorAll('.tree-node.selected').forEach((el) => el.classList.remove('selected'));
    node.classList.add('selected');
    selectedNode = node;
    const panel = treeHost.querySelector('[data-tree-selected]');
    const role = panel.querySelector('[data-tree-selected-role]');
    role.textContent = node.dataset.role;
    role.hidden = false;
    const text = panel.querySelector('[data-tree-selected-text]');
    text.textContent = node.querySelector('.text')?.textContent ?? '';
    text.classList.remove('text-secondary');
    panel.querySelector('[data-action="tree-fork-before"]').disabled = !node.dataset.prev;
    panel.querySelector('[data-action="tree-fork-at"]').disabled = false;
    if (treeSwitch) treeSwitch.disabled = Number(node.dataset.agent) === agentId;
  }

  treeHost?.addEventListener('click', (event) => {
    const node = event.target.closest('[data-tree-node]');
    if (node) selectNode(node);
  });

  // ── Delegated actions on rendered fragments (items, panels, dialogs) and the top bar ──

  document.addEventListener('click', async (event) => {
    const target = event.target.closest('[data-action]');
    if (!target || form.contains(target)) return;
    const action = target.dataset.action;
    if (action === 'approve') {
      const card = target.closest('[data-approval]');
      const result = await post(`${base}/approvals/${encodeURIComponent(card.dataset.approval)}`, { decision: target.dataset.decision });
      if (result.ok) decidedHere = card.dataset.approval;
      if (report(result, 'Deciding')) card.querySelectorAll('button').forEach((button) => button.setAttribute('disabled', ''));
    } else if (action === 'approval-details') {
      openApprovalDialog(target.closest('[data-approval]'));
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
      await fork(Number(target.dataset.entry));
    } else if (action === 'clone') {
      await clone();
    } else if (action === 'retry') {
      const entry = Number(target.dataset.entry);
      if (!entry) return;
      if (errorModal?.contains(target)) hideModal(errorModal);
      target.setAttribute('disabled', '');
      await retry(entry);
      target.removeAttribute('disabled');
    } else if (action === 'error-details') {
      openErrorDetails(target.closest('[data-error-detail]'));
    } else if (action === 'spend-review') {
      await reviewStop(target.dataset.op, target);
    } else if (action === 'copy-error') {
      const text = ['subtitle', 'stop', 'attempts', 'message']
        .map((name) => errorModal.querySelector(name === 'subtitle' ? '[data-error-subtitle]' : `[data-error-field="${name}"]`)?.textContent ?? '')
        .filter(Boolean).join('\n');
      await copyText(text, 'Details');
    } else if (action === 'tree-fork-before' || action === 'tree-fork-at') {
      if (!selectedNode) return;
      const entry = Number(action === 'tree-fork-before' ? selectedNode.dataset.prev : selectedNode.dataset.entry);
      if (entry) await fork(entry, Number(selectedNode.dataset.agent));
    } else if (action === 'tree-switch') {
      if (selectedNode) window.location.assign(`/chat/${encodeURIComponent(chatId)}?agent=${encodeURIComponent(selectedNode.dataset.agent)}`);
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
      await copyText([...article.querySelectorAll('[data-entry-text]')].map((el) => el.innerText).join('\n\n'), 'Message');
    } else if (action === 'archive') {
      const title = root.dataset.title || 'This chat';
      const branches = Number(root.dataset.branches) || 1;
      const ok = await confirmAction({
        title: 'Delete this chat?',
        body: `“${title}” stops ${busy ? 'its running branch' : 'any running branch'} and moves to the archive`
          + `${branches > 1 ? ` with its ${branches} branches` : ''}. Nothing is erased.`,
        detail: 'device\\archive\\',
        ok: 'Delete chat',
        icon: 'bi-trash3',
      });
      if (!ok) return;
      if (report(await post(`${base}/archive`, {}), 'Deleting')) window.location.assign('/chat/new');
    } else if (action === 'toggle-info') {
      root.classList.toggle('info-collapsed');
    }
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
    document.querySelectorAll('[data-chat-title-copy]').forEach((el) => { el.textContent = title; });
    root.dataset.title = title;
    document.title = `${title} · PiSharp Web Agent`;
    hideModal(renameForm.closest('.modal'));
  });

  // ── Compaction ──

  compactModal?.addEventListener('show.bs.modal', () => {
    const now = document.querySelector('[data-panel="info"] [data-info="contextTokens"]')?.textContent;
    const before = compactModal.querySelector('[data-compact-before]');
    if (before && now) before.textContent = now;
  });

  compactModal?.querySelector('[data-auto-compaction]')?.addEventListener('change', async (event) => {
    const box = event.currentTarget;
    const result = await post(`${base}/compaction`, { enabled: box.checked });
    if (!report(result, 'Changing auto-compaction')) {
      box.checked = !box.checked;
      return;
    }
    const info = document.querySelector('[data-panel="info"] [data-info="compaction"]');
    if (info) info.textContent = result.data.enabled ? `on · reserve ${Number(result.data.reserveTokens).toLocaleString()}` : 'off';
    showToast({ title: result.data.enabled ? 'Auto-compaction on' : 'Auto-compaction off', tone: 'info', delay: 3000 });
  });

  compactModal?.querySelector('[data-compact-form]')?.addEventListener('submit', async (event) => {
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
    compactForm.elements.instructions.value = '';
    hideModal(compactModal);
    showToast({ title: 'Compacting', body: 'A context note will appear in the chat.', tone: 'info' });
  });
}

wireTooltips();
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
