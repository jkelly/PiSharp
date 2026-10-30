// Decision 0011 (run W4, R2): Compare's request mode. Run all (Ctrl+Enter) sends the shared prompt and every column to POST /api/compare/run:
// the run engine starts the columns as one group, reserved by the Spend Guard all or none; the page never calls a provider. A group the
// guard refuses shows the over-cap banner and modal-spend-confirm, and is sent again (same group id) only with the user's choice. Each
// column then follows its run's events (GET /api/runs/{id}/events, SSE) as plain text (textContent only) and, when it ends, swaps in the
// server's _CompareColumn, where the answer is Markdown through the sanitizing MarkdownRenderer; the Metrics tab and the status line follow.
// The URL becomes ?group={id}, so a reload reopens the comparison, live while it runs. A column's model comes from #modal-model-picker
// (Provider → Model); a new model resets the column and keeps its overrides (one the model does not take stays in the form, not sent).
// Re-run sends one column again into its group (POST /api/compare/groups/{id}/rerun), Stop this column cancels its run, Stop all (Esc)
// cancels the group's. The estimate by Run all and the columns' chips follow the draft (POST /api/compare/plan, debounced). Sync scroll
// keeps the columns' scroll positions in step. Tooltips start lazily on hover or focus.
import { showToast, providerModelValue } from './shell.js';
import { showSpendConfirm } from './spend-confirm.js';
import { postRequest, getFragment, copyText, RequestError } from './request-api.js';

let host;
let prompt;
let grid;
let metricsHost;
let group = null;
let busy = false;
let planTimer = 0;
let planSeq = 0;
let statusTimer = 0;
let pickerTarget = null;
let pending = null;
let syncing = false;
const streams = new Map();

const $ = (selector, root = document) => root.querySelector(selector);
const toast = (title, body, tone = 'danger') => showToast({ title, body, tone });
const columns = () => [...grid.querySelectorAll('[data-compare-col]')];
const maxColumns = () => Number(host.dataset.maxColumns || 4);
const minColumns = () => Number(host.dataset.minColumns || 2);
const streaming = () => columns().some((column) => column.dataset.state === 'streaming');

// ---- draft --------------------------------------------------------------------------------------------------------------------

function number(input, integer) {
  const text = input?.value.trim() ?? '';
  if (text === '') return null;
  const value = Number(text);
  if (!Number.isFinite(value)) throw new Error(`${input.dataset.shared ?? input.dataset.override} is not a number.`);
  return integer ? Math.trunc(value) : value;
}

function shared() {
  const thinking = $('[data-shared="thinking"]', prompt).value;
  const system = $('[data-compare-system]', prompt).value;
  return {
    systemPrompt: system.trim() === '' ? null : system,
    prompt: $('[data-compare-text]', prompt).value,
    thinking: thinking === 'off' ? null : thinking,
    maxTokens: number($('[data-shared="maxTokens"]', prompt), true),
    temperature: number($('[data-shared="temperature"]', prompt), false),
  };
}

function columnDraft(column) {
  const thinking = $('[data-override="thinking"]', column)?.value || null;
  const temperature = $('[data-override="temperature"]', column);
  return {
    provider: column.dataset.provider,
    model: column.dataset.model,
    thinking,
    temperature: temperature && !temperature.disabled ? number(temperature, false) : null,
  };
}

function draft() {
  return { ...shared(), columns: columns().map(columnDraft) };
}

// ---- layout -------------------------------------------------------------------------------------------------------------------

function layout() {
  const count = columns().length;
  const add = $('[data-compare-add]', grid);
  const slots = maxColumns() - count;
  grid.style.setProperty('--cmp-cols', String(count));
  grid.classList.toggle('has-add', slots > 0);
  add.hidden = slots <= 0;
  $('[data-compare-slots]', add).textContent = `Up to ${maxColumns()} columns · ${slots} ${slots === 1 ? 'slot' : 'slots'} left`;
  const subtitle = $('[data-compare-subtitle]');
  if (subtitle) subtitle.textContent = `${count} ${count === 1 ? 'model' : 'models'} · one shared prompt`;
  const live = streaming();
  for (const column of columns()) {
    const remove = $('[data-col-remove]', column);
    if (remove) remove.disabled = live && column.dataset.state === 'streaming' || count <= minColumns();
  }
  $('[data-col-add]', add).disabled = live;
  $('[data-run-all]', prompt).hidden = live;
  $('[data-run-all]', prompt).disabled = busy;
  $('[data-stop-all]', prompt).hidden = !live;
  document.querySelectorAll('[data-ac-mode]').forEach((radio) => { radio.disabled = live; });
  window.clearInterval(statusTimer);
  if (live) {
    statusTimer = window.setInterval(liveStatus, 500);
    liveStatus();
  }
}

