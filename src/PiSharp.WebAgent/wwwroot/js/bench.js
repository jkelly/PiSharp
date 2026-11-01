// Decision 0011 (run W4, R7): the Tool Bench page (GET /bench). The loop runs on the server (BenchRunner): this page sends the user's
// actions to /api/bench/sessions/{id} (Run, Stop, Reset, Add, Add & run, remove a message, a call's result) and renders the parts the
// server answers with (_BenchRunControls, _BenchTimelineHead, _BenchTimelineBody, _BenchToolList, _BenchCallLog; model text through the
// server's sanitizing MarkdownRenderer). It follows the session's event stream (GET .../events: a frame per change, then GET .../view) and,
// while a turn runs, the turn's run events (GET /api/runs/{id}/events) into the turn as plain text (textContent). A turn the Spend Guard
// would ask about opens modal-spend-confirm and is sent again only with the user's choice. What the user is typing in an awaiting card or
// the message box survives every update. Ctrl+Enter runs (or sends the focused result), Esc stops. Nothing here runs a tool.
import { showToast, providerModelValue } from './shell.js';
import { showSpendConfirm } from './spend-confirm.js';
import { postRequest, getRequest, copyText, RequestError } from './request-api.js';
import { readSetup, changeSetup } from './bench-setup.js';
import { initBenchTools, restoreSelection } from './bench-tools.js';

const root = document.querySelector('[data-bench]');
let rendered = Number(root?.dataset.version ?? 0);
let viewSeq = 0;
let sessionEvents = null;
let runStream = null;
let systemTimer = 0;
let selectedCall = null;
let acting = false;
let pinBottom = false;

function api(path = '') {
  return `/api/bench/sessions/${encodeURIComponent(root.dataset.session)}${path}`;
}

function part(name) {
  return root.querySelector(`[data-bench-part="${name}"]`);
}

function toast(title, error, tone = 'danger') {
  const body = typeof error === 'string' ? error : error?.message ?? '';
  showToast({ title, body, tone: error instanceof RequestError && error.status === 409 ? 'warn' : tone });
}

function parseHtml(html) {
  const template = document.createElement('template');
  template.innerHTML = (html ?? '').trim();
  return template.content;
}

// ---- rendering --------------------------------------------------------------------------------------------------------------------

function swapInner(name, html) {
  const host = part(name);
  if (host && typeof html === 'string') host.replaceChildren(parseHtml(html));
}

function swapOuter(name, html) {
  const host = part(name);
  if (!host || typeof html !== 'string') return null;
  const fresh = parseHtml(html).firstElementChild;
  if (!fresh) return null;
  const scroll = host.querySelector('.pane-body')?.scrollTop ?? 0;
  host.replaceWith(fresh);
  const body = fresh.querySelector('.pane-body');
  if (body) body.scrollTop = scroll;
  return fresh;
}

// What the user typed in each awaiting card, by call id.
function awaitingDrafts() {
  const drafts = new Map();
  for (const card of root.querySelectorAll('[data-awaiting]')) {
    const text = card.querySelector('[data-result-text]');
    drafts.set(card.dataset.awaiting, {
      text: text?.value ?? '',
      focused: document.activeElement === text,
      start: text?.selectionStart ?? 0,
      end: text?.selectionEnd ?? 0,
      format: card.querySelector('[data-result-format]:checked')?.value ?? 'json',
      isError: card.querySelector('[data-result-error]')?.checked === true,
      filled: card.dataset.filled,
      filledText: card.dataset.filledText,
    });
  }
  return drafts;
}

