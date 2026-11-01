// Decision 0011 (run W4, R7): the Tool Bench's tools: the tool list (_BenchToolList: select a card for the schema preview, switch a tool on
// or off, the tool choice and the parallel switch) and the shared tool editor (#modal-tool-editor, R1's _ToolEditorModal), which this page
// drives itself and extends with the bench's mock responses, the built-in note, Delete tool and the line under Strict (templates in
// _BenchMocks). Strict (constrainedSampling) is the tool's own switch: the bench enables the shared editor's #tool-strict, saves it as the
// tool's strict flag (sent as strict: true where the model's API takes it; Run refuses it where not) and says, under the switch, what the
// bench's model does with it (the tool list's data-strict-note), in place of the shared editor's own line. A definition is checked here
// (the name, a JSON Schema object), then by POST /api/bench/tools/check (types, enums, patterns, required ⊆ properties, Pi's compile check;
// with Strict on, what OpenAI's strict mode would also ask, as a warning), and again by the server when the setup is saved. A built-in's
// declaration is PiSharp's own and read-only; only Strict and its mocks change. Every change saves the whole setup (bench-setup.js); the
// page re-renders from the answer. Nothing here runs a tool.
import { showToast, confirmAction } from './shell.js';
import { postRequest, copyText } from './request-api.js';
import { checkTool } from './request-tools.js';
import { readSetup, changeSetup } from './bench-setup.js';

const MAX_MOCKS = 50;
let root = null;
let modal = null;
let editing = null;
let checkTimer = 0;
let checkSeq = 0;
let selected = null;

function tools() {
  return readSetup(root)?.setup?.tools ?? [];
}

function builtinNames() {
  try { return JSON.parse(root.dataset.builtins || '[]'); } catch { return []; }
}

function failed(title, error) {
  showToast({ title, body: error?.message ?? String(error ?? ''), tone: 'danger' });
}

// ---- the tool list ------------------------------------------------------------------------------------------------------------

/** Shows `name`'s definition in the schema preview and marks its card. */
export function selectTool(name) {
  const cards = [...root.querySelectorAll('[data-tool-def]')];
  const card = cards.find((item) => item.dataset.toolDef === name);
  if (!card) return;
  selected = name;
  for (const item of cards) {
    const active = item === card;
    item.classList.toggle('active', active);
    if (active) item.setAttribute('aria-current', 'true'); else item.removeAttribute('aria-current');
  }
  const schema = root.querySelector('[data-bench-schema]');
  if (schema) schema.textContent = card.dataset.definition ?? '';
  const label = root.querySelector('[data-bench-schema-name]');
  if (label) label.textContent = name;
  const file = root.querySelector('[data-bench-schema-file]');
  if (file) file.textContent = `${name}.json`;
}

/** Re-applies the selected card after the tool list was swapped. */
export function restoreSelection() {
  if (selected && root.querySelector(`[data-tool-def="${CSS.escape(selected)}"]`)) selectTool(selected);
}

function choicePanes(kind) {
  root.querySelectorAll('[data-choice-pane]').forEach((pane) => { pane.hidden = pane.dataset.choicePane !== kind; });
}

function onChange(event) {
  const target = event.target;
  if (target.matches('[data-bench-enable]')) {
    const name = target.dataset.benchEnable;
    const on = target.checked;
    changeSetup((setup) => {
      const tool = setup.tools?.find((item) => item.name === name);
      if (tool) tool.enabled = on;
      if (!on && setup.toolChoice === name) delete setup.toolChoice;
    }, root).catch((error) => failed('Tool not changed', error));
  } else if (target.matches('[data-bench-choice-kind]')) {
    const kind = target.value;
    choicePanes(kind);
    const name = root.querySelector('[data-bench-choice-tool]')?.value;
    if (kind === 'tool' && !name) return;
    changeSetup((setup) => {
      if (kind === 'auto') delete setup.toolChoice;
      else setup.toolChoice = kind === 'tool' ? name : kind;
    }, root).catch((error) => failed('Tool choice not saved', error));
  } else if (target.matches('[data-bench-choice-tool]')) {
    const name = target.value;
    changeSetup((setup) => { setup.toolChoice = name; }, root).catch((error) => failed('Tool choice not saved', error));
  } else if (target.matches('[data-bench-parallel]')) {
    const on = target.checked;
    changeSetup((setup) => { setup.parallel = on; }, root).catch((error) => failed('Parallel calls not saved', error));
  }
}

