// Decision 0011 (run W3, CP): Settings › Custom providers' behaviour: the provider editor (#modal-custom-provider: its tabs, the key
// source, headers and models tables, model discovery, per-model compat and thinkingLevelMap, the server's JSON preview and the
// connection test), the row actions (Test, Edit, Duplicate, Delete behind the confirm dialog), Re-check all, Import and Export, and model
// overrides. Everything goes through /api/custom-providers. A key typed into the editor is sent only with a save or a test, and the field
// is cleared when the editor closes; the preview never sends it. Text is always set with textContent or an input's value, never as HTML.
import { showToast, confirmAction, providerModelValue } from './shell.js';
import { postJson, getJson, flashAndReload, showFlash, showError, setText, setShown, plural, fillLogo, busy } from './settings-api.js';

const API = '/api/custom-providers';
const LOGO_ROOT = '/_content/PiSharp.Web.Common/img/providers/';
const LOGOS = { lmstudio: 'lmstudio', ollama: 'ollama' };
const TEMPLATES = {
  lmstudio: { id: 'lmstudio', name: 'LM Studio (local)', baseUrl: 'http://localhost:1234/v1', api: 'openai-completions' },
  ollama: { id: 'ollama', name: 'Ollama', baseUrl: 'http://localhost:11434/v1', api: 'openai-completions' },
  vllm: { id: 'vllm', name: 'vLLM', baseUrl: 'http://localhost:8000/v1', api: 'openai-completions' },
  'openai-compatible': { id: '', name: '', baseUrl: 'https://', api: 'openai-completions' },
};
const LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];

const page = document.querySelector('[data-custom-providers]');
const modalEl = document.getElementById('modal-custom-provider');
const form = modalEl?.querySelector('[data-cp-form]');
const offline = page?.dataset.offline === 'true';

let draft = null;
let scope = '';
let tlmModel = null;
let dirty = false;
let variableTimer = 0;

function $(selector, root = modalEl) {
  return root?.querySelector(selector) ?? null;
}

function field(name) {
  return $(`[data-cp-field="${name}"]`);
}

// ─── Editor: open and fill ─────────────────────────────────────────────

function blankDraft(template) {
  const base = TEMPLATES[template] ?? TEMPLATES['openai-compatible'];
  return {
    originalId: null, id: base.id, name: base.name, baseUrl: base.baseUrl, api: base.api, authHeader: false,
    key: { source: 'none' }, headers: [], compat: null, models: [],
  };
}

async function openEditor(next) {
  // Hide the Advanced pane first, so its fields (still set from the last provider) are not stored into this one.
  setShown($('[data-cp-pane="advanced"]'), false);
  draft = structuredClone(next);
  draft.models = (draft.models ?? []).map((model) => ({ ...model }));
  scope = '';
  tlmModel = null;
  const editing = Boolean(draft.originalId);
  setText($('[data-cp-title]'), editing ? 'Edit custom provider' : 'Add custom provider');
  field('id').value = draft.id ?? '';
  field('name').value = draft.name ?? '';
  field('baseUrl').value = draft.baseUrl ?? '';
  field('api').value = draft.api || 'openai-completions';
  field('authHeader').checked = Boolean(draft.authHeader);
  const source = draft.key?.source === 'stored' || draft.key?.source === 'env' ? draft.key.source : 'none';
  modalEl.querySelectorAll('[data-cp-key-source]').forEach((radio) => { radio.checked = radio.value === source; });
  $('[data-cp-key-input]').value = '';
  $('[data-cp-key-input]').type = 'password';
  $('[data-cp-key-var]').value = draft.key?.variable ?? '';
  setShown($('[data-cp-key-hint]'), Boolean(draft.key?.hint));
  setText($('[data-cp-key-hint-text]'), draft.key?.hint ?? '');
  setShown($('[data-cp-key-problem]'), Boolean(draft.key?.problem));
  setText($('[data-cp-key-problem-text]'), draft.key?.problem ?? '');
  showKeyPane();
  showVariableState(draft.key?.set);
  const headers = $('[data-cp-headers]');
  headers.replaceChildren();
  for (const header of draft.headers ?? []) addHeaderRow(header, false);
  const models = $('[data-cp-models]');
  models.replaceChildren();
  for (const model of draft.models) addModelRow(model, false);
  setShown($('[data-cp-discovered]'), false);
  showEditorError(null);
  showTest('idle');
  refreshHeader();
  refreshCounts();
  showTab('general');
  setDirty(false);
  window.bootstrap?.Modal.getOrCreateInstance(modalEl).show();
}

