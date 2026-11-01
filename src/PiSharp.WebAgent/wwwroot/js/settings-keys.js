// Decision 0011 (run W2, WU9): the API key modal (#modal-api-key, _ApiKeyModal). A _CredentialButton opens it; its data attributes say which
// provider, which variant (single, azure, bedrock, vertex, cloudflare) and, to replace a key, which account. The key is read from the
// password field once, POSTed once (/api/credentials/accounts to add, /accounts/{id}/key to replace) and the field is cleared after saving
// and whenever the modal closes. The answer holds the account's masked hint only, which is all the page ever shows. "Test connection" POSTs
// /api/credentials/test (the typed key) or /accounts/{id}/test (the stored key, when replacing with the field left blank).
import { postJson, setText, setShown, fillLogo, flashAndReload, busy } from './settings-api.js';

const VARIANTS = ['single', 'azure', 'bedrock', 'vertex', 'cloudflare'];
const AZURE_MAP = 'AZURE_OPENAI_DEPLOYMENT_NAME_MAP';

let modal = null;
let form = null;
let current = null;

/** Wires the API key modal, when the page has one. */
export function initKeys() {
  modal = document.getElementById('modal-api-key');
  if (!modal) return;
  form = modal.querySelector('[data-key-form]');
  modal.addEventListener('show.bs.modal', (event) => open(event.relatedTarget));
  modal.addEventListener('shown.bs.modal', () => visibleStep()?.querySelector('[data-key-secret]:not([disabled])')?.focus());
  modal.addEventListener('hidden.bs.modal', () => { clear(); current = null; });
  form.addEventListener('submit', onSubmit);
  form.addEventListener('change', onChange);
  form.addEventListener('click', onClick);
  modal.querySelector('[data-key-test-button]')?.addEventListener('click', onTest);
}

function open(trigger) {
  const data = trigger?.dataset ?? {};
  if (!data.keyProvider) return;
  clear();
  let settings = {};
  try { settings = data.keySettings ? JSON.parse(data.keySettings) : {}; } catch { settings = {}; }
  current = {
    provider: data.keyProvider,
    name: data.keyName || data.keyProvider,
    variant: VARIANTS.includes(data.keyForm) ? data.keyForm : 'single',
    account: data.keyAccount || null,
    accountLabel: data.keyAccountLabel || '',
    hint: data.keyHint || '',
    variables: data.keyVariables || '',
    baseUrl: data.keyBaseUrl || '',
    settings: settings && typeof settings === 'object' ? settings : {},
  };
  const replacing = Boolean(current.account);
  fillLogo(modal.querySelector('[data-key-logo]'), current.name, data.keyLogo);
  setText(modal.querySelector('[data-key-verb]'), replacing ? 'Replace the key of' : 'Connect');
  setText(modal.querySelector('[data-key-title]'), current.name);
  setText(modal.querySelector('[data-key-subtitle]'), data.keySubtitle || current.provider);
  setShown(modal.querySelector('[data-key-replacing]'), replacing);
  setText(modal.querySelector('[data-key-account-label]'), current.accountLabel);
  setText(modal.querySelector('[data-key-hint]'), current.hint || 'settings only');
  modal.querySelectorAll('[data-key-variables]').forEach((el) => setText(el, current.variables || 'the provider\'s environment variable'));
  setShown(modal.querySelector('[data-key-sources]'), !replacing && current.variables.length > 0);
  modal.querySelectorAll('[data-key-label]').forEach((input) => setShown(input.parentElement, !replacing));
  const baseUrl = modal.querySelector('#baseUrlOverride');
  if (baseUrl) baseUrl.placeholder = current.baseUrl;
  setShown(modal.querySelector('[data-cf-gateway]'), current.provider === 'cloudflare-ai-gateway');
  modal.querySelectorAll('[data-key-step]').forEach((step) => setShown(step, step.dataset.keyStep === current.variant));
  prefill();
  sync();
}

function visibleStep() {
  return current ? modal.querySelector(`[data-key-step="${current.variant}"]`) : null;
}

