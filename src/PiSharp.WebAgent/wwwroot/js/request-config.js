// Decision 0011 (run W4, R1): _RequestConfig's behaviour. Touching an option sets it ([data-set="true"]: it is sent); Clear puts it back
// to its default (not sent). Each row is enabled only when the chosen model takes the option with the chosen thinking level
// (applyCapabilities, from the run engine's RunCapabilities: data-capabilities, or GET /api/playground/model after a model change). A set
// option the model does not take keeps its value and its row says the run will be refused until it is cleared (brief rule 4: never
// dropped silently); "Clear them" clears every such option at once. Rows marked [data-unsent] are never enabled. The modified count and the
// accordions' dots follow what is set. W4 fix 2: Model & auth's account choice ([data-account-choice]) lists the provider's accounts (ids and
// labels from GET /api/playground/model) and keeps the account wanted (config.dataset.account: the user's pick, or one a preset or a run named)
// across a change of provider, selecting it again when the provider lists it; the Source and Account rows follow the choice.
// A tool's strict flag lives in the tool list (request-tools.js): the tools row is marked when a
// strict tool meets a model that takes no strict tools (RunModelOptions' "strict"), and "Clear them" turns strict off rather than
// removing the tools.

// Every option with its own row (RequestConfigView.Counted on the server).
const COUNTED = ['thinking', 'maxTokens', 'temperature', 'topP', 'topK', 'stop', 'seed', 'presencePenalty', 'frequencyPenalty',
  'responseFormat', 'toolChoice', 'serviceTier', 'headers', 'providerOptions', 'parallelToolCalls', 'textVerbosity', 'reasoningSummary',
  'sessionId', 'timeoutMs'];
const SECTIONS = {
  sampling: ['temperature', 'topP', 'topK', 'presencePenalty', 'frequencyPenalty', 'stop', 'seed'],
  output: ['maxTokens', 'responseFormat', 'textVerbosity'],
  reasoning: ['thinking', 'reasoningSummary'],
  cache: ['sessionId'],
  tier: ['serviceTier'],
  transport: ['timeoutMs'],
  headers: ['headers'],
  provider: ['providerOptions'],
};
// The thinking levels a run sends (RunSpec.ThinkingLevels, max last).
const SEND_LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
const REFUSED = 'Set: the run will be refused until you clear it.';

function changed(config) {
  config.dispatchEvent(new CustomEvent('request-change', { bubbles: true }));
}

/** The capabilities the config was last given (RunCapabilities as JSON), or null. */
export function capabilities(config) {
  if (config._capabilities !== undefined) return config._capabilities;
  try { config._capabilities = config.dataset.capabilities ? JSON.parse(config.dataset.capabilities) : null; } catch { config._capabilities = null; }
  return config._capabilities;
}

/** The thinking level the control shows. */
export function thinkingLevel(config) {
  return config.querySelector('[data-opt="thinking"]:checked')?.value ?? 'off';
}

function support(caps, option) {
  return caps?.options?.find((entry) => entry.option === option) ?? null;
}

/** Whether the model takes `option` with the shown thinking level. */
export function takes(config, option, caps = capabilities(config)) {
  const entry = support(caps, option);
  if (!entry) return false;
  return thinkingLevel(config) !== 'off' ? (entry.withThinking ?? entry.supported) : entry.supported;
}

/** Why the model does not take `option` (or how it does), or ''. */
export function optionReason(config, option, caps = capabilities(config)) {
  return why(config, option, caps);
}

function why(config, option, caps) {
  if (!caps) return 'Choose a model PiSharp knows.';
  const entry = support(caps, option);
  if (!entry) return '';
  if (!takes(config, option, caps) && !entry.reason) return thinkingLevel(config) !== 'off' && entry.supported ? 'not while the model thinks' : 'this model does not take it';
  return entry.reason ?? '';
}

function displayValue(row) {
  const input = row.querySelector('[data-opt]');
  const shown = row.querySelector('[data-option-value]');
  if (!shown || !input) return;
  if (input.dataset.kind === 'level') shown.textContent = thinkingLevel(row.closest('[data-request-config]'));
  else if (row.dataset.set !== 'true') shown.textContent = 'default';
  else if (input.dataset.kind === 'radio') shown.textContent = row.querySelector('[data-opt]:checked')?.value ?? 'default';
  else if (input.dataset.kind === 'bool') shown.textContent = input.checked ? 'true' : 'false';
  else shown.textContent = input.value;
}