function refreshHeader() {
  const id = field('id').value.trim();
  const name = field('name').value.trim();
  const api = field('api').value;
  setText($('[data-cp-subtitle]'), [id || 'new provider', name, api].filter(Boolean).join(' · '));
  modalEl.querySelectorAll('[data-cp-id-echo]').forEach((element) => setText(element, id || 'id'));
  const logo = $('[data-cp-logo]');
  if (LOGOS[id]) fillLogo(logo, name || id, LOGO_ROOT + LOGOS[id] + '.svg');
  else {
    logo.replaceChildren();
    const icon = document.createElement('i');
    icon.className = 'bi bi-hdd-network';
    icon.setAttribute('aria-hidden', 'true');
    logo.append(icon);
    logo.setAttribute('aria-label', name || id || 'Custom provider');
  }
}

function showKeyPane() {
  const source = modalEl.querySelector('[data-cp-key-source]:checked')?.value ?? 'none';
  modalEl.querySelectorAll('[data-cp-key-pane]').forEach((pane) => setShown(pane, pane.dataset.cpKeyPane === source));
}

function showVariableState(set) {
  const chip = $('[data-cp-var-state]');
  if (set === true) { setText(chip, 'set'); chip.dataset.tone = 'ok'; }
  else if (set === false) { setText(chip, 'not set'); chip.dataset.tone = 'warn'; }
  else { setText(chip, 'not checked'); delete chip.dataset.tone; }
  setText($('[data-cp-var-echo]'), $('[data-cp-key-var]').value.trim().replace(/^\$/, '') || 'VAR');
}

function checkVariable() {
  window.clearTimeout(variableTimer);
  variableTimer = window.setTimeout(async () => {
    const name = $('[data-cp-key-var]').value.trim().replace(/^\$/, '').replace(/^\{|\}$/g, '');
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) { showVariableState(undefined); return; }
    try { showVariableState((await postJson(`${API}/variable`, { name })).set); } catch { showVariableState(undefined); }
  }, 300);
}

function addHeaderRow(header, isNew) {
  const row = document.getElementById('cp-header-row').content.firstElementChild.cloneNode(true);
  const name = row.querySelector('[data-cp-header-name]');
  const value = row.querySelector('[data-cp-header-value]');
  name.value = header?.name ?? '';
  value.value = header?.value ?? '';
  row.dataset.hidden = header?.hidden && header.value == null ? 'true' : 'false';
  if (row.dataset.hidden === 'true') value.placeholder = 'hidden: the file\'s value is kept unless you type a new one';
  setShown(row.querySelector('[data-cp-new]'), isNew);
  $('[data-cp-headers]').append(row);
  refreshCounts();
  return row;
}

function addModelRow(model, isNew) {
  const row = document.getElementById('cp-model-row').content.firstElementChild.cloneNode(true);
  const set = (name, value) => { row.querySelector(`[data-cp-model-field="${name}"]`).value = value ?? ''; };
  set('id', model?.id);
  set('name', model?.name);
  row.querySelector('[data-cp-model-field="reasoning"]').checked = Boolean(model?.reasoning);
  set('images', model?.images ? 'true' : 'false');
  set('contextWindow', model?.contextWindow ?? 128000);
  set('maxTokens', model?.maxTokens ?? 16384);
  set('costInput', model?.costInput ?? 0);
  set('costOutput', model?.costOutput ?? 0);
  row.cpModel = { compat: model?.compat ?? null, thinkingLevelMap: model?.thinkingLevelMap ?? null };
  setShown(row.querySelector('[data-cp-new]'), isNew);
  $('[data-cp-models]').append(row);
  refreshCounts();
  return row;
}

function modelRows() {
  return [...modalEl.querySelectorAll('[data-cp-models] [data-cp-model]')];
}

function rowId(row) {
  return row.querySelector('[data-cp-model-field="id"]').value.trim();
}

