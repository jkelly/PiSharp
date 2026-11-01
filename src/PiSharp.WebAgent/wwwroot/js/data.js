// Decision 0011 (run W4, unit R9): Settings › Data & storage (Views/Settings/Data.cshtml). Open folder posts a target (never a path) to
// /api/data/open; Run history tracks its three fields against the values it loaded with (the dirty state: .is-modified rows, "modified ·
// was …" chips, _SettingsSaveBar), says how many runs a shorter retention deletes, resets to the defaults without saving, and saves through
// /api/data/history; workspace Remove and Clean up, and the danger zone's four actions, ask in #modal-data-confirm first (the danger zone
// only after DELETE is typed exactly); Import settings reads the chosen file here, shows every change /api/data/import/preview reports in
// #modal-data-import, and imports only the items left checked. Each change reloads the page with a toast (flashAndReload). Text is always
// set with textContent; nothing here holds a secret.
import { showToast } from './shell.js';
import { postJson, flashAndReload, showFlash, showError, setText, setShown, busy, plural } from './settings-api.js';

const API = '/api/data';
const CONFIRM_WORD = 'DELETE';
const NAMES = { retentionDays: 'history.retentionDays', storeRawPayloads: 'history.storeRawPayloads', storeSseEvents: 'history.storeSseEvents' };

showFlash();
const page = document.querySelector('[data-data-page]');
if (page) {
  document.addEventListener('click', onClick);
  setupHistory(document.querySelector('form[data-history-form]'));
  setupImport();
}

/** "18.2 MB", "212 MB", "512 KB", "300 bytes": as the page shows sizes (DataFolders.Size). */
export function size(bytes) {
  const value = Number(bytes) || 0;
  if (value < 1024) return value === 1 ? '1 byte' : `${value.toLocaleString('en-US')} bytes`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let amount = value / 1024;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit += 1;
  }
  const text = amount < 100 ? String(Math.round(amount * 10) / 10) : String(Math.round(amount));
  return `${text} ${units[unit]}`;
}

async function onClick(event) {
  const open = event.target.closest('[data-open-folder]');
  if (open && !open.disabled) {
    await openFolder(open);
    return;
  }
  const remove = event.target.closest('[data-ws-remove]');
  if (remove && !remove.disabled) {
    await removeWorkspace(remove);
    return;
  }
  const cleanup = event.target.closest('[data-ws-cleanup]');
  if (cleanup && !cleanup.disabled) {
    await cleanUp(cleanup);
    return;
  }
  const danger = event.target.closest('[data-danger]');
  if (danger && !danger.disabled) {
    await runDanger(danger);
    return;
  }
  const exported = event.target.closest('[data-export-link]');
  if (exported) {
    const chats = exported.getAttribute('href')?.endsWith('/chats');
    showToast({ title: 'Export ready', body: chats ? 'Your download has started.' : 'Your download has started. No credentials are included.', tone: 'ok' });
  }
}

// ─── Open folder ────────────────────────────────────────────────────────

async function openFolder(button) {
  const body = { target: button.dataset.openFolder, run: button.dataset.run || null, slot: button.dataset.slot || null };
  try {
    const answer = await busy(button, () => postJson(`${API}/open`, body));
    showToast({ title: `Opened in ${answer?.opener ?? page.dataset.opener ?? 'the file manager'}`, body: 'The folder opened on the machine running the web agent.', tone: 'info' });
  } catch (error) {
    showError('Could not open the folder', error);
  }
}

// ─── The confirm dialog ─────────────────────────────────────────────────

/** Asks in #modal-data-confirm with the texts of `source`'s data-confirm-* attributes; `typed` needs DELETE typed first. Resolves to the
 * typed text (or true) when confirmed, false otherwise. */
