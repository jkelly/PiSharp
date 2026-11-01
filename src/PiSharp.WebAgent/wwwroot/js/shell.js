// Decision 0011 (run W2, WU4): the shell's behaviour on every page: the theme toggle (Web.Common's theme.js), toasts (_Toasts), the confirm
// dialog (_ConfirmModal), the linked Provider -> Model dropdowns (_ProviderModelSelect) and the global keyboard shortcuts (_ShortcutsModal).
// Page scripts import what they need from here; site.js calls initShell() once. Everything is delegated from document, so markup swapped in
// later (a modal's body, a stream fragment) works without wiring. Text is always set with textContent, never as HTML.
// Run W3, unit AP: the theme is Settings › Appearance's (appearance.js in <head>, window.PiSharpAppearance), which can also follow the
// system. The toggle and Ctrl+Shift+L switch between dark and light through it, so the choice is the same one the Appearance page shows;
// the toggle icons follow the theme on the page whatever changed it (the "pisharp:appearance" event). Without appearance.js the toggle
// falls back to Web.Common's dark/light theme.
import { applyTheme, currentTheme } from '/_content/PiSharp.Web.Common/js/theme.js';

const TONES = ['ok', 'warn', 'danger', 'info'];
let started = false;

/** Switches the theme between dark and light (the top bar's moon, Ctrl+Shift+L) and remembers it in this browser; returns the theme shown.
 * With "System" chosen in Settings › Appearance, it switches away from what the system shows, and the choice becomes that theme. */
export function toggleTheme() {
  const appearance = window.PiSharpAppearance;
  if (appearance) return appearance.toggleTheme();
  return applyTheme(currentTheme() === 'dark' ? 'light' : 'dark', { store: true });
}

// The toggle, and its icons kept in step with the theme on the page (applyTheme with the current theme only updates the icons).
function wireTheme() {
  applyTheme(currentTheme());
  document.addEventListener('pisharp:appearance', () => applyTheme(currentTheme()));
  document.addEventListener('click', (event) => {
    if (event.target.closest('[data-theme-toggle]')) toggleTheme();
  });
}

/** Shows a toast in #toasts. `tone` is 'ok', 'warn', 'danger' or 'info'; danger toasts stay longer. Returns the toast element. */
export function showToast({ title, body = '', tone = 'ok', delay } = {}) {
  const host = document.getElementById('toasts');
  const template = document.getElementById('toast-template');
  if (!host || !template) return null;
  const el = template.content.firstElementChild.cloneNode(true);
  const dot = el.querySelector('[data-toast-dot]');
  dot.classList.remove(...TONES);
  dot.classList.add(TONES.includes(tone) ? tone : 'info');
  el.querySelector('[data-toast-title]').textContent = title ?? '';
  const bodyEl = el.querySelector('[data-toast-body]');
  if (body) bodyEl.textContent = body; else bodyEl.remove();
  host.append(el);
  el.addEventListener('hidden.bs.toast', () => el.remove(), { once: true });
  if (window.bootstrap) window.bootstrap.Toast.getOrCreateInstance(el, { delay: delay ?? (tone === 'danger' ? 8000 : 5000) }).show();
  else el.classList.add('show');
  return el;
}

/**
 * Asks in #modal-confirm. Resolves true when the user presses OK, false when the dialog closes any other way.
 * `tone` is 'danger' (default: a red OK button and warning icon) or 'primary'.
 */
export function confirmAction({ title = 'Are you sure?', body = '', detail = '', ok = 'Confirm', tone = 'danger', icon } = {}) {
  const modalEl = document.getElementById('modal-confirm');
  if (!modalEl || !window.bootstrap) return Promise.resolve(window.confirm([title, body].filter(Boolean).join('\n\n')));
  const danger = tone !== 'primary';
  modalEl.querySelector('[data-confirm-title]').textContent = title;
  const bodyEl = modalEl.querySelector('[data-confirm-body]');
  bodyEl.textContent = body;
  bodyEl.hidden = !body;
  const detailEl = modalEl.querySelector('[data-confirm-detail]');
  detailEl.textContent = detail;
  detailEl.hidden = !detail;
  const iconEl = modalEl.querySelector('[data-confirm-icon]');
  iconEl.classList.toggle('is-danger', danger);
  iconEl.classList.toggle('is-primary', !danger);
  iconEl.querySelector('.bi').className = 'bi ' + (icon || (danger ? 'bi-exclamation-triangle' : 'bi-question-circle'));
  const okButton = modalEl.querySelector('[data-confirm-ok]');
  okButton.textContent = ok;
  okButton.className = 'btn ' + (danger ? 'btn-danger' : 'btn-primary');
  const modal = window.bootstrap.Modal.getOrCreateInstance(modalEl);
  return new Promise((resolve) => {
    let answer = false;
    const onOk = () => { answer = true; modal.hide(); };
    okButton.addEventListener('click', onOk);
    modalEl.addEventListener('hidden.bs.modal', () => {
      okButton.removeEventListener('click', onOk);
      resolve(answer);
    }, { once: true });
    modal.show();
  });
}