function setStatus(text, tone = 'faint') {
  const line = $('[data-compare-status]');
  if (!line) return;
  line.classList.remove('text-ps-faint', 'text-ps-blue', 'text-ps-danger');
  line.classList.add(tone === 'live' ? 'text-ps-blue' : tone === 'danger' ? 'text-ps-danger' : 'text-ps-faint');
  const dot = document.createElement('span');
  dot.className = 'status-dot live me-1';
  const label = document.createElement('span');
  label.dataset.compareStatusText = '';
  label.textContent = text;
  line.replaceChildren(...(tone === 'live' ? [dot, label] : [label]));
}

function liveStatus() {
  const live = columns().filter((column) => column.dataset.state === 'streaming');
  if (live.length === 0) return;
  const started = Math.min(...live.map((column) => Number(column.dataset.started) || Date.now()));
  setStatus(`${live.length} streaming · ${((Date.now() - started) / 1000).toFixed(1)} s`, 'live');
}

function statusFromMetrics() {
  const root = $('[data-compare-metrics]', metricsHost);
  if (root && !streaming()) setStatus(root.dataset.statusLine ?? '', root.dataset.statusTone ?? 'faint');
}

// ---- tooltips -----------------------------------------------------------------------------------------------------------------

function wireTooltips() {
  const show = (event) => {
    const target = event.target.closest?.('[data-bs-toggle="tooltip"]');
    if (!target || !window.bootstrap) return;
    window.bootstrap.Tooltip.getOrCreateInstance(target).show();
  };
  document.addEventListener('mouseover', show);
  document.addEventListener('focusin', show);
}

function disposeTooltips(element) {
  if (!window.bootstrap) return;
  element.querySelectorAll('[data-bs-toggle="tooltip"]').forEach((item) => window.bootstrap.Tooltip.getInstance(item)?.dispose());
}

// ---- plan and estimate --------------------------------------------------------------------------------------------------------

function schedulePlan() {
  window.clearTimeout(planTimer);
  planTimer = window.setTimeout(plan, 400);
}

function chip(item) {
  const span = document.createElement('span');
  span.className = 'ps-chip';
  if (item.tone) span.dataset.tone = item.tone;
  if (item.title) span.title = item.title;
  span.dataset.chip = item.kind ?? 'adjusted';
  span.textContent = item.text ?? '';
  return span;
}

async function plan() {
  const seq = ++planSeq;
  let body;
  try {
    body = draft();
  } catch {
    return;
  }
  const estimateHost = $('[data-estimate-host]', prompt);
  try {
    const planned = await postRequest('/api/compare/plan', body);
    if (seq !== planSeq) return;
    disposeTooltips(estimateHost);
    // The estimate chip is the server's _RunEstimate markup (no model text).
    const markup = planned?.estimate?.html ?? '';
    const template = document.createElement('template');
    template.innerHTML = markup;
    estimateHost.replaceChildren(template.content);
    const list = columns();
    (planned?.columns ?? []).forEach((item, index) => {
      const column = list[index];
      if (!column) return;
      $('[data-col-draft-chips]', column)?.replaceChildren(...(item.chips ?? []).map(chip));
      if (item.ok) delete column.dataset.refused;
    });
    const run = $('[data-run-all]', prompt);
    run.title = planned?.valid ? 'Run all (Ctrl+Enter)' : `Run all (Ctrl+Enter) · ${planned?.message ?? ''}`;
  } catch {
    if (seq === planSeq) estimateHost.replaceChildren();
  }
}

