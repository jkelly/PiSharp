// Decision 0011 (run W4, R1): the request partials as a run spec, and back. collectSpec(root) reads _RequestConfig ([data-request-config])
// and _MessageListEditor ([data-message-list]) under `root` into the RunSpec POST /api/runs takes; applySpec(root, spec) sets the controls
// from one (Presets load, Compare columns, History's "Open in Playground"). An option row is sent only when the user set it
// ([data-option][data-set="true"]): at its default nothing is sent and the provider's default applies. A set option the model does not
// take is still sent, so the run engine refuses it with a clear message instead of the page dropping it silently (brief rule 4). Options
// the run engine has no field for ([data-unsent]) are never read. W4 fix 2: Model & auth's account choice ([data-account-select]) is the
// RunSpec's account (an id; none for the provider's active account), and applySpec sets it (config.dataset.account is the account wanted,
// which request-config.js selects again once a newly chosen provider's accounts are listed).
// Every option RunSpec takes is read (SENT_OPTIONS, and a tool's strict flag with its definition); options the run engine has no field for
// ([data-unsent]) are never read.

import { editorMessage, setMessages } from './request-messages.js';
import { setTools } from './request-tools.js';
import { selectAccount } from './request-config.js';

export { editorMessage };

/** The options collectSpec sends when their row is set, by RunSpec's names (tools and their strict flags go with the tool list). */
export const SENT_OPTIONS = ['thinking', 'maxTokens', 'temperature', 'topP', 'topK', 'stop', 'seed', 'presencePenalty', 'frequencyPenalty',
  'responseFormat', 'toolChoice', 'serviceTier', 'headers', 'providerOptions', 'parallelToolCalls', 'textVerbosity', 'reasoningSummary',
  'sessionId', 'timeoutMs'];

const KINDS = {
  range: (input) => (input.value === '' ? null : Number(input.value)),
  int: (input) => (input.value.trim() === '' ? null : Number(input.value)),
  lines: (input) => {
    const lines = input.value.split('\n').map((line) => line.replace(/\r$/, '')).filter((line) => line.length > 0);
    return lines.length > 0 ? lines : null;
  },
  // A group of radios (text verbosity): the checked one's value.
  radio: (input) => input.closest('[data-option]')?.querySelector('[data-opt]:checked')?.value ?? null,
  // A select whose first option is "default (not sent)".
  select: (input) => input.value || null,
  text: (input) => input.value.trim() || null,
  // A switch the user touched sends its state, on or off (parallel tool calls).
  bool: (input) => input.checked,
};

/** The config pane and the message list under `root` (a page, a Compare column...). */
export function requestParts(root) {
  const scope = root ?? document;
  return {
    config: scope.matches?.('[data-request-config]') ? scope : scope.querySelector('[data-request-config]'),
    list: scope.matches?.('[data-message-list]') ? scope : scope.querySelector('[data-message-list]'),
  };
}

/** The provider and model the config's Provider → Model dropdowns show. */
export function chosenModel(config) {
  const root = config?.querySelector('[data-provider-model]');
  const provider = root?.querySelector('[data-provider-select]')?.value ?? config?.dataset.provider ?? '';
  const pane = root ? [...root.querySelectorAll('[data-provider]')].find((element) => element.dataset.provider === provider) : null;
  const model = pane?.querySelector('[data-model-select]')?.value ?? config?.dataset.model ?? '';
  return { provider, model };
}

/** The account id Model & auth's account choice names, or null for the provider's active account. */
export function chosenAccount(config) {
  return config?.querySelector('[data-account-select]')?.value || null;
}

function row(config, option) {
  return config?.querySelector(`[data-option="${option}"]`) ?? null;
}

/** Whether the user set `option` (its row is [data-set="true"]). */
export function isSet(config, option) {
  return row(config, option)?.dataset.set === 'true';
}

