// Decision 0011 (run W4, R3): History › Runs (GET /history). The filters are a plain GET form: a change applies at once (empty fields are
// left out of the address, and a new provider clears the model). Checking rows shows _BulkActionBar (its count and the export links of
// the chosen runs); Delete confirms, then POST /api/runs/delete. Row actions (Re-run, Pin, Delete) are history-actions.js's. Clear
// history posts /api/history/clear; the saved filters post /api/history/filters and /api/history/filters/manage. Text is set with
// textContent only.
import { showToast } from './shell.js';
import { flashAndReload, showFlash } from './settings-api.js';
import { postRequest } from './request-api.js';
import { deleteRuns, failed, wireRunActions } from './history-actions.js';

const root = document.querySelector('[data-run-history]');
if (root) {
  showFlash();
  wireRunActions(root);
  wireFilters();
  wirePageSize();
  wireSelection();
  wireClear();
  wireSaveFilter();
  wireManageFilters();
  root.addEventListener('click', (event) => {
    const link = event.target.closest('[data-run-export]');
    if (link) showToast({ title: 'Export started', body: `${link.dataset.runExport} of ${root.dataset.state === 'filtered' ? 'the matching runs' : 'all runs'}.`, tone: 'info' });
  });
}

// ---- filters -------------------------------------------------------------------------------------------------------------------

function wireFilters() {
  const form = root.querySelector('[data-run-filters]');
  if (!form) return;
  const submit = () => {
    // Empty fields stay out of the address, so a link carries only the filters that are set.
    for (const field of form.elements) {
      if (field.name && !field.disabled && field.value === '') field.disabled = true;
    }
    form.submit();
  };
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    submit();
  });
  form.addEventListener('change', (event) => {
    const field = event.target.closest('[data-run-auto]');
    if (!field) return;
    if (field.matches('[data-run-provider]')) {
      const model = form.querySelector('[data-run-model]');
      if (model) model.disabled = true;
    }
    submit();
  });
}

function wirePageSize() {
  const select = root.querySelector('[data-run-size]');
  if (!select) return;
  select.addEventListener('change', () => {
    const url = new URL(window.location.href);
    url.searchParams.delete('page');
    if (select.value === '25') url.searchParams.delete('size');
    else url.searchParams.set('size', select.value);
    window.location.assign(url.pathname + url.search);
  });
}

// ---- selection and bulk actions ------------------------------------------------------------------------------------------------

function wireSelection() {
  const bar = root.querySelector('[data-bulk-bar]');
  const all = root.querySelector('[data-run-select-all]');
  if (!bar) return;
  const boxes = () => [...root.querySelectorAll('[data-run-select]')];
  const chosen = () => boxes().filter((box) => box.checked).map((box) => box.value);
  const update = () => {
    const ids = chosen();
    const count = ids.length;
    bar.hidden = count === 0;
    root.dataset.state = count > 0 ? 'selected' : root.dataset.baseState;
    bar.querySelector('[data-bulk-count]').textContent = String(count);
    for (const box of boxes()) box.closest('tr')?.classList.toggle('row-sel', box.checked);
    for (const link of bar.querySelectorAll('[data-bulk-export]')) {
      link.href = `/api/history/export?format=${encodeURIComponent(link.dataset.bulkExport)}&ids=${ids.map(encodeURIComponent).join(',')}`;
    }
    if (all) {
      const total = boxes().length;
      all.checked = total > 0 && count === total;
      all.indeterminate = count > 0 && count < total;
    }
  };
  root.dataset.baseState = root.dataset.state ?? 'default';
  root.addEventListener('change', (event) => {
    if (event.target.matches('[data-run-select-all]')) {
      for (const box of boxes()) box.checked = event.target.checked;
      update();
    } else if (event.target.matches('[data-run-select]')) {
      update();
    }
  });
  bar.querySelector('[data-bulk-clear]')?.addEventListener('click', () => {
    for (const box of boxes()) box.checked = false;
    update();
  });
  bar.querySelector('[data-bulk-delete]')?.addEventListener('click', () => deleteRuns(chosen()));
  bar.addEventListener('click', (event) => {
    const link = event.target.closest('[data-bulk-export]');
    if (link) showToast({ title: 'Export started', body: `${chosen().length} selected runs as ${link.dataset.bulkExport.toUpperCase()}.`, tone: 'info' });
  });
  update();
}

