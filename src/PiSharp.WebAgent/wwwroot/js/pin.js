// Decision 0011 (run W5, E7): Pin as test case (mockup modal-test-case). _TestCaseModal's shell loads this module on every host page (chat,
// Playground, Compare and the Arena's reveal, History, the inspector, a suite page). A button with data-bs-toggle="modal"
// data-bs-target="#modal-test-case" opens the modal; its data attributes say what to pin (data-pin-source with data-pin-run; or
// data-pin-chat, data-pin-agent and data-pin-entry; or data-pin-suite for a suite's Add case; or data-pin-case for Edit case). The content
// is rendered on the server (GET /api/evals/pins/modal) and swapped in whole; a newer opening wins over an older answer. The Suite select
// shows its pane; Add check asks the server for the check and what it makes of the reference output (POST /api/evals/pins/checks); Save
// posts to /api/evals/pins with the case id (and new suite id) the content came with, so a retry saves once, then shows the "Test case
// saved" toast with Open suite and fires pisharp:pin-saved on document ({caseId, suiteId, created, suiteCreated, url}) for a host page to
// refresh itself. Text from the server is set with textContent only.
import { postRequest } from './request-api.js';
import { showToast } from './shell.js';

const API = '/api/evals/pins';
const MAX_CHECKS = 20;

const modal = document.getElementById('modal-test-case');
if (modal && !modal.dataset.pinWired) {
  modal.dataset.pinWired = 'true';
  wire(modal);
}

function wire(root) {
  const loading = root.querySelector('[data-pin-content]')?.cloneNode(true);
  let serial = 0;

  const content = () => root.querySelector('[data-pin-content]');

  function replace(fresh) {
    const current = content();
    if (current) current.replaceWith(fresh); else root.querySelector('.modal-dialog')?.append(fresh);
  }

  function showLoading() {
    if (loading) replace(loading.cloneNode(true));
  }

  function loadFailed(status) {
    showLoading();
    const text = content()?.querySelector('[data-pin-loading-text]');
    content()?.querySelector('.spinner-border')?.remove();
    if (text) text.textContent = status === 0 ? 'The app could not be reached. Is it still running?' : `The test case could not be read (${status}). Close this and try again.`;
  }

  async function load(params) {
    const mine = ++serial;
    showLoading();
    let status = 0;
    let html = '';
    try {
      const response = await fetch(`${API}/modal?${params}`, { headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store' });
      status = response.status;
      html = await response.text();
    } catch {
      status = 0;
    }
    if (mine !== serial) return;
    const template = document.createElement('template');
    template.innerHTML = html.trim();
    const fresh = template.content.firstElementChild;
    if (fresh && fresh.matches('[data-pin-content]')) replace(fresh);
    else loadFailed(status);
  }

  root.addEventListener('show.bs.modal', (event) => {
    const opener = event.relatedTarget;
    if (!opener || !opener.dataset) return;
    load(paramsOf(opener.dataset));
  });

  // Bootstrap sets aria-hidden on the closing modal; a button inside it must not keep the focus (the browser warns, and a screen reader
  // would be left on a hidden element).
  root.addEventListener('hide.bs.modal', () => {
    if (root.contains(document.activeElement)) document.activeElement.blur();
  });

  root.addEventListener('change', (event) => {
    const select = event.target.closest('[data-pin-suite]');
    if (select) {
      for (const pane of root.querySelectorAll('[data-pin-pane]')) pane.hidden = pane.dataset.pinPane !== select.value;
      if (select.value === 'new') root.querySelector('[data-pin-new-suite-name]')?.focus();
      return;
    }
    const kind = event.target.closest('[data-pin-check-kind]');
    if (kind) {
      const value = root.querySelector('[data-pin-check-value]');
      if (value) value.placeholder = kind.selectedOptions[0]?.dataset.placeholder ?? '';
    }
  });

  root.addEventListener('click', (event) => {
    const remove = event.target.closest('[data-pin-remove]');
    if (remove) {
      remove.closest('[data-pin-check]')?.remove();
      updateLine(content());
      return;
    }
    if (event.target.closest('[data-pin-check-add]')) {
      addCheck(content());
      return;
    }
    if (event.target.closest('[data-pin-save]')) save(content());
  });

  root.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && event.target.closest('[data-pin-check-value]')) {
      event.preventDefault();
      addCheck(content());
    }
  });

  async function addCheck(box) {
    if (!box) return;
    const error = box.querySelector('[data-pin-check-error]');
    const input = box.querySelector('[data-pin-check-value]');
    const kind = box.querySelector('[data-pin-check-kind]')?.value ?? 'contains';
    const list = box.querySelector('[data-pin-checks]');
    const button = box.querySelector('[data-pin-check-add]');
    const say = (text) => { if (error) { error.textContent = text; error.hidden = !text; } };
    say('');
    if (list && list.querySelectorAll('[data-pin-check]').length >= MAX_CHECKS) {
      say(`A test case has at most ${MAX_CHECKS} checks.`);
      return;
    }
    if (button) button.disabled = true;
    try {
      const answer = await postRequest(`${API}/checks`, { target: targetOf(box), add: { kind, value: input?.value ?? '' } });
      if (!answer?.item || box !== content()) return;
      list?.append(row(answer.item));
      if (input) input.value = '';
      updateLine(box);
    } catch (failure) {
      say(failure?.message ?? 'The check could not be added.');
    } finally {
      if (button) button.disabled = false;
    }
  }

  async function save(box) {
    if (!box || box.dataset.pinState !== 'ready') return;
    const button = box.querySelector('[data-pin-save]');
    const error = box.querySelector('[data-pin-error]');
    const say = (text) => { if (error) { error.textContent = text; error.hidden = !text; } };
    say('');
    const body = {
      target: targetOf(box),
      caseId: box.dataset.pinCase,
      name: box.querySelector('[data-pin-name]')?.value ?? '',
      tags: box.querySelector('[data-pin-tags]')?.value ?? '',
      checks: [...box.querySelectorAll('[data-pin-check]')].map((item) => JSON.parse(item.dataset.pinCheck)),
    };
    const mode = box.dataset.pinMode;
    // What the server would refuse anyway is said here first (the same words), without a round trip.
    const missing = (selector, text) => {
      const field = box.querySelector(selector);
      if (field && field.value.trim()) return false;
      say(text);
      field?.focus();
      return true;
    };
    if (mode === 'pin') {
      const suite = box.querySelector('[data-pin-suite]')?.value ?? 'new';
      if (suite === 'new') {
        if (missing('[data-pin-new-suite-name]', 'Give the new suite a name.')) return;
        body.newSuite = { id: box.dataset.pinNewSuite, name: box.querySelector('[data-pin-new-suite-name]')?.value ?? '' };
      } else body.suiteId = suite;
    } else if (mode === 'add') {
      if (missing('[data-pin-prompt]', 'Write the user message the case asks.')) return;
      body.systemPrompt = box.querySelector('[data-pin-system]')?.value ?? '';
      body.prompt = box.querySelector('[data-pin-prompt]')?.value ?? '';
      body.maxTokens = box.querySelector('[data-pin-max-tokens]')?.value ?? '';
    }
    if (button) button.disabled = true;
    try {
      const answer = await postRequest(API, body);
      window.bootstrap?.Modal.getOrCreateInstance(root).hide();
      const toast = showToast({ title: answer?.toast?.title ?? 'Test case saved', body: answer?.toast?.body ?? '', tone: answer?.suite?.paused ? 'warn' : 'ok' });
      const where = toast?.querySelector('[data-toast-body]');
      if (where && answer?.toast?.link) {
        const link = document.createElement('a');
        link.href = answer.toast.link;
        link.textContent = 'Open suite';
        link.dataset.pinOpenSuite = '';
        where.append(' ', link);
      }
      document.dispatchEvent(new CustomEvent('pisharp:pin-saved', {
        detail: { caseId: answer?.case?.id ?? null, suiteId: answer?.suite?.id ?? null, created: !!answer?.created, suiteCreated: !!answer?.suiteCreated, url: answer?.url ?? null },
      }));
    } catch (failure) {
      say(failure?.message ?? 'The test case could not be saved.');
    } finally {
      if (button) button.disabled = false;
    }
  }
}

