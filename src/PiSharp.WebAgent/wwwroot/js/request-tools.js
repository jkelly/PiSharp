// Decision 0011 (run W4, R1): the request's tools: _RequestConfig's tool list ([data-tool-list], one [data-tool] per definition, switched
// on or off) and _ToolEditorModal (#modal-tool-editor) that adds or edits one. A definition is checked here before it is added (a name of
// letters, digits, _ and -, up to 64; a JSON Schema object) and again by the run engine. A tool's mock response and isError stay in the
// page (data-mock, data-mock-error) and answer the model's calls ("Add toolResult & continue"); nothing runs and nothing is sent for them.
// The tool choice's names and the tool-call name suggestions follow the list.

const NAME = /^[a-zA-Z0-9_-]{1,64}$/;
let editing = null;

function changed(element) {
  element.dispatchEvent(new CustomEvent('request-change', { bubbles: true }));
}

/** The tool list items of `config`. */
export function toolItems(config) {
  return [...(config?.querySelectorAll('[data-tool-list] [data-tool]') ?? [])];
}

function definition(item) {
  try { return JSON.parse(item.dataset.tool); } catch { return { name: item.dataset.toolName }; }
}

/** The mock responses of `config`'s tools: Map name → { text, isError }. */
export function toolMocks(config) {
  const mocks = new Map();
  for (const item of toolItems(config)) {
    if (item.dataset.mock !== undefined) mocks.set(item.dataset.toolName, { text: item.dataset.mock, isError: item.dataset.mockError === 'true' });
  }
  return mocks;
}

/** Checks a definition's fields; returns { tool, error, properties, required }. */
export function checkTool(name, description, parametersText) {
  const trimmed = name.trim();
  if (!NAME.test(trimmed)) return { error: 'The name is letters, digits, _ and -, up to 64 characters.' };
  let parameters;
  try { parameters = parametersText.trim() ? JSON.parse(parametersText) : { type: 'object', properties: {} }; } catch (error) {
    return { error: 'The parameters are not valid JSON: ' + error.message };
  }
  if (!parameters || typeof parameters !== 'object' || Array.isArray(parameters)) return { error: 'The parameters are a JSON Schema object.' };
  if (parameters.type !== undefined && parameters.type !== 'object') return { error: 'The parameters\' schema has type "object".' };
  const properties = parameters.properties && typeof parameters.properties === 'object' ? Object.keys(parameters.properties).length : 0;
  const required = Array.isArray(parameters.required) ? parameters.required.length : 0;
  return { tool: { name: trimmed, description: description.trim(), parameters }, properties, required };
}

function refreshNames(config) {
  const names = toolItems(config).map((item) => item.dataset.toolName);
  const choice = config.querySelector('[data-choice-tool]');
  if (choice) {
    const current = choice.value;
    choice.replaceChildren(...names.map((name) => new Option(name, name)));
    if (names.includes(current)) choice.value = current;
  }
  const prefix = config.dataset.idPrefix || 'pg';
  const list = document.getElementById(`${prefix}-tool-names`);
  if (list) list.replaceChildren(...names.map((name) => new Option(name)));
  const count = config.querySelector('[data-tools-count]');
  const on = toolItems(config).filter((item) => item.querySelector('[data-tool-on]')?.checked).length;
  if (count) count.textContent = String(names.length);
  const summary = config.querySelector('[data-tools-summary]');
  if (summary) summary.textContent = names.length === 0 ? 'none' : `${on} sent`;
  const empty = config.querySelector('[data-tools-empty]');
  if (empty) empty.hidden = names.length > 0;
  const row = config.querySelector('[data-option="tools"]');
  if (row) row.dataset.set = on > 0 ? 'true' : 'false';
}

function iconButton(attribute, icon, label) {
  const button = document.createElement('button');
  button.className = 'btn btn-icon py-0';
  button.type = 'button';
  button.title = label;
  button.setAttribute('aria-label', label);
  button.setAttribute(attribute, '');
  const i = document.createElement('i');
  i.className = `bi ${icon} fs-8`;
  button.append(i);
  return button;
}