function refreshCounts() {
  if (!modalEl) return;
  const count = modelRows().length;
  setText($('[data-cp-model-count]'), String(count));
  setText($('[data-cp-models-label]'), plural(count, 'model'));
  setShown($('[data-cp-headers-empty]'), modalEl.querySelectorAll('[data-cp-headers] [data-cp-header]').length === 0);
}

function setDirty(value) {
  dirty = value;
  setShown($('[data-cp-dirty]'), dirty);
}

// ─── Tabs, compat and thinking levels ──────────────────────────────────

function showTab(name) {
  if (name !== 'advanced' && !$('[data-cp-pane="advanced"]').hidden) storeAdvanced();
  modalEl.querySelectorAll('[data-cp-tab]').forEach((tab) => {
    const active = tab.dataset.cpTab === name;
    tab.classList.toggle('active', active);
    tab.setAttribute('aria-selected', active ? 'true' : 'false');
  });
  modalEl.querySelectorAll('[data-cp-pane]').forEach((pane) => setShown(pane, pane.dataset.cpPane === name));
  if (name === 'advanced') loadAdvanced();
  if (name === 'preview') preview();
}

function scopeTarget() {
  if (!scope) return draft;
  return modelRows().find((row) => rowId(row) === scope)?.cpModel ?? null;
}

function loadAdvanced() {
  const select = $('[data-cp-compat-scope]');
  const ids = modelRows().map(rowId).filter(Boolean);
  if (scope && !ids.includes(scope)) scope = '';
  select.replaceChildren(new Option('Provider (all models)', ''), ...ids.map((id) => new Option(`${id} only`, id)));
  select.value = scope;
  loadCompat();
  const reasoning = modelRows().filter((row) => row.querySelector('[data-cp-model-field="reasoning"]').checked && rowId(row));
  const tlm = $('[data-cp-tlm-model]');
  tlm.replaceChildren(...modelRows().filter(rowId).map((row) => {
    const option = new Option(rowId(row) + (reasoning.includes(row) ? '' : ' (reasoning off)'), rowId(row));
    option.disabled = !reasoning.includes(row);
    return option;
  }));
  if (!reasoning.some((row) => rowId(row) === tlmModel)) tlmModel = reasoning[0] ? rowId(reasoning[0]) : null;
  tlm.value = tlmModel ?? '';
  tlm.disabled = !tlmModel;
  setShown($('[data-cp-tlm-empty]'), !tlmModel);
  loadLevels();
}

function loadCompat() {
  const compat = scopeTarget()?.compat ?? {};
  modalEl.querySelectorAll('[data-cp-compat]').forEach((select) => {
    const value = compat[select.dataset.cpCompat];
    select.value = value === true ? 'true' : value === false ? 'false' : typeof value === 'string' ? value : '';
  });
}

function storeCompat() {
  const target = scopeTarget();
  if (!target) return;
  const compat = {};
  modalEl.querySelectorAll('[data-cp-compat]').forEach((select) => {
    const name = select.dataset.cpCompat;
    if (select.value === '') compat[name] = null;
    else compat[name] = select.value === 'true' ? true : select.value === 'false' ? false : select.value;
  });
  target.compat = compat;
}

function levelRow(level) {
  return $(`[data-cp-tlm="${level}"]`);
}

function loadLevels() {
  const row = modelRows().find((item) => rowId(item) === tlmModel);
  const map = row?.cpModel.thinkingLevelMap ?? {};
  for (const level of LEVELS) {
    const tr = levelRow(level);
    const mode = tr.querySelector('[data-cp-tlm-mode]');
    const value = tr.querySelector('[data-cp-tlm-value]');
    if (!(level in map)) { mode.value = 'default'; value.value = ''; }
    else if (map[level] === null) { mode.value = 'null'; value.value = ''; }
    else { mode.value = 'map'; value.value = map[level]; }
    mode.disabled = !row;
    value.disabled = !row || mode.value !== 'map';
  }
}

function storeLevels() {
  const row = modelRows().find((item) => rowId(item) === tlmModel);
  if (!row) return;
  const map = {};
  for (const level of LEVELS) {
    const tr = levelRow(level);
    const mode = tr.querySelector('[data-cp-tlm-mode]').value;
    if (mode === 'null') map[level] = null;
    else if (mode === 'map') map[level] = tr.querySelector('[data-cp-tlm-value]').value.trim();
  }
  row.cpModel.thinkingLevelMap = map;
}