/** The value `option` sends, or null (unset, or empty). Throws an Error with a message for JSON the user typed that does not parse. */
export function optionValue(config, option) {
  const element = row(config, option);
  if (!element || element.dataset.set !== 'true') return null;
  switch (option) {
    case 'thinking': {
      const checked = element.querySelector('[data-opt="thinking"]:checked');
      return checked && checked.value !== 'off' ? checked.value : null;
    }
    case 'responseFormat': {
      const type = element.querySelector('[data-opt="responseFormat"]').value;
      if (!type) return null;
      if (type !== 'json_schema') return { type };
      const name = element.querySelector('[data-format-name]').value.trim() || 'response';
      const text = element.querySelector('[data-format-schema-text]').value.trim();
      let schema;
      try { schema = JSON.parse(text || '{}'); } catch { throw new Error('responseFormat: the JSON Schema is not valid JSON.'); }
      const format = { type, name, schema };
      if (element.querySelector('[data-format-strict]').checked) format.strict = true;
      return format;
    }
    case 'toolChoice': {
      const kind = element.querySelector('[data-opt="toolChoice"]').value;
      if (!kind) return null;
      return kind === 'tool' ? element.querySelector('[data-choice-tool]')?.value || null : kind;
    }
    case 'serviceTier':
      return element.querySelector('[data-opt="serviceTier"]').value || null;
    case 'headers': {
      const headers = {};
      let count = 0;
      element.querySelectorAll('[data-header-row]').forEach((line) => {
        const name = line.querySelector('[data-header-name]').value.trim();
        if (!name) return;
        headers[name] = line.querySelector('[data-header-value]').value;
        count++;
      });
      return count > 0 ? headers : null;
    }
    case 'providerOptions': {
      const text = element.querySelector('[data-opt="providerOptions"]').value.trim();
      if (!text) return null;
      let value;
      try { value = JSON.parse(text); } catch { throw new Error('providerOptions: not valid JSON.'); }
      if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('providerOptions: a JSON object, such as { "parallel_tool_calls": false }.');
      return Object.keys(value).length > 0 ? value : null;
    }
    default: {
      const input = element.querySelector('[data-opt]');
      const read = KINDS[input?.dataset.kind];
      return read ? read(input) : null;
    }
  }
}

/** The tools of the config's tool list that are switched on: [{ name, description, parameters, strict? }]. */
export function collectTools(config) {
  const tools = [];
  config?.querySelectorAll('[data-tool-list] [data-tool]').forEach((item) => {
    if (!item.querySelector('[data-tool-on]')?.checked) return;
    try {
      const tool = JSON.parse(item.dataset.tool);
      const sent = { name: tool.name, description: tool.description || undefined, parameters: tool.parameters ?? undefined };
      if (tool.strict === true) sent.strict = true;
      tools.push(sent);
    } catch { /* a definition the editor wrote is always JSON */ }
  });
  return tools;
}

/** The request under `root` as a RunSpec (source "playground" unless `source` says otherwise). Throws an Error for JSON that does not parse. */
export function collectSpec(root, { source = 'playground' } = {}) {
  const { config, list } = requestParts(root);
  const { provider, model } = chosenModel(config);
  const spec = { provider, model, source };
  const account = chosenAccount(config);
  if (account) spec.account = account;
  let systemPrompt = null;
  const messages = [];
  list?.querySelectorAll('[data-msg-list] > [data-msg]').forEach((editor) => {
    if (editor.dataset.msgPrompt === 'true') {
      const text = editor.querySelector('[data-msg-text]').value;
      systemPrompt = text.trim() === '' ? null : text;
      return;
    }
    const message = editorMessage(editor);
    if (message) messages.push(message);
  });
  if (systemPrompt !== null) spec.systemPrompt = systemPrompt;
  spec.messages = messages;
  const tools = collectTools(config);
  if (tools.length > 0) spec.tools = tools;
  for (const option of SENT_OPTIONS) {
    const value = optionValue(config, option);
    if (value !== null && value !== undefined) spec[option] = value;
  }
  return spec;
}

function setRow(config, option, set) {
  const element = row(config, option);
  if (element) element.dataset.set = set ? 'true' : 'false';
  return element;
}