// ---- clear history -------------------------------------------------------------------------------------------------------------

function wireClear() {
  const modal = document.getElementById('modal-clear-history');
  if (!modal) return;
  const error = modal.querySelector('[data-clear-error]');
  const button = modal.querySelector('[data-clear-confirm]');
  button.addEventListener('click', async () => {
    error.hidden = true;
    button.disabled = true;
    const keepPinned = modal.querySelector('[data-clear-keep-pinned]')?.checked ?? true;
    try {
      const answer = await postRequest('/api/history/clear', { keepPinned });
      const deleted = Number(answer?.deleted ?? 0);
      flashAndReload({ title: 'History cleared', body: `${deleted.toLocaleString()} ${deleted === 1 ? 'run' : 'runs'} deleted${keepPinned ? '; pinned runs kept' : ''}.`, tone: 'ok' }, '/history');
    } catch (problem) {
      error.textContent = problem.message;
      error.hidden = false;
      button.disabled = false;
    }
  });
}

// ---- saved filters -------------------------------------------------------------------------------------------------------------

function wireSaveFilter() {
  const form = document.querySelector('[data-run-save-filter]');
  if (!form) return;
  const name = form.querySelector('[data-rsf-name]');
  const error = form.querySelector('[data-rsf-error]');
  form.closest('.modal')?.addEventListener('shown.bs.modal', () => name.focus());
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    error.hidden = true;
    const text = name.value.trim();
    if (!text) {
      error.textContent = 'Name the filter.';
      error.hidden = false;
      return;
    }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    try {
      await postRequest('/api/history/filters', { name: text, query: form.dataset.query ?? '' });
      flashAndReload({ title: 'Filter saved', body: `"${text}" is under Saved filters.`, tone: 'ok' });
    } catch (problem) {
      error.textContent = problem.message;
      error.hidden = false;
      submit.disabled = false;
    }
  });
}

function wireManageFilters() {
  const form = document.querySelector('[data-run-manage-filters]');
  if (!form) return;
  const error = form.querySelector('[data-rmf-error]');
  const mark = (row, removed) => {
    row.dataset.removed = removed ? 'true' : 'false';
    row.querySelector('[data-rmf-removed]').hidden = !removed;
    row.querySelector('[data-rmf-name]').disabled = removed;
    row.querySelector('[data-rmf-remove]').setAttribute('aria-pressed', removed ? 'true' : 'false');
  };
  form.addEventListener('click', (event) => {
    const remove = event.target.closest('[data-rmf-remove]');
    if (remove) mark(remove.closest('[data-rmf-row]'), true);
    const undo = event.target.closest('[data-rmf-undo]');
    if (undo) mark(undo.closest('[data-rmf-row]'), false);
  });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    error.hidden = true;
    const filters = [];
    for (const row of form.querySelectorAll('[data-rmf-row]')) {
      if (row.dataset.removed === 'true') continue;
      const name = row.querySelector('[data-rmf-name]').value.trim();
      if (!name) {
        error.textContent = 'Every saved filter needs a name.';
        error.hidden = false;
        return;
      }
      filters.push({ name, query: row.dataset.query ?? '' });
    }
    const submit = form.querySelector('[type="submit"]');
    submit.disabled = true;
    try {
      await postRequest('/api/history/filters/manage', { filters });
      flashAndReload({ title: 'Saved filters updated', body: `${filters.length} ${filters.length === 1 ? 'filter' : 'filters'} kept.`, tone: 'ok' });
    } catch (problem) {
      failed('Not saved', problem);
      error.textContent = problem.message;
      error.hidden = false;
      submit.disabled = false;
    }
  });
}