/** The current choice of a _ProviderModelSelect (its root, or any element inside it): provider, model and the model's thinking levels. */
export function providerModelValue(element) {
  const root = element?.closest ? element.closest('[data-provider-model]') : null;
  if (!root) return null;
  const provider = root.querySelector('[data-provider-select]')?.value ?? null;
  const pane = provider ? paneOf(root, provider) : null;
  const modelSelect = pane?.querySelector('[data-model-select]');
  const option = modelSelect?.selectedOptions[0];
  return {
    provider,
    model: modelSelect?.value ?? null,
    thinking: (option?.dataset.thinking ?? '').split(' ').filter(Boolean),
    summary: option?.dataset.summary ?? '',
  };
}

function paneOf(root, provider) {
  return [...root.querySelectorAll('[data-provider]')].find((pane) => pane.dataset.provider === provider) ?? null;
}

function showProvider(root, provider) {
  root.querySelectorAll('[data-provider]').forEach((pane) => {
    const active = pane.dataset.provider === provider;
    pane.hidden = !active;
    const select = pane.querySelector('[data-model-select]');
    if (select) select.disabled = !active;
  });
}

function updateSummary(modelSelect) {
  const pane = modelSelect.closest('[data-provider]');
  const summary = pane?.querySelector('[data-model-summary]');
  if (summary) summary.textContent = modelSelect.selectedOptions[0]?.dataset.summary ?? '';
}

function onChange(event) {
  const target = event.target;
  if (!(target instanceof HTMLSelectElement)) return;
  const root = target.closest('[data-provider-model]');
  if (!root) return;
  if (target.matches('[data-provider-select]')) showProvider(root, target.value);
  else if (target.matches('[data-model-select]')) updateSummary(target);
  else return;
  // One event for page scripts (a thinking control, a model chip), whichever list changed.
  root.dispatchEvent(new CustomEvent('provider-model-change', { bubbles: true, detail: providerModelValue(root) }));
}

// A form with data-confirm (or a submit button with it) asks first. The answer re-submits the form with the same submitter.
async function onSubmit(event) {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) return;
  const submitter = event.submitter;
  const source = submitter?.hasAttribute('data-confirm') ? submitter : form.hasAttribute('data-confirm') ? form : null;
  if (!source || form.dataset.confirmed === 'true') {
    delete form.dataset.confirmed;
    return;
  }
  event.preventDefault();
  const data = source.dataset;
  const confirmed = await confirmAction({
    title: data.confirm || 'Are you sure?',
    body: data.confirmBody,
    detail: data.confirmDetail,
    ok: data.confirmOk,
    tone: data.confirmTone,
  });
  if (!confirmed) return;
  form.dataset.confirmed = 'true';
  form.requestSubmit(submitter ?? undefined);
}

function typing(target) {
  return target instanceof HTMLElement && (target.isContentEditable || target.closest('input, textarea, select, [contenteditable="true"]') !== null);
}

function showModal(id) {
  const el = document.getElementById(id);
  if (!el || !window.bootstrap) return false;
  window.bootstrap.Modal.getOrCreateInstance(el).show();
  return true;
}

function onKeydown(event) {
  if (event.defaultPrevented || event.isComposing) return;
  const key = event.key.toLowerCase();
  const ctrl = event.ctrlKey || event.metaKey;
  if (ctrl && !event.shiftKey && !event.altKey && key === 'k') {
    // The model picker is the chat page's (#modal-model-picker); elsewhere Ctrl+K is left alone.
    if (showModal('modal-model-picker')) event.preventDefault();
  } else if (ctrl && event.shiftKey && !event.altKey && key === 'o') {
    event.preventDefault();
    window.location.assign('/chat/new');
  } else if (ctrl && event.shiftKey && !event.altKey && key === 'l') {
    event.preventDefault();
    toggleTheme();
  } else if (event.key === '?' && !ctrl && !event.altKey && !typing(event.target) && !document.querySelector('.modal.show')) {
    if (showModal('modal-shortcuts')) event.preventDefault();
  }
}

// Below 992px the settings nav is one scrolling row; bring the current page's item into view (only the row scrolls, not the page).
function revealSettingsItem() {
  const nav = document.querySelector('.settings-nav');
  const active = nav?.querySelector('a.active');
  if (!active || nav.scrollWidth <= nav.clientWidth) return;
  nav.scrollLeft = active.offsetLeft - nav.offsetLeft - (nav.clientWidth - active.offsetWidth) / 2;
}

// A closing modal sets aria-hidden while the button that closed it (the model picker's Use model) still has focus, which the browser blocks
// with a warning (W5 live test, Compare and Tool Bench). Bootstrap's events bubble, so one listener lets go of the focus first; Bootstrap
// then returns it to the element that opened the modal.
function releaseFocus(event) {
  const focused = document.activeElement;
  if (event.target instanceof HTMLElement && focused instanceof HTMLElement && event.target.contains(focused)) focused.blur();
}

/** Wires the shell once per page: theme, Provider -> Model dropdowns, confirm forms, global shortcuts and modal focus. */
export function initShell() {
  if (started) return;
  started = true;
  wireTheme();
  revealSettingsItem();
  document.addEventListener('change', onChange);
  document.addEventListener('submit', onSubmit);
  document.addEventListener('keydown', onKeydown);
  document.addEventListener('hide.bs.modal', releaseFocus);
}
