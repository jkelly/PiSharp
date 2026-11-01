// Decision 0011 (run W2, WU9): model visibility on Settings › Models and a provider's page. A model switch ([data-model-toggle], the row's
// provider/model key) saves at once through POST /api/settings/models/visibility; "Enable/Disable all shown" sends this page's keys in one
// call; a group's "enable all" / "disable all" sends the provider. The filter form submits itself when a filter changes, and the provider
// menu filters its list. _ModelDetailsModal is filled from the details button's data-model with textContent only.
import { showToast } from './shell.js';
import { postJson, setText, setShown, fillLogo, flashAndReload, showError, plural, busy } from './settings-api.js';

const LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];

/** Wires model switches, the Models page's filters and the model details modal. */
export function initModels() {
  document.addEventListener('change', onChange);
  document.addEventListener('click', onClick);
  document.addEventListener('input', onInput);
  const details = document.getElementById('modal-model-details');
  if (details) details.addEventListener('show.bs.modal', (event) => fillDetails(details, event.relatedTarget));
}

async function setHidden(keys, hidden) {
  return postJson('/api/settings/models/visibility', { models: keys, hidden });
}

// Updates every switch (table and details modal) of the saved keys, and the saved-section preview.
function reflect(keys, hidden, answer) {
  const set = new Set(keys);
  document.querySelectorAll('[data-model-toggle]').forEach((toggle) => {
    if (!set.has(toggle.dataset.modelToggle)) return;
    toggle.checked = !hidden;
    toggle.closest('tr')?.classList.toggle('is-off', hidden);
  });
  const modalToggle = document.querySelector('[data-md-toggle]');
  if (modalToggle && set.has(modalToggle.dataset.key)) modalToggle.checked = !hidden;
  if (answer && Array.isArray(answer.hidden)) {
    setText(document.querySelector('[data-models-json]'), JSON.stringify({ hidden: answer.hidden }, null, 2));
    const count = document.querySelector('[data-models-enabled-count]');
    const section = count?.closest('[data-provider-models]');
    if (count && section) {
      const toggles = section.querySelectorAll('[data-model-toggle]');
      const prefix = `${section.dataset.providerModels}/`;
      const hiddenHere = answer.hidden.filter((key) => key.startsWith(prefix)).length;
      const total = Number(section.dataset.modelTotal || toggles.length);
      setText(count, String(Math.max(0, total - hiddenHere)));
    }
  }
}

async function onChange(event) {
  const target = event.target;
  if (target.matches('[data-model-toggle], [data-md-toggle]')) {
    const key = target.dataset.modelToggle ?? target.dataset.key;
    if (!key) return;
    const hidden = !target.checked;
    target.disabled = true;
    try {
      reflect([key], hidden, await setHidden([key], hidden));
    } catch (error) {
      target.checked = hidden;
      showError(hidden ? 'Could not hide the model' : 'Could not show the model', error);
    } finally {
      target.disabled = false;
    }
    return;
  }
  if (target.matches('[data-filter-auto]')) target.form?.requestSubmit();
}

async function onClick(event) {
  const bulk = event.target.closest('[data-models-bulk]');
  if (bulk) {
    const keys = [...document.querySelectorAll('[data-models-table] [data-model-toggle]')].map((toggle) => toggle.dataset.modelToggle);
    if (keys.length === 0) return;
    const hidden = bulk.dataset.modelsBulk === 'hide';
    try {
      const answer = await busy(bulk, () => setHidden(keys, hidden));
      reflect(keys, hidden, answer);
      showToast({ title: hidden ? 'Models hidden' : 'Models shown', body: `${plural(answer?.changed ?? 0, 'model')} changed on this page.` });
    } catch (error) {
      showError('Could not change the models', error);
    }
    return;
  }
  const group = event.target.closest('[data-models-provider]');
  if (group) {
    const hidden = group.dataset.hidden === 'true';
    try {
      const answer = await busy(group, () => postJson('/api/settings/models/visibility', { provider: group.dataset.modelsProvider, hidden }));
      flashAndReload({ title: hidden ? 'Models hidden' : 'Models shown', body: `${plural(answer?.changed ?? 0, 'model')} of ${group.dataset.modelsProvider} changed.` });
    } catch (error) {
      showError('Could not change the models', error);
    }
    return;
  }
  const menu = event.target.closest('[data-provider-menu]');
  if (menu) {
    const all = menu.dataset.providerMenu === 'all';
    menu.closest('.dropdown-menu')?.querySelectorAll('[data-provider-option]').forEach((option) => {
      if (!option.hidden) option.querySelector('input[type="checkbox"]').checked = all;
    });
  }
}