function makeItem(config, tool) {
  const prefix = config.dataset.idPrefix || 'pg';
  const item = document.createElement('li');
  item.className = 'd-flex align-items-center gap-2 py-1';
  const id = `${prefix}-tool-${tool.name.replace(/[^a-zA-Z0-9_-]/g, '-')}-${Math.random().toString(36).slice(2, 7)}`;
  const check = document.createElement('div');
  check.className = 'form-check form-switch mb-0 min-w-0';
  const input = document.createElement('input');
  input.className = 'form-check-input';
  input.type = 'checkbox';
  input.setAttribute('role', 'switch');
  input.id = id;
  input.checked = true;
  input.dataset.toolOn = '';
  const label = document.createElement('label');
  label.className = 'form-check-label ps-mono fs-8 text-truncate';
  label.htmlFor = id;
  check.append(input, label);
  const props = document.createElement('span');
  props.className = 'fs-8 text-ps-faint text-truncate';
  props.dataset.toolProps = '';
  const edit = iconButton('data-tool-edit', 'bi-pencil', 'Edit tool');
  edit.classList.add('ms-auto');
  item.append(check, props, edit, iconButton('data-tool-remove', 'bi-x', 'Remove tool'));
  fillItem(item, tool);
  return item;
}

function fillItem(item, tool) {
  item.dataset.tool = JSON.stringify({ name: tool.name, description: tool.description ?? '', parameters: tool.parameters ?? null });
  item.dataset.toolName = tool.name;
  item.querySelector('label').textContent = tool.name;
  const props = item.querySelector('[data-tool-props]');
  if (props) props.textContent = Object.keys(tool.parameters?.properties ?? {}).join(', ');
  item.querySelector('[data-tool-edit]')?.setAttribute('aria-label', `Edit ${tool.name}`);
  item.querySelector('[data-tool-remove]')?.setAttribute('aria-label', `Remove ${tool.name}`);
  if (tool.mock === undefined || tool.mock === null) {
    delete item.dataset.mock;
    delete item.dataset.mockError;
  } else {
    item.dataset.mock = tool.mock;
    item.dataset.mockError = tool.mockError ? 'true' : 'false';
  }
}

/** Adds or replaces (by name) a tool in `config`'s list: { name, description, parameters, mock?, mockError? }. */
export function upsertTool(config, tool, replacing = null) {
  const list = config.querySelector('[data-tool-list]');
  const existing = replacing ?? toolItems(config).find((item) => item.dataset.toolName === tool.name);
  if (existing) fillItem(existing, tool);
  else list.append(makeItem(config, tool));
  refreshNames(config);
  changed(config);
}

/** Replaces `config`'s tools with `tools` ([{ name, description, parameters }]). */
export function setTools(config, tools) {
  config.querySelector('[data-tool-list]')?.replaceChildren();
  for (const tool of tools ?? []) config.querySelector('[data-tool-list]').append(makeItem(config, tool));
  refreshNames(config);
}

function editorFields(modal) {
  const field = (name) => modal.querySelector(`[data-tool-field="${name}"]`);
  return {
    name: field('name'), description: field('description'), parameters: field('parameters'),
    mockOn: field('mockOn'), mock: field('mock'), mockError: field('mockError'),
  };
}

function showCheck(modal) {
  const fields = editorFields(modal);
  const box = modal.querySelector('[data-tool-check]');
  const result = checkTool(fields.name.value, fields.description.value, fields.parameters.value);
  box.replaceChildren();
  const icon = document.createElement('i');
  const text = document.createElement('span');
  if (result.error) {
    box.className = 'd-flex flex-wrap align-items-center gap-2 px-3 py-2 mt-2 rounded fs-8 ps-mono bg-danger-subtle text-danger-emphasis';
    icon.className = 'bi bi-exclamation-triangle';
    text.textContent = result.error;
  } else {
    box.className = 'd-flex flex-wrap align-items-center gap-2 px-3 py-2 mt-2 rounded fs-8 ps-mono bg-success-subtle text-success-emphasis';
    icon.className = 'bi bi-check-circle';
    text.textContent = `Valid JSON Schema · ${result.properties} ${result.properties === 1 ? 'property' : 'properties'} · ${result.required} required`;
  }
  box.append(icon, text);
  if (fields.mock) fields.mock.disabled = !fields.mockOn?.checked;
  if (fields.mockError) fields.mockError.disabled = !fields.mockOn?.checked;
  return result;
}

