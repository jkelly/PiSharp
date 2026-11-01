// Decision 0011 (run W3, S4): History › Spend. The filters are a GET form: a change applies at once (a new provider clears the model),
// and empty fields are left out of the address. Rows per page keeps the filters. Export CSV downloads the matching rows and says so.
// Resolve… fetches the row's Resolve modal (GET /history/spend/resolve?op=, a fragment of the server's own markup), swaps it in and posts the
// choice to /api/spend/resolve; the page then reloads with the resolved toast. ?resolve=op opens it when the page loads. Saved filters (W3
// fix, spend-ui): Save current filters… posts {name, query} to /api/spend/filters; Manage saved filters marks rows removed (with Undo),
// keeps renamed ones, and posts the rest in order to /api/spend/filters/manage; both reload with the server's toast, and closing the
// Manage modal puts its rows back. Text is set with textContent only.
import { postJson, flashAndReload, showFlash, setText, busy } from './settings-api.js';
import { showToast } from './shell.js';

const root = document.querySelector('[data-spend-history]');

function initFilters() {
  const form = document.querySelector('form[data-spend-filters]');
  if (!form) return;
  form.addEventListener('submit', () => {
    for (const el of form.elements) {
      if (el.name && !el.disabled && el.value === '') el.disabled = true;
    }
  });
  form.querySelectorAll('[data-spend-auto]').forEach((el) => el.addEventListener('change', () => {
    if (el.matches('[data-spend-provider]')) {
      const model = form.querySelector('[data-spend-model]');
      if (model) model.value = '';
    }
    form.requestSubmit();
  }));
  document.querySelector('[data-spend-size]')?.addEventListener('change', (event) => {
    const url = new URL(window.location.href);
    url.searchParams.set('size', event.target.value);
    url.searchParams.delete('page');
    url.searchParams.delete('resolve');
    window.location.assign(url.toString());
  });
  document.querySelector('[data-spend-export]')?.addEventListener('click', (event) => {
    showToast({ title: 'Export started', body: `${event.currentTarget.dataset.file} · requests matching the current filters.` });
  });
}

function modalEl() {
  return document.getElementById('modal-resolve-reservation');
}

function addressWithout(name) {
  const url = new URL(window.location.href);
  url.searchParams.delete(name);
  return url.pathname + url.search;
}

function wireResolve(content) {
  const error = content.querySelector('[data-rr-error]');
  const show = (choice) => content.querySelectorAll('[data-rr-pane]').forEach((el) => { el.hidden = el.dataset.rrPane !== choice; });
  content.querySelectorAll('input[name="rrChoice"]').forEach((radio) => radio.addEventListener('change', () => {
    show(radio.value);
    if (error) error.hidden = true;
  }));
  show(content.querySelector('input[name="rrChoice"]:checked')?.value ?? 'estimate');
  content.addEventListener('submit', async (event) => {
    event.preventDefault();
    const choice = content.querySelector('input[name="rrChoice"]:checked')?.value ?? 'estimate';
    const body = { op: content.dataset.op, choice };
    if (choice === 'amount') body.amount = content.querySelector('[data-rr-amount]')?.value ?? '';
    const button = [...content.querySelectorAll('[data-rr-submit]')].find((el) => !el.hidden);
    await busy(button, async () => {
      try {
        const answer = await postJson('/api/spend/resolve', body);
        window.bootstrap?.Modal.getOrCreateInstance(modalEl()).hide();
        flashAndReload(answer?.toast ?? { title: 'Reservation resolved', body: '' }, addressWithout('resolve'));
      } catch (problem) {
        setText(error, problem instanceof Error ? problem.message : String(problem));
        if (error) error.hidden = false;
      }
    });
  });
}