// The overrides' "inherit (…)" labels follow the shared options.
function sharedChanged() {
  let values;
  try { values = shared(); } catch { return; }
  for (const column of columns()) {
    const inherit = $('[data-override="thinking"] [data-inherit]', column);
    if (inherit) inherit.textContent = `inherit (${values.thinking ?? 'off'})`;
    const temperature = $('[data-override="temperature"]', column);
    if (temperature && !temperature.disabled) temperature.placeholder = `inherit (${values.temperature ?? 'provider default'})`;
  }
  const preview = $('[data-system-preview]', prompt);
  if (preview) {
    const text = values.systemPrompt ?? '';
    preview.textContent = text ? '· ' + (text.length > 70 ? text.slice(0, 70) + '…' : text) : '· none';
  }
}

// ---- runs ---------------------------------------------------------------------------------------------------------------------

function showOvercap(message) {
  const banner = $('[data-overcap]', prompt);
  $('[data-overcap-text]', banner).textContent = message ?? 'The Spend Guard asked first, so nothing has been reserved or sent.';
  banner.hidden = false;
}

function hideOvercap() {
  $('[data-overcap]', prompt).hidden = true;
  pending = null;
}

// The user's answer to the confirm modal. Choose a model opens #modal-model-picker (spend-confirm.js) for the column the guard refused.
async function decide(choice, retry, answer) {
  if (choice?.choice === 'choose-model') {
    const column = columns().find((item) => item.dataset.provider === answer?.provider && item.dataset.model === answer?.model);
    if (column) pickerTarget = { key: column.dataset.key };
    return;
  }
  if (choice?.choice === 'proceed-once') await retry({ choice: 'proceed-once' });
  else if (choice?.choice === 'cap-output' && choice.maxOutputTokens) await retry({ choice: 'cap-output', maxOutputTokens: choice.maxOutputTokens });
}

// A refusal of the Spend Guard: nothing was reserved or sent. The banner stays until a run goes through; Review asks again.
async function refused(answer, retry) {
  if (!answer?.spend) {
    toast('Not run', answer?.message ?? 'The Spend Guard refused the run.');
    return;
  }
  pending = { answer, retry };
  showOvercap(answer.message);
  setStatus('Not run · waiting for your decision · last results below');
  const choice = await showSpendConfirm(answer.spend);
  await decide(choice, retry, answer);
}

function markRefused(error) {
  const index = Number(error?.data?.error?.column);
  if (!Number.isInteger(index) || index < 1) return;
  const column = columns()[index - 1];
  if (column) column.dataset.refused = 'true';
}

async function runAll(spend, groupId) {
  if (busy || streaming()) return;
  let body;
  try {
    body = draft();
  } catch (error) {
    toast('Not run', error.message, 'warn');
    return;
  }
  if (body.prompt.trim() === '') {
    toast('Not run', 'Write a prompt to compare the models on.', 'warn');
    $('[data-compare-text]', prompt).focus();
    return;
  }
  if (body.columns.length < minColumns()) {
    toast('Not run', `Add at least ${minColumns()} models to compare.`, 'warn');
    return;
  }
  busy = true;
  layout();
  try {
    const answer = await postRequest('/api/compare/run', { ...body, ...(groupId ? { groupId } : {}), ...(spend ? { spend } : {}) });
    if (!answer?.posted) {
      busy = false;
      layout();
      await refused(answer, (choice) => runAll(choice, answer?.groupId));
      return;
    }
    hideOvercap();
    group = answer.groupId;
    host.dataset.group = group;
    history.replaceState(null, '', '/compare?group=' + encodeURIComponent(group));
    const list = columns();
    await Promise.all((answer.columns ?? []).map((item) => (list[item.index] ? load(list[item.index], item.runId) : null)));
  } catch (error) {
    if (error instanceof RequestError) markRefused(error);
    toast('Not run', error.message);
  } finally {
    busy = false;
    layout();
    await refresh();
  }
}

async function rerun(column, spend) {
  if (!group || busy || column.dataset.state === 'streaming') return;
  let body;
  try {
    body = { column: columnDraft(column) };
  } catch (error) {
    toast('Not run', error.message, 'warn');
    return;
  }
  body.replaces = column.dataset.runId || column.dataset.replaces || null;
  body.position = columns().indexOf(column);
  if (spend) body.spend = spend;
  const button = $('[data-col-rerun]', column);
  if (button) button.disabled = true;
  try {
    const answer = await postRequest(`/api/compare/groups/${encodeURIComponent(group)}/rerun`, body);
    if (!answer?.posted) {
      if (button) button.disabled = false;
      const key = column.dataset.key;
      await refused(answer, (choice) => {
        const current = columns().find((item) => item.dataset.key === key);
        return current ? rerun(current, choice) : null;
      });
      return;
    }
    hideOvercap();
    await load(column, answer.id);
  } catch (error) {
    if (button) button.disabled = false;
    if (error instanceof RequestError) column.dataset.refused = 'true';
    toast('Not run', error.message);
  } finally {
    layout();
    await refresh();
  }
}

