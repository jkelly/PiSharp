// Decision 0011 (run W4, R8): the Presets page (GET /presets). The filters (search, used in, sort, tags) swap in GET /presets/grid and keep
// the query in the address bar; New preset opens the shared save modal's new pane (a model and a system prompt to start from); a card's
// menu edits (the modal's edit pane, GET /presets/{id}/edit), duplicates, exports (a download of /api/presets/{id}/export) or deletes it
// (a confirm). Import JSON reads a file in the browser and posts its text to /api/presets/import, which checks it whole before saving
// anything; Export all downloads /api/presets/export. After a change the page's content is fetched again from the server (so the count,
// the tags and the empty state follow), and every value shown is server markup or set with textContent.
import { showToast } from './shell.js';
import { postRequest } from './request-api.js';
import { initSaveModal, openEditModal, deletePreset, swapContent, modalOf } from './presets-modals.js';

const MAX_FILE = 32 * 1024 * 1024;
const $ = (selector, root = document) => root.querySelector(selector);
const pageRoot = () => $('[data-presets]');
let newPane = null;
let gridRequest = null;
let filterTimer = 0;

function toast(title, body, tone = 'ok', delay) {
  showToast({ title, body, tone, delay });
}

function filterQuery() {
  const form = $('[data-preset-filters]');
  if (!form) return '';
  const query = new URLSearchParams();
  for (const [name, value] of new FormData(form)) {
    if (typeof value === 'string' && value.trim() !== '') query.append(name, value.trim());
  }
  if (query.get('sort') === 'recent') query.delete('sort');
  return query.toString();
}

// The grid for the filters, from the server; the count line follows (data-count).
async function refreshGrid() {
  const results = $('[data-preset-results]');
  if (!results) return;
  const query = filterQuery();
  gridRequest?.abort();
  const request = new AbortController();
  gridRequest = request;
  let html;
  try {
    const response = await fetch('/presets/grid' + (query ? '?' + query : ''), {
      headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store', signal: request.signal,
    });
    if (!response.ok) throw new Error(`The presets could not be listed (${response.status}).`);
    html = await response.text();
  } catch (error) {
    if (error?.name !== 'AbortError') toast('Presets not listed', error.message, 'danger');
    return;
  }
  if (gridRequest !== request) return;
  const template = document.createElement('template');
  template.innerHTML = html.trim();
  const grid = template.content.querySelector('[data-preset-grid]');
  if (!grid) return;
  results.replaceChildren(grid);
  const count = $('[data-preset-count]');
  if (count) count.textContent = grid.dataset.count ?? '';
  window.history.replaceState(null, '', '/presets' + (query ? '?' + query : ''));
}

// The page's content again (after a save, an import, a duplicate or a delete), for the same filters.
async function refreshPage() {
  const root = pageRoot();
  if (!root) return;
  let html;
  try {
    const response = await fetch(window.location.pathname + window.location.search, {
      headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store',
    });
    if (!response.ok) throw new Error(`The page could not be refreshed (${response.status}).`);
    html = await response.text();
  } catch {
    window.location.reload();
    return;
  }
  const fresh = new DOMParser().parseFromString(html, 'text/html').querySelector('[data-presets]');
  if (!fresh) {
    window.location.reload();
    return;
  }
  root.replaceWith(document.adoptNode(fresh));
}

function cardOf(element) {
  const card = element.closest('[data-preset-card]');
  return card ? { id: card.dataset.presetCard, name: card.dataset.presetName, uses: card.dataset.presetUses } : null;
}

async function duplicate(card) {
  try {
    const answer = await postRequest(`/api/presets/${encodeURIComponent(card.id)}/duplicate`, {});
    toast('Preset duplicated', `“${card.name}” was copied as “${answer?.preset?.name ?? 'a new preset'}”.`);
    await refreshPage();
  } catch (error) {
    toast('Preset not duplicated', error.message, 'danger');
  }
}

function plural(count, noun) {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}