function onClick(event) {
  const button = event.target.closest('button, a');
  if (button && root.contains(button)) {
    if (button.matches('[data-bench-new-tool]')) {
      openEditor(null);
      return;
    }
    if (button.matches('[data-bench-template]')) {
      let template = null;
      try { template = JSON.parse(button.dataset.benchTemplate); } catch { template = null; }
      if (template) openEditor(null, template);
      return;
    }
    if (button.matches('[data-bench-edit]')) {
      const tool = tools().find((item) => item.name === button.dataset.benchEdit);
      if (tool) openEditor(tool);
      return;
    }
    if (button.matches('[data-bench-copy]')) {
      const target = root.querySelector(button.dataset.benchCopy);
      copyText(target?.textContent ?? '').then((ok) => showToast({ title: ok ? 'Copied' : 'Copy failed', tone: ok ? 'ok' : 'danger' }));
      return;
    }
    return;
  }
  const card = event.target.closest('[data-tool-def]');
  if (card && !event.target.closest('input, label')) selectTool(card.dataset.toolDef);
}

// ---- the editor -----------------------------------------------------------------------------------------------------------------

function field(name) {
  return modal.querySelector(`[data-tool-field="${name}"]`);
}

function mockList() {
  return modal.querySelector('[data-mock-list]');
}

function strictSwitch() {
  return modal.querySelector('#tool-strict');
}

function strictOn() {
  return strictSwitch()?.checked === true;
}

// The line under Strict: what the bench's model does with it (the tool list's data-strict-note; danger when its API takes no strict tools).
function showStrictNote() {
  const note = modal.querySelector('[data-bench-strict-editor-note]');
  if (!note) return;
  const list = root.querySelector('[data-bench-part="tools"]');
  const taken = list?.dataset.strictTaken !== 'false';
  const text = note.querySelector('[data-bench-strict-text]');
  if (text) text.textContent = list?.dataset.strictNote ?? '';
  note.classList.toggle('text-ps-danger', !taken);
  const icon = note.querySelector('[data-bench-strict-icon]');
  if (icon) icon.className = `bi ${taken ? 'bi-info-circle' : 'bi-exclamation-triangle'} me-1`;
}

function ensureExtras() {
  if (modal.querySelector('[data-bench-mocks]')) return;
  const section = document.getElementById('bench-mock-section')?.content.firstElementChild?.cloneNode(true);
  const error = modal.querySelector('[data-tool-error]');
  if (section && error) error.before(section);
  const note = document.getElementById('bench-builtin-note')?.content.firstElementChild?.cloneNode(true);
  if (note) {
    note.hidden = true;
    modal.querySelector('.modal-body')?.prepend(note);
  }
  const remove = document.getElementById('bench-tool-delete')?.content.firstElementChild?.cloneNode(true);
  if (remove) {
    remove.hidden = true;
    modal.querySelector('.modal-footer')?.prepend(remove);
  }
  // The shared editor's line under Strict is the Playground's; on this page the bench's line says what its model does with Strict.
  const strictBox = strictSwitch()?.closest('.form-check');
  const strictNote = document.getElementById('bench-strict-note')?.content.firstElementChild?.cloneNode(true);
  if (strictBox && strictNote) {
    for (const line of strictBox.parentElement?.querySelectorAll(':scope > .form-text') ?? []) line.hidden = true;
    strictBox.after(strictNote);
  }
}

let mockSeq = 0;

function addMockRow(mock) {
  const list = mockList();
  const template = document.getElementById('bench-mock-row');
  if (!list || !template) return null;
  const row = template.content.firstElementChild.cloneNode(true);
  const n = ++mockSeq;
  row.querySelectorAll('[data-mock-field]').forEach((input) => { input.id = `bench-mock-${n}-${input.dataset.mockField}`; });
  row.querySelectorAll('[data-label-for]').forEach((label) => { label.htmlFor = `bench-mock-${n}-${label.dataset.labelFor}`; });
  const get = (name) => row.querySelector(`[data-mock-field="${name}"]`);
  get('name').value = mock?.name ?? '';
  get('match').value = mock?.match && Object.keys(mock.match).length > 0 ? JSON.stringify(mock.match) : '';
  get('isError').checked = mock?.isError === true;
  get('text').value = mock?.text ?? '';
  list.append(row);
  countMocks();
  return row;
}

function countMocks() {
  const count = mockList()?.children.length ?? 0;
  const chip = modal.querySelector('[data-mock-count]');
  if (chip) chip.textContent = String(count);
  const empty = modal.querySelector('[data-mock-empty]');
  if (empty) empty.hidden = count > 0;
  const add = modal.querySelector('[data-mock-add]');
  if (add) add.disabled = count >= MAX_MOCKS;
}

