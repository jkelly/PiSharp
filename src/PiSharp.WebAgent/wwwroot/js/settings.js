// Decision 0011 (run W2, WU9): the one entry module of the settings pages (Views/Settings: Providers, a provider's page, Accounts, Models,
// Defaults, Tools), loaded with asp-append-version; it imports the other settings modules, so each is instantiated once. Here: the toast
// kept across a reload, the Providers page's filters, the account actions ([data-account-action]: test, activate, rename, remove), the
// active account select, "Forget <provider> settings", and copy buttons. Every change is an explicit click and a POST; nothing here
// switches an account or model on its own. Text is always set with textContent.
import { showToast, confirmAction } from './shell.js';
import { postJson, flashAndReload, showFlash, showError, setText, setShown, busy, plural } from './settings-api.js';
import { initKeys, describe } from './settings-keys.js';
import { initSignIn } from './settings-signin.js';
import { initModels } from './settings-models.js';
import { initForms } from './settings-forms.js';

const ACCOUNTS = '/api/credentials/accounts';

showFlash();
initKeys();
initSignIn();
initModels();
initForms();
initProviderFilters();
initRename();
document.addEventListener('click', onClick);
document.addEventListener('change', onChange);

function accountUrl(id, action) {
  return `${ACCOUNTS}/${encodeURIComponent(id)}/${action}`;
}

async function onClick(event) {
  const action = event.target.closest('[data-account-action]');
  if (action && !action.disabled) {
    const run = { test: testAccount, activate: activateAccount, rename: openRename, remove: removeAccount }[action.dataset.accountAction];
    if (run) await run(action);
    return;
  }
  const forget = event.target.closest('[data-forget-provider]');
  if (forget && !forget.disabled) {
    await forgetProvider(forget);
    return;
  }
  const copy = event.target.closest('[data-copy]');
  if (copy) copyText(copy);
}

async function onChange(event) {
  if (event.target.matches('[data-active-account]')) await chooseActive(event.target);
}

async function testAccount(button) {
  const card = button.closest('[data-account-card], [data-oauth-card], .provider-row');
  const out = card?.querySelector('[data-account-test-result]');
  try {
    const result = await busy(button, () => postJson(accountUrl(button.dataset.accountId, 'test')));
    const text = !result?.supported ? (result?.message || 'There is no connection test for this account.')
      : result.ok ? `Connected. ${describe(result)}` : `${result.statusCode ? `${result.statusCode} · ` : ''}${result.message || 'The test failed.'}`;
    const tone = !result?.supported ? 'info' : result.ok ? 'ok' : 'danger';
    if (out) {
      setText(out, text);
      out.dataset.tone = tone;
      setShown(out, true);
    } else {
      showToast({ title: result?.ok ? `${button.dataset.accountLabel || 'Account'} works` : `${button.dataset.accountLabel || 'Account'}: test failed`, body: text, tone });
    }
    const last = card?.querySelector('[data-account-last-test]');
    if (last && result?.supported) setText(last, `${result.ok ? 'Last test passed' : 'Last test failed'} just now`);
  } catch (error) {
    showError('Could not test the account', error);
  }
}

async function activateAccount(button) {
  try {
    await busy(button, () => postJson(accountUrl(button.dataset.accountId, 'activate')));
    const provider = button.dataset.providerName ? `${button.dataset.providerName} ` : '';
    flashAndReload({ title: 'Active account changed', body: `New ${provider}chats use ${button.dataset.accountLabel || 'this account'}. Running chats keep theirs.` });
  } catch (error) {
    showError('Could not change the active account', error);
  }
}

async function chooseActive(select) {
  const id = select.value;
  if (!id) return;
  const label = select.selectedOptions[0]?.textContent ?? '';
  const previous = [...select.options].find((option) => option.defaultSelected)?.value ?? '';
  select.disabled = true;
  try {
    await postJson(accountUrl(id, 'activate'));
    const provider = select.dataset.providerName ? `${select.dataset.providerName} ` : '';
    flashAndReload({ title: 'Active account changed', body: `New ${provider}chats use ${label}. Running chats keep theirs.` });
  } catch (error) {
    select.value = previous;
    select.disabled = false;
    showError('Could not change the active account', error);
  }
}

async function removeAccount(button) {
  const oauth = button.dataset.accountKind === 'oauth';
  const label = button.dataset.accountLabel || 'this account';
  const provider = button.dataset.providerName || 'the provider';
  const ok = await confirmAction({
    title: oauth ? `Disconnect ${label}?` : `Remove the ${provider} key "${label}"?`,
    body: oauth
      ? 'Its tokens are deleted from this machine. Chats that use this account stop working until you sign in again or choose another account for them.'
      : 'The key is deleted from this machine. Chats that use this account stop working until you add a key again or choose another account for them.',
    ok: oauth ? 'Disconnect' : 'Remove key',
  });
  if (!ok) return;
  try {
    const answer = await busy(button, () => postJson(accountUrl(button.dataset.accountId, 'remove')));
    const next = answer?.active?.label;
    flashAndReload({ title: oauth ? `${label} disconnected` : `${label} removed`, body: next ? `New ${provider} chats use ${next}.` : `${provider} has no active account now.` });
  } catch (error) {
    showError(oauth ? 'Could not disconnect the account' : 'Could not remove the key', error);
  }
}

