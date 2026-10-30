// Decision 0011 (run W4, R6): a new agent run's setup (_AgentSetup). Nothing runs before Run agents.
// - The shared system prompt (collapsed beside the Task label, its start shown on the toggle) goes to every model as the same
//   instructions; an empty one is not sent.
// - The project row: the folder (Change: a recent one or another path) is inspected through POST /compare/agent/project (a same-origin
//   JSON POST, since it runs git in that folder) after the page loads and after every change of folder, isolation, base or excludes; its
//   fragment brings the isolation chip, the banners (uncommitted changes a worktree leaves out, with Use a temporary copy instead or
//   Continue without them; a copy over the size warning; a worktree that can't be used) and modal-agent-workspace's content, whose Use
//   git worktrees / Use temporary copies set this run's isolation.
// - The columns (1 to 4, positions a to d): Add model and a column's model chip open #modal-model-picker; a choice loads the column from
//   GET /compare/agent/column. Removing a column re-letters the others. The shared thinking level and the limits reload every column
//   (each shows how the level applies to its model and its limits).
// - Limits and approvals: the summary line and the run cap chip follow the inputs.
// - Run agents (Ctrl+Enter) posts R5's create (POST /api/compare/agent) with a fresh request id; a copy over the size warning (409
//   too-large) asks first and is sent again only with the user's yes; the page then opens the run (?run={id}).
import { confirmAction, providerModelValue } from './shell.js';
import { RequestError, getFragment, postRequest } from './request-api.js';
import { $, $$, API, PAGES, busy, disposeTooltips, escapeHtml, hideModal, postFragment, requestId, setTooltip, toast } from './compare-agent-common.js';
// W5 E5 (blind-state lines): with the Blind switch on, Run agents starts a blind agent round (arena-start.js) and opens it instead.
import { blindOn, initBlindSwitch, startAgentRound } from './arena-start.js';

const SLOTS = ['a', 'b', 'c', 'd'];
const APPROVAL_TEXT = { ask: 'Ask', allowList: 'Allow-list', auto: 'Auto in workspaces' };

let root;
let grid;
let pickerTarget = null;
let projectSeq = 0;
let columnsSeq = 0;
let columnsTimer = 0;
let projectTimer = 0;
let dirtyDismissed = false;
let project = { ok: false, kind: '' };

const columns = () => $$('[data-ac-setup-col]', grid);
const maxColumns = () => Number(root.dataset.maxColumns) || SLOTS.length;
const projectPath = () => ($('[data-ac-path]', root)?.textContent ?? '').trim();
const excludes = () => ($('[data-ac-excludes]', root)?.value ?? '').split(/[,\n]/).map((item) => item.trim()).filter(Boolean);
const intValue = (selector) => {
  const value = Number.parseInt($(selector, root)?.value ?? '', 10);
  return Number.isFinite(value) && value > 0 ? value : null;
};
const sharedThinking = () => $('[data-ac-thinking]', root)?.value || '';
const systemPrompt = () => ($('[data-ac-system]', root)?.value ?? '').trim();
const approval = () => $('[data-ac-approval]:checked', root)?.value ?? 'ask';
const capText = () => ($('[data-ac-cap]', root)?.value ?? '').trim().replace(/^\$/, '');
const capValid = (text) => text === '' || (/^\d{0,7}(\.\d{0,6})?$/.test(text) && /\d/.test(text) && Number(text) > 0);

// ---- project ------------------------------------------------------------------------------------------------------------------

function scheduleProject(delay = 400) {
  window.clearTimeout(projectTimer);
  projectTimer = window.setTimeout(loadProject, delay);
}