// The mocks of the form: { mocks } or { error }.
function readMocks() {
  const mocks = [];
  let index = 0;
  for (const row of mockList()?.children ?? []) {
    index++;
    const get = (name) => row.querySelector(`[data-mock-field="${name}"]`);
    const name = get('name').value.trim() || `Mock ${index}`;
    const matchText = get('match').value.trim();
    let match;
    if (matchText) {
      try { match = JSON.parse(matchText); } catch (error) { return { error: `Mock ${index}: the match is not valid JSON (${error.message}).` }; }
      if (!match || typeof match !== 'object' || Array.isArray(match)) return { error: `Mock ${index}: the match is a JSON object of argument values.` };
    }
    const mock = { name, text: get('text').value, isError: get('isError').checked };
    if (match && Object.keys(match).length > 0) mock.match = match;
    mocks.push(mock);
  }
  return { mocks };
}

function setReadOnly(readOnly) {
  for (const name of ['name', 'description', 'parameters']) {
    const input = field(name);
    if (input) input.readOnly = readOnly;
  }
  const note = modal.querySelector('[data-bench-builtin-note]');
  if (note) note.hidden = !readOnly;
}

function fill(tool, builtin) {
  field('name').value = tool?.name ?? '';
  field('description').value = tool?.description ?? '';
  field('parameters').value = JSON.stringify(tool?.parameters ?? { type: 'object', properties: {} }, null, 2);
  const strict = strictSwitch();
  if (strict) strict.checked = tool?.strict === true;
  editing.builtin = builtin === true;
  setReadOnly(editing.builtin);
  modal.querySelector('[data-tool-error]').hidden = true;
  check();
}

function showCheck(kind, text, errors = []) {
  const box = modal.querySelector('[data-tool-check]');
  box.replaceChildren();
  const tones = {
    ok: ['bg-success-subtle text-success-emphasis', 'bi-check-circle'],
    wait: ['bg-body-tertiary text-secondary', 'bi-hourglass-split'],
    warn: ['bg-warning-subtle text-warning-emphasis', 'bi-exclamation-circle'],
    error: ['bg-danger-subtle text-danger-emphasis', 'bi-exclamation-triangle'],
  };
  const [tone, glyph] = tones[kind] ?? tones.error;
  box.className = 'd-flex flex-wrap align-items-center gap-2 px-3 py-2 mt-2 rounded fs-8 ps-mono ' + tone;
  const icon = document.createElement('i');
  icon.className = 'bi ' + glyph;
  const span = document.createElement('span');
  span.textContent = text;
  box.append(icon, span);
  if (errors.length > 0) {
    const list = document.createElement('ul');
    list.className = 'w-100 mb-0 ps-3';
    for (const error of errors.slice(0, 8)) {
      const item = document.createElement('li');
      item.textContent = error;
      list.append(item);
    }
    if (errors.length > 8) {
      const more = document.createElement('li');
      more.textContent = `and ${errors.length - 8} more`;
      list.append(more);
    }
    box.append(list);
  }
}

// The local check now, and the server's after a pause. Returns the local result.
function check() {
  const result = checkTool(field('name').value, field('description').value, field('parameters').value);
  window.clearTimeout(checkTimer);
  const seq = ++checkSeq;
  if (result.error) {
    showCheck('error', result.error);
    return result;
  }
  const summary = `${result.properties} ${result.properties === 1 ? 'property' : 'properties'} · ${result.required} required`;
  const builtin = editing?.builtin === true;
  const strict = strictOn();
  if (builtin && !strict) {
    showCheck('ok', `PiSharp's own declaration · ${summary}`);
    return result;
  }
  showCheck('wait', `Valid JSON · ${summary} · checking the schema…`);
  checkTimer = window.setTimeout(async () => {
    try {
      const answer = await postRequest('/api/bench/tools/check', { tool: { ...result.tool, strict } });
      if (seq !== checkSeq) return;
      const valid = `${builtin ? "PiSharp's own declaration" : 'Valid JSON Schema'} · ${answer.summary ?? summary}`;
      const problems = strict ? answer.strictProblems ?? [] : [];
      if (!answer.valid) showCheck('error', 'The schema has problems:', answer.errors ?? []);
      else if (problems.length > 0) showCheck('warn', `${valid} · OpenAI's strict mode would refuse it:`, problems);
      else showCheck('ok', strict ? `${valid} · fits strict mode` : valid);
    } catch (error) {
      if (seq === checkSeq) showCheck('error', error.message);
    }
  }, 300);
  return result;
}