/** Sets the config's options from `spec` (an option missing from it goes back to its default). Messages and tools: see applySpec. */
export function applyOptions(config, spec) {
  if (!config) return;
  const value = (name) => spec?.[name] ?? null;
  for (const option of ['temperature', 'topP', 'presencePenalty', 'frequencyPenalty']) {
    const element = setRow(config, option, value(option) !== null);
    const input = element?.querySelector('[data-opt]');
    if (input) input.value = value(option) ?? input.dataset.default ?? '';
  }
  for (const option of ['maxTokens', 'topK', 'seed', 'timeoutMs', 'sessionId', 'reasoningSummary']) {
    const element = setRow(config, option, value(option) !== null);
    const input = element?.querySelector('[data-opt]');
    if (input) input.value = value(option) ?? '';
  }
  const verbosity = setRow(config, 'textVerbosity', value('textVerbosity') !== null);
  verbosity?.querySelectorAll('[data-opt="textVerbosity"]').forEach((radio) => { radio.checked = radio.value === value('textVerbosity'); });
  const parallel = setRow(config, 'parallelToolCalls', typeof spec?.parallelToolCalls === 'boolean');
  const parallelSwitch = parallel?.querySelector('[data-opt="parallelToolCalls"]');
  if (parallelSwitch) parallelSwitch.checked = typeof spec?.parallelToolCalls === 'boolean' ? spec.parallelToolCalls : parallelSwitch.dataset.default === 'true';
  const stop = setRow(config, 'stop', Array.isArray(spec?.stop) && spec.stop.length > 0);
  if (stop) stop.querySelector('[data-opt]').value = (spec?.stop ?? []).join('\n');
  const level = value('thinking') ?? 'off';
  const thinking = setRow(config, 'thinking', level !== 'off');
  thinking?.querySelectorAll('[data-opt="thinking"]').forEach((radio) => { radio.checked = radio.value === level; });
  const format = value('responseFormat');
  const formatRow = setRow(config, 'responseFormat', format !== null);
  if (formatRow) {
    formatRow.querySelector('[data-opt="responseFormat"]').value = format?.type ?? '';
    formatRow.querySelector('[data-format-name]').value = format?.name ?? '';
    formatRow.querySelector('[data-format-schema-text]').value = format?.schema ? JSON.stringify(format.schema, null, 2) : '';
    formatRow.querySelector('[data-format-strict]').checked = format?.strict === true;
  }
  const choice = value('toolChoice');
  const choiceRow = setRow(config, 'toolChoice', choice !== null);
  if (choiceRow) {
    const kind = choice === null ? '' : ['auto', 'none', 'required'].includes(choice) ? choice : 'tool';
    choiceRow.querySelector('[data-opt="toolChoice"]').value = kind;
    const named = choiceRow.querySelector('[data-choice-tool]');
    if (named && kind === 'tool') {
      if (![...named.options].some((option) => option.value === choice)) named.append(new Option(choice, choice));
      named.value = choice;
    }
  }
  const tier = setRow(config, 'serviceTier', value('serviceTier') !== null);
  if (tier) tier.querySelector('[data-opt="serviceTier"]').value = value('serviceTier') ?? '';
  const extra = setRow(config, 'providerOptions', value('providerOptions') !== null);
  if (extra) extra.querySelector('[data-opt="providerOptions"]').value = value('providerOptions') ? JSON.stringify(spec.providerOptions, null, 2) : '';
  const headers = setRow(config, 'headers', value('headers') !== null && Object.keys(spec.headers).length > 0);
  if (headers) {
    const rows = headers.querySelector('[data-header-rows]');
    rows.replaceChildren();
    const template = headers.querySelector('[data-header-template]');
    for (const [name, text] of Object.entries(spec?.headers ?? {})) {
      const line = template.content.firstElementChild.cloneNode(true);
      line.querySelector('[data-header-name]').value = name;
      line.querySelector('[data-header-value]').value = text ?? '';
      rows.append(line);
    }
  }
  config.dispatchEvent(new CustomEvent('request-options-applied', { bubbles: true }));
}

/** Sets the request under `root` from `spec`: model (when the dropdowns list it), options, tools, system prompt and messages. The page's
 *  modules listen for 'request-spec-applied' to refresh what depends on them (capabilities, token counts). */
export function applySpec(root, spec) {
  const { config, list } = requestParts(root);
  if (config) {
    // The account wanted: selected now when the provider's list has it, else once request-config.js lists the new provider's accounts.
    config.dataset.account = spec?.account ?? '';
    selectAccount(config);
  }
  if (config && spec?.provider && spec?.model) {
    const picker = config.querySelector('[data-provider-model]');
    const providerSelect = picker?.querySelector('[data-provider-select]');
    if (providerSelect && [...providerSelect.options].some((option) => option.value === spec.provider)) {
      providerSelect.value = spec.provider;
      providerSelect.dispatchEvent(new Event('change', { bubbles: true }));
      const pane = [...picker.querySelectorAll('[data-provider]')].find((element) => element.dataset.provider === spec.provider);
      const modelSelect = pane?.querySelector('[data-model-select]');
      if (modelSelect && [...modelSelect.options].some((option) => option.value === spec.model)) {
        modelSelect.value = spec.model;
        modelSelect.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
  }
  if (config) setTools(config, spec?.tools ?? []);
  applyOptions(config, spec);
  if (list) setMessages(list, spec?.systemPrompt ?? '', spec?.messages ?? []);
  (root ?? document).dispatchEvent(new CustomEvent('request-spec-applied', { bubbles: true, detail: { spec, config, list } }));
}