async function importFile(input) {
  const file = input.files?.[0];
  input.value = '';
  if (!file) return;
  if (file.size > MAX_FILE) {
    toast('Nothing imported', `${file.name} is larger than 32 MB.`, 'danger');
    return;
  }
  let text;
  try {
    text = await file.text();
  } catch {
    toast('Nothing imported', `${file.name} could not be read.`, 'danger');
    return;
  }
  let answer;
  try {
    answer = await postRequest('/api/presets/import', { text });
  } catch (error) {
    toast('Nothing imported', `${error.message} No preset was saved.`, 'danger', 12000);
    return;
  }
  const imported = answer?.imported ?? [];
  const skipped = (answer?.skipped ?? []).length;
  const renamed = answer?.renamed ?? [];
  const lines = [];
  if (imported.length > 0) lines.push(`${plural(imported.length, 'preset')} imported from ${file.name}.`);
  if (skipped > 0) lines.push(`${plural(skipped, 'preset')} ${skipped === 1 ? 'was' : 'were'} already here unchanged.`);
  if (renamed.length > 0) {
    lines.push(`${plural(renamed.length, 'id')} ${renamed.length === 1 ? 'was' : 'were'} taken by a different preset, so ${renamed.length === 1 ? 'it was' : 'they were'} saved under a new id (${renamed.map((pair) => `${pair.from} → ${pair.to}`).join(', ')}).`);
  }
  if (imported.length === 0) {
    toast('Nothing new to import', lines.join(' ') || `${file.name} holds no presets.`, 'info');
    return;
  }
  toast('Presets imported', lines.join(' '), renamed.length > 0 ? 'info' : 'ok', renamed.length > 0 ? 10000 : undefined);
  await refreshPage();
}

function openNew() {
  const modal = document.getElementById('modal-save-preset');
  if (!modal || !newPane) return;
  // The edit pane may have replaced the new pane: put a fresh copy of the page's own back.
  swapContent(modal, newPane.cloneNode(true));
  modalOf('modal-save-preset')?.show();
  modal.addEventListener('shown.bs.modal', () => $('#sp-name', modal)?.focus(), { once: true });
}

function start() {
  if (!pageRoot()) return;
  const modal = document.getElementById('modal-save-preset');
  const content = modal?.querySelector('.modal-content');
  if (content) newPane = content.cloneNode(true);
  initSaveModal({ onSaved: () => refreshPage(), onDeleted: () => refreshPage() });

  document.addEventListener('click', (event) => {
    if (!(event.target instanceof Element) || !event.target.closest('[data-presets]')) return;
    const target = event.target.closest('button, a');
    if (!target || target.disabled) return;
    const card = cardOf(target);
    if (target.matches('[data-preset-new]')) {
      openNew();
    } else if (target.matches('[data-preset-edit]') && card) {
      openEditModal(card.id);
    } else if (target.matches('[data-preset-duplicate]') && card) {
      duplicate(card);
    } else if (target.matches('[data-preset-delete]') && card) {
      deletePreset(card.id, card.name, card.uses).then((deleted) => { if (deleted) refreshPage(); });
    } else if (target.matches('[data-preset-export]') && card) {
      toast('Preset exported', `“${card.name}” downloads as ${card.id}.preset.json, with no API key, token or account credential.`);
    } else if (target.matches('[data-preset-export-all]')) {
      const total = Number(pageRoot()?.dataset.total || 0);
      toast('All presets exported', `${plural(total, 'preset')} in one JSON file, with no API key, token or account credential.`);
    }
  });

  document.addEventListener('change', (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    if (target.matches('[data-preset-import]')) importFile(target);
    // The search box refreshes as it is typed in (below); its change on blur would re-render the grid under an open menu.
    else if (target.closest('[data-preset-filters]') && !target.matches('input[type="search"]')) refreshGrid();
  });

  document.addEventListener('input', (event) => {
    const target = event.target;
    if (!(target instanceof Element) || !target.matches('[data-preset-filters] input[type="search"]')) return;
    window.clearTimeout(filterTimer);
    filterTimer = window.setTimeout(refreshGrid, 250);
  });

  document.addEventListener('submit', (event) => {
    if (event.target instanceof Element && event.target.matches('[data-preset-filters]')) {
      event.preventDefault();
      refreshGrid();
    }
  });
}

start();
