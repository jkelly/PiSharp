// Decision 0011 (run W4, R8): the shared preset modals, for the Presets page (presets.js) and the pages that host them (presets-host.js).
// #modal-save-preset (_SavePresetModal): its content is always server markup, swapped in whole (.modal-content) from POST
// /api/presets/preview (a page's request, captured with the page's own readers) or GET /presets/{id}/edit; Save posts the form's fields
// (name, description, tags, pin, the parts to include, update or new) to /api/presets or /api/presets/{id}. #modal-load-preset
// (_LoadPresetModal): refreshed from GET /presets/picker?page= each time it opens; search and the tag chips filter the list, a click
// previews a preset, and Load hands its id to the page. Text the user typed is only ever read from inputs and shown with textContent.
import { showToast, confirmAction } from './shell.js';
import { postRequest, RequestError } from './request-api.js';

const $ = (selector, root = document) => root.querySelector(selector);

/** The Bootstrap modal of element `id`, or null. */
export function modalOf(id) {
  const element = document.getElementById(id);
  return element && window.bootstrap ? window.bootstrap.Modal.getOrCreateInstance(element) : null;
}

/** The first element of server-rendered `html`. */
export function fragmentOf(html) {
  const template = document.createElement('template');
  template.innerHTML = (html ?? '').trim();
  return template.content.firstElementChild;
}

/** Replaces `modal`'s .modal-content with the one in `fresh` (a whole modal the server rendered). Returns the new content. */
export function swapContent(modal, fresh) {
  const content = fresh?.matches?.('.modal-content') ? fresh : fresh?.querySelector?.('.modal-content');
  const current = modal?.querySelector('.modal-content');
  if (!content || !current) return current ?? null;
  current.replaceWith(content);
  return content;
}

/** Hides `modal` and resolves once it is hidden (so another modal can open cleanly). */
export function hideModal(modal) {
  return new Promise((resolve) => {
    if (!modal || !modal.classList.contains('show') || !window.bootstrap) {
      resolve();
      return;
    }
    modal.addEventListener('hidden.bs.modal', () => resolve(), { once: true });
    window.bootstrap.Modal.getOrCreateInstance(modal).hide();
  });
}

function tagsOf(text) {
  return (text ?? '').split(',').map((tag) => tag.trim()).filter((tag) => tag.length > 0);
}

/** The save form's fields: { name, description, tags, pinned, include, target }. */
export function formFields(form) {
  const value = (name) => form.elements.namedItem(name);
  const include = [...form.querySelectorAll('input[name="include"]')].filter((box) => box.checked && !box.disabled).map((box) => box.value);
  const target = form.querySelector('input[name="target"]:checked')?.value ?? 'new';
  return {
    name: value('name')?.value.trim() ?? '',
    description: value('description')?.value ?? '',
    tags: tagsOf(value('tags')?.value),
    pinned: value('pinned')?.checked === true,
    include,
    target,
  };
}

function showError(form, message) {
  const box = $('[data-preset-save-error]', form);
  if (!box) {
    showToast({ title: 'Preset not saved', body: message, tone: 'danger' });
    return;
  }
  box.textContent = message;
  box.hidden = !message;
}

function addTag(form, tag) {
  const input = form.elements.namedItem('tags');
  if (!input) return;
  const tags = tagsOf(input.value);
  if (!tags.some((item) => item.toLowerCase() === tag.toLowerCase())) tags.push(tag);
  input.value = tags.join(', ');
  input.focus();
}

/** Asks, then deletes preset `id`; resolves to whether it was deleted. */
export async function deletePreset(id, name, uses) {
  const ok = await confirmAction({
    title: 'Delete preset?',
    body: `“${name}” will be permanently deleted. Runs that used it stay in History, and JSON copies you exported aren't affected. This can't be undone.`,
    detail: `preset id ${id} · used ${uses ?? 0}×`,
    ok: 'Delete preset',
    icon: 'bi-trash3',
  });
  if (!ok) return false;
  try {
    await postRequest(`/api/presets/${encodeURIComponent(id)}/delete`, {});
    showToast({ title: 'Preset deleted', body: `“${name}” was deleted.`, tone: 'ok' });
    return true;
  } catch (error) {
    showToast({ title: 'Preset not deleted', body: error.message, tone: 'danger' });
    return false;
  }
}

