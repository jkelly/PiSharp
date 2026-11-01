// Decision 0011 (run W3, S4): Settings › Spend. The settings form ([data-spend-form]) tracks what changed against each field's
// data-initial ("modified · was X", the save bar's count and names) and saves only the changes with POST /api/spend/settings; Discard puts
// the saved values back. The cap modal adds a cap (POST /api/spend/caps), edits one (the same, with its id) or removes one
// (POST /api/spend/caps/{id}/remove). Enter price saves at once (POST /api/spend/prices), outside the save bar. After a change the page
// reloads with a toast (flashAndReload). Text is set with textContent only.
import { postJson, flashAndReload, showFlash, showError, setText, busy } from './settings-api.js';

const root = document.querySelector('[data-spend-settings]');

/** The save bar's name of a field: warnPercent, unknownPriceRules[work-gateway], billing[anthropic]. */
function fieldName(el) {
  const key = el.dataset.spendField ?? '';
  if (key.startsWith('unknown:')) return `unknownPriceRules[${el.dataset.provider}]`;
  if (key.startsWith('billing:')) return `billing[${el.dataset.provider}]`;
  return key;
}

/** The text of a field's saved value: the option's text for a select, the number otherwise. */
function savedText(el) {
  if (el.tagName === 'SELECT') {
    const option = [...el.options].find((item) => item.value === el.dataset.initial);
    return (option?.textContent ?? el.dataset.initial ?? '').replace(' (detected)', '');
  }
  const number = Number(el.dataset.initial);
  return Number.isFinite(number) ? number.toLocaleString('en-US') : (el.dataset.initial ?? '');
}

function initForm() {
  const form = root?.querySelector('form[data-spend-form]');
  if (!form) return;
  const fields = [...form.querySelectorAll('[data-spend-field]')];
  const bar = form.querySelector('[data-savebar]');
  const changed = () => fields.filter((el) => el.value !== el.dataset.initial);

  function refresh() {
    const list = changed();
    if (bar) bar.hidden = list.length === 0;
    setText(form.querySelector('[data-savebar-count]'), list.length === 1 ? '1 unsaved change' : `${list.length} unsaved changes`);
    setText(form.querySelector('[data-savebar-names]'), list.map(fieldName).join(' · '));
    for (const el of fields) {
      const modified = el.value !== el.dataset.initial;
      el.classList.toggle('border-warning', modified);
      const row = el.closest('[data-setting-row]');
      row?.classList.toggle('is-modified', modified);
      const chip = row?.querySelector('[data-modified-chip]');
      if (chip) {
        chip.hidden = !modified;
        chip.textContent = `modified · was ${savedText(el)}`;
      }
    }
  }

  form.addEventListener('input', (event) => { if (!event.target.closest('[data-spend-ignore]')) refresh(); });
  form.addEventListener('change', (event) => { if (!event.target.closest('[data-spend-ignore]')) refresh(); });
  form.querySelector('[data-discard]')?.addEventListener('click', () => {
    for (const el of fields) el.value = el.dataset.initial ?? '';
    refresh();
  });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const list = changed();
    if (list.length === 0) return;
    const invalid = list.find((el) => el.tagName === 'INPUT' && !el.checkValidity());
    if (invalid) {
      invalid.reportValidity();
      return;
    }
    const body = {};
    for (const el of list) {
      const key = el.dataset.spendField;
      if (key === 'warnPercent' || key === 'reservationOutputTokens') body[key] = Number.parseInt(el.value, 10);
      else if (key === 'unknownPrice') body.unknownPrice = el.value;
      else if (key.startsWith('unknown:')) (body.unknownPriceRules ??= []).push({ provider: el.dataset.provider, behavior: el.value });
      else if (key.startsWith('billing:')) (body.billing ??= []).push({ provider: el.dataset.provider, account: el.dataset.account || null, mode: el.value });
    }
    await busy(form.querySelector('[data-save]'), async () => {
      try {
        await postJson('/api/spend/settings', body);
        flashAndReload({ title: 'Spend settings saved', body: 'New requests use them right away. Requests already running keep their reservation.' });
      } catch (error) {
        showError('Spend settings not saved', error);
      }
    });
  });
  refresh();
}