// Fills the settings fields of the account being replaced (non-secret values only) and picks the matching authentication choice.
function prefill() {
  const step = visibleStep();
  const settings = current.settings;
  step.querySelectorAll('[data-setting]').forEach((input) => {
    const value = settings[input.dataset.setting];
    if (typeof value === 'string') input.value = value;
  });
  if (current.variant === 'azure') {
    const map = typeof settings[AZURE_MAP] === 'string' ? settings[AZURE_MAP] : '';
    for (const pair of map.split(',').map((part) => part.trim()).filter(Boolean)) {
      const [model, deployment] = pair.split('=');
      addAzureRow(model?.trim() ?? '', deployment?.trim() ?? '');
    }
    if (!map) addAzureRow('', '');
    choose(step, 'azEndpoint', settings.AZURE_OPENAI_BASE_URL && !settings.AZURE_OPENAI_RESOURCE_NAME ? 'url' : 'resource');
  } else if (current.variant === 'bedrock' && current.account) {
    choose(step, 'awsAuth', settings.AWS_PROFILE ? 'profile' : current.hint ? 'bearer' : 'chain');
  } else if (current.variant === 'vertex' && current.account) {
    choose(step, 'gcpAuth', settings.GOOGLE_APPLICATION_CREDENTIALS ? 'file' : current.hint ? 'key' : 'adc');
  }
}

function choose(step, name, value) {
  const radio = step.querySelector(`input[name="${name}"][value="${value}"]`);
  if (radio) radio.checked = true;
}

function radioValue(name) {
  return form.querySelector(`input[name="${name}"]:checked`)?.value ?? '';
}

// Shows the fields the current choices use: the key source, the Azure endpoint style, the Bedrock or Vertex authentication.
function sync() {
  if (!current) return;
  const env = current.variant === 'single' && radioValue('keySource') === 'env';
  setShown(modal.querySelector('[data-key-store]'), !env);
  setShown(modal.querySelector('[data-key-env]'), env);
  const save = modal.querySelector('[data-key-save]');
  if (save) save.disabled = env;
  const testButton = modal.querySelector('[data-key-test-button]');
  if (testButton && !testButton.title) testButton.hidden = env;
  modal.querySelectorAll('[data-az-only]').forEach((el) => setShown(el, el.dataset.azOnly === radioValue('azEndpoint')));
  const step = visibleStep();
  const auth = current.variant === 'bedrock' ? radioValue('awsAuth') : current.variant === 'vertex' ? radioValue('gcpAuth') : '';
  step?.querySelectorAll('[data-auth-only]').forEach((el) => setShown(el, el.dataset.authOnly === auth));
}

function onChange(event) {
  if (event.target.matches('[data-key-source], [data-az-endpoint], [data-auth-choice]')) sync();
}

function onClick(event) {
  const reveal = event.target.closest('[data-reveal]');
  if (reveal) {
    const input = modal.querySelector(reveal.dataset.reveal);
    if (input) {
      input.type = input.type === 'password' ? 'text' : 'password';
      reveal.querySelector('.bi')?.classList.toggle('bi-eye-slash', input.type === 'text');
    }
    return;
  }
  if (event.target.closest('[data-az-add]')) {
    addAzureRow('', '');
    return;
  }
  const remove = event.target.closest('[data-az-remove]');
  if (remove) remove.closest('tr')?.remove();
}

function addAzureRow(model, deployment) {
  const template = modal.querySelector('template[data-az-row]');
  const body = modal.querySelector('[data-az-map]');
  if (!template || !body) return;
  const row = template.content.firstElementChild.cloneNode(true);
  row.querySelector('[data-az-model]').value = model;
  row.querySelector('[data-az-deployment]').value = deployment;
  body.append(row);
}

// Whether an element of the current step is in use (not inside a hidden part).
function inUse(element, step) {
  for (let node = element; node && node !== step; node = node.parentElement) {
    if (node.hidden) return false;
  }
  return true;
}

// The current entries: the typed secret (or ''), the account name, and the non-secret settings of the visible fields.
function collect() {
  const step = visibleStep();
  const secretInput = [...step.querySelectorAll('[data-key-secret]')].find((input) => inUse(input, step));
  const settings = {};
  step.querySelectorAll('[data-setting]').forEach((input) => {
    const value = input.value.trim();
    if (value && inUse(input, step)) settings[input.dataset.setting] = value;
  });
  if (current.variant === 'azure') {
    const pairs = [...step.querySelectorAll('[data-az-map] tr')]
      .map((row) => [row.querySelector('[data-az-model]').value.trim(), row.querySelector('[data-az-deployment]').value.trim()])
      .filter(([model, deployment]) => model && deployment)
      .map(([model, deployment]) => `${model}=${deployment}`);
    if (pairs.length) settings[AZURE_MAP] = pairs.join(',');
  }
  const label = step.querySelector('[data-key-label]')?.value.trim() ?? '';
  return { secret: secretInput ? secretInput.value.trim() : '', needsSecret: Boolean(secretInput), settings, label };
}