/** Opens the editor: a new tool (from `template` when given) or `tool` to edit. */
export function openEditor(tool, template = null) {
  if (!modal || !window.bootstrap) return;
  ensureExtras();
  editing = { original: tool?.name ?? null, enabled: tool?.enabled !== false, builtin: false };
  modal.querySelectorAll('[data-template]').forEach((radio) => {
    radio.checked = false;
    radio.disabled = tool?.builtin === true;
  });
  const title = modal.querySelector('#modal-tool-editor-title');
  if (title) title.textContent = tool ? (tool.builtin ? `Built-in tool · ${tool.name}` : 'Edit tool') : 'New tool';
  const submit = modal.querySelector('[data-tool-submit-text]');
  if (submit) submit.textContent = tool ? 'Save tool' : 'Add tool';
  const icon = modal.querySelector('[data-tool-submit] .bi');
  if (icon) icon.className = `bi ${tool ? 'bi-check2' : 'bi-plus-lg'} me-1`;
  const remove = modal.querySelector('[data-bench-delete-tool]');
  if (remove) remove.hidden = !tool;
  mockList()?.replaceChildren();
  for (const mock of tool?.mocks ?? []) addMockRow(mock);
  countMocks();
  const strict = strictSwitch();
  if (strict) strict.disabled = false;
  showStrictNote();
  const source = tool ?? template;
  fill(source, tool ? tool.builtin === true : template?.builtin === true);
  window.bootstrap.Modal.getOrCreateInstance(modal).show();
}

async function save(event) {
  event.preventDefault();
  if (!editing) return;
  const error = modal.querySelector('[data-tool-error]');
  const fail = (text) => {
    error.textContent = text;
    error.hidden = false;
  };
  const result = check();
  if (result.error) return fail(result.error);
  const mocks = readMocks();
  if (mocks.error) return fail(mocks.error);
  const name = result.tool.name;
  const { original, enabled, builtin } = editing;
  if (tools().some((item) => item.name === name && item.name !== original)) return fail(`There is already a tool named ${name}.`);
  const tool = { name, description: result.tool.description, parameters: result.tool.parameters, enabled, builtin, strict: strictOn(), mocks: mocks.mocks };
  const submit = modal.querySelector('[data-tool-submit]');
  submit.disabled = true;
  try {
    await changeSetup((setup) => {
      setup.tools ??= [];
      const index = original ? setup.tools.findIndex((item) => item.name === original) : -1;
      if (index >= 0) setup.tools[index] = tool; else setup.tools.push(tool);
      if (original && original !== name && setup.toolChoice === original) setup.toolChoice = name;
    }, root);
    selected = name;
    restoreSelection();
    window.bootstrap.Modal.getOrCreateInstance(modal).hide();
    showToast({ title: original ? 'Tool saved' : 'Tool added', body: name, tone: 'ok' });
  } catch (problem) {
    fail(problem.message);
  } finally {
    submit.disabled = false;
  }
}

async function removeTool() {
  const name = editing?.original;
  if (!name) return;
  const sure = await confirmAction({
    title: `Delete ${name}?`,
    body: 'The tool and its mock responses leave the bench. Calls already in the conversation stay.',
    ok: 'Delete tool',
  });
  if (!sure) return;
  try {
    await changeSetup((setup) => {
      setup.tools = (setup.tools ?? []).filter((item) => item.name !== name);
      if (setup.toolChoice === name) delete setup.toolChoice;
    }, root);
    window.bootstrap?.Modal.getOrCreateInstance(modal).hide();
    showToast({ title: 'Tool deleted', body: name, tone: 'ok' });
  } catch (error) {
    failed('Tool not deleted', error);
  }
}

function wireEditor() {
  if (!modal || modal.dataset.benchWired === 'true') return;
  modal.dataset.benchWired = 'true';
  const form = modal.querySelector('[data-tool-editor]');
  modal.addEventListener('change', (event) => {
    const template = event.target.closest('[data-template]');
    if (template && editing) {
      try {
        const tool = JSON.parse(template.dataset.template);
        const id = template.id.startsWith('tpl-') ? template.id.slice(4) : '';
        fill(tool, builtinNames().includes(id) && builtinNames().includes(tool.name));
      } catch { /* the server writes the templates as JSON */ }
      return;
    }
    if (event.target.closest('[data-tool-field]') || event.target === strictSwitch()) check();
  });
  modal.addEventListener('input', (event) => {
    if (event.target.closest('[data-tool-field]')) check();
  });
  modal.addEventListener('click', (event) => {
    const button = event.target.closest('button');
    if (!button) return;
    if (button.matches('[data-mock-add]')) {
      addMockRow(null)?.querySelector('[data-mock-field="name"]')?.focus();
    } else if (button.matches('[data-mock-remove]')) {
      button.closest('[data-mock]')?.remove();
      countMocks();
    } else if (button.matches('[data-bench-delete-tool]')) {
      removeTool();
    }
  });
  form?.addEventListener('submit', save);
  modal.addEventListener('hidden.bs.modal', () => {
    editing = null;
    window.clearTimeout(checkTimer);
  });
}

/** Wires the tool list and the tool editor of the bench `page`. */
export function initBenchTools(page) {
  root = page;
  modal = document.getElementById('modal-tool-editor');
  if (!root) return;
  root.addEventListener('click', onClick);
  root.addEventListener('change', onChange);
  wireEditor();
  const active = root.querySelector('[data-tool-def].active');
  if (active) selected = active.dataset.toolDef;
}