function onInput(event) {
  if (!event.target.matches('[data-provider-menu-search]')) return;
  const text = event.target.value.trim().toLowerCase();
  event.target.closest('.dropdown-menu')?.querySelectorAll('[data-provider-option]').forEach((option) => {
    option.hidden = Boolean(text) && !option.dataset.search.includes(text);
  });
}

function tokens(count) {
  if (!count) return '—';
  if (count >= 1_000_000) return `${+(count / 1_000_000).toFixed(count % 1_000_000 ? 1 : 0)}M`;
  if (count >= 1000) return `${Math.round(count / 1000)}K`;
  return String(count);
}

function dollars(value) {
  return value == null ? '—' : `$${Number(value).toFixed(2)}`;
}

function cap(host, icon, text, extra) {
  const span = document.createElement('span');
  span.className = `cap ${extra ?? ''}`.trim();
  if (icon) {
    const i = document.createElement('i');
    i.className = `bi ${icon}`;
    i.setAttribute('aria-hidden', 'true');
    span.append(i);
  }
  span.append(document.createTextNode(text));
  host.append(span);
}

function fillDetails(modal, trigger) {
  let details = null;
  try { details = JSON.parse(trigger?.dataset.model ?? 'null'); } catch { details = null; }
  if (!details) return;
  const model = details.model ?? {};
  const field = (name, text) => setText(modal.querySelector(`[data-md="${name}"]`), text);
  fillLogo(modal.querySelector('[data-md-logo]'), details.providerName, details.logo);
  field('name', details.name);
  field('key', details.key);
  const caps = modal.querySelector('[data-md-caps]');
  caps.replaceChildren();
  if (model.reasoning) cap(caps, 'bi-lightbulb', 'reasoning', 'cap-reasoning');
  cap(caps, 'bi-fonts', 'text');
  if ((model.input ?? []).includes('image')) cap(caps, 'bi-image', 'image', 'cap-image');
  cap(caps, null, `${tokens(model.contextWindow)} ctx`);
  if (model.maxTokens) cap(caps, null, `${tokens(model.maxTokens)} out`);
  const chip = document.createElement('span');
  chip.className = 'ps-chip';
  chip.dataset.tone = details.hidden ? 'outline' : 'ok';
  chip.textContent = details.hidden ? 'hidden' : 'enabled';
  caps.append(chip);
  field('provider', `${details.providerName} · ${details.provider}`);
  field('credential', details.credential ?? 'not connected');
  field('billing', details.billing === 'subscription' ? 'subscription (list prices for comparison)' : details.billing);
  field('context', (model.contextWindow ?? 0).toLocaleString('en-US'));
  field('maxTokens', model.maxTokens ? model.maxTokens.toLocaleString('en-US') : '—');
  field('input', (model.input ?? ['text']).join(' + '));
  field('reasoning', model.reasoning ? 'yes' : 'no');
  const levels = model.thinkingLevels ?? ['off'];
  field('thinking', LEVELS.filter((level) => levels.includes(level)).join(' · ') || 'off');
  field('shown', details.hidden ? 'no · hidden in Settings › Models' : 'yes');
  field('default', details.isDefault ? 'yes · Settings › Defaults' : 'no');
  const cost = model.cost ?? null;
  field('costInput', dollars(cost?.input));
  field('costOutput', dollars(cost?.output));
  field('costCacheRead', dollars(cost?.cacheRead));
  field('costCacheWrite', dollars(cost?.cacheWrite));
  field('pricingNote', cost ? 'List prices per 1M tokens from the PiSharp catalog.' : 'The catalog has no price for this model.');
  field('rawTitle', `${details.provider} · ${details.id}`);
  field('raw', JSON.stringify(model, null, 2));
  const toggle = modal.querySelector('[data-md-toggle]');
  toggle.dataset.key = details.key;
  toggle.checked = !details.hidden;
  // A switch changed in the table since the page loaded wins over the button's snapshot.
  const row = [...document.querySelectorAll('[data-model-toggle]')].find((input) => input.dataset.modelToggle === details.key);
  if (row) toggle.checked = row.checked;
  setShown(modal.querySelector('[data-md-toggle]')?.closest('.form-check'), true);
  window.bootstrap?.Tab.getOrCreateInstance(modal.querySelector('[data-bs-target="#md-overview"]'))?.show();
}