// Swaps in the server's column for run `runId` and follows it while it streams.
async function load(column, runId) {
  if (!group) return;
  const key = column.dataset.key;
  const fresh = await getFragment(`/compare/groups/${encodeURIComponent(group)}/runs/${encodeURIComponent(runId)}?key=${encodeURIComponent(key)}`);
  if (!fresh) return;
  streams.get(key)?.close();
  streams.delete(key);
  disposeTooltips(column);
  column.replaceWith(fresh);
  if (fresh.dataset.state === 'streaming') follow(fresh);
}

function follow(column) {
  const key = column.dataset.key;
  const id = column.dataset.runId;
  if (!id) return;
  streams.get(key)?.close();
  const text = $('[data-stream-text]', column);
  const thinking = $('[data-stream-thinking]', column);
  const thinkingBox = $('[data-col-thinking]', column);
  const ttft = $('[data-foot="ttft"]', column);
  const elapsed = $('[data-foot="elapsed"]', column);
  const started = Number(column.dataset.started) || Date.now();
  let ended = false;
  const timer = window.setInterval(() => {
    if (elapsed) elapsed.textContent = `${((Date.now() - started) / 1000).toFixed(1)} s…`;
  }, 200);
  const source = new EventSource(`/api/runs/${encodeURIComponent(id)}/events?after=${Number(column.dataset.lastSeq) || 0}`);
  const close = () => {
    ended = true;
    source.close();
    window.clearInterval(timer);
  };
  source.addEventListener('event', (message) => {
    let item;
    try { item = JSON.parse(message.data); } catch { return; }
    const event = item.event ?? {};
    if (event.type === 'text_delta' && typeof event.delta === 'string') {
      if (text) text.append(event.delta);
      if (ttft && ttft.textContent === 'TTFT …' && Number.isFinite(item.atMs)) ttft.textContent = `TTFT ${(item.atMs / 1000).toFixed(2)} s`;
    } else if (event.type === 'thinking_delta' && typeof event.delta === 'string' && thinking) {
      thinking.append(event.delta);
      if (thinkingBox) thinkingBox.hidden = false;
    }
    column.dataset.lastSeq = String(item.seq ?? column.dataset.lastSeq);
  });
  source.addEventListener('status', async () => {
    if (ended) return;
    close();
    streams.delete(key);
    const current = columns().find((item) => item.dataset.key === key);
    if (!current || current.dataset.runId !== id) return;
    try {
      await load(current, id);
    } catch (error) {
      toast('Could not show the answer', error.message);
    }
    layout();
    await refresh();
  });
  streams.set(key, { close });
}

// The Metrics tab and the status line; once nothing streams, every column again (fastest and best are the group's).
async function refresh() {
  if (!group) return;
  try {
    const metrics = await getFragment(`/compare/groups/${encodeURIComponent(group)}/metrics`);
    if (metrics) metricsHost.replaceChildren(metrics);
  } catch {
    return;
  }
  if (!streaming()) {
    for (const column of columns()) {
      if (!column.dataset.runId) continue;
      try { await load(column, column.dataset.runId); } catch { /* the column stays as it was */ }
    }
    layout();
  }
  statusFromMetrics();
}

async function stopAll() {
  if (!group || !streaming()) return;
  try {
    await postRequest(`/api/compare/groups/${encodeURIComponent(group)}/stop`, {});
  } catch (error) {
    toast('Could not stop the runs', error.message);
  }
}

async function stopColumn(column) {
  const id = column.dataset.runId;
  if (!id) return;
  try {
    await postRequest(`/api/runs/${encodeURIComponent(id)}/cancel`, {});
  } catch (error) {
    toast('Could not stop the column', error.message);
  }
}

async function markBest(column) {
  if (!group || !column.dataset.runId) return;
  const best = column.dataset.best !== 'true';
  try {
    await postRequest(`/api/compare/groups/${encodeURIComponent(group)}/best`, { runId: best ? column.dataset.runId : null });
  } catch (error) {
    toast('Could not mark the column', error.message);
    return;
  }
  await refresh();
}