async function loadProject() {
  window.clearTimeout(projectTimer);
  const seq = ++projectSeq;
  const host = $('[data-ac-iso-host]', root);
  if (host && !host.querySelector('.spinner-border')) {
    disposeTooltips(host);
    host.innerHTML = '<span class="ps-chip" data-tone="outline"><span class="spinner-border spinner-border-sm" aria-hidden="true"></span>Checking the folder…</span>';
  }
  const body = {
    path: projectPath(),
    isolation: root.dataset.isolation || 'automatic',
    baseRef: root.dataset.baseRef || null,
    excludes: excludes(),
    models: Math.max(1, columns().length),
  };
  let fragment;
  try {
    fragment = await postFragment(`${PAGES}/project`, body);
  } catch (error) {
    if (seq !== projectSeq) return;
    project = { ok: false, kind: '' };
    if (host) host.innerHTML = '<span class="ps-chip" data-tone="danger">Not checked</span>';
    toast('Folder not checked', error.message);
    return;
  }
  if (seq !== projectSeq || !fragment) return;
  project = { ok: fragment.dataset.ok === 'true', kind: fragment.dataset.kind ?? '' };
  if (host) {
    disposeTooltips(host);
    host.replaceChildren(...($('[data-ac-iso-part]', fragment)?.childNodes ?? []));
  }
  const banners = $('[data-ac-banners]', root);
  if (banners) {
    banners.replaceChildren(...($('[data-ac-banners-part]', fragment)?.childNodes ?? []));
    if (dirtyDismissed) $('[data-ac-banner="dirty"]', banners)?.remove();
  }
  const content = $('[data-ac-workspace-content]', fragment);
  const shell = document.getElementById('modal-agent-workspace');
  const current = shell && $('[data-ac-workspace-content]', shell);
  if (content && current) current.replaceWith(content);
}

function setProject(path) {
  const text = (path ?? '').trim();
  if (!text) return;
  const pathEl = $('[data-ac-path]', root);
  if (pathEl) pathEl.textContent = text;
  for (const item of $$('[data-ac-folder]', root)) {
    const current = item.dataset.acFolder.toLowerCase() === text.toLowerCase();
    if (current) item.setAttribute('aria-current', 'true');
    else item.removeAttribute('aria-current');
    item.querySelector('.bi-check2')?.classList.toggle('invisible', !current);
  }
  const toggle = $('[data-ac-project] [data-bs-toggle="dropdown"]', root);
  if (toggle && window.bootstrap) window.bootstrap.Dropdown.getOrCreateInstance(toggle).hide();
  root.dataset.baseRef = '';
  dirtyDismissed = false;
  loadProject();
}

function setIsolation(isolation) {
  root.dataset.isolation = isolation;
  dirtyDismissed = false;
  loadProject();
}

function onWorkspaceChange(event) {
  const radio = event.target.closest?.('[data-ac-ws-radio]');
  if (!radio) return;
  const content = radio.closest('[data-ac-workspace-content]');
  if (!content) return;
  content.dataset.pane = radio.value;
  for (const pane of $$('[data-ac-ws-pane]', content)) pane.hidden = pane.dataset.acWsPane !== radio.value;
  for (const button of $$('[data-ac-ws-apply]', content)) button.hidden = button.dataset.acWsApply !== radio.value;
}

function applyWorkspace(button) {
  const content = button.closest('[data-ac-workspace-content]');
  if (!content) return;
  const kind = button.dataset.acWsApply;
  const excludeInput = $('[data-ac-ws-excludes]', content);
  if (excludeInput) {
    const hidden = $('[data-ac-excludes]', root);
    if (hidden) hidden.value = excludeInput.value.split(/[,\n]/).map((item) => item.trim()).filter(Boolean).join(', ');
  }
  if (kind === 'worktree') {
    const commit = ($('[data-ac-ws-commit]', content)?.value ?? '').trim();
    const branchSelect = $('[data-ac-ws-branch]', content);
    const branch = branchSelect?.value ?? '';
    root.dataset.baseRef = commit || (branch && branch !== branchSelect.dataset.current ? branch : '');
  }
  hideModal(content);
  setIsolation(kind === 'copy' ? 'copy' : 'worktree');
}

// ---- columns ------------------------------------------------------------------------------------------------------------------

function columnValues(column) {
  return {
    provider: column.dataset.provider,
    model: column.dataset.model,
    thinking: $('[data-ac-override="thinking"]', column)?.value || column.dataset.keptThinking || '',
  };
}

function columnFragment(slot, { provider, model, thinking }, count) {
  const query = new URLSearchParams({ slot, provider, model, count: String(count) });
  if (thinking) query.set('thinking', thinking);
  const shared = sharedThinking();
  if (shared) query.set('sharedThinking', shared);
  const turns = intValue('[data-ac-max-turns]');
  const minutes = intValue('[data-ac-max-minutes]');
  if (turns) query.set('maxTurns', String(turns));
  if (minutes) query.set('maxMinutes', String(minutes));
  return getFragment(`${PAGES}/column?${query}`);
}