function storeAdvanced() {
  storeCompat();
  storeLevels();
}

// ─── Reading the editor ────────────────────────────────────────────────

function number(value) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : null;
}

/** The draft the editor holds now; `withKey` adds a typed key (a save or a test, never the preview). */
function collect(withKey) {
  if (!$('[data-cp-pane="advanced"]').hidden) storeAdvanced();
  const source = modalEl.querySelector('[data-cp-key-source]:checked')?.value ?? 'none';
  const key = { source };
  if (source === 'env') key.variable = $('[data-cp-key-var]').value.trim().replace(/^\$/, '').replace(/^\{|\}$/g, '');
  const typed = $('[data-cp-key-input]').value.trim();
  if (source === 'stored' && withKey && typed) key.apiKey = typed;
  return {
    originalId: draft?.originalId ?? null,
    id: field('id').value.trim(),
    name: field('name').value.trim(),
    baseUrl: field('baseUrl').value.trim(),
    api: field('api').value,
    authHeader: field('authHeader').checked,
    key,
    headers: [...modalEl.querySelectorAll('[data-cp-headers] [data-cp-header]')].map((row) => {
      const value = row.querySelector('[data-cp-header-value]').value;
      const hidden = row.dataset.hidden === 'true' && value === '';
      return { name: row.querySelector('[data-cp-header-name]').value.trim(), value: hidden ? null : value, hidden };
    }),
    compat: draft?.compat ?? null,
    models: modelRows().map((row) => {
      const get = (name) => row.querySelector(`[data-cp-model-field="${name}"]`);
      return {
        id: get('id').value.trim(),
        name: get('name').value.trim(),
        reasoning: get('reasoning').checked,
        images: get('images').value === 'true',
        contextWindow: number(get('contextWindow').value),
        maxTokens: number(get('maxTokens').value),
        costInput: number(get('costInput').value) ?? 0,
        costOutput: number(get('costOutput').value) ?? 0,
        compat: row.cpModel?.compat ?? null,
        thinkingLevelMap: row.cpModel?.thinkingLevelMap ?? null,
      };
    }),
  };
}

function showEditorError(message) {
  setShown($('[data-cp-error]'), Boolean(message));
  setText($('[data-cp-error-text]'), message ?? '');
}

async function preview() {
  const state = $('[data-cp-preview-state]');
  setText(state, 'checking…');
  delete state.dataset.tone;
  const id = field('id').value.trim() || 'id';
  setText($('[data-cp-preview-title]'), `providers.${id}`);
  try {
    const answer = await postJson(`${API}/preview`, collect(false));
    setText($('[data-cp-preview-json]'), answer.json ?? '');
    setText(state, answer.valid ? 'valid' : 'not valid');
    state.dataset.tone = answer.valid ? 'ok' : 'danger';
    setText($('[data-cp-preview-note]'), answer.valid
      ? 'Save merges this entry into models.json; other providers aren\'t touched.'
      : answer.error ?? 'The draft is not valid.');
  } catch (error) {
    setText(state, 'not checked');
    setText($('[data-cp-preview-note]'), error.message);
  }
}

// ─── Test, discover and save ───────────────────────────────────────────

function showTest(state, result) {
  modalEl.querySelectorAll('[data-cp-test-state]').forEach((pane) => setShown(pane, pane.dataset.cpTestState === state));
  if (state === 'running') setText($('[data-cp-test-endpoint]'), `GET ${field('baseUrl').value.trim().replace(/\/+$/, '')}/models`);
  if (state === 'ok') setText($('[data-cp-test-ok-text]'), `${result.latencyMs ?? 0} ms · ${plural(result.models.length, 'model')} listed · ${result.endpoint ?? ''}`);
  if (state === 'fail') {
    setText($('[data-cp-test-fail-title]'), result.supported === false ? 'Not tested.' : 'Not connected.');
    setText($('[data-cp-test-fail-text]'), result.message ?? '');
  }
}

async function runTest() {
  showEditorError(null);
  showTest('running');
  try {
    const result = await postJson(`${API}/test`, collect(true));
    showTest(result.ok ? 'ok' : 'fail', result);
    return result;
  } catch (error) {
    showTest('fail', { message: error.message });
    return null;
  }
}

