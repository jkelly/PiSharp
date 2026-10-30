// Decision 0011 (run W4, R8): Presets on the Playground, Compare (request mode) and the Tool Bench. The pages render their Save preset
// ([data-preset-save]) and Load preset ([data-preset-load]) buttons disabled; this module turns them on (and any a fragment swap brings in
// later), opens the shared modals (_SavePresetModal, _LoadPresetModal; Ctrl+S saves), and loads a preset with the page's own readers and
// writers:
//   Playground  in place: request-spec.js applySpec (model and its account, options, headers, system prompt, messages, tools with their
//               mock and on/off, execution mode); a model the dropdowns do not list reopens /playground?preset=&provider=&model= so the page
//               lists it.
//   Compare     in place: compare-request.js applyDraft (the shared prompt, system prompt and options, and the columns with their accounts and
//               overrides from /compare/column fragments; a single-model preset replaces the first column and keeps the others). New
//               columns start a fresh comparison: one the page showed stays in History. The URL becomes /compare?preset={id}.
//   Tool Bench  a new session (bench-setup.js openSession) with the preset's setup and user messages; the open session is kept.
// Only the preset's parts change; the page keeps the rest. GET /api/presets/{id}/load converts a preset from another page and names what
// was left out, which the page shows; a preset whose model cannot run here is not loaded (never a silent switch). A load counts as a use
// (POST /api/presets/{id}/used), but not a reload of the page. Nothing here reads or sends a credential.
import { showToast } from './shell.js';
import { getRequest, postRequest } from './request-api.js';
import { collectSpec, applySpec, requestParts, chosenModel } from './request-spec.js';
import { toolItems } from './request-tools.js';
import { readSetup, openSession } from './bench-setup.js';
import { applyDraft, compareDraft } from './compare-request.js';
import { initSaveModal, initLoadModal, openSaveModal, modalOf } from './presets-modals.js';

const page = document.body?.dataset.page;
const STASH = 'pisharp:preset-loaded';
const OPTIONS = ['thinking', 'maxTokens', 'temperature', 'topP', 'topK', 'stop', 'seed', 'presencePenalty', 'frequencyPenalty',
  'responseFormat', 'serviceTier', 'providerOptions'];
let origin = null;

const $ = (selector, root = document) => root.querySelector(selector);

function toast(title, body, tone = 'danger', delay) {
  showToast({ title, body, tone, delay });
}

function stash(value) {
  try { window.sessionStorage.setItem(STASH, JSON.stringify(value)); } catch { /* a page without storage just shows no notes */ }
}

function unstash() {
  try {
    const text = window.sessionStorage.getItem(STASH);
    window.sessionStorage.removeItem(STASH);
    return text ? JSON.parse(text) : null;
  } catch {
    return null;
  }
}

function reloaded() {
  const entry = window.performance?.getEntriesByType?.('navigation')?.[0];
  return entry?.type === 'reload' || entry?.type === 'back_forward';
}

function loadedToast(name, notes = []) {
  const body = [`“${name}” is loaded.`, ...notes].join(' ');
  toast('Preset loaded', body, notes.length > 0 ? 'info' : 'ok', notes.length > 0 ? 12000 : undefined);
}

async function recordUse(id) {
  try { await postRequest(`/api/presets/${encodeURIComponent(id)}/used`, { page }); } catch { /* the count is a convenience */ }
}

// ---- Playground --------------------------------------------------------------------------------------------------------------