let renaming = null;

function initRename() {
  const modal = document.getElementById('modal-rename-account');
  if (!modal) return;
  const form = modal.querySelector('[data-rename-form]');
  modal.addEventListener('shown.bs.modal', () => modal.querySelector('[data-rename-label]')?.select());
  modal.addEventListener('hidden.bs.modal', () => { renaming = null; });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    if (!renaming) return;
    const label = modal.querySelector('[data-rename-label]').value.trim();
    const error = modal.querySelector('[data-rename-error]');
    if (!label) {
      setText(error, 'Enter a name.');
      setShown(error, true);
      return;
    }
    try {
      const account = await busy(form.querySelector('[type="submit"]'), () => postJson(accountUrl(renaming, 'rename'), { label }));
      flashAndReload({ title: 'Account renamed', body: `It's now ${account?.label ?? label}.` });
    } catch (failure) {
      setText(error, failure.message);
      setShown(error, true);
    }
  });
}

function openRename(button) {
  const modal = document.getElementById('modal-rename-account');
  if (!modal || !window.bootstrap) return;
  renaming = button.dataset.accountId;
  modal.querySelector('[data-rename-label]').value = button.dataset.accountLabel ?? '';
  setShown(modal.querySelector('[data-rename-error]'), false);
  window.bootstrap.Modal.getOrCreateInstance(modal).show();
}

async function forgetProvider(button) {
  const name = button.dataset.providerName || button.dataset.forgetProvider;
  const count = Number(button.dataset.forgetCount || 0);
  const ok = await confirmAction({
    title: `Forget ${name} settings?`,
    body: `${count ? `Deletes ${plural(count, 'stored key or sign-in', 'stored keys and sign-ins')} and shows` : 'Shows'} its hidden models again. Environment variables aren't touched. This can't be undone.`,
    ok: 'Forget settings',
  });
  if (!ok) return;
  try {
    const answer = await busy(button, () => postJson(`/api/settings/providers/${encodeURIComponent(button.dataset.forgetProvider)}/forget`));
    flashAndReload({ title: `${name} settings forgotten`, body: `${plural(answer?.removedAccounts ?? 0, 'account')} removed · ${plural(answer?.shownModels ?? 0, 'model')} shown again.` });
  } catch (error) {
    showError('Could not forget the settings', error);
  }
}

async function copyText(button) {
  const target = document.querySelector(button.dataset.copy);
  const text = target ? (('value' in target && target.value) || target.textContent || '').trim() : '';
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
    showToast({ title: 'Copied', tone: 'ok', delay: 2000 });
  } catch {
    showToast({ title: 'Copy failed', body: 'Select the text and copy it instead.', tone: 'warn' });
  }
}

// The Providers page: search, the status pills, the auth kind select and "Show N more".
function initProviderFilters() {
  const root = document.querySelector('[data-provider-filters]');
  if (!root) return;
  const search = root.querySelector('[data-provider-search]');
  const auth = root.querySelector('[data-provider-auth]');
  let status = '';
  let expanded = false;
  const apply = () => {
    const text = search.value.trim().toLowerCase();
    const filtering = Boolean(text || status || auth.value);
    let shown = 0;
    document.querySelectorAll('[data-provider-row]').forEach((row) => {
      const match = (!text || row.dataset.search.includes(text)) &&
        (!status || row.dataset.state === status) &&
        (!auth.value || row.dataset.auth.split(' ').includes(auth.value));
      row.hidden = !match;
      const wrapper = row.closest('[data-provider-more]');
      if (match && (!wrapper || filtering || expanded)) shown++;
    });
    document.querySelectorAll('[data-provider-more]').forEach((wrapper) => setShown(wrapper, filtering || expanded));
    document.querySelectorAll('[data-provider-more-toggle]').forEach((toggle) => setShown(toggle, !filtering && !expanded));
    document.querySelectorAll('[data-provider-placeholder]').forEach((placeholder) => setShown(placeholder, !filtering));
    setShown(document.querySelector('[data-provider-no-match]'), filtering && shown === 0);
  };
  search.addEventListener('input', apply);
  auth.addEventListener('change', apply);
  root.querySelectorAll('[data-provider-filter]').forEach((pill) => {
    pill.addEventListener('click', () => {
      status = pill.dataset.providerFilter;
      root.querySelectorAll('[data-provider-filter]').forEach((other) => {
        other.classList.toggle('active', other === pill);
        other.setAttribute('aria-pressed', String(other === pill));
      });
      apply();
    });
  });
  document.querySelector('[data-provider-show-more]')?.addEventListener('click', () => {
    expanded = true;
    apply();
  });
}