function fillEditor(modal, tool) {
  const fields = editorFields(modal);
  fields.name.value = tool?.name ?? '';
  fields.description.value = tool?.description ?? '';
  fields.parameters.value = JSON.stringify(tool?.parameters ?? { type: 'object', properties: {} }, null, 2);
  if (fields.mockOn) fields.mockOn.checked = tool?.mock !== undefined && tool?.mock !== null;
  if (fields.mock) fields.mock.value = tool?.mock ?? '';
  if (fields.mockError) fields.mockError.checked = tool?.mockError === true;
  modal.querySelector('[data-tool-error]').hidden = true;
  showCheck(modal);
}

/** Opens the tool editor for a new tool of `config`, or to edit `item`. */
export function openToolEditor(config, item = null) {
  const modal = document.getElementById('modal-tool-editor');
  if (!modal || !window.bootstrap) return;
  editing = { config, item };
  modal.querySelectorAll('[data-template]').forEach((radio) => { radio.checked = false; });
  const submit = modal.querySelector('[data-tool-submit-text]');
  if (submit) {
    submit.dataset.addText ??= submit.textContent;
    submit.textContent = item ? 'Save tool' : submit.dataset.addText;
  }
  if (item) {
    const tool = definition(item);
    fillEditor(modal, { ...tool, mock: item.dataset.mock, mockError: item.dataset.mockError === 'true' });
  } else {
    fillEditor(modal, null);
  }
  window.bootstrap.Modal.getOrCreateInstance(modal).show();
}

function wireEditor(modal) {
  if (modal.dataset.wired === 'true') return;
  modal.dataset.wired = 'true';
  const form = modal.querySelector('[data-tool-editor]');
  modal.addEventListener('change', (event) => {
    const template = event.target.closest('[data-template]');
    if (template) {
      try {
        const tool = JSON.parse(template.dataset.template);
        fillEditor(modal, { name: tool.name, description: tool.description, parameters: tool.parameters, mock: tool.mock ?? undefined });
      } catch { /* the server writes the templates as JSON */ }
      return;
    }
    showCheck(modal);
  });
  modal.addEventListener('input', () => showCheck(modal));
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    const result = showCheck(modal);
    const error = modal.querySelector('[data-tool-error]');
    if (result.error || !editing) {
      error.textContent = result.error ?? 'Open the editor from a request.';
      error.hidden = false;
      return;
    }
    const { config, item } = editing;
    const clash = toolItems(config).find((other) => other.dataset.toolName === result.tool.name && other !== item);
    if (clash) {
      error.textContent = `There is already a tool named ${result.tool.name}.`;
      error.hidden = false;
      return;
    }
    const fields = editorFields(modal);
    const tool = { ...result.tool };
    if (fields.mockOn?.checked) {
      tool.mock = fields.mock.value;
      tool.mockError = fields.mockError.checked;
    }
    upsertTool(config, tool, item);
    window.bootstrap.Modal.getOrCreateInstance(modal).hide();
  });
}

/** Wires `config`'s tool list and the page's tool editor. */
export function initTools(config) {
  if (!config || config.dataset.toolsWired === 'true') return;
  config.dataset.toolsWired = 'true';
  const modal = document.getElementById('modal-tool-editor');
  if (modal) wireEditor(modal);
  config.addEventListener('click', (event) => {
    const button = event.target.closest('button');
    if (!button || !config.contains(button)) return;
    if (button.matches('[data-tool-add]')) openToolEditor(config);
    else if (button.matches('[data-tool-edit]')) openToolEditor(config, button.closest('[data-tool]'));
    else if (button.matches('[data-tool-remove]')) {
      button.closest('[data-tool]').remove();
      refreshNames(config);
      changed(config);
    }
  });
  config.addEventListener('change', (event) => {
    if (event.target.matches('[data-tool-on]')) {
      refreshNames(config);
      changed(config);
    }
  });
  refreshNames(config);
}