function confirmData(source, typed) {
  const modalEl = document.getElementById('modal-data-confirm');
  const data = source.dataset;
  if (!modalEl || !window.bootstrap) {
    const answer = typed ? window.prompt(`${data.confirmTitle}\n\n${data.confirmBody}\n\nType ${CONFIRM_WORD} to confirm.`) : window.confirm(`${data.confirmTitle}\n\n${data.confirmBody}`);
    return Promise.resolve(typed ? (answer === CONFIRM_WORD ? answer : false) : answer);
  }
  setText(modalEl.querySelector('[data-confirm-title]'), data.confirmTitle || 'Are you sure?');
  setText(modalEl.querySelector('[data-confirm-body]'), data.confirmBody || '');
  const note = modalEl.querySelector('[data-confirm-note]');
  setText(note, data.confirmNote || '');
  setShown(note, Boolean(data.confirmNote));
  setText(modalEl.querySelector('[data-confirm-detail]'), data.confirmDetail || '');
  const typedArea = modalEl.querySelector('[data-confirm-typed]');
  const input = modalEl.querySelector('[data-confirm-input]');
  const ok = modalEl.querySelector('[data-confirm-ok]');
  setShown(typedArea, typed);
  input.value = '';
  setText(ok, data.confirmOk || 'Confirm');
  ok.disabled = typed;
  const onInput = () => { ok.disabled = typed && input.value !== CONFIRM_WORD; };
  const modal = window.bootstrap.Modal.getOrCreateInstance(modalEl);
  return new Promise((resolve) => {
    let answer = false;
    const onOk = () => {
      if (ok.disabled) return;
      answer = typed ? input.value : true;
      modal.hide();
    };
    const onKey = (event) => {
      if (event.key === 'Enter' && !ok.disabled) {
        event.preventDefault();
        onOk();
      }
    };
    input.addEventListener('input', onInput);
    input.addEventListener('keydown', onKey);
    ok.addEventListener('click', onOk);
    modalEl.addEventListener('shown.bs.modal', () => { if (typed) input.focus(); }, { once: true });
    modalEl.addEventListener('hidden.bs.modal', () => {
      input.removeEventListener('input', onInput);
      input.removeEventListener('keydown', onKey);
      ok.removeEventListener('click', onOk);
      input.value = '';
      resolve(answer);
    }, { once: true });
    modal.show();
  });
}

// ─── Workspaces ─────────────────────────────────────────────────────────

function removedText(result) {
  const parts = [];
  if (result.removed?.length) parts.push(`Removed ${result.removed.join(', ')} and freed ${size(result.freed)}. Changes applied to your projects are unchanged.`);
  else parts.push('Nothing was removed.');
  if (result.kept?.length) parts.push(`Kept: ${result.kept.join('; ')}.`);
  if (result.failed?.length) parts.push(`Not removed: ${result.failed.join('; ')}.`);
  return parts.join(' ');
}

async function removeWorkspace(button) {
  if (!(await confirmData(button, false))) return;
  try {
    const result = await postJson(`${API}/workspaces/remove`, { run: button.dataset.run, slot: button.dataset.slot });
    flashAndReload({ title: 'Workspace removed', body: removedText(result), tone: 'ok' });
  } catch (error) {
    showError('Could not remove the workspace', error);
  }
}

async function cleanUp(button) {
  if (!(await confirmData(button, false))) return;
  try {
    const result = await busy(button, () => postJson(`${API}/workspaces/cleanup`));
    flashAndReload({ title: plural(result.removed?.length ?? 0, 'workspace') + ' removed', body: removedText(result), tone: result.failed?.length ? 'warn' : 'ok' });
  } catch (error) {
    showError('Could not clean up the workspaces', error);
  }
}

// ─── Danger zone ────────────────────────────────────────────────────────

const DANGER_TITLES = { history: 'Run history cleared', chats: 'Chats deleted', credentials: 'Credentials deleted', settings: 'Settings reset' };

async function runDanger(button) {
  const action = button.dataset.danger;
  const typed = await confirmData(button, true);
  if (!typed) return;
  try {
    const result = await busy(button, () => postJson(`${API}/danger/${encodeURIComponent(action)}`, { confirm: typed }));
    const failed = result.failed?.length ? ` Not done: ${result.failed.join('; ')}` : '';
    flashAndReload({ title: DANGER_TITLES[action] ?? 'Done', body: `${result.message}${failed}`, tone: result.failed?.length ? 'warn' : 'ok' });
  } catch (error) {
    showError('Nothing was deleted', error);
  }
}

// ─── Run history ────────────────────────────────────────────────────────

