// Decision 0011 (run W3, SU): the setup wizard (Views/Setup). It switches the steps in the page (Welcome → Connect → Default model → Done)
// and keeps ?step= in the address, so the reload after a key is saved or a sign-in ends comes back to the same step. Connecting uses the
// Settings area's own modules: settings-keys.js runs #modal-api-key (opened for the provider chosen in the list) and settings-signin.js runs
// #modal-chatgpt-signin, both on /api/credentials. The inline "Paste an API key" field is read once, POSTed once to /api/credentials/accounts
// and cleared; no key is kept anywhere else, and the answer's masked hint is all the page shows. The thinking levels follow the chosen model
// (unsupported ones are disabled and the choice is clamped as Pi clamps it). Skip setup POSTs /api/setup/skip; Save and continue POSTs
// /api/setup/finish (the default model, through the Defaults section) and shows Done. Text is always set with textContent.
import { providerModelValue } from './shell.js';
import { postJson, flashAndReload, showFlash, showError, setText, setShown, busy } from './settings-api.js';
import { initKeys } from './settings-keys.js';
import { initSignIn } from './settings-signin.js';

const STEPS = ['welcome', 'connect', 'model', 'done'];
const LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
const KEY_FIELDS = ['keyProvider', 'keyName', 'keyLogo', 'keyForm', 'keySubtitle', 'keyVariables', 'keyBaseUrl'];

const wizard = document.querySelector('[data-setup-wizard]');

showFlash();
initKeys();
initSignIn();
if (wizard) {
  initSteps();
  initKeyForm();
  initThinking();
  wizard.querySelector('[data-setup-skip]')?.addEventListener('click', skip);
  wizard.querySelector('[data-setup-finish]')?.addEventListener('click', finish);
}

// The Default model step needs a connected provider, and Done a finished wizard (the server renders the same rule).
function reachable(step) {
  if (step === 'model') return Number(wizard.dataset.setupConnected || 0) > 0;
  if (step === 'done') return wizard.dataset.setupFinished === 'true';
  return STEPS.includes(step);
}

function show(step, focus) {
  if (!reachable(step)) return;
  wizard.dataset.setupCurrent = step;
  const index = STEPS.indexOf(step);
  wizard.querySelectorAll('[data-setup-step]').forEach((el) => setShown(el, el.dataset.setupStep === step));
  wizard.querySelectorAll('[data-setup-only]').forEach((el) => setShown(el, el.dataset.setupOnly.split(' ').includes(step)));
  wizard.querySelectorAll('[data-setup-indicator]').forEach((el) => {
    const at = STEPS.indexOf(el.dataset.setupIndicator);
    el.classList.toggle('active', at === index);
    el.classList.toggle('done', at < index);
    if (at === index) el.setAttribute('aria-current', 'step');
    else el.removeAttribute('aria-current');
  });
  try {
    const url = new URL(window.location.href);
    url.searchParams.set('step', step);
    window.history.replaceState(null, '', url);
  } catch {
    // The step still shows; only a reload would start elsewhere.
  }
  if (focus) wizard.querySelector(`[data-setup-step="${step}"] [tabindex="-1"]`)?.focus();
}

function initSteps() {
  wizard.addEventListener('click', (event) => {
    const go = event.target.closest('[data-setup-go]');
    if (go && !go.disabled) show(go.dataset.setupGo, true);
  });
  show(wizard.dataset.setupCurrent, false);
}

// ---- Connect: Paste an API key ----

function initKeyForm() {
  const form = wizard.querySelector('[data-setup-key-form]');
  if (!form) return;
  const select = form.querySelector('[data-setup-key-provider]');
  const input = form.querySelector('[data-setup-key-input]');
  // The modal triggers ("More options", "Set up <cloud account>") open #modal-api-key for the provider chosen in the list.
  const sync = () => {
    const option = select.selectedOptions[0];
    if (!option) return;
    const data = option.dataset;
    wizard.querySelectorAll('[data-setup-key-modal]').forEach((trigger) => {
      for (const field of KEY_FIELDS) {
        if (data[field]) trigger.dataset[field] = data[field];
        else delete trigger.dataset[field];
      }
    });
    const single = data.keyForm === 'single';
    setShown(form.querySelector('[data-setup-key-single]'), single);
    setShown(form.querySelector('[data-setup-key-cloud]'), !single);
    setText(form.querySelector('[data-setup-key-cloud-name]'), data.keyName || option.value);
    if (!single) clearKey(input);
    showKeyError('');
  };
  select.addEventListener('change', sync);
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    saveKey(form, select, input);
  });
  form.querySelector('[data-setup-reveal]')?.addEventListener('click', (event) => {
    input.type = input.type === 'password' ? 'text' : 'password';
    event.currentTarget.querySelector('.bi')?.classList.toggle('bi-eye-slash', input.type === 'text');
  });
  // Leaving the page never keeps a typed key (the browser's back-forward cache included).
  window.addEventListener('pagehide', () => clearKey(input));
  sync();
}

function clearKey(input) {
  if (!input) return;
  input.value = '';
  input.type = 'password';
  wizard.querySelector('[data-setup-reveal] .bi')?.classList.remove('bi-eye-slash');
}

function showKeyError(message) {
  setText(wizard.querySelector('[data-setup-key-error-text]'), message);
  setShown(wizard.querySelector('[data-setup-key-error]'), Boolean(message));
}