/** How many of the switched-on tools are strict (their definition, data-tool, says strict: true). */
function strictTools(config) {
  let count = 0;
  config.querySelectorAll('[data-tool-list] [data-tool]').forEach((item) => {
    if (!item.querySelector('[data-tool-on]')?.checked) return;
    try { if (JSON.parse(item.dataset.tool).strict === true) count++; } catch { /* the tool editor writes JSON */ }
  });
  return count;
}

/** A new session id for the sessionId row's "New" button: pg- and 8 hex digits. */
function newSessionId() {
  const bytes = new Uint8Array(4);
  window.crypto.getRandomValues(bytes);
  return 'pg-' + [...bytes].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

function markSet(row, set) {
  row.dataset.set = set ? 'true' : 'false';
  const clear = row.querySelector('[data-option-clear]');
  if (clear) clear.hidden = !set;
  displayValue(row);
}

function resetRow(row) {
  const option = row.dataset.option;
  row.querySelectorAll('[data-opt]').forEach((input) => {
    if (input.type === 'radio') input.checked = input.value === 'off';
    else if (input.type === 'checkbox') input.checked = input.dataset.default === 'true';
    else if (input.tagName === 'SELECT') input.value = '';
    else input.value = input.dataset.default ?? '';
  });
  if (option === 'headers') row.querySelector('[data-header-rows]')?.replaceChildren();
  if (option === 'responseFormat') {
    row.querySelector('[data-format-schema]').hidden = true;
  }
  if (option === 'toolChoice') row.querySelectorAll('[data-choice-note]').forEach((note) => { note.hidden = note.dataset.choiceNote !== 'auto'; });
  markSet(row, false);
}

/** Clears every option back to its default (Reset parameters). */
export function resetOptions(config) {
  config.querySelectorAll('[data-option]').forEach((row) => { if (row.dataset.option !== 'tools') resetRow(row); });
  refresh(config);
  changed(config);
}

/** Recomputes rows (enabled, notes, conflicts), the modified count and the section dots. */
export function refresh(config) {
  const caps = capabilities(config);
  let conflicts = 0;
  config.querySelectorAll('[data-option]').forEach((row) => {
    const option = row.dataset.option;
    const set = row.dataset.set === 'true';
    const ok = option === 'tools' ? true : takes(config, option, caps);
    // The tools row also carries the tools' strict flags: a strict tool on a model that takes none is refused like any option.
    const strict = option === 'tools' ? strictTools(config) : 0;
    const toolsRefused = option === 'tools' && set && !takes(config, 'tools', caps);
    const strictRefused = strict > 0 && !takes(config, 'strict', caps);
    const conflict = option === 'tools' ? toolsRefused || strictRefused
      : set && (option === 'thinking' ? !levelTaken(caps, thinkingLevel(config)) : !ok);
    if (conflict) conflicts++;
    row.classList.toggle('param-disabled', !ok && !set);
    if (conflict) row.dataset.conflict = 'true';
    else delete row.dataset.conflict;
    if (option !== 'thinking' && option !== 'tools') {
      // A set option the model does not take stays editable so it can be cleared or fixed; an unset one is disabled.
      row.querySelectorAll('[data-opt], [data-header-name], [data-header-value], [data-header-add], [data-format-name], [data-format-schema-text], [data-format-strict], [data-choice-tool], [data-session-new]')
        .forEach((input) => { input.disabled = !ok && !set; });
    }
    const clear = row.querySelector('[data-option-clear]');
    if (clear) clear.hidden = !set;
    displayValue(row);
    const note = row.querySelector('[data-option-note]');
    if (note && option === 'tools') {
      note.textContent = toolsRefused ? `${why(config, 'tools', caps)}. ${REFUSED}`
        : strictRefused ? `${strict} strict ${strict === 1 ? 'tool' : 'tools'}: ${why(config, 'strict', caps)}. ${REFUSED}` : '';
      note.classList.toggle('text-ps-warn', conflict);
    } else if (note && option !== 'thinking') {
      const reason = why(config, option, caps);
      note.textContent = conflict ? `${reason ? reason + '. ' : ''}${REFUSED}` : reason || (note.dataset.defaultNote ?? '');
      note.classList.toggle('text-ps-warn', conflict);
    }
    if (option === 'temperature') {
      const alert = row.querySelector('[data-option-alert]');
      if (alert) {
        alert.hidden = ok;
        const reasonText = alert.querySelector('[data-option-reason]');
        if (reasonText) reasonText.textContent = ok ? '' : `doesn't take temperature: ${why(config, option, caps)}.`;
        const ref = alert.querySelector('[data-model-ref]');
        if (ref && caps) ref.textContent = `${caps.provider}/${caps.model}`;
      }
      row.dataset.tempPane = ok ? 'temp-supported' : 'temp-unsupported';
    }
  });
  applyLevels(config, caps);
  applyTiers(config, caps);
  const modified = COUNTED.filter((option) => config.querySelector(`[data-option="${option}"]`)?.dataset.set === 'true').length;
  const chip = config.querySelector('[data-modified-chip]');
  if (chip) {
    chip.hidden = modified === 0;
    chip.querySelector('[data-modified-count]').textContent = String(modified);
  }
  config.querySelectorAll('[data-section]').forEach((section) => {
    const options = SECTIONS[section.dataset.section];
    if (!options) return;
    const count = options.filter((option) => config.querySelector(`[data-option="${option}"]`)?.dataset.set === 'true').length;
    const dot = section.closest('.accordion-item')?.querySelector('[data-section-dot]');
    if (dot) {
      dot.hidden = count === 0;
      dot.title = `${count} modified`;
    }
  });
  const banner = config.querySelector('[data-config-conflicts]');
  if (banner) {
    banner.hidden = conflicts === 0;
    banner.querySelector('[data-config-conflicts-text]').textContent = conflicts === 0 ? ''
      : `${conflicts === 1 ? 'An option you set isn\'t' : `${conflicts} options you set aren't`} taken by this model, so Run will refuse the request until you clear ${conflicts === 1 ? 'it' : 'them'}.`;
  }
  config.dataset.conflicts = String(conflicts);
  return conflicts;
}

/** Disables the service tiers the model's API does not take (capabilities.serviceTiers); the chosen one stays, so it can be changed. */
function applyTiers(config, caps) {
  const select = config.querySelector('[data-opt="serviceTier"]');
  if (!select || !caps) return;
  const tiers = caps.serviceTiers ?? [];
  select.querySelectorAll('option').forEach((option) => {
    if (option.value === '' || option.selected) return;
    option.disabled = tiers.length > 0 && !tiers.includes(option.value);
  });
}

function levelTaken(caps, level) {
  if (level === 'off') return true;
  return SEND_LEVELS.includes(level) && (caps?.thinkingLevels ?? ['off']).includes(level);
}

function applyLevels(config, caps) {
  const row = config.querySelector('[data-option="thinking"]');
  if (!row) return;
  const levels = caps?.thinkingLevels ?? ['off'];
  row.querySelectorAll('[data-opt="thinking"]').forEach((radio) => {
    const ok = levelTaken(caps, radio.value);
    radio.disabled = !ok && !radio.checked;
    const label = row.querySelector(`label[for="${radio.id}"]`);
    if (label) label.title = ok ? '' : !SEND_LEVELS.includes(radio.value) ? 'PiSharp\'s run engine does not send it yet.'
      : levels.length <= 1 ? 'This model does not think.' : `This model thinks at ${levels.slice(1).join(', ')}.`;
  });
  const note = row.querySelector('[data-option-note]');
  const level = thinkingLevel(config);
  const conflict = !levelTaken(caps, level);
  if (note) {
    note.textContent = conflict ? `${caps ? (levels.length <= 1 ? 'This model does not think' : `This model thinks at ${levels.slice(1).join(', ')}`) : 'Choose a model'}; ${level} is set, so the run will be refused until you change it.`
      : levels.length > 1 ? `This model thinks at ${levels.slice(1).join(', ')}. Default: off.` : 'This model does not think.';
    note.classList.toggle('text-ps-warn', conflict);
  }
  displayValue(row);
}

// ---- account ------------------------------------------------------------------------------------------------------------------

function accountParts(config) {
  const choice = config?.querySelector('[data-account-choice]');
  return { choice, select: choice?.querySelector('[data-account-select]') ?? null };
}

// The Source chip and the Account row as the choice stands (only while the choice is offered: otherwise they are the server's).
function showAccount(config) {
  const { choice, select } = accountParts(config);
  if (!choice || !select || choice.hidden) return;
  const option = select.selectedOptions[0] ?? select.options[0];
  const chosen = select.value !== '';
  const label = option?.dataset.label || null;
  const source = config.querySelector('[data-auth-source]');
  if (source) {
    source.textContent = chosen ? (label ? 'The account you chose' : 'Account not on this machine') : (label ? "The provider's active account" : 'No account connected');
    source.dataset.tone = label ? 'ok' : 'warn';
  }
  const row = config.querySelector('[data-auth-account-row]');
  if (row) {
    row.hidden = !label;
    row.querySelector('[data-auth-account]').textContent = label ?? '';
  }
}

/** Selects the account the config wants (config.dataset.account) when its choice lists it, else the provider's active account; the choice
 *  shows once it names an account. */
export function selectAccount(config) {
  const { choice, select } = accountParts(config);
  if (!choice || !select) return;
  const wanted = config.dataset.account ?? '';
  const listed = wanted !== '' && [...select.options].some((option) => option.value === wanted);
  select.value = listed ? wanted : '';
  if (listed) choice.hidden = false;
  showAccount(config);
}

// Lists the provider's accounts ({ accounts, activeLabel, offline } of GET /api/playground/model's auth) and selects the one wanted.
function setAccounts(config, auth) {
  const { choice, select } = accountParts(config);
  if (!choice || !select) return;
  const accounts = auth && !auth.offline ? auth.accounts ?? [] : [];
  const wanted = config.dataset.account ?? '';
  const first = new Option(auth?.activeLabel ? `Active account (${auth.activeLabel})` : 'Active account', '');
  if (auth?.activeLabel) first.dataset.label = auth.activeLabel;
  const options = [first, ...accounts.map((account) => {
    const option = new Option(account.usable ? account.label : `${account.label} · ${account.status}`, account.id);
    option.dataset.label = account.label;
    option.disabled = !account.usable && account.id !== wanted;
    return option;
  })];
  select.replaceChildren(...options);
  choice.hidden = accounts.length < 2;
  selectAccount(config);
}

/** Applies a newly chosen model's capabilities ({ capabilities, auth } of GET /api/playground/model): rows, chips, API, account. */
export function applyModel(config, answer) {
  const caps = answer?.capabilities ?? null;
  config._capabilities = caps;
  config.dataset.api = caps?.api ?? '';
  config.dataset.provider = caps?.provider ?? '';
  config.dataset.model = caps?.model ?? '';
  const api = config.querySelector('[data-model-api]');
  if (api) api.value = caps?.api ?? '';
  const chips = config.querySelector('[data-cap-chips]');
  if (chips) {
    const items = [];
    const chip = (text, kind, icon) => {
      const span = document.createElement('span');
      span.className = 'cap' + (kind ? ' ' + kind : '');
      if (icon) {
        const i = document.createElement('i');
        i.className = 'bi ' + icon;
        span.append(i);
      }
      span.append(text);
      items.push(span);
    };
    const tokens = (value) => (value >= 1_000_000 ? `${+(value / 1_000_000).toFixed(1)}M` : value >= 1000 ? `${Math.round(value / 1000)}K` : String(value));
    if (caps?.thinkingLevels?.length > 1) chip('reasoning', 'cap-reasoning', 'bi-lightbulb');
    if (caps?.images) chip('image', 'cap-image', 'bi-image');
    if (caps?.contextWindow) chip(`${tokens(caps.contextWindow)} ctx`);
    if (caps?.maxTokens) chip(`${tokens(caps.maxTokens)} out`);
    if (caps?.offline) chip('offline', null, 'bi-plug');
    chips.replaceChildren(...items);
  }
  config.querySelector('[data-model-unknown]')?.remove();
  const max = config.querySelector('[data-opt="maxTokens"]');
  if (max) {
    if (caps?.maxTokens) max.max = String(caps.maxTokens);
    else max.removeAttribute('max');
  }
  const auth = answer?.auth;
  const source = config.querySelector('[data-auth-source]');
  if (source) {
    source.textContent = auth?.source ?? 'Choose a model';
    source.dataset.tone = auth?.tone ?? 'warn';
  }
  const accountRow = config.querySelector('[data-auth-account-row]');
  if (accountRow) {
    accountRow.hidden = !auth?.accountLabel;
    accountRow.querySelector('[data-auth-account]').textContent = auth?.accountLabel ?? '';
  }
  const billing = config.querySelector('[data-auth-billing]');
  if (billing) billing.textContent = auth?.billing ?? 'unknown';
  setAccounts(config, auth);
  const manage = config.querySelector('[data-auth-manage]');
  if (manage && caps) manage.href = '/settings/providers/' + encodeURIComponent(caps.provider);
  const paneApi = config.querySelector('[data-pane-api]');
  if (paneApi) paneApi.textContent = `${caps?.api || 'This model'} (active)`;
  refresh(config);
}

function onInput(config, event) {
  const target = event.target;
  const row = target.closest('[data-option]');
  if (!row || target.closest('[data-unsent]')) return;
  if (target.matches('[data-opt="thinking"]')) {
    markSet(row, target.value !== 'off');
    refresh(config);
    changed(config);
    return;
  }
  if (target.matches('[data-opt="responseFormat"]')) {
    row.querySelector('[data-format-schema]').hidden = target.value !== 'json_schema';
  }
  if (target.matches('[data-opt="toolChoice"]')) {
    row.querySelectorAll('[data-choice-note]').forEach((note) => { note.hidden = note.dataset.choiceNote !== (target.value || 'auto'); });
  }
  if (target.matches('[data-tool-on]')) return;
  const empty = row.dataset.option === 'headers'
    ? ![...row.querySelectorAll('[data-header-name]')].some((input) => input.value.trim() !== '')
    : target.matches('[data-opt]') && target.dataset.kind !== 'range' && target.value.trim() === '';
  markSet(row, !empty);
  refresh(config);
  changed(config);
}

/** Wires one config pane. */
export function initConfig(config) {
  if (!config || config.dataset.wired === 'true') return;
  config.dataset.wired = 'true';
  config.addEventListener('input', (event) => onInput(config, event));
  config.addEventListener('change', (event) => {
    if (event.target.matches('[data-account-select]')) {
      config.dataset.account = event.target.value;
      showAccount(config);
      changed(config);
      return;
    }
    if (event.target.matches('[data-provider-pane-select]')) {
      config.querySelectorAll('[data-provider-pane]').forEach((pane) => { pane.hidden = pane.dataset.providerPane !== event.target.value; });
      return;
    }
    // Selects, radios and checkboxes report through change only.
    if (event.target.matches('select[data-opt], [data-opt][type="radio"], [data-opt][type="checkbox"], [data-format-strict], [data-choice-tool]')) onInput(config, event);
  });
  config.addEventListener('click', (event) => {
    const button = event.target.closest('button');
    if (!button) return;
    if (button.matches('[data-option-clear]')) {
      resetRow(button.closest('[data-option]'));
      refresh(config);
      changed(config);
    } else if (button.matches('[data-config-clear-conflicts]')) {
      const caps = capabilities(config);
      config.querySelectorAll('[data-option][data-conflict="true"]').forEach((row) => {
        if (row.dataset.option !== 'tools') resetRow(row);
        else if (takes(config, 'tools', caps)) {
          // Only the strict flags are refused: request-tools.js turns them off and keeps the tools.
          config.dispatchEvent(new CustomEvent('request-clear-strict'));
        } else {
          row.querySelector('[data-tool-list]')?.replaceChildren();
          row.dataset.set = 'false';
        }
      });
      refresh(config);
      changed(config);
    } else if (button.matches('[data-session-new]')) {
      const row = button.closest('[data-option]');
      row.querySelector('[data-opt="sessionId"]').value = newSessionId();
      markSet(row, true);
      refresh(config);
      changed(config);
    } else if (button.matches('[data-header-add]')) {
      const row = button.closest('[data-option]');
      const line = row.querySelector('[data-header-template]').content.firstElementChild.cloneNode(true);
      row.querySelector('[data-header-rows]').append(line);
      line.querySelector('[data-header-name]').focus();
    } else if (button.matches('[data-header-remove]')) {
      const row = button.closest('[data-option]');
      button.closest('[data-header-row]').remove();
      markSet(row, [...row.querySelectorAll('[data-header-name]')].some((input) => input.value.trim() !== ''));
      refresh(config);
      changed(config);
    }
  });
  config.addEventListener('request-options-applied', () => refresh(config));
  if (config.dataset.account === undefined) config.dataset.account = accountParts(config).select?.value ?? '';
  // A tool added, edited, removed or switched (request-tools.js): its strict flag may meet the model differently.
  config.addEventListener('request-tools-changed', () => refresh(config));
  refresh(config);
}

/** Marks the rows of options a run refused ([{ option, reason }] from POST /api/runs) and opens their sections. */
export function markRefused(config, refused) {
  for (const entry of refused ?? []) {
    // A tool's strict flag has no row of its own: the tools row says it.
    const row = config.querySelector(`[data-option="${entry.option === 'strict' ? 'tools' : entry.option}"]`);
    if (!row) continue;
    row.dataset.conflict = 'true';
    const note = row.querySelector('[data-option-note]');
    if (note) {
      note.textContent = `${entry.reason ? entry.reason + '. ' : ''}The run was refused: clear it or choose another model.`;
      note.classList.add('text-ps-warn');
    }
    const collapse = row.closest('.accordion-collapse');
    if (collapse && window.bootstrap && !collapse.classList.contains('show')) window.bootstrap.Collapse.getOrCreateInstance(collapse, { toggle: false }).show();
  }
}