/**
 * Wires #modal-save-preset once (its content is swapped, so every handler is delegated on the modal element).
 * `context()` returns what the open pane saves: { page, capture } for a page's request (null on the Presets page). `onSaved(preset,
 * created)` runs after a save; `onDeleted(id)` after Delete in the edit pane.
 */
export function initSaveModal({ context = () => null, onSaved = () => {}, onDeleted = () => {} } = {}) {
  const modal = document.getElementById('modal-save-preset');
  if (!modal || modal.dataset.presetWired) return modal;
  modal.dataset.presetWired = 'true';
  let busy = false;

  modal.addEventListener('click', async (event) => {
    const tag = event.target.closest('[data-preset-tag]');
    const form = $('[data-preset-save-form]', modal);
    if (tag && form) {
      addTag(form, tag.dataset.presetTag);
      return;
    }
    const remove = event.target.closest('[data-preset-delete]');
    if (remove && form?.dataset.presetId) {
      const id = form.dataset.presetId;
      const name = form.elements.namedItem('name')?.defaultValue || id;
      await hideModal(modal);
      if (await deletePreset(id, name, form.dataset.uses)) onDeleted(id);
    }
  });

  modal.addEventListener('submit', async (event) => {
    const form = event.target.closest('[data-preset-save-form]');
    if (!form) return;
    event.preventDefault();
    if (busy) return;
    const fields = formFields(form);
    const nameInput = form.elements.namedItem('name');
    if (!fields.name) {
      nameInput?.classList.add('is-invalid');
      nameInput?.focus();
      return;
    }
    nameInput?.classList.remove('is-invalid');
    showError(form, '');
    const mode = form.dataset.mode;
    const body = { name: fields.name, description: fields.description, tags: fields.tags, pinned: fields.pinned };
    let url = '/api/presets';
    let updating = false;
    if (mode === 'edit') {
      url = `/api/presets/${encodeURIComponent(form.dataset.presetId)}`;
      body.include = fields.include;
      updating = true;
    } else if (form.dataset.page === 'presets') {
      const picker = $('[data-provider-model]', form);
      const provider = $('[data-provider-select]', picker)?.value ?? '';
      const pane = picker ? [...picker.querySelectorAll('[data-provider]')].find((element) => element.dataset.provider === provider) : null;
      const model = $('[data-model-select]', pane ?? form)?.value ?? '';
      body.page = 'presets';
      body.capture = { provider, model, systemPrompt: form.elements.namedItem('systemPrompt')?.value ?? '' };
    } else {
      const current = context();
      if (!current?.capture) {
        showError(form, 'The page has nothing to save yet.');
        return;
      }
      body.page = current.page;
      body.capture = current.capture;
      body.include = fields.include;
      if (fields.target === 'update' && form.dataset.origin) {
        body.update = form.dataset.origin;
        updating = true;
      }
    }
    busy = true;
    const button = $('[data-preset-save-submit]', form);
    if (button) button.disabled = true;
    try {
      const answer = await postRequest(url, body);
      const preset = answer?.preset;
      window.bootstrap?.Modal.getOrCreateInstance(modal).hide();
      showToast({ title: 'Preset saved', body: `“${preset?.name ?? fields.name}” was ${updating ? 'updated' : 'saved'}.`, tone: 'ok' });
      onSaved(preset, !updating);
    } catch (error) {
      showError(form, error instanceof RequestError || error instanceof Error ? error.message : String(error));
    } finally {
      busy = false;
      if (button) button.disabled = false;
    }
  });
  return modal;
}

/** Fills #modal-save-preset from POST /api/presets/preview for `page`'s `capture` (and `origin`, the preset the page loaded) and shows it. */
export async function openSaveModal({ page, capture, origin }) {
  const modal = document.getElementById('modal-save-preset');
  if (!modal) return;
  const instance = window.bootstrap?.Modal.getOrCreateInstance(modal);
  instance?.show();
  try {
    const answer = await postRequest('/api/presets/preview', { page, capture, origin: origin ?? undefined });
    swapContent(modal, fragmentOf(answer?.html));
    modal.querySelector('[data-preset-save-form] input[name="name"]')?.focus();
  } catch (error) {
    instance?.hide();
    showToast({ title: 'Preset not saved', body: error.message, tone: 'danger' });
  }
}