/** Positions are letters: after a removal or an insert the columns are a, b, c in order (ids and labels follow). */
function reletter() {
  columns().forEach((column, index) => {
    const slot = SLOTS[index];
    if (column.dataset.slot === slot) return;
    column.dataset.slot = slot;
    const letter = $('[data-ac-letter]', column);
    if (letter) letter.textContent = slot.toUpperCase();
    const name = $('[data-ac-workspace-name]', column);
    if (name) name.textContent = slot;
    const select = $('[data-ac-override="thinking"]', column);
    if (select) select.id = `ac-${slot}-thinking`;
    const label = $('label[data-ac-for="thinking"]', column);
    if (label) label.htmlFor = `ac-${slot}-thinking`;
    column.setAttribute('aria-label', `${slot.toUpperCase()}: ${column.dataset.provider}/${column.dataset.model}`);
  });
}

function layout() {
  const count = columns().length;
  const max = maxColumns();
  const free = Math.max(0, max - count);
  grid.style.setProperty('--cmp-cols', String(count));
  grid.classList.toggle('has-add', free > 0);
  const add = $('[data-ac-add]', grid);
  if (add) add.hidden = free <= 0;
  const slots = $('[data-ac-slots]', grid);
  if (slots) slots.textContent = `Up to ${max} agents · ${free} ${free === 1 ? 'slot' : 'slots'} left`;
  const status = $('[data-ac-setup-status]', root);
  if (status) status.textContent = `${count} ${count === 1 ? 'agent' : 'agents'} · each in its own workspace`;
}

function scheduleColumns(delay = 300) {
  window.clearTimeout(columnsTimer);
  columnsTimer = window.setTimeout(reloadColumns, delay);
}

/** Reloads every column with the current shared thinking and limits (and the count, which decides whether Remove is enabled). */
async function reloadColumns() {
  window.clearTimeout(columnsTimer);
  const seq = ++columnsSeq;
  const list = columns();
  let fresh;
  try {
    fresh = await Promise.all(list.map((column, index) => columnFragment(SLOTS[index], columnValues(column), list.length)));
  } catch (error) {
    if (seq === columnsSeq) toast('Columns not updated', error.message);
    return;
  }
  if (seq !== columnsSeq) return;
  list.forEach((column, index) => {
    if (!fresh[index] || !column.isConnected) return;
    disposeTooltips(column);
    column.replaceWith(fresh[index]);
  });
  layout();
}

function setPicker(form, provider, model) {
  const select = $('[data-provider-model]', form);
  const providerSelect = select && $('[data-provider-select]', select);
  if (!providerSelect || ![...providerSelect.options].some((option) => option.value === provider)) return;
  if (providerSelect.value !== provider) {
    providerSelect.value = provider;
    providerSelect.dispatchEvent(new Event('change', { bubbles: true }));
  }
  const pane = [...select.querySelectorAll('[data-provider]')].find((element) => element.dataset.provider === provider);
  const modelSelect = pane && $('[data-model-select]', pane);
  if (!modelSelect || ![...modelSelect.options].some((option) => option.value === model)) return;
  if (modelSelect.value !== model) {
    modelSelect.value = model;
    modelSelect.dispatchEvent(new Event('change', { bubbles: true }));
  }
}

function openPicker(target) {
  const element = document.getElementById('modal-model-picker');
  const form = element && $('[data-model-picker]', element);
  if (!form || !window.bootstrap) return;
  pickerTarget = target;
  const list = columns();
  const column = target?.column ?? list[list.length - 1];
  if (column) setPicker(form, column.dataset.provider, column.dataset.model);
  window.bootstrap.Modal.getOrCreateInstance(element).show();
}

async function chooseModel(provider, model) {
  const target = pickerTarget;
  pickerTarget = null;
  const list = columns();
  try {
    if (target?.column && target.column.isConnected) {
      const column = target.column;
      const index = list.indexOf(column);
      // A new model keeps the thinking override; one it does not take is kept aside (data-kept-thinking), not sent.
      const fresh = await columnFragment(SLOTS[index], { provider, model, thinking: columnValues(column).thinking }, list.length);
      if (!fresh || !column.isConnected) return;
      disposeTooltips(column);
      column.replaceWith(fresh);
    } else {
      if (list.length >= maxColumns()) {
        toast('No free column', `Agent compare runs at most ${maxColumns()} models; remove one first.`, 'warn');
        return;
      }
      const fresh = await columnFragment(SLOTS[list.length], { provider, model, thinking: '' }, list.length + 1);
      if (!fresh) return;
      $('[data-ac-add]', grid)?.before(fresh);
      if (list.length === 1) scheduleColumns(0); // the first column can be removed now
    }
  } catch (error) {
    toast('Model not loaded', error.message);
    return;
  }
  layout();
  scheduleProject();
}