const playground = {
  root: () => $('[data-playground]'),

  tools(config) {
    return toolItems(config).map((item) => {
      let definition = {};
      try { definition = JSON.parse(item.dataset.tool); } catch { definition = { name: item.dataset.toolName }; }
      const tool = {
        name: definition.name ?? item.dataset.toolName,
        description: definition.description || undefined,
        parameters: definition.parameters ?? undefined,
        enabled: item.querySelector('[data-tool-on]')?.checked !== false,
      };
      if (item.dataset.mock !== undefined) {
        tool.mock = item.dataset.mock;
        tool.mockError = item.dataset.mockError === 'true';
      }
      return tool;
    });
  },

  capture() {
    const root = playground.root();
    const spec = collectSpec(root);
    delete spec.tools;
    const { config } = requestParts(root);
    const exec = $('[data-exec]:checked', config ?? root)?.value;
    return { spec, tools: playground.tools(config), parallel: exec ? exec === 'parallel' : undefined };
  },

  listed(config, provider, model) {
    const picker = $('[data-provider-model]', config);
    const providers = $('[data-provider-select]', picker ?? config);
    if (!providers || ![...providers.options].some((option) => option.value === provider)) return false;
    const pane = [...(picker?.querySelectorAll('[data-provider]') ?? [])].find((element) => element.dataset.provider === provider);
    const models = $('[data-model-select]', pane ?? picker);
    return !!models && [...models.options].some((option) => option.value === model);
  },

  async apply(answer, { fromStart }) {
    const root = playground.root();
    const { config } = requestParts(root);
    if (!root || !config) return false;
    const parts = answer.parts ?? [];
    const has = (part) => parts.includes(part);
    const source = answer.payload?.spec ?? {};
    if (has('model') && !playground.listed(config, source.provider, source.model)) {
      if (fromStart) {
        toast('Preset not loaded', `The Playground does not list ${source.provider}/${source.model} (it may be hidden in Settings › Models).`, 'warn');
        return false;
      }
      // The page lists the model it opens on, hidden or not: reopen on it, and the preset loads there.
      window.location.assign(`/playground?preset=${encodeURIComponent(answer.id)}&provider=${encodeURIComponent(source.provider)}&model=${encodeURIComponent(source.model)}`);
      return 'navigated';
    }
    let current = {};
    try { current = collectSpec(root); } catch { current = {}; }
    const currentTools = playground.tools(config);
    const spec = { source: 'playground' };
    const chosen = chosenModel(config);
    spec.provider = has('model') ? source.provider : chosen.provider;
    spec.model = has('model') ? source.model : chosen.model;
    // The account goes with the model: the preset's (none: the provider's active account), else the one the page has.
    const account = has('model') ? source.account : current.account;
    if (account) spec.account = account;
    for (const option of OPTIONS) {
      const value = has('parameters') ? source[option] : current[option];
      if (value !== undefined && value !== null) spec[option] = value;
    }
    const headers = has('headers') ? source.headers : current.headers;
    if (headers && Object.keys(headers).length > 0) spec.headers = headers;
    const system = has('systemPrompt') ? source.systemPrompt : current.systemPrompt;
    if (system) spec.systemPrompt = system;
    spec.messages = has('messages') ? (source.messages ?? []) : (current.messages ?? []);
    const tools = has('tools') ? (answer.payload?.tools ?? []) : currentTools;
    const choice = has('tools') ? source.toolChoice : current.toolChoice;
    if (choice) spec.toolChoice = choice;
    spec.tools = tools.map((tool) => {
      const item = { name: tool.name, description: tool.description ?? '', parameters: tool.parameters ?? null };
      if (tool.mock !== undefined && tool.mock !== null) {
        item.mock = tool.mock;
        item.mockError = tool.mockError === true;
      }
      return item;
    });
    applySpec(root, spec);
    for (const item of toolItems(config)) {
      const tool = tools.find((entry) => entry.name === item.dataset.toolName);
      const on = item.querySelector('[data-tool-on]');
      if (tool && on) on.checked = tool.enabled !== false;
    }
    const parallel = has('tools') ? answer.payload?.parallel : undefined;
    if (parallel === true || parallel === false) {
      const radio = $(`[data-exec][value="${parallel ? 'parallel' : 'sequential'}"]`, config);
      if (radio) radio.checked = true;
    }
    config.dispatchEvent(new CustomEvent('request-change', { bubbles: true }));
    const now = chosenModel(config);
    if (now.provider !== spec.provider || now.model !== spec.model) {
      toast('Model not changed', `The Playground kept ${now.provider}/${now.model}: ${spec.provider}/${spec.model} is not in its list.`, 'warn');
    }
    origin = answer.id;
    window.history.replaceState(null, '', `/playground?preset=${encodeURIComponent(answer.id)}`);
    return true;
  },
};

// ---- Compare -----------------------------------------------------------------------------------------------------------------

const compare = {
  root: () => $('[data-compare]'),

  // The page's draft as Run all would send it (the columns with their accounts and overrides).
  capture() {
    if (!compare.root()) throw new Error('There is no comparison on this page.');
    return { draft: compareDraft() };
  },

  async apply(answer) {
    if (!compare.root()) return false;
    // In place (W4 fix 2): compare-request.js sets the preset's parts; with its models, the page starts a fresh comparison.
    const { notes, fresh } = await applyDraft(answer.payload?.draft ?? {}, answer.parts ?? []);
    origin = answer.id;
    answer.notes = [...(answer.notes ?? []), ...notes];
    if (fresh) window.history.replaceState(null, '', `/compare?preset=${encodeURIComponent(answer.id)}`);
    return true;
  },
};

// ---- Tool Bench --------------------------------------------------------------------------------------------------------------