async function discover(button) {
  await busy(button, async () => {
    const result = await runTest();
    const panel = $('[data-cp-discovered]');
    if (!result) { setShown(panel, false); return; }
    setShown(panel, true);
    const dot = $('[data-cp-discover-dot]');
    dot.className = 'status-dot ' + (result.ok ? 'ok' : 'danger');
    setText($('[data-cp-discover-title]'), result.ok ? `Found ${plural(result.models.length, 'model')}` : 'Could not list models');
    setText($('[data-cp-discover-detail]'), [result.endpoint, result.statusCode, result.latencyMs != null ? `${result.latencyMs} ms` : null].filter(Boolean).join(' · '));
    const existing = new Set(modelRows().map(rowId));
    const embeddings = new Set(result.embeddings ?? []);
    const list = $('[data-cp-discover-list]');
    list.replaceChildren(...(result.models ?? []).map((id, index) => {
      const col = document.createElement('div');
      col.className = 'col cp-discovered-item';
      const check = document.createElement('div');
      check.className = 'form-check';
      const input = document.createElement('input');
      input.className = 'form-check-input';
      input.type = 'checkbox';
      input.id = `cp-dm-${index}`;
      input.value = id;
      input.dataset.cpDiscoveredModel = '';
      const label = document.createElement('label');
      label.className = 'form-check-label fs-7';
      label.htmlFor = input.id;
      const mono = document.createElement('span');
      mono.className = 'ps-mono fs-8';
      mono.textContent = id;
      label.append(mono);
      const note = existing.has(id) ? 'already added' : embeddings.has(id) ? 'embedding · not a chat model' : null;
      if (note) {
        const chip = document.createElement('span');
        chip.className = 'ps-chip ms-1';
        chip.textContent = note;
        label.append(chip);
        input.disabled = true;
        input.checked = existing.has(id);
      }
      check.append(input, label);
      col.append(check);
      return col;
    }));
  });
}

function addDiscovered() {
  const picked = [...modalEl.querySelectorAll('[data-cp-discovered-model]:checked:not(:disabled)')].map((input) => input.value);
  for (const id of picked) addModelRow({ id, name: '', contextWindow: 128000, maxTokens: 16384 }, true);
  if (picked.length) { setDirty(true); showTab('models'); }
}

async function save(button) {
  showEditorError(null);
  await busy(button, async () => {
    try {
      const saved = await postJson(API, collect(true));
      $('[data-cp-key-input]').value = '';
      const id = saved?.provider?.id ?? field('id').value.trim();
      flashAndReload({
        title: 'models.json saved',
        body: offline ? `Validated and saved. With --faux, ${id} is not routed.` : `Validated and saved. ${id} is in the model picker.`,
        tone: 'ok',
      });
    } catch (error) {
      showEditorError(error.message);
    }
  });
}

// ─── Page actions ──────────────────────────────────────────────────────

async function testSaved(button) {
  const id = button.dataset.cpTest;
  await busy(button, async () => {
    try {
      const result = await postJson(`${API}/${encodeURIComponent(id)}/test`);
      flashAndReload(result.ok
        ? { title: 'Reachable', body: `${result.endpoint ?? id} answered in ${result.latencyMs ?? 0} ms and listed ${plural(result.models.length, 'model')}.`, tone: 'ok' }
        : { title: result.supported === false ? 'Not tested' : `${id} is unreachable`, body: result.message ?? '', tone: result.supported === false ? 'info' : 'danger' });
    } catch (error) {
      showError('Test failed', error);
    }
  });
}

async function recheckAll(button) {
  const ids = [...document.querySelectorAll('[data-cp-test]')].map((element) => element.dataset.cpTest);
  await busy(button, async () => {
    let ok = 0;
    for (const id of ids) {
      try { if ((await postJson(`${API}/${encodeURIComponent(id)}/test`)).ok) ok += 1; } catch { /* counted as not reachable */ }
    }
    flashAndReload({ title: 'Checked every provider', body: `${ok} of ${ids.length} reachable.`, tone: ok === ids.length ? 'ok' : 'warn' });
  });
}

async function editSaved(button) {
  await busy(button, async () => {
    try { await openEditor(await getJson(`${API}/${encodeURIComponent(button.dataset.cpEdit)}`)); } catch (error) { showError('Could not open the provider', error); }
  });
}