function setupHistory(form) {
  if (!form) return;
  const retention = form.querySelector('select[name="retentionDays"]');
  const raw = form.querySelector('input[name="storeRawPayloads"]');
  const events = form.querySelector('input[name="storeSseEvents"]');
  const initial = {
    retentionDays: retention.dataset.initial === '' ? null : Number(retention.dataset.initial),
    storeRawPayloads: raw.dataset.initial === 'true',
    storeSseEvents: events.dataset.initial === 'true',
  };
  const read = () => ({
    retentionDays: retention.value === '' ? null : Number(retention.value),
    storeRawPayloads: raw.checked,
    storeSseEvents: events.checked,
  });
  const labelOf = (days) => {
    const option = [...retention.options].find((item) => item.value === (days === null ? '' : String(days)));
    return option?.dataset.label ?? (days === null ? 'Forever' : `${days} days`);
  };
  const was = { retentionDays: labelOf(initial.retentionDays), storeRawPayloads: initial.storeRawPayloads ? 'on' : 'off', storeSseEvents: initial.storeSseEvents ? 'on' : 'off' };

  const update = () => {
    const now = read();
    const keys = Object.keys(now).filter((key) => now[key] !== initial[key]);
    const bar = form.querySelector('[data-savebar]');
    setShown(bar, keys.length > 0);
    setText(form.querySelector('[data-savebar-count]'), keys.length === 1 ? '1 unsaved change' : `${keys.length} unsaved changes`);
    setText(form.querySelector('[data-savebar-names]'), keys.map((key) => NAMES[key]).join(' · '));
    form.querySelectorAll('[data-setting-row]').forEach((row) => {
      const key = row.dataset.settingRow;
      const modified = keys.includes(key);
      row.classList.toggle('is-modified', modified);
      const chip = row.querySelector('[data-modified-chip]');
      setText(chip, `modified · was ${was[key]}`);
      setShown(chip, modified);
    });
    const note = form.querySelector('[data-retention-note]');
    const past = Number(retention.selectedOptions[0]?.dataset.past ?? 0);
    const shorter = now.retentionDays !== null && now.retentionDays !== initial.retentionDays && past > 0;
    setText(note, shorter ? `Saving deletes ${plural(past, 'run')} older than ${labelOf(now.retentionDays)} now.` : '');
    setShown(note, shorter);
  };

  form.addEventListener('change', update);
  form.addEventListener('input', update);
  form.querySelector('[data-history-reset]')?.addEventListener('click', (event) => {
    const data = event.currentTarget.dataset;
    retention.value = data.defaultRetention ?? '';
    raw.checked = data.defaultRaw === 'true';
    events.checked = data.defaultEvents === 'true';
    update();
    showToast({ title: 'Reset to defaults', body: 'The fields were set back to their defaults. Select Save changes to keep them.', tone: 'info' });
  });
  form.addEventListener('click', (event) => {
    if (event.target.closest('[data-discard]')) window.location.reload();
  });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const body = read();
    try {
      const result = await busy(form.querySelector('[data-save]'), () => postJson(form.dataset.endpoint, body));
      const saved = result?.settings ?? body;
      const parts = [saved.retentionDays === null || saved.retentionDays === undefined ? 'Runs are kept forever.' : `Runs are kept for ${labelOf(saved.retentionDays)}.`];
      parts.push(saved.storeRawPayloads ? 'New runs are stored with raw payloads, for 7 days at most.' : 'New runs are stored without raw payloads.');
      if (!saved.storeSseEvents) parts.push('Stream events are not stored for new runs.');
      if (result?.removedRuns > 0) parts.push(`${plural(result.removedRuns, 'older run')} deleted.`);
      flashAndReload({ title: 'Settings saved', body: parts.join(' '), tone: 'ok' });
    } catch (error) {
      showError('Could not save', error);
    }
  });
  update();
}

// ─── Import ─────────────────────────────────────────────────────────────