async function removeColumn(column) {
  if (columns().length <= minColumns()) return;
  const run = column.dataset.runId || column.dataset.replaces;
  if (group && run) {
    try {
      await postRequest(`/api/compare/groups/${encodeURIComponent(group)}/remove`, { runId: run });
    } catch (error) {
      if (!(error instanceof RequestError && error.status === 404)) {
        toast('Could not remove the column', error.message);
        return;
      }
    }
  }
  streams.get(column.dataset.key)?.close();
  streams.delete(column.dataset.key);
  disposeTooltips(column);
  column.remove();
  layout();
  schedulePlan();
  await refresh();
}

// ---- model picker -------------------------------------------------------------------------------------------------------------

function setPicker(root, provider, model) {
  const providerSelect = $('[data-provider-select]', root);
  if (!providerSelect || ![...providerSelect.options].some((option) => option.value === provider)) return;
  if (providerSelect.value !== provider) {
    providerSelect.value = provider;
    providerSelect.dispatchEvent(new Event('change', { bubbles: true }));
  }
  const pane = [...root.querySelectorAll('[data-provider]')].find((element) => element.dataset.provider === provider);
  const modelSelect = $('[data-model-select]', pane ?? root);
  if (!pane || !modelSelect || ![...modelSelect.options].some((option) => option.value === model)) return;
  if (modelSelect.value !== model) {
    modelSelect.value = model;
    modelSelect.dispatchEvent(new Event('change', { bubbles: true }));
  }
}

function openPicker(target) {
  const modal = document.getElementById('modal-model-picker');
  if (!modal || !window.bootstrap) return;
  pickerTarget = target;
  const select = $('[data-model-picker] [data-provider-model]', modal);
  const column = target?.key ? columns().find((item) => item.dataset.key === target.key) : columns()[columns().length - 1];
  if (select && column) setPicker(select, column.dataset.provider, column.dataset.model);
  window.bootstrap.Modal.getOrCreateInstance(modal).show();
}

function nextKey() {
  const used = new Set(columns().map((column) => column.dataset.key));
  for (let index = 1; ; index++) if (!used.has(`c${index}`)) return `c${index}`;
}

async function columnFragment({ provider, model, key, thinking, temperature, count }) {
  const values = (() => { try { return shared(); } catch { return {}; } })();
  const query = new URLSearchParams({ provider, model, key, count: String(count) });
  if (thinking) query.set('thinking', thinking);
  if (temperature !== null && temperature !== undefined && temperature !== '') query.set('temperature', String(temperature));
  if (values.thinking) query.set('sharedThinking', values.thinking);
  if (values.temperature !== null && values.temperature !== undefined) query.set('sharedTemperature', String(values.temperature));
  if (values.maxTokens) query.set('sharedMaxTokens', String(values.maxTokens));
  if (group) query.set('group', group);
  return getFragment('/compare/column?' + query.toString());
}

async function chooseModel(provider, model) {
  const target = pickerTarget;
  pickerTarget = null;
  try {
    if (target?.key) {
      const column = columns().find((item) => item.dataset.key === target.key);
      if (!column || column.dataset.state === 'streaming') return;
      // A new model resets the column (its answer was another model's) and keeps the overrides; one it does not take is kept, not sent.
      const thinking = $('[data-override="thinking"]', column)?.value || column.dataset.keptThinking || null;
      const temperature = $('[data-override="temperature"]', column)?.value ?? '';
      const fresh = await columnFragment({ provider, model, key: column.dataset.key, thinking, temperature, count: columns().length });
      if (!fresh) return;
      const replaces = column.dataset.runId || column.dataset.replaces;
      if (replaces) fresh.dataset.replaces = replaces;
      streams.get(column.dataset.key)?.close();
      streams.delete(column.dataset.key);
      disposeTooltips(column);
      column.replaceWith(fresh);
    } else {
      if (columns().length >= maxColumns()) {
        toast('No free column', `Compare runs at most ${maxColumns()} models; remove one first.`, 'warn');
        return;
      }
      const fresh = await columnFragment({ provider, model, key: nextKey(), count: columns().length + 1 });
      if (!fresh) return;
      $('[data-compare-add]', grid).before(fresh);
    }
  } catch (error) {
    toast('Model not loaded', error.message);
    return;
  }
  layout();
  schedulePlan();
}