async function duplicate(button) {
  await busy(button, async () => {
    try {
      const copy = await postJson(`${API}/${encodeURIComponent(button.dataset.cpDuplicate)}/duplicate`);
      flashAndReload({ title: 'Provider duplicated', body: `Created ${copy.provider.id} with the same settings and models. A stored key is not copied.`, tone: 'info' });
    } catch (error) {
      showError('Could not duplicate the provider', error);
    }
  });
}

async function remove(button) {
  const id = button.dataset.cpDelete;
  const row = button.closest('[data-cp-row]');
  const models = Number.parseInt(row?.dataset.cpModels ?? '0', 10);
  const ok = await confirmAction({
    title: `Delete custom provider '${id}'?`,
    body: `The ${id} entry and its ${plural(models, 'model')} will be removed from models.json and the model picker, with its stored key. Chats that used ${id} keep their history but can't continue on its models.`,
    detail: 'Previous file kept as models.json.bak',
    ok: 'Delete provider',
  });
  if (!ok) return;
  try {
    await postJson(`${API}/${encodeURIComponent(id)}/remove`);
    flashAndReload({ title: 'Custom provider deleted', body: `${id} was removed from models.json.`, tone: 'warn' });
  } catch (error) {
    showError('Could not delete the provider', error);
  }
}

async function importFile(input) {
  const file = input.files?.[0];
  input.value = '';
  if (!file) return;
  try {
    const text = await file.text();
    const result = await postJson(`${API}/import`, { text });
    const parts = [`${plural(result.providers, 'provider')} and ${plural(result.overrides, 'override')} imported.`, ...result.warnings];
    flashAndReload({ title: 'models.json imported', body: parts.join(' '), tone: result.warnings.length ? 'warn' : 'ok' });
  } catch (error) {
    showError('Could not import the file', error);
  }
}

async function saveOverride(button) {
  const area = document.querySelector(button.dataset.target);
  await busy(button, async () => {
    try {
      await postJson(`${API}/overrides`, { provider: area.dataset.provider, model: area.dataset.model, json: area.value });
      flashAndReload({ title: 'models.json saved', body: `The override of ${area.dataset.provider}/${area.dataset.model} was saved.`, tone: 'ok' });
    } catch (error) {
      showError('Could not save the override', error);
    }
  });
}

async function removeOverride(button) {
  const row = button.closest('[data-cp-override]');
  await busy(button, async () => {
    try {
      await postJson(`${API}/overrides/remove`, { provider: row.dataset.provider, model: row.dataset.model });
      flashAndReload({ title: 'Override removed', body: 'The model uses its catalog values again.', tone: 'warn' });
    } catch (error) {
      showError('Could not remove the override', error);
    }
  });
}

async function addOverride(button) {
  const root = button.closest('[data-cp-override-add]');
  const choice = providerModelValue(root.querySelector('[data-provider-model]'));
  if (!choice?.provider || !choice?.model) { showToast({ title: 'Choose a model', body: 'Pick the provider and the model to override.', tone: 'warn' }); return; }
  const fieldName = root.querySelector('[data-cp-override-field]').value;
  const value = root.querySelector('[data-cp-override-value]').value;
  await busy(button, async () => {
    try {
      await postJson(`${API}/overrides`, { provider: choice.provider, model: choice.model, field: fieldName, value });
      flashAndReload({ title: 'models.json saved', body: `${choice.provider}/${choice.model}: ${fieldName} overridden.`, tone: 'ok' });
    } catch (error) {
      showError('Could not add the override', error);
    }
  });
}

async function copy(button) {
  const target = document.querySelector(button.dataset.cpCopy);
  try {
    await navigator.clipboard.writeText(target?.textContent ?? '');
    showToast({ title: 'Copied', body: 'The JSON is on the clipboard.', tone: 'ok' });
  } catch {
    showToast({ title: 'Could not copy', body: 'Select the text and copy it instead.', tone: 'warn' });
  }
}

// ─── Wiring ────────────────────────────────────────────────────────────