function setupImport() {
  const input = document.querySelector('[data-import-file]');
  const modalEl = document.getElementById('modal-data-import');
  if (!input || !modalEl) return;
  const apply = modalEl.querySelector('[data-import-apply]');
  let file = null;

  const checked = () => [...modalEl.querySelectorAll('[data-import-check]')].filter((box) => box.checked && !box.disabled).map((box) => box.value);
  const sync = () => {
    const count = checked().length;
    apply.disabled = count === 0;
    setText(apply, count === 0 ? 'Import' : `Import ${plural(count, 'change')}`);
  };
  modalEl.addEventListener('change', (event) => { if (event.target.matches('[data-import-check]')) sync(); });

  input.addEventListener('change', async () => {
    const chosen = input.files?.[0];
    input.value = '';
    if (!chosen) return;
    const max = Number(input.dataset.maxBytes || 0);
    if (max > 0 && chosen.size > max) {
      showError('Could not import', `That file is ${size(chosen.size)}; a settings export is ${size(max)} at most.`);
      return;
    }
    let parsed;
    try {
      parsed = JSON.parse(await chosen.text());
    } catch {
      showError('Could not import', 'That file is not JSON. Choose a file made with Export settings.');
      return;
    }
    try {
      const plan = await postJson(`${API}/import/preview`, { file: parsed });
      file = parsed;
      render(modalEl, plan, chosen.name);
      sync();
      if (window.bootstrap) window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
    } catch (error) {
      showError('Could not import', error);
    }
  });

  apply.addEventListener('click', async () => {
    const accepted = checked();
    if (!file || accepted.length === 0) return;
    try {
      const result = await busy(apply, () => postJson(`${API}/import`, { file, accepted }));
      const parts = [`Imported ${plural(result.applied?.length ?? 0, 'change')}.`];
      if (result.failed?.length) parts.push(`Not imported: ${result.failed.map((item) => `${item.title} (${item.error})`).join('; ')}.`);
      if (result.notes?.length) parts.push(result.notes.join(' '));
      flashAndReload({ title: 'Settings imported', body: parts.join(' '), tone: result.failed?.length ? 'warn' : 'ok' });
    } catch (error) {
      showError('Could not import', error);
    }
  });
}

function render(modalEl, plan, name) {
  const items = plan.items ?? [];
  const when = plan.exportedAt ? new Date(plan.exportedAt) : null;
  setText(modalEl.querySelector('[data-import-source]'), [name, plan.app, when && !Number.isNaN(when.getTime()) ? `exported ${when.toISOString().slice(0, 10)}` : null].filter(Boolean).join(' · '));
  const unchanged = plan.unchanged > 0 ? ` ${plural(plan.unchanged, 'item')} in the file ${plan.unchanged === 1 ? 'is' : 'are'} the same here already.` : '';
  setText(modalEl.querySelector('[data-import-summary]'), items.length === 0
    ? `Nothing to import: this machine has everything in the file already.${unchanged}`
    : `Review ${plural(items.length, 'change')} before anything is saved. Uncheck what you want to keep as it is.${unchanged}`);
  const host = modalEl.querySelector('[data-import-items]');
  const template = modalEl.querySelector('[data-import-item-template]');
  host.replaceChildren();
  items.forEach((item, index) => {
    const el = template.content.firstElementChild.cloneNode(true);
    const box = el.querySelector('[data-import-check]');
    box.id = `importItem${index}`;
    box.value = item.id;
    box.checked = !item.problem;
    box.disabled = Boolean(item.problem);
    const label = el.querySelector('[data-import-title]');
    label.htmlFor = box.id;
    setText(label, item.title);
    const problem = el.querySelector('[data-import-problem]');
    setText(problem, item.problem ? `Can't be imported: ${item.problem}` : '');
    setShown(problem, Boolean(item.problem));
    const body = el.querySelector('[data-import-changes]');
    for (const change of item.changes ?? []) {
      const row = document.createElement('tr');
      const key = document.createElement('td');
      key.className = 'ps-mono text-break';
      key.textContent = change.key || '(value)';
      const before = document.createElement('td');
      before.className = 'ps-mono text-break text-ps-faint';
      before.textContent = change.before ?? '—';
      const after = document.createElement('td');
      after.className = 'ps-mono text-break';
      after.textContent = change.after ?? '(removed)';
      row.append(key, before, after);
      body.append(row);
    }
    host.append(el);
  });
  const notes = modalEl.querySelector('[data-import-notes]');
  notes.replaceChildren(...(plan.notes ?? []).map((text) => {
    const li = document.createElement('li');
    li.textContent = text;
    return li;
  }));
  setShown(notes, (plan.notes ?? []).length > 0);
}