function initCapModal() {
  const modalEl = document.getElementById('modal-spend-cap');
  const form = modalEl?.querySelector('[data-cap-form]');
  if (!form || !window.bootstrap) return;
  const modal = () => window.bootstrap.Modal.getOrCreateInstance(modalEl);
  const scope = form.querySelector('[data-cap-scope-select]');
  const accountProvider = form.querySelector('[data-cap-account-provider]');
  const account = form.querySelector('[data-cap-key="account"]');
  const error = form.querySelector('[data-cap-error]');

  function mode(name) {
    form.dataset.mode = name;
    form.querySelectorAll('[data-cap-only]').forEach((el) => { el.hidden = el.dataset.capOnly !== name; });
    if (error) error.hidden = true;
  }
  function showScope() {
    form.querySelectorAll('[data-cap-scope-pane]').forEach((el) => { el.hidden = el.dataset.capScopePane !== scope.value; });
  }
  function filterAccounts() {
    if (!account || !accountProvider) return;
    let first = null;
    for (const option of account.options) {
      const shown = option.dataset.provider === accountProvider.value;
      option.hidden = !shown;
      option.disabled = !shown;
      if (shown && !first) first = option;
    }
    if (account.selectedOptions[0]?.disabled && first) account.value = first.value;
  }
  function fail(message) {
    setText(error, message);
    if (error) error.hidden = false;
  }

  scope.addEventListener('change', showScope);
  accountProvider?.addEventListener('change', filterAccounts);
  document.querySelectorAll('[data-cap-new]').forEach((button) => button.addEventListener('click', () => {
    form.reset();
    delete form.dataset.capId;
    mode('new');
    showScope();
    filterAccounts();
    modal().show();
  }));
  document.querySelectorAll('[data-cap-edit]').forEach((button) => button.addEventListener('click', () => {
    form.reset();
    form.dataset.capId = button.dataset.capId;
    mode('edit');
    setText(form.querySelector('[data-cap-field="title"]'), button.dataset.capTitle);
    setText(form.querySelector('[data-cap-field="scope"]'), button.dataset.capScope);
    setText(form.querySelector('[data-cap-field="window"]'), button.dataset.capWindow);
    setText(form.querySelector('[data-cap-field="used"]'), button.dataset.capUsed);
    form.querySelector('[data-cap-amount-edit]').value = button.dataset.capAmount ?? '';
    const behaviour = form.querySelector(`input[name="capEditBehaviour"][value="${button.dataset.capBehaviour === 'block' ? 'block' : 'ask'}"]`);
    if (behaviour) behaviour.checked = true;
    modal().show();
  }));

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    let body;
    if (form.dataset.mode === 'edit') {
      body = {
        id: Number(form.dataset.capId),
        limit: form.querySelector('[data-cap-amount-edit]').value,
        action: form.querySelector('input[name="capEditBehaviour"]:checked')?.value,
      };
    } else {
      const chosen = scope.value;
      body = {
        scope: chosen,
        key: chosen === 'everything' ? null : form.querySelector(`[data-cap-key="${chosen}"]`)?.value ?? null,
        period: form.querySelector('input[name="capWindow"]:checked')?.value,
        limit: form.querySelector('[data-cap-amount-new]').value,
        action: form.querySelector('input[name="capBehaviour"]:checked')?.value,
      };
    }
    await busy(form.querySelector('[data-cap-save]'), async () => {
      try {
        const answer = await postJson('/api/spend/caps', body);
        modal().hide();
        flashAndReload({
          title: 'Cap saved',
          body: answer?.existing ? 'That cap already exists with these values; nothing changed.'
            : 'The next metered request is checked against it. Spend already recorded counts toward it.',
        });
      } catch (problem) {
        fail(problem instanceof Error ? problem.message : String(problem));
      }
    });
  });

  form.querySelector('[data-cap-remove]')?.addEventListener('click', async (event) => {
    const id = Number(form.dataset.capId);
    if (!Number.isInteger(id) || id <= 0) return;
    await busy(event.currentTarget, async () => {
      try {
        await postJson(`/api/spend/caps/${id}/remove`);
        modal().hide();
        flashAndReload({ title: 'Cap removed', body: 'Requests are no longer checked against it. The ledger keeps every recorded cost.', tone: 'info' });
      } catch (problem) {
        fail(problem instanceof Error ? problem.message : String(problem));
      }
    });
  });
}

function initPrices() {
  root?.querySelectorAll('[data-price-save]').forEach((button) => button.addEventListener('click', async () => {
    const menu = button.closest('[data-price-menu]');
    const provider = menu.dataset.provider;
    const model = menu.querySelector('[data-price-model]')?.value;
    const body = { provider, model, input: menu.querySelector('[data-price-input]').value, output: menu.querySelector('[data-price-output]').value };
    await busy(button, async () => {
      try {
        await postJson('/api/spend/prices', body);
        flashAndReload({ title: 'Price saved', body: `${provider}/${model} now has a price and counts as metered.` });
      } catch (error) {
        showError('Price not saved', error);
      }
    });
  }));
  root?.querySelectorAll('[data-price-remove]').forEach((button) => button.addEventListener('click', async () => {
    const { provider, model } = button.dataset;
    await busy(button, async () => {
      try {
        await postJson('/api/spend/prices/remove', { provider, model });
        flashAndReload({ title: 'Price removed', body: `${provider}/${model} has no price again.`, tone: 'info' });
      } catch (error) {
        showError('Price not removed', error);
      }
    });
  }));
}

if (root) {
  initForm();
  initCapModal();
  initPrices();
  showFlash();
}