function removeColumn(column) {
  if (columns().length <= 1) return;
  disposeTooltips(column);
  column.remove();
  reletter();
  layout();
  scheduleColumns(0);
  scheduleProject();
}

function wirePicker() {
  const form = $('[data-model-picker]');
  form?.addEventListener('submit', (event) => {
    event.preventDefault();
    const choice = providerModelValue($('[data-provider-model]', form));
    hideModal(form);
    if (choice?.provider && choice?.model) chooseModel(choice.provider, choice.model);
  });
  form?.closest('.modal')?.addEventListener('hidden.bs.modal', () => {
    window.setTimeout(() => { pickerTarget = null; }, 0);
  });
}

// ---- limits -------------------------------------------------------------------------------------------------------------------

function updateLimits() {
  const turns = intValue('[data-ac-max-turns]') ?? '?';
  const minutes = intValue('[data-ac-max-minutes]') ?? '?';
  const cap = capText();
  const valid = capValid(cap);
  const capInput = $('[data-ac-cap]', root);
  capInput?.classList.toggle('is-invalid', !valid);
  const summary = $('[data-ac-limits-summary]', root);
  if (summary) summary.textContent = `· ${turns} turns · ${minutes} min · ${cap && valid ? `cap $${cap}` : 'no cap'} · ${APPROVAL_TEXT[approval()] ?? 'Ask'}`;
  const chip = $('[data-ac-cap-chip]', root);
  if (chip) {
    const capped = cap !== '' && valid;
    chip.textContent = capped ? `Cap $${cap}` : 'No run cap';
    chip.setAttribute('aria-label', capped ? `Run cap: $${cap}` : 'No run cap');
    const action = $('[data-ac-cap-action]', root)?.value === 'block' ? 'stops' : 'asks before going over';
    setTooltip(chip, capped
      ? `<strong>Run cap $${escapeHtml(cap)}</strong><br>Shared equally by the metered models; each ${action} its share. Subscription models aren't counted.`
      : '<strong>No run cap</strong><br>Turns and time limit each model. Each turn still reserves its worst case first, and your Spend Guard caps still apply.');
  }
}

/** The System prompt toggle's preview: its first 70 characters, or "none" (as the server renders it). */
function updateSystemPreview() {
  const preview = $('[data-ac-system-preview]', root);
  if (!preview) return;
  const text = systemPrompt();
  preview.textContent = text ? `· ${text.length > 70 ? `${text.slice(0, 70)}…` : text}` : '· none';
}

// ---- run ----------------------------------------------------------------------------------------------------------------------

function showError(message) {
  const alert = $('[data-ac-error]', root);
  if (!alert) return;
  alert.textContent = message ?? '';
  alert.hidden = !message;
}

function createBody() {
  const task = ($('[data-ac-task]', root)?.value ?? '').trim();
  if (!task) throw new Error('Describe the task first.');
  const path = projectPath();
  if (!path) throw new Error('Choose the project folder first.');
  const list = columns();
  if (list.length === 0) throw new Error('Add at least one model.');
  const unknown = list.find((column) => column.dataset.known !== 'true');
  if (unknown) throw new Error(`${unknown.dataset.name || unknown.dataset.model} is not available; choose another model for column ${unknown.dataset.slot.toUpperCase()}.`);
  const cap = capText();
  if (!capValid(cap)) throw new Error('The run cap is an amount in dollars, such as 1.50, or empty for no cap.');
  const timeout = intValue('[data-ac-check-timeout]');
  const check = ($('[data-ac-check]', root)?.value ?? '').trim();
  const body = {
    task,
    systemPrompt: systemPrompt() || null,
    projectPath: path,
    models: list.map((column) => ({ provider: column.dataset.provider, model: column.dataset.model, thinking: column.dataset.effective || null })),
    isolation: root.dataset.isolation || 'automatic',
    extraExcludes: excludes(),
    checkCommand: check || null,
    maxTurns: intValue('[data-ac-max-turns]'),
    maxMinutes: intValue('[data-ac-max-minutes]'),
    capAction: $('[data-ac-cap-action]', root)?.value === 'block' ? 'block' : 'ask',
    approval: approval(),
  };
  if (root.dataset.baseRef) body.baseRef = root.dataset.baseRef;
  if (timeout) body.checkTimeoutSeconds = timeout * 60;
  if (cap) body.capUsd = cap;
  return body;
}