/** Shows the edit pane of preset `id` (GET /presets/{id}/edit). */
export async function openEditModal(id) {
  const modal = document.getElementById('modal-save-preset');
  if (!modal) return;
  let html;
  try {
    const response = await fetch(`/presets/${encodeURIComponent(id)}/edit`, { headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) throw new Error(response.status === 404 ? 'This preset was deleted.' : `The preset could not be opened (${response.status}).`);
    html = await response.text();
  } catch (error) {
    showToast({ title: 'Preset not opened', body: error.message, tone: 'danger' });
    return;
  }
  const content = swapContent(modal, fragmentOf(html));
  const form = content?.matches('[data-preset-save-form]') ? content : $('[data-preset-save-form]', content ?? modal);
  window.bootstrap?.Modal.getOrCreateInstance(modal).show();
  form?.elements.namedItem('name')?.focus();
}

// ---- the load modal -------------------------------------------------------------------------------------------------------------

function select(modal, item) {
  modal.querySelectorAll('[data-lp-item]').forEach((other) => {
    const on = other === item;
    other.classList.toggle('active', on);
    if (on) other.setAttribute('aria-current', 'true');
    else other.removeAttribute('aria-current');
  });
  modal.querySelectorAll('[data-lp-pane]').forEach((pane) => { pane.hidden = pane.dataset.lpPane !== item?.dataset.lpItem; });
  const load = $('[data-lp-load]', modal);
  if (load) {
    const ok = item?.dataset.lpOk === 'true';
    load.disabled = !item || !ok;
    if (item && !ok && item.dataset.lpProblem) load.title = item.dataset.lpProblem;
    else load.removeAttribute('title');
  }
}

function filter(modal) {
  const text = ($('[data-lp-search]', modal)?.value ?? '').trim().toLowerCase();
  const tag = $('[data-lp-tag][aria-pressed="true"]', modal)?.dataset.lpTag ?? '';
  let firstShown = null;
  let selectedShown = false;
  modal.querySelectorAll('[data-lp-item]').forEach((item) => {
    const tags = (item.dataset.lpTags ?? '').split('|');
    const shown = (!text || (item.dataset.lpText ?? '').includes(text)) && (!tag || tags.includes(tag.toLowerCase()));
    item.hidden = !shown;
    if (shown && !firstShown) firstShown = item;
    if (shown && item.classList.contains('active')) selectedShown = true;
  });
  const none = $('[data-lp-none]', modal);
  if (none) none.hidden = firstShown !== null;
  if (!selectedShown) select(modal, firstShown);
}

/** Wires #modal-load-preset once for `page`: it refreshes when it opens, and Load calls `onLoad(id)`. */
export function initLoadModal({ page, onLoad }) {
  const modal = document.getElementById('modal-load-preset');
  if (!modal || modal.dataset.presetWired) return modal;
  modal.dataset.presetWired = 'true';
  modal.addEventListener('show.bs.modal', async () => {
    try {
      const response = await fetch(`/presets/picker?page=${encodeURIComponent(page)}`, { headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) throw new Error(`The presets could not be listed (${response.status}).`);
      swapContent(modal, fragmentOf(await response.text()));
    } catch (error) {
      showToast({ title: 'Presets not listed', body: error.message, tone: 'danger' });
    }
  });
  modal.addEventListener('shown.bs.modal', () => $('[data-lp-search]', modal)?.focus());
  modal.addEventListener('input', (event) => {
    if (event.target.matches('[data-lp-search]')) filter(modal);
  });
  modal.addEventListener('click', (event) => {
    const tag = event.target.closest('[data-lp-tag]');
    if (tag) {
      modal.querySelectorAll('[data-lp-tag]').forEach((chip) => {
        const on = chip === tag;
        chip.setAttribute('aria-pressed', on ? 'true' : 'false');
        if (on) chip.dataset.tone = 'accent';
        else delete chip.dataset.tone;
      });
      filter(modal);
      return;
    }
    const item = event.target.closest('[data-lp-item]');
    if (item) {
      select(modal, item);
      return;
    }
    const load = event.target.closest('[data-lp-load]');
    if (load && !load.disabled) {
      const chosen = $('[data-lp-item].active', modal);
      if (!chosen) return;
      hideModal(modal).then(() => onLoad(chosen.dataset.lpItem));
    }
  });
  modal.addEventListener('dblclick', (event) => {
    const item = event.target.closest('[data-lp-item]');
    if (!item || item.dataset.lpOk !== 'true') return;
    hideModal(modal).then(() => onLoad(item.dataset.lpItem));
  });
  return modal;
}