async function openResolve(op) {
  const host = modalEl();
  if (!host || !window.bootstrap) return;
  let response;
  try {
    response = await fetch(`/history/spend/resolve?op=${encodeURIComponent(op)}`, { headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store' });
  } catch {
    showToast({ title: "Can't resolve it now", body: 'The app could not be reached. Is it still running?', tone: 'danger' });
    return;
  }
  const text = await response.text();
  if (!response.ok) {
    showToast({ title: "Can't resolve it now", body: text || `The request failed (${response.status}).`, tone: 'danger' });
    return;
  }
  const fresh = new DOMParser().parseFromString(text, 'text/html').querySelector('[data-resolve-content]');
  if (!fresh) return;
  const content = document.importNode(fresh, true);
  host.querySelector('[data-resolve-content]').replaceWith(content);
  wireResolve(content);
  window.bootstrap.Modal.getOrCreateInstance(host).show();
}

function initResolve() {
  document.addEventListener('click', (event) => {
    const button = event.target.closest('[data-resolve-op]');
    if (!button) return;
    event.preventDefault();
    openResolve(button.dataset.resolveOp);
  });
  const content = modalEl()?.querySelector('[data-resolve-content]');
  if (content?.dataset.open === 'true' && window.bootstrap) {
    wireResolve(content);
    window.bootstrap.Modal.getOrCreateInstance(modalEl()).show();
  }
}

function failWith(error, problem) {
  setText(error, problem instanceof Error ? problem.message : String(problem));
  if (error) error.hidden = false;
}

function hideModal(element) {
  const modal = element?.closest('.modal');
  if (modal) window.bootstrap?.Modal.getOrCreateInstance(modal).hide();
}

function initSaveFilter() {
  const form = document.querySelector('form[data-save-filter]');
  if (!form) return;
  const error = form.querySelector('[data-sf-error]');
  const name = form.querySelector('[data-sf-name]');
  const modal = form.closest('.modal');
  modal?.addEventListener('shown.bs.modal', () => name?.select());
  modal?.addEventListener('hidden.bs.modal', () => { if (error) error.hidden = true; });
  name?.addEventListener('input', () => { if (error) error.hidden = true; });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const text = name?.value.trim() ?? '';
    if (!text) {
      failWith(error, 'Give the filter a name.');
      name?.focus();
      return;
    }
    await busy(form.querySelector('[data-sf-submit]'), async () => {
      try {
        const answer = await postJson('/api/spend/filters', { name: text, query: form.dataset.query ?? '' });
        hideModal(form);
        flashAndReload(answer?.toast ?? { title: 'Filter saved', body: text }, addressWithout('resolve'));
      } catch (problem) {
        failWith(error, problem);
      }
    });
  });
}

function setRemoved(row, removed) {
  row.dataset.removed = removed ? 'true' : '';
  row.classList.toggle('opacity-50', removed);
  const input = row.querySelector('[data-mf-name]');
  if (input) input.disabled = removed;
  const button = row.querySelector('[data-mf-remove]');
  if (button) {
    button.setAttribute('aria-pressed', removed ? 'true' : 'false');
    button.hidden = removed;
  }
  const note = row.querySelector('[data-mf-removed]');
  if (note) note.hidden = !removed;
}

function initManageFilters() {
  const form = document.querySelector('form[data-manage-filters]');
  if (!form) return;
  const error = form.querySelector('[data-mf-error]');
  const rows = () => [...form.querySelectorAll('[data-mf-row]')];
  form.addEventListener('click', (event) => {
    const remove = event.target.closest('[data-mf-remove]');
    const undo = event.target.closest('[data-mf-undo]');
    const row = (remove ?? undo)?.closest('[data-mf-row]');
    if (!row) return;
    setRemoved(row, Boolean(remove));
    (remove ? row.querySelector('[data-mf-undo]') : row.querySelector('[data-mf-name]'))?.focus();
    if (error) error.hidden = true;
  });
  form.addEventListener('input', () => { if (error) error.hidden = true; });
  form.closest('.modal')?.addEventListener('hidden.bs.modal', () => {
    for (const row of rows()) {
      setRemoved(row, false);
      const input = row.querySelector('[data-mf-name]');
      if (input) input.value = row.dataset.name ?? '';
    }
    if (error) error.hidden = true;
  });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const kept = rows().filter((row) => row.dataset.removed !== 'true');
    const blank = kept.find((row) => !(row.querySelector('[data-mf-name]')?.value.trim()));
    if (blank) {
      failWith(error, 'Give every saved filter a name, or remove it.');
      blank.querySelector('[data-mf-name]')?.focus();
      return;
    }
    const filters = kept.map((row) => ({ name: row.querySelector('[data-mf-name]').value.trim(), query: row.dataset.query ?? '' }));
    await busy(form.querySelector('[data-mf-submit]'), async () => {
      try {
        const answer = await postJson('/api/spend/filters/manage', { filters });
        hideModal(form);
        flashAndReload(answer?.toast ?? { title: 'Saved filters updated', body: '' }, addressWithout('resolve'));
      } catch (problem) {
        failWith(error, problem);
      }
    });
  });
}

if (root) {
  initFilters();
  initSaveFilter();
  initManageFilters();
  initResolve();
  showFlash();
}