function wirePicker() {
  const form = $('[data-model-picker]');
  form?.addEventListener('submit', (event) => {
    event.preventDefault();
    const choice = providerModelValue($('[data-provider-model]', form));
    window.bootstrap?.Modal.getOrCreateInstance(form.closest('.modal')).hide();
    if (choice?.provider && choice?.model) chooseModel(choice.provider, choice.model);
  });
  form?.closest('.modal')?.addEventListener('hidden.bs.modal', () => {
    window.setTimeout(() => { pickerTarget = null; }, 0);
  });
}

// ---- sync scroll and copy -----------------------------------------------------------------------------------------------------

function onScroll(event) {
  const body = event.target;
  if (!(body instanceof HTMLElement) || !body.matches('[data-col-body]') || syncing || !$('[data-sync-scroll]', prompt)?.checked) return;
  const range = body.scrollHeight - body.clientHeight;
  const ratio = range > 0 ? body.scrollTop / range : 0;
  syncing = true;
  for (const other of grid.querySelectorAll('[data-col-body]')) {
    if (other === body) continue;
    other.scrollTop = ratio * (other.scrollHeight - other.clientHeight);
  }
  window.requestAnimationFrame(() => { syncing = false; });
}

async function copyTable(button) {
  const table = document.querySelector(button.dataset.copyTable);
  if (!table) return;
  const text = [...table.rows].map((row) => [...row.cells].map((cell) => cell.textContent.trim().replace(/\s+/g, ' ')).join('\t')).join('\n');
  if (await copyText(text)) toast('Copied', 'The metrics table is on the clipboard.', 'ok');
  else toast('Not copied', 'The browser did not allow the clipboard.', 'warn');
}

// ---- wiring -------------------------------------------------------------------------------------------------------------------

export function initCompareRequest(root) {
  host = root;
  prompt = $('[data-compare-prompt]', host);
  grid = $('[data-compare-grid]', host);
  metricsHost = $('[data-metrics-host]', host);
  group = host.dataset.group || null;
  if (!prompt || !grid || !metricsHost) return;

  wireTooltips();
  wirePicker();
  prompt.addEventListener('input', (event) => {
    if (event.target.matches('[data-shared], [data-compare-system]')) sharedChanged();
    schedulePlan();
  });
  prompt.addEventListener('change', (event) => {
    if (event.target.matches('[data-shared]')) {
      sharedChanged();
      schedulePlan();
    }
  });
  grid.addEventListener('change', (event) => {
    if (event.target.matches('[data-override]')) schedulePlan();
  });
  grid.addEventListener('input', (event) => {
    if (event.target.matches('[data-override="temperature"]')) schedulePlan();
  });
  grid.addEventListener('scroll', onScroll, true);
  host.addEventListener('click', (event) => {
    const target = event.target.closest('button, a');
    if (!target || target.disabled) return;
    const column = target.closest('[data-compare-col]');
    if (target.matches('[data-run-all]')) runAll();
    else if (target.matches('[data-stop-all]')) stopAll();
    else if (target.matches('[data-overcap-review]') && pending) {
      const { answer, retry } = pending;
      showSpendConfirm(answer.spend).then((choice) => decide(choice, retry, answer));
    } else if (target.matches('[data-col-add]')) openPicker({ add: true });
    else if (target.matches('[data-col-model]') && column) openPicker({ key: column.dataset.key });
    else if (target.matches('[data-col-rerun], [data-col-retry]') && column) rerun(column);
    else if (target.matches('[data-col-stop]') && column) stopColumn(column);
    else if (target.matches('[data-col-best]') && column) markBest(column);
    else if (target.matches('[data-col-remove]') && column) removeColumn(column);
    else if (target.matches('[data-copy-table]')) copyTable(target);
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey) {
      if (document.querySelector('.modal.show')) return;
      event.preventDefault();
      if (!streaming()) runAll();
    } else if (event.key === 'Escape' && streaming() && !document.querySelector('.modal.show, .dropdown-menu.show')) {
      stopAll();
    }
  });

  layout();
  for (const column of columns()) if (column.dataset.state === 'streaming') follow(column);
  if (!streaming()) statusFromMetrics();
  schedulePlan();
}