function showError(message) {
  setText(modal.querySelector('[data-key-error-text]'), message);
  setShown(modal.querySelector('[data-key-error]'), Boolean(message));
}

function showTest(state, text) {
  modal.querySelectorAll('[data-key-test]').forEach((el) => {
    setShown(el, el.dataset.keyTest === state);
    if (el.dataset.keyTest === state) el.querySelectorAll('[data-key-test-text]').forEach((span) => setText(span, text ?? ''));
  });
}

// Clears every secret field (and the rest of the form), so no key stays in the page.
function clearSecrets() {
  modal.querySelectorAll('[data-key-secret]').forEach((input) => {
    input.value = '';
    input.type = 'password';
  });
  modal.querySelectorAll('[data-reveal] .bi').forEach((icon) => icon.classList.remove('bi-eye-slash'));
}

function clear() {
  if (!modal) return;
  clearSecrets();
  form.reset();
  modal.querySelectorAll('[data-setting], [data-key-label]').forEach((input) => { input.value = ''; });
  modal.querySelector('[data-az-map]')?.replaceChildren();
  showError('');
  showTest('idle');
  sync();
}

async function onSubmit(event) {
  event.preventDefault();
  if (!current) return;
  const entries = collect();
  const replacing = Boolean(current.account);
  if (!replacing && entries.needsSecret && !entries.secret) {
    showError('Enter the key first.');
    return;
  }
  showError('');
  const save = modal.querySelector('[data-key-save]');
  try {
    const answer = await busy(save, () => replacing
      ? postJson(`/api/credentials/accounts/${encodeURIComponent(current.account)}/key`, { apiKey: entries.secret || null, settings: entries.settings })
      : postJson('/api/credentials/accounts', { provider: current.provider, apiKey: entries.secret || null, label: entries.label || null, settings: entries.settings }));
    clearSecrets();
    const account = answer?.account ?? answer;
    const hint = account?.hint ? `Stored as ${account.hint}.` : 'Settings stored.';
    const body = replacing ? `${hint} Chats that use ${current.accountLabel || 'this account'} keep using it.`
      : answer?.becameActive ? `${hint} New ${current.name} chats use it.` : `${hint} Make it active on the provider's page to use it for new chats.`;
    const title = replacing ? 'Key replaced' : answer?.becameActive ? `${current.name} connected` : `${current.name} key added`;
    flashAndReload({ title, body, tone: 'ok' });
  } catch (error) {
    showError(error.message);
  }
}

async function onTest(event) {
  if (!current) return;
  const entries = collect();
  const useStored = Boolean(current.account) && !entries.secret;
  if (!useStored && entries.needsSecret && !entries.secret) {
    showTest('fail', 'Enter the key to test it.');
    return;
  }
  showTest('running');
  try {
    const result = await busy(event.currentTarget, () => useStored
      ? postJson(`/api/credentials/accounts/${encodeURIComponent(current.account)}/test`)
      : postJson('/api/credentials/test', { provider: current.provider, apiKey: entries.secret || null, settings: entries.settings }));
    if (!result?.supported) showTest('unsupported', result?.message || 'There is no connection test for this provider.');
    else if (result.ok) showTest('ok', describe(result));
    else showTest('fail', (result.statusCode ? `${result.statusCode} · ` : '') + (result.message || 'The provider refused the key.'));
  } catch (error) {
    showTest('fail', error.message);
  }
}

/** A passed test as one line: "312 ms · 17 models · GET https://…". */
export function describe(result) {
  return [
    result.latencyMs != null ? `${result.latencyMs} ms` : null,
    result.modelCount != null ? `${result.modelCount} models available` : null,
    result.endpoint || null,
  ].filter(Boolean).join(' · ') || result.message || 'Connected.';
}