const bench = {
  capture() {
    const current = readSetup();
    if (!current) throw new Error('There is no Tool Bench on this page.');
    return { setup: current.setup, messages: current.messages ?? [] };
  },

  async apply(answer, { fromStart }) {
    const current = readSetup() ?? { setup: {}, messages: [] };
    const parts = answer.parts ?? [];
    const has = (part) => parts.includes(part);
    const source = answer.payload?.setup ?? {};
    const setup = { ...(current.setup ?? {}) };
    if (has('model')) {
      setup.provider = source.provider;
      setup.model = source.model;
      setup.account = source.account ?? null;
    }
    if (has('parameters')) {
      setup.thinking = source.thinking ?? null;
      if (source.rules) setup.rules = source.rules;
    }
    if (has('systemPrompt')) setup.systemPrompt = source.systemPrompt ?? null;
    if (has('tools')) {
      setup.tools = source.tools ?? [];
      setup.toolChoice = source.toolChoice ?? null;
      if (source.parallel === true || source.parallel === false) setup.parallel = source.parallel;
    }
    const messages = has('messages') ? (answer.payload?.messages ?? []) : (current.messages ?? []);
    // The new session's page shows the notes and counts the use (a fetch now could be cut off by the navigation).
    stash({ page, id: answer.id, name: answer.name, notes: answer.notes ?? [], count: !(fromStart && reloaded()) });
    // Back from the new session returns to the bench, not to this preset link.
    const before = window.location.href;
    if (new URLSearchParams(window.location.search).has('preset')) window.history.replaceState(null, '', '/bench');
    try {
      await openSession(setup, messages);
    } catch (error) {
      unstash();
      window.history.replaceState(null, '', before);
      throw error;
    }
    return 'navigated';
  },
};

const adapters = { playground, compare, bench };
const adapter = adapters[page];

// ---- loading and saving ------------------------------------------------------------------------------------------------------

async function load(id, { fromStart = false } = {}) {
  let answer;
  try {
    answer = await getRequest(`/api/presets/${encodeURIComponent(id)}/load?page=${encodeURIComponent(page)}`);
  } catch (error) {
    toast('Preset not loaded', error.message);
    return;
  }
  if (!answer.available) {
    toast('Preset not loaded', [answer.problem, answer.connectLabel ? `${answer.connectLabel} in Settings first.` : ''].filter(Boolean).join(' '), 'warn');
    return;
  }
  let result;
  try {
    result = await adapter.apply(answer, { fromStart });
  } catch (error) {
    toast('Preset not loaded', error.message);
    return;
  }
  if (!result) return;
  // A load counts once: not when the page is reloaded or reopened from history, and not before the page it moves to loads it.
  if (result === 'navigated') return;
  if (!(fromStart && reloaded())) await recordUse(id);
  loadedToast(answer.name, answer.notes ?? []);
}

function openSave() {
  let capture;
  try {
    capture = adapter.capture();
  } catch (error) {
    toast('Preset not saved', error.message, 'warn');
    return;
  }
  openSaveModal({ page, capture, origin });
}

function openLoad() {
  modalOf('modal-load-preset')?.show();
}

// Turns on the buttons the page rendered disabled until Presets were available (and any a fragment brings in later).
function enable(root = document) {
  root.querySelectorAll('[data-preset-save]:not([data-preset-ready]), [data-preset-load]:not([data-preset-ready])').forEach((button) => {
    button.dataset.presetReady = 'true';
    if (button.disabled) button.disabled = false;
    const title = button.getAttribute('title');
    if (title !== null) {
      const clean = title.replace(/\s*·\s*Presets are not available yet\s*$/u, '').replace(/^Presets are not available yet$/u, 'Load a preset');
      button.setAttribute('title', clean);
    }
  });
}

function start() {
  if (!adapter) return;
  initSaveModal({
    context: () => { try { return { page, capture: adapter.capture() }; } catch { return null; } },
    // The next save offers to update the preset just saved.
    onSaved: (preset) => { if (preset?.id) origin = preset.id; },
  });
  initLoadModal({ page, onLoad: (id) => load(id) });
  enable();
  // Only the nodes a swap adds are looked at (streamed output adds many, and most hold no button).
  if (window.MutationObserver) {
    new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        for (const node of mutation.addedNodes) {
          if (node.nodeType !== Node.ELEMENT_NODE) continue;
          if (node.matches('[data-preset-save], [data-preset-load]')) enable(node.parentElement ?? document);
          else if (node.firstElementChild) enable(node);
        }
      }
    }).observe(document.body, { childList: true, subtree: true });
  }
  document.addEventListener('click', (event) => {
    const save = event.target.closest?.('[data-preset-save]');
    const loader = event.target.closest?.('[data-preset-load]');
    if (save && !save.disabled) {
      event.preventDefault();
      openSave();
    } else if (loader && !loader.disabled) {
      event.preventDefault();
      openLoad();
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.defaultPrevented || event.isComposing) return;
    if ((event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 's') {
      event.preventDefault();
      if (!document.querySelector('.modal.show')) openSave();
    }
  });
  const loaded = unstash();
  if (loaded && loaded.page === page) {
    origin = loaded.id ?? null;
    if (loaded.count && loaded.id) recordUse(loaded.id);
    loadedToast(loaded.name ?? 'The preset', loaded.notes ?? []);
  }
  const id = new URLSearchParams(window.location.search).get('preset');
  if (id) load(id, { fromStart: true });
}

start();