async function saveKey(form, select, input) {
  const option = select.selectedOptions[0];
  if (!option || option.dataset.keyForm !== 'single') return;
  const key = input.value.trim();
  if (!key) {
    showKeyError('Paste the key first.');
    input.focus();
    return;
  }
  showKeyError('');
  const name = option.dataset.keyName || option.value;
  const models = Number(option.dataset.modelCount || 0);
  try {
    const answer = await busy(form.querySelector('[data-setup-key-save]'), () => postJson('/api/credentials/accounts', { provider: option.value, apiKey: key }));
    clearKey(input);
    const hint = answer?.account?.hint ? ` as ${answer.account.hint}` : '';
    const body = answer?.becameActive
      ? `${name} is connected${hint}.${models > 0 ? ` ${models === 1 ? '1 model was' : `${models} models were`} added to the picker.` : ''}`
      : `${name} key stored${hint}. Make it active on the provider's page to use it for new chats.`;
    flashAndReload({ title: 'Credential saved', body, tone: 'ok' });
  } catch (error) {
    showKeyError(error.message);
  }
}

// ---- Default model: thinking levels ----

function modelRoot() {
  return wizard.querySelector('[data-setup-model] [data-provider-model]');
}

function modelName(root) {
  const pane = [...root.querySelectorAll('[data-provider]')].find((el) => !el.hidden);
  return pane?.querySelector('[data-model-select]')?.selectedOptions[0]?.textContent?.trim() || 'This model';
}

/** Pi's clampThinkingLevel: the level when supported, else the nearest supported above it, else below, else the first supported. */
export function clampLevel(available, level) {
  if (!available.length) return 'off';
  if (available.includes(level)) return level;
  const requested = LEVELS.indexOf(level);
  if (requested < 0) return available[0];
  for (let i = requested; i < LEVELS.length; i++) if (available.includes(LEVELS[i])) return LEVELS[i];
  for (let i = requested - 1; i >= 0; i--) if (available.includes(LEVELS[i])) return LEVELS[i];
  return available[0];
}

/** The note under the levels, as SetupText.ThinkingNote words it. */
export function thinkingNote(name, supported) {
  const levels = LEVELS.filter((level) => supported.includes(level));
  if (!levels.length || (levels.length === 1 && levels[0] === 'off')) return `${name} doesn't reason, so only off is available.`;
  const first = levels[0];
  const last = levels[levels.length - 1];
  const range = levels.length === 1 ? `${name} supports ${first} only` : `${name} supports ${first} to ${last}`;
  const gaps = LEVELS.slice(LEVELS.indexOf(first), LEVELS.indexOf(last)).filter((level) => !levels.includes(level));
  if (!levels.includes('off')) gaps.unshift('off');
  const missing = gaps.length ? `; ${gaps.join(', ')} ${gaps.length === 1 ? "isn't" : "aren't"} available` : '';
  return `${range}${missing}. The level is clamped when you switch to a model with fewer levels.`;
}

function initThinking() {
  const group = wizard.querySelector('[data-setup-thinking]');
  const root = modelRoot();
  if (!group || !root) return;
  let preferred = group.querySelector('input:checked')?.value ?? 'medium';
  group.addEventListener('change', (event) => {
    if (event.target.matches('input[data-thinking-level]')) preferred = event.target.value;
  });
  const sync = () => {
    const choice = providerModelValue(root);
    const supported = choice?.thinking?.length ? choice.thinking : ['off'];
    const name = modelName(root);
    group.querySelectorAll('input[data-thinking-level]').forEach((input) => {
      const level = input.dataset.thinkingLevel;
      const ok = supported.includes(level);
      input.disabled = !ok;
      const label = group.querySelector(`label[for="${input.id}"]`);
      if (label && ok) label.removeAttribute('title');
      else if (label) label.title = `${name} doesn't support ${level}`;
    });
    const next = group.querySelector(`input[data-thinking-level="${clampLevel(supported, preferred)}"]`);
    if (next) next.checked = true;
    setText(wizard.querySelector('[data-setup-thinking-note]'), thinkingNote(name, supported));
  };
  root.addEventListener('provider-model-change', sync);
  sync();
}

// ---- Footer: Skip setup, Save and continue ----

async function skip(event) {
  try {
    await busy(event.currentTarget, () => postJson('/api/setup/skip'));
    window.location.assign('/chat/new');
  } catch (error) {
    showError('Could not skip setup', error);
  }
}

function showFinishError(message) {
  setText(wizard.querySelector('[data-setup-finish-error-text]'), message);
  setShown(wizard.querySelector('[data-setup-finish-error]'), Boolean(message));
}

async function finish(event) {
  const root = modelRoot();
  const choice = root ? providerModelValue(root) : null;
  const useAsDefault = Boolean(choice?.model) && (wizard.querySelector('[data-setup-use-default]')?.checked ?? false);
  const thinking = wizard.querySelector('[data-setup-thinking] input:checked:not(:disabled)')?.value ?? null;
  const body = useAsDefault
    ? { provider: choice.provider, model: choice.model, thinking, useAsDefault: true }
    : { provider: null, model: null, thinking: null, useAsDefault: false };
  showFinishError('');
  try {
    const answer = await busy(event.currentTarget, () => postJson('/api/setup/finish', body));
    const saved = answer?.defaults;
    const toast = answer?.savedDefaults && saved?.provider
      ? { title: 'Defaults saved', body: `New chats start with ${saved.provider}/${saved.model}${saved.thinking ? ` at ${saved.thinking} thinking` : ''}.`, tone: 'ok' }
      : { title: 'Setup finished', body: 'The default model was left as it was.', tone: 'ok' };
    flashAndReload(toast, '/setup?step=done');
  } catch (error) {
    showFinishError(error.message);
  }
}