async function create(body) {
  if (blindOn()) return createBlind(body);
  try {
    return await postRequest(API, { ...body, requestId: requestId() });
  } catch (error) {
    if (!(error instanceof RequestError) || error.code !== 'too-large' || body.confirmLargeCopy) throw error;
    const yes = await confirmAction({
      title: 'Copy a large folder?',
      body: error.message,
      detail: 'Each model gets its own copy. You can leave more out under Workspace details instead.',
      ok: 'Copy and run',
      tone: 'primary',
    });
    if (!yes) return null;
    return postRequest(API, { ...body, confirmLargeCopy: true, requestId: requestId() });
  }
}

// A blind round: the same create body, shuffled by the server; a copy over the size warning asks first, as a named run's does.
async function createBlind(body) {
  try {
    return await startAgentRound({ ...body, requestId: requestId() });
  } catch (error) {
    if (!(error instanceof RequestError) || error.code !== 'too-large' || body.confirmLargeCopy) throw error;
    const yes = await confirmAction({
      title: 'Copy a large folder?',
      body: error.message,
      detail: 'Each model gets its own copy. You can leave more out under Workspace details instead.',
      ok: 'Copy and run',
      tone: 'primary',
    });
    if (!yes) return null;
    return startAgentRound({ ...body, confirmLargeCopy: true, requestId: requestId() });
  }
}

async function run(button) {
  showError('');
  let body;
  try {
    body = createBody();
  } catch (error) {
    showError(error.message);
    return;
  }
  await busy(button, async () => {
    try {
      const status = await create(body);
      if (status?.url) return;
      if (status?.id) window.location.assign(`/compare?mode=agent&run=${encodeURIComponent(status.id)}`);
    } catch (error) {
      showError(error.message);
    }
  });
}

// ---- wiring -------------------------------------------------------------------------------------------------------------------

function onClick(event) {
  const target = event.target.closest?.('button, a');
  if (!target) return;
  if (target.matches('[data-ac-ws-apply]')) applyWorkspace(target);
  else if (target.matches('[data-ac-use-copy]')) setIsolation('copy');
  else if (target.matches('[data-ac-dismiss-banner]')) {
    dirtyDismissed = true;
    target.closest('[data-ac-banner]')?.remove();
  } else if (!root.contains(target)) return;
  else if (target.matches('[data-ac-folder]')) setProject(target.dataset.acFolder);
  else if (target.matches('[data-ac-folder-use]')) setProject($('[data-ac-folder-input]', root)?.value);
  else if (target.matches('[data-ac-add-model]')) openPicker({ add: true });
  else if (target.matches('[data-ac-col-model]')) openPicker({ column: target.closest('[data-ac-setup-col]') });
  else if (target.matches('[data-ac-col-remove]')) removeColumn(target.closest('[data-ac-setup-col]'));
  else if (target.matches('[data-ac-run]')) run(target);
}

function onChange(event) {
  const target = event.target;
  onWorkspaceChange(event);
  if (!root.contains(target)) return;
  if (target.matches('[data-ac-override="thinking"]')) {
    const column = target.closest('[data-ac-setup-col]');
    if (column) column.dataset.keptThinking = '';
    scheduleColumns(0);
  } else if (target.matches('[data-ac-thinking]')) scheduleColumns(0);
  else if (target.matches('[data-ac-approval], [data-ac-cap-action]')) updateLimits();
}

function onInput(event) {
  const target = event.target;
  if (!root.contains(target)) return;
  if (target.matches('[data-ac-max-turns], [data-ac-max-minutes]')) {
    updateLimits();
    scheduleColumns();
  } else if (target.matches('[data-ac-cap]')) updateLimits();
  else if (target.matches('[data-ac-task]')) showError('');
  else if (target.matches('[data-ac-system]')) updateSystemPreview();
}

function onKeydown(event) {
  if (event.key !== 'Enter') return;
  if ((event.ctrlKey || event.metaKey) && root.contains(event.target)) {
    event.preventDefault();
    run($('[data-ac-run]', root));
  } else if (event.target.matches?.('[data-ac-folder-input]')) {
    event.preventDefault();
    setProject(event.target.value);
  }
}

export function initAgentSetup(page) {
  root = page;
  grid = $('[data-ac-grid]', root);
  if (!grid) return;
  wirePicker();
  initBlindSwitch((on) => { root.dataset.blind = on ? 'true' : 'false'; });
  document.addEventListener('click', onClick);
  document.addEventListener('change', onChange);
  root.addEventListener('input', onInput);
  document.addEventListener('keydown', onKeydown);
  layout();
  updateLimits();
  loadProject();
}