document.addEventListener('click', async (event) => {
  const target = event.target;
  if (!(target instanceof Element)) return;
  const hit = (selector) => target.closest(selector);
  let button;
  if ((button = hit('[data-cp-add]'))) await openEditor(blankDraft('openai-compatible'));
  else if ((button = hit('[data-cp-template]'))) await openEditor(blankDraft(button.dataset.cpTemplate));
  else if ((button = hit('[data-cp-edit]'))) await editSaved(button);
  else if ((button = hit('[data-cp-test]'))) await testSaved(button);
  else if ((button = hit('[data-cp-recheck]'))) await recheckAll(button);
  else if ((button = hit('[data-cp-duplicate]'))) await duplicate(button);
  else if ((button = hit('[data-cp-delete]'))) await remove(button);
  else if ((button = hit('[data-cp-copy]'))) await copy(button);
  else if ((button = hit('[data-cp-override-save]'))) await saveOverride(button);
  else if ((button = hit('[data-cp-override-remove]'))) await removeOverride(button);
  else if ((button = hit('[data-cp-override-add-button]'))) await addOverride(button);
  else if (hit('[data-cp-export]')) showToast({ title: 'models.json exported', body: 'Your providers and overrides, without stored secrets.', tone: 'ok' });
  if (!modalEl?.contains(target)) return;
  if ((button = hit('[data-cp-tab]'))) showTab(button.dataset.cpTab);
  else if ((button = hit('[data-cp-tab-go]'))) showTab(button.dataset.cpTabGo);
  else if ((button = hit('[data-cp-header-add]'))) { addHeaderRow(null, true).querySelector('input').focus(); setDirty(true); }
  else if ((button = hit('[data-cp-header-remove]'))) { button.closest('[data-cp-header]').remove(); refreshCounts(); setDirty(true); }
  else if ((button = hit('[data-cp-model-add]'))) { addModelRow(null, true).querySelector('input').focus(); setDirty(true); }
  else if ((button = hit('[data-cp-model-remove]'))) { button.closest('[data-cp-model]').remove(); refreshCounts(); setDirty(true); }
  else if ((button = hit('[data-cp-discover]'))) await discover(button);
  else if (hit('[data-cp-discover-add]')) addDiscovered();
  else if ((button = hit('[data-cp-discover-all], [data-cp-discover-none]'))) {
    const all = button.matches('[data-cp-discover-all]');
    modalEl.querySelectorAll('[data-cp-discovered-model]:not(:disabled)').forEach((input) => { input.checked = all; });
  } else if ((button = hit('[data-cp-test-draft]'))) await busy(button, runTest);
  else if ((button = hit('[data-cp-reveal]'))) {
    const input = $('[data-cp-key-input]');
    input.type = input.type === 'password' ? 'text' : 'password';
    button.querySelector('.bi')?.classList.toggle('bi-eye-slash', input.type === 'text');
  }
});

document.addEventListener('change', async (event) => {
  const target = event.target;
  if (!(target instanceof Element)) return;
  if (target.matches('[data-cp-import]')) { await importFile(target); return; }
  if (!modalEl?.contains(target)) return;
  setDirty(true);
  if (target.matches('[data-cp-key-source]')) showKeyPane();
  else if (target.matches('[data-cp-compat-scope]')) { storeCompat(); scope = target.value; loadCompat(); }
  else if (target.matches('[data-cp-tlm-model]')) { storeLevels(); tlmModel = target.value; loadLevels(); }
  else if (target.matches('[data-cp-tlm-mode]')) {
    const value = target.closest('tr').querySelector('[data-cp-tlm-value]');
    value.disabled = target.value !== 'map';
    if (target.value !== 'map') value.value = '';
    storeLevels();
  } else if (target.matches('[data-cp-model-field="reasoning"]')) refreshCounts();
});

modalEl?.addEventListener('input', (event) => {
  setDirty(true);
  if (event.target.matches('[data-cp-field="id"], [data-cp-field="name"]')) refreshHeader();
  if (event.target.matches('[data-cp-key-var]')) { showVariableState(undefined); checkVariable(); }
  if (event.target.matches('[data-cp-tlm-value]')) storeLevels();
});

modalEl?.addEventListener('hidden.bs.modal', () => {
  const input = $('[data-cp-key-input]');
  if (input) { input.value = ''; input.type = 'password'; }
  draft = null;
});

form?.addEventListener('submit', async (event) => {
  event.preventDefault();
  await save($('[data-cp-save]'));
});

if (offline) document.querySelectorAll('[data-cp-test], [data-cp-recheck]').forEach((button) => { button.disabled = true; button.title = 'Offline (--faux): nothing is tested'; });
showFlash();