/** The modal query of an opener's data attributes. */
function paramsOf(data) {
  const params = new URLSearchParams();
  const pairs = [['source', data.pinSource], ['run', data.pinRun], ['chat', data.pinChat], ['agent', data.pinAgent], ['entry', data.pinEntry],
    ['suite', data.pinSuite], ['case', data.pinCase]];
  for (const [key, value] of pairs) if (value) params.set(key, value);
  return params;
}

function targetOf(box) {
  try { return JSON.parse(box.dataset.pinTarget || '{}'); } catch { return {}; }
}

/** A check row from the server's {check, label, code, summary, limit, verdict, detail}, as _TestCaseModal renders one. */
function row(item) {
  const li = document.createElement('li');
  li.className = 'list-group-item d-flex align-items-center gap-2';
  li.dataset.pinCheck = JSON.stringify(item.check ?? {});
  if (item.verdict) li.dataset.verdict = item.verdict;
  if (item.detail) li.title = item.detail;
  const label = document.createElement('span');
  label.className = 'fs-7 text-nowrap';
  label.textContent = item.label ?? '';
  li.append(label);
  const value = document.createElement(item.code ? 'code' : 'span');
  value.className = item.code ? 'fs-8 text-truncate' : 'fs-8 text-ps-faint text-truncate';
  value.style.minWidth = '0';
  value.textContent = item.code ?? item.summary ?? '';
  li.append(value);
  if (item.limit) {
    const limit = document.createElement('span');
    limit.className = 'fs-8 text-ps-faint text-nowrap d-none d-sm-inline';
    limit.textContent = item.limit;
    li.append(limit);
  }
  if (item.verdict === 'failed' || item.verdict === 'error') {
    const chip = document.createElement('span');
    chip.className = 'ps-chip text-nowrap';
    chip.dataset.tone = 'warn';
    chip.textContent = 'fails on the reference';
    li.append(chip);
  }
  const remove = document.createElement('button');
  remove.className = 'btn btn-icon ms-auto';
  remove.type = 'button';
  remove.dataset.pinRemove = '';
  remove.setAttribute('aria-label', `Remove the ${(item.label ?? 'this').toLowerCase()} check`);
  const icon = document.createElement('i');
  icon.className = 'bi bi-x-lg fs-8';
  icon.setAttribute('aria-hidden', 'true');
  remove.append(icon);
  li.append(remove);
  return li;
}

/** The checks line, as PinText.ChecksLine says it (keep the two in step). */
function checksLine(count, passed, hasReference) {
  if (count === 0) return 'none yet';
  if (!hasReference) return `${count === 1 ? '1 check' : `${count} checks`} · no reference output to try them on`;
  if (passed === count) return count === 1 ? 'it passes on the reference output' : `all ${count} pass on the reference output`;
  return `${passed} of ${count} pass on the reference output`;
}

function updateLine(box) {
  if (!box) return;
  const rows = [...box.querySelectorAll('[data-pin-check]')];
  const line = box.querySelector('[data-pin-checks-line]');
  if (line) line.textContent = checksLine(rows.length, rows.filter((item) => item.dataset.verdict === 'passed').length, box.dataset.pinReference === 'true');
}