function restoreDrafts(drafts) {
  for (const card of root.querySelectorAll('[data-awaiting]')) {
    const draft = drafts.get(card.dataset.awaiting);
    if (!draft) continue;
    const text = card.querySelector('[data-result-text]');
    if (text) text.value = draft.text;
    for (const radio of card.querySelectorAll('[data-result-format]')) radio.checked = radio.value === draft.format;
    const error = card.querySelector('[data-result-error]');
    if (error) error.checked = draft.isError;
    if (draft.filled !== undefined) {
      card.dataset.filled = draft.filled;
      card.dataset.filledText = draft.filledText ?? '';
    }
    resultCheck(card);
    if (draft.focused && text) {
      text.focus({ preventScroll: true });
      text.setSelectionRange(draft.start, draft.end);
    }
  }
}

function swapTimeline(html) {
  const host = part('timeline');
  if (!host || typeof html !== 'string') return;
  const drafts = awaitingDrafts();
  const opened = new Map([...host.querySelectorAll('[data-call] > details')].map((details) => [details.parentElement.dataset.call, details.open]));
  const atBottom = host.scrollHeight - host.scrollTop - host.clientHeight < 48;
  const scroll = host.scrollTop;
  const hadAwaiting = drafts.size > 0;
  host.replaceChildren(parseHtml(html));
  for (const details of host.querySelectorAll('[data-call] > details')) {
    const open = opened.get(details.parentElement.dataset.call);
    if (open !== undefined) details.open = open;
  }
  restoreDrafts(drafts);
  showStream();
  const firstAwaiting = host.querySelector('[data-awaiting]');
  if (firstAwaiting && !hadAwaiting) {
    firstAwaiting.scrollIntoView({ block: 'nearest' });
    if (!root.contains(document.activeElement) || document.activeElement === document.body) {
      firstAwaiting.querySelector('[data-result-text]')?.focus({ preventScroll: true });
    }
  } else if (atBottom || pinBottom) {
    host.scrollTop = host.scrollHeight;
  } else {
    host.scrollTop = scroll;
  }
  pinBottom = false;
}

function selectCall(id, reveal = false) {
  const log = part('log');
  if (!log) return;
  const rows = [...log.querySelectorAll('[data-bench-log-row]')];
  if (!rows.some((row) => row.dataset.benchLogRow === id)) return;
  for (const row of rows) {
    const active = row.dataset.benchLogRow === id;
    row.classList.toggle('table-active', active);
    row.setAttribute('aria-selected', active ? 'true' : 'false');
  }
  for (const detail of log.querySelectorAll('[data-bench-log-detail]')) detail.hidden = detail.dataset.benchLogDetail !== id;
  if (reveal) root.querySelector(`[data-call="${CSS.escape(id)}"]`)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

function swapLog(html) {
  const fresh = swapOuter('log', html);
  if (fresh && selectedCall) selectCall(selectedCall);
}

function setToolbar(view) {
  const toolbar = root.querySelector('[data-bench-toolbar]');
  if (!toolbar) return;
  const model = view.model ?? {};
  const provider = toolbar.querySelector('[data-chip-provider]');
  if (provider) provider.textContent = model.provider ?? '';
  const name = toolbar.querySelector('[data-chip-name]');
  if (name) name.textContent = model.name || model.model || 'Choose a model';
  const dot = toolbar.querySelector('[data-chip-dot]');
  if (dot) {
    dot.classList.toggle('ok', model.known === true);
    dot.classList.toggle('warn', model.known !== true);
  }
  const setup = view.setup ?? {};
  const thinking = toolbar.querySelector('[data-bench-thinking]');
  if (thinking && Array.isArray(model.levels)) {
    const level = setup.thinking ?? 'off';
    thinking.replaceChildren(...model.levels.map((item) => {
      const option = new Option(item.taken ? item.value : `${item.value} (unsupported)`, item.value, false, item.value === level);
      option.disabled = !item.taken && item.value !== level;
      if (item.why) option.title = item.why;
      return option;
    }));
    thinking.value = level;
  }
  const rules = setup.rules ?? {};
  for (const input of root.querySelectorAll('[data-bench-rule]')) {
    const key = input.dataset.benchRule;
    if (input.type === 'checkbox') input.checked = rules[key] !== false;
    else if (rules[key] !== undefined) input.value = rules[key];
  }
  const system = root.querySelector('[data-bench-system]');
  if (system && document.activeElement !== system && !systemTimer) system.value = setup.systemPrompt ?? '';
  systemCount();
  const reset = root.querySelector('[data-bench-reset]');
  if (reset) reset.disabled = view.state === 'running';
}

function setFooter(view) {
  const busyState = view.pageState === 'awaiting' || view.pageState === 'running';
  const message = root.querySelector('[data-bench-message]');
  if (message) {
    message.disabled = busyState;
    message.placeholder = view.pageState === 'awaiting' ? 'Send the pending tool result first' : 'Add a user message…';
  }
  const add = root.querySelector('[data-bench-add]');
  if (add) add.disabled = busyState;
  const addRun = root.querySelector('[data-bench-add-run]');
  if (addRun) {
    addRun.disabled = busyState;
    addRun.hidden = view.pageState === 'empty';
  }
}

/** Renders the server's answer (the page's parts); an older version than the one shown is ignored. */
function render(view) {
  if (!view || view.id !== root.dataset.session) return;
  const version = Number(view.version ?? 0);
  if (version < rendered) return;
  rendered = version;
  root.dataset.version = String(version);
  root.dataset.state = view.pageState ?? 'default';
  root.dataset.benchState = view.state ?? '';
  root.dataset.runId = view.runId ?? '';
  root.dataset.setup = JSON.stringify(view.setup ?? {});
  root.dataset.messages = JSON.stringify(view.messages ?? []);
  const html = view.html ?? {};
  swapInner('controls', html.controls);
  swapInner('timelineHead', html.timelineHead);
  swapTimeline(html.timeline);
  swapOuter('tools', html.tools);
  restoreSelection();
  swapLog(html.log);
  setToolbar(view);
  setFooter(view);
  follow(view);
}

async function refresh() {
  const seq = ++viewSeq;
  try {
    const view = await getRequest(api('/view'));
    if (seq === viewSeq) render(view);
  } catch (error) {
    if (error instanceof RequestError && error.status === 404) toast('Session gone', 'This bench session no longer exists. Reload the page.');
  }
}

// ---- live: the session's changes and the running turn's events ------------------------------------------------------------------

function listen() {
  sessionEvents = new EventSource(api('/events'));
  sessionEvents.addEventListener('session', (message) => {
    let frame = null;
    try { frame = JSON.parse(message.data); } catch { return; }
    if (Number(frame?.version ?? 0) > rendered) refresh();
  });
}

function streamTarget() {
  if (!runStream) return null;
  return root.querySelector(`[data-turn-run="${CSS.escape(runStream.id)}"] [data-bench-stream]`);
}

function showStream() {
  const target = streamTarget();
  if (!target || !runStream.text) return;
  const host = part('timeline');
  const atBottom = host && host.scrollHeight - host.scrollTop - host.clientHeight < 48;
  target.textContent = runStream.text;
  if (atBottom) host.scrollTop = host.scrollHeight;
  target.hidden = false;
  const waiting = target.parentElement?.querySelector('[data-bench-waiting-model]');
  if (waiting) waiting.hidden = true;
}

function follow(view) {
  const id = view.state === 'running' ? view.runId : null;
  if (runStream && runStream.id === id) return;
  if (runStream) {
    runStream.source.close();
    runStream = null;
  }
  if (!id) return;
  const source = new EventSource(`/api/runs/${encodeURIComponent(id)}/events`);
  runStream = { id, source, text: '' };
  const stream = runStream;
  source.addEventListener('event', (message) => {
    let item = null;
    try { item = JSON.parse(message.data); } catch { return; }
    const event = item?.event ?? {};
    if (event.type === 'text_delta' && typeof event.delta === 'string') {
      stream.text += event.delta;
      if (runStream === stream) showStream();
    }
  });
  source.addEventListener('status', () => {
    source.close();
    if (runStream === stream) refresh();
  });
}

// ---- actions ------------------------------------------------------------------------------------------------------------------------

// Sends an action that may start a turn; asks the Spend Guard's question and sends `retry(spend)` with the user's choice.
async function act(title, send) {
  if (acting) return null;
  acting = true;
  try {
    let answer = await send(null);
    // What the user just did shows at the end of the conversation.
    pinBottom = true;
    render(answer?.view);
    while (answer && answer.posted === false && answer.spend) {
      if (!answer.spend.html) {
        toast('Not sent', answer.message ?? 'The Spend Guard did not let this turn through.', 'warn');
        return answer;
      }
      const choice = await showSpendConfirm(answer.spend);
      if (choice.choice !== 'proceed-once' && choice.choice !== 'cap-output') return answer;
      const spend = { choice: choice.choice };
      if (choice.maxOutputTokens) spend.maxOutputTokens = choice.maxOutputTokens;
      answer = await postRequest(api('/run'), { spend });
      render(answer?.view);
    }
    return answer;
  } catch (error) {
    toast(title, error);
    return null;
  } finally {
    acting = false;
  }
}

function run() {
  const button = root.querySelector('[data-bench-run]');
  if (!button || button.disabled) return;
  act('Run not started', () => postRequest(api('/run'), {}));
}

async function stop() {
  try {
    render(await postRequest(api('/stop'), {}));
  } catch (error) {
    toast('Not stopped', error);
  }
}

async function reset() {
  try {
    render(await postRequest(api('/reset'), {}));
  } catch (error) {
    toast('Not reset', error);
  }
}

async function addMessage(andRun) {
  const box = root.querySelector('[data-bench-message]');
  const text = box?.value.trim() ?? '';
  if (!text) {
    box?.focus();
    return;
  }
  const answer = await act(andRun ? 'Run not started' : 'Message not added', () => postRequest(api('/messages'), { text, run: andRun }));
  if (answer && box.value.trim() === text) {
    box.value = '';
    box.style.height = '';
  }
}

async function removeMessage(index) {
  try {
    render(await postRequest(api(`/messages/${index}/remove`), {}));
  } catch (error) {
    toast('Message not removed', error);
  }
}

// ---- the awaiting card ----------------------------------------------------------------------------------------------------------------

function resultCheck(card) {
  const text = card.querySelector('[data-result-text]')?.value ?? '';
  const check = card.querySelector('[data-result-check]');
  if (!check) return;
  const format = card.querySelector('[data-result-format]:checked')?.value ?? 'json';
  check.classList.remove('text-ps-ok', 'text-ps-danger');
  if (!text.trim()) {
    check.textContent = 'empty';
    return;
  }
  const chars = `${text.length.toLocaleString('en-US')} chars`;
  if (format === 'text') {
    check.textContent = `text · ${chars}`;
    return;
  }
  try {
    JSON.parse(text);
    check.textContent = `valid JSON · ${chars}`;
    check.classList.add('text-ps-ok');
  } catch {
    check.textContent = format === 'json' ? `not valid JSON · ${chars}` : `text · ${chars}`;
    if (format === 'json') check.classList.add('text-ps-danger');
  }
}

function fill(card, button) {
  const text = card.querySelector('[data-result-text]');
  text.value = button.dataset.fill ?? '';
  const isError = button.dataset.fillError === 'true';
  card.querySelector('[data-result-error]').checked = isError;
  let format = isError ? 'error' : 'json';
  if (!isError) {
    try { JSON.parse(text.value); } catch { format = 'text'; }
  }
  for (const radio of card.querySelectorAll('[data-result-format]')) radio.checked = radio.value === format;
  card.dataset.filled = button.dataset.fillName ?? '';
  card.dataset.filledText = text.value;
  for (const other of card.querySelectorAll('[data-fill]')) other.setAttribute('aria-pressed', other === button ? 'true' : 'false');
  resultCheck(card);
  text.focus();
}

async function sendResult(card, resume) {
  const id = card.dataset.awaiting;
  const text = card.querySelector('[data-result-text]')?.value ?? '';
  const format = card.querySelector('[data-result-format]:checked')?.value ?? 'json';
  const isError = format === 'error' || card.querySelector('[data-result-error]')?.checked === true;
  if (format === 'json' && text.trim()) {
    try { JSON.parse(text); } catch {
      toast('Result not sent', 'The result is not valid JSON. Fix it, or choose Text to send it as it is.', 'warn');
      return;
    }
  }
  const mock = card.dataset.filled && card.dataset.filledText === text ? card.dataset.filled : undefined;
  const body = { text, isError, continue: resume };
  if (mock) body.mock = mock;
  card.querySelectorAll('[data-result-send]').forEach((button) => { button.disabled = true; });
  const answer = await act('Result not sent', () => postRequest(api(`/calls/${encodeURIComponent(id)}/result`), body));
  if (!answer) card.querySelectorAll('[data-result-send]').forEach((button) => { button.disabled = false; });
}

// ---- the toolbar ----------------------------------------------------------------------------------------------------------------------

function systemCount() {
  const system = root.querySelector('[data-bench-system]');
  const count = root.querySelector('[data-bench-system-count]');
  if (system && count) count.textContent = `${system.value.length.toLocaleString('en-US')} chars`;
}

function saveSystem() {
  window.clearTimeout(systemTimer);
  systemTimer = window.setTimeout(() => {
    systemTimer = 0;
    const value = root.querySelector('[data-bench-system]')?.value ?? '';
    changeSetup((setup) => {
      if (value.trim()) setup.systemPrompt = value; else delete setup.systemPrompt;
    }, root).catch((error) => toast('System prompt not saved', error));
  }, 600);
}

function wirePicker() {
  const form = document.querySelector('[data-model-picker]');
  form?.addEventListener('submit', (event) => {
    event.preventDefault();
    const choice = providerModelValue(form.querySelector('[data-provider-model]'));
    if (!choice?.provider || !choice?.model) return;
    window.bootstrap?.Modal.getOrCreateInstance(form.closest('.modal')).hide();
    changeSetup((setup) => {
      const changed = setup.provider !== choice.provider || setup.model !== choice.model;
      setup.provider = choice.provider;
      setup.model = choice.model;
      if (changed) delete setup.account;
      if (setup.thinking && !choice.thinking.includes(setup.thinking)) delete setup.thinking;
    }, root).then(() => showToast({ title: 'Model changed', body: 'The next turn runs on it.', tone: 'ok' }))
      .catch((error) => toast('Model not changed', error));
  });
}

// ---- wiring ---------------------------------------------------------------------------------------------------------------------------

function onClick(event) {
  const target = event.target;
  const button = target.closest('button');
  if (button && root.contains(button) && !button.disabled) {
    if (button.matches('[data-bench-run]')) return run();
    if (button.matches('[data-bench-stop]')) return stop();
    if (button.matches('[data-bench-reset]')) return reset();
    if (button.matches('[data-bench-add]')) return addMessage(false);
    if (button.matches('[data-bench-add-run]')) return addMessage(true);
    if (button.matches('[data-bench-remove]')) return removeMessage(Number(button.dataset.benchRemove));
    if (button.matches('[data-bench-copy-text]')) {
      copyText(button.dataset.benchCopyText ?? '').then((ok) => showToast({ title: ok ? 'Copied' : 'Copy failed', tone: ok ? 'ok' : 'danger' }));
      return undefined;
    }
    const card = button.closest('[data-awaiting]');
    if (card && button.matches('[data-fill]')) return fill(card, button);
    if (card && button.matches('[data-result-send]')) return sendResult(card, button.dataset.resultSend === 'continue');
  }
  const row = target.closest('[data-bench-log-row]');
  if (row && root.contains(row)) {
    selectedCall = row.dataset.benchLogRow;
    selectCall(selectedCall, true);
  }
  return undefined;
}

function onInput(event) {
  const target = event.target;
  if (target.matches('[data-result-text]')) resultCheck(target.closest('[data-awaiting]'));
  else if (target.matches('[data-bench-system]')) {
    systemCount();
    saveSystem();
  } else if (target.matches('[data-bench-message]')) {
    target.style.height = 'auto';
    target.style.height = `${Math.min(target.scrollHeight, 200)}px`;
  }
}

function onChange(event) {
  const target = event.target;
  const card = target.closest('[data-awaiting]');
  if (card && target.matches('[data-result-format]')) {
    const error = card.querySelector('[data-result-error]');
    if (error) error.checked = target.value === 'error';
    resultCheck(card);
  } else if (target.matches('[data-bench-thinking]')) {
    const level = target.value;
    changeSetup((setup) => {
      if (level === 'off') delete setup.thinking; else setup.thinking = level;
    }, root).catch((error) => toast('Thinking not changed', error));
  } else if (target.matches('[data-bench-rule]')) {
    const key = target.dataset.benchRule;
    const value = target.type === 'checkbox' ? target.checked : target.value;
    changeSetup((setup) => {
      setup.rules = { ...(setup.rules ?? {}), [key]: value };
    }, root).catch((error) => toast('Rule not saved', error));
  }
}

function onKeydown(event) {
  if (event.defaultPrevented || event.isComposing) return;
  const ctrl = event.ctrlKey || event.metaKey;
  const modalOpen = document.querySelector('.modal.show');
  if (ctrl && event.key === 'Enter' && !modalOpen) {
    event.preventDefault();
    const card = event.target.closest?.('[data-awaiting]');
    if (card) return void sendResult(card, true);
    if (event.target.matches?.('[data-bench-message]') && event.target.value.trim()) return void addMessage(true);
    return void run();
  }
  if (event.key === 'Escape' && !modalOpen && !document.querySelector('.dropdown-menu.show')) {
    const state = root.dataset.benchState;
    if (state === 'running' || state === 'awaiting') stop();
    return undefined;
  }
  const row = event.target.closest?.('[data-bench-log-row]');
  if (row && (event.key === 'Enter' || event.key === ' ')) {
    event.preventDefault();
    selectedCall = row.dataset.benchLogRow;
    selectCall(selectedCall, true);
  }
  return undefined;
}

function tick() {
  const now = Date.now();
  for (const chip of root.querySelectorAll('[data-paused-at]')) {
    const seconds = Math.max(0, Math.floor((now - Number(chip.dataset.pausedAt)) / 1000));
    const label = chip.querySelector('[data-paused-for]');
    if (label) label.textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }
}

function start() {
  if (!root) return;
  initBenchTools(root);
  wirePicker();
  root.addEventListener('click', onClick);
  root.addEventListener('input', onInput);
  root.addEventListener('change', onChange);
  root.addEventListener('bench-view', (event) => render(event.detail));
  document.addEventListener('keydown', onKeydown);
  for (const card of root.querySelectorAll('[data-awaiting]')) resultCheck(card);
  // The page opens on the end of the conversation (or the first call that waits for a result).
  const timeline = part('timeline');
  const waiting = timeline?.querySelector('[data-awaiting]');
  if (waiting) waiting.scrollIntoView({ block: 'nearest' });
  else if (timeline) timeline.scrollTop = timeline.scrollHeight;
  systemCount();
  tick();
  window.setInterval(tick, 1000);
  listen();
  if (root.dataset.benchState === 'running' && root.dataset.runId) {
    follow({ state: 'running', runId: root.dataset.runId });
  }
  if (readSetup(root)?.setup?.tools?.length === 0 && root.dataset.benchState === 'idle') {
    root.querySelector('[data-bench-new-tool]')?.focus({ preventScroll: true });
  }
}

start();
