// Decision 0011 (run W4, R6): an agent run (_AgentRun, ?run={id}), live.
// - While the run is active the page follows R5's event stream (GET /api/compare/agent/{run}/events). Each "status" frame schedules one
//   fetch of the server's view (GET /compare/agent/runs/{run}); only the regions ([data-ac-region]) and columns ([data-ac-col]) whose
//   markup changed since the last fetch are swapped (the ticking clocks are left out of that comparison), so a column the user reads, an
//   open output tail or an open menu elsewhere stays as it is. The stream is closed once the run is done (else EventSource reconnects).
// - The clocks ([data-ac-elapsed][data-since]) tick every second between server renders.
// - Actions are R5's: stop (one, or all after a confirm), approve (one call, this command in every workspace, all the waiting calls the
//   page listed: [data-ac-items] names them, so a call that arrives after the render is not answered unseen), continue over
//   a budget share, discard a workspace (after a confirm), open as chat. Each answer is followed by a fetch of the view.
// - View diff loads modal-agent-diff's content (GET /compare/agent/runs/{run}/diff?slot=); Copy patch copies R4's whole patch.
// - Keep loads modal-keep-result's content (GET /compare/agent/runs/{run}/keep?slot=); Keep x posts apply or keep-branch with the user's
//   choices. A refusal shows in the modal (its text set with textContent); nothing is kept without the user's click.
import { confirmAction } from './shell.js';
import { copyText, getFragment, getRequest, postRequest } from './request-api.js';
import { $, $$, API, PAGES, busy, disposeTooltips, hideModal, modal, startTicking, tick, toast } from './compare-agent-common.js';

let root;
let grid;
let runId = '';
let stream = null;
let pollTimer = 0;
let refreshTimer = 0;
let refreshing = false;
let again = false;
let failed = false;
let diffSeq = 0;
let keepSeq = 0;
const signatures = new Map();

const runUrl = (path = '') => `${API}/${encodeURIComponent(runId)}${path}`;
const pageUrl = (path = '') => `${PAGES}/runs/${encodeURIComponent(runId)}${path}`;
const active = () => root.dataset.active === 'true';

// ---- live view ----------------------------------------------------------------------------------------------------------------

/** The markup that decides whether a part changed: the server's HTML with the ticking clocks blanked. */
function signature(element) {
  const clone = element.cloneNode(true);
  for (const clock of clone.querySelectorAll('[data-ac-elapsed]')) clock.textContent = '';
  return clone.outerHTML;
}

function remember(scope) {
  for (const region of $$('[data-ac-region]', scope)) signatures.set(`region:${region.dataset.acRegion}`, signature(region));
  for (const column of $$('[data-ac-col]', scope)) signatures.set(`col:${column.dataset.slot}`, signature(column));
}

function swap(live, fresh) {
  disposeTooltips(live);
  const open = new Set($$('details[data-ac-detail][open]', live).map((item) => item.dataset.acDetail));
  for (const item of $$('details[data-ac-detail]', fresh)) if (open.has(item.dataset.acDetail)) item.open = true;
  live.replaceWith(fresh);
}

function apply(fresh) {
  root.dataset.state = fresh.dataset.state ?? root.dataset.state;
  root.dataset.active = fresh.dataset.active ?? root.dataset.active;
  let projectChanged = false;
  for (const region of $$('[data-ac-region]', fresh)) {
    const name = region.dataset.acRegion;
    const key = `region:${name}`;
    const sign = signature(region);
    if (signatures.get(key) === sign) continue;
    const live = $(`[data-ac-region="${name}"]`, root);
    if (!live) continue;
    swap(live, region);
    signatures.set(key, sign);
    if (name === 'project') {
      projectChanged = true;
      const toggle = $('[data-bs-target="#acRunDetails"]', region);
      if (toggle) toggle.setAttribute('aria-expanded', document.getElementById('acRunDetails')?.classList.contains('show') ? 'true' : 'false');
    }
  }
  for (const column of $$('[data-ac-col]', fresh)) {
    const key = `col:${column.dataset.slot}`;
    const sign = signature(column);
    if (signatures.get(key) === sign) continue;
    const live = $(`[data-ac-col][data-slot="${column.dataset.slot}"]`, grid);
    if (!live) continue;
    swap(live, column);
    signatures.set(key, sign);
  }
  tick();
  if (projectChanged) reloadWorkspace();
  if (!active()) stopFollowing();
}

async function refresh() {
  window.clearTimeout(refreshTimer);
  if (refreshing) {
    again = true;
    return;
  }
  refreshing = true;
  try {
    const fresh = await getFragment(pageUrl());
    if (fresh) apply(fresh);
    failed = false;
  } catch (error) {
    if (!failed) toast('Run not updated', error.message, 'warn');
    failed = true;
  } finally {
    refreshing = false;
    if (again) {
      again = false;
      scheduleRefresh(0);
    }
  }
}

function scheduleRefresh(delay = 200) {
  window.clearTimeout(refreshTimer);
  refreshTimer = window.setTimeout(refresh, delay);
}

async function reloadWorkspace() {
  const shell = document.getElementById('modal-agent-workspace');
  const current = shell && $('[data-ac-workspace-content]', shell);
  if (!current) return;
  try {
    const fresh = await getFragment(pageUrl('/workspace'));
    if (fresh && current.isConnected) current.replaceWith(fresh);
  } catch {
    // The modal keeps what it showed; the next change of the project region tries again.
  }
}

function follow() {
  if (!active()) return;
  if (!window.EventSource) {
    pollTimer = window.setInterval(refresh, 3000);
    return;
  }
  stream = new EventSource(runUrl('/events'));
  stream.addEventListener('status', (event) => {
    let status = null;
    try { status = JSON.parse(event.data); } catch { status = null; }
    scheduleRefresh();
    if (status && status.state !== 'preparing' && status.state !== 'running') {
      stream?.close();
      stream = null;
    }
  });
  stream.addEventListener('error', () => {
    if (!active()) stopFollowing();
  });
}

function stopFollowing() {
  stream?.close();
  stream = null;
  window.clearInterval(pollTimer);
  pollTimer = 0;
}

// ---- actions ------------------------------------------------------------------------------------------------------------------

async function act(button, title, action) {
  await busy(button, async () => {
    try {
      await action();
    } catch (error) {
      toast(title, error.message);
    }
  });
  await refresh();
}

function stop(button, slot) {
  return act(button, 'Not stopped', () => postRequest(runUrl('/stop'), slot ? { slot } : {}));
}

async function stopAll(button) {
  const yes = await confirmAction({
    title: 'Stop all agents?',
    body: 'Every agent of this run stops now. Each workspace keeps what its agent changed so far.',
    ok: 'Stop all',
  });
  if (yes) await stop(button, null);
}

function approve(button, body) {
  return act(button, 'Not answered', () => postRequest(runUrl('/approve'), body));
}

/** The calls "Allow all" / "Deny all" showed ("a:12 b:7" from the server's render) as [{slot, taskId}]: the server answers only these, so a
 * call that starts waiting after the render keeps waiting (and the button comes back with the new count). */
function listedCalls(text) {
  return String(text ?? '').split(' ').filter(Boolean).map((item) => {
    const [slot, taskId] = item.split(':');
    return { slot, taskId: Number(taskId) };
  }).filter((item) => item.slot && Number.isSafeInteger(item.taskId));
}

async function discard(button, slot) {
  const yes = await confirmAction({
    title: `Discard workspace ${slot}?`,
    body: `Removes workspace ${slot} and the changes in it from your disk. Its answer, check and file list stay with the run.`,
    ok: 'Discard',
  });
  if (yes) await act(button, 'Not discarded', () => postRequest(runUrl('/discard'), { slot }));
}

async function openChat(button, slot) {
  await busy(button, async () => {
    try {
      const answer = await postRequest(runUrl('/open-chat'), { slot });
      if (answer?.url) window.location.assign(answer.url);
    } catch (error) {
      toast('Chat not opened', error.message);
    }
  });
}

async function copyPath(path) {
  if (await copyText(path)) toast('Copied', 'The workspace path is on the clipboard.', 'ok');
  else toast('Not copied', 'The browser did not allow the clipboard.', 'warn');
}

// ---- diff ---------------------------------------------------------------------------------------------------------------------

function placeholder(attribute, title, text) {
  const content = document.createElement('div');
  content.className = 'modal-content';
  content.setAttribute(attribute, '');
  content.innerHTML = '<div class="modal-header"><h2 class="modal-title"></h2><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button></div>'
    + '<div class="modal-body"><div class="d-flex align-items-center gap-2 fs-7 text-ps-faint"><span class="spinner-border spinner-border-sm" aria-hidden="true"></span><span></span></div></div>';
  content.querySelector('.modal-title').textContent = title;
  content.querySelector('.modal-body span:last-child').textContent = text;
  return content;
}

function failure(attribute, title, message) {
  const content = placeholder(attribute, title, '');
  const body = content.querySelector('.modal-body');
  const alert = document.createElement('div');
  alert.className = 'alert alert-danger py-2 px-3 fs-7 mb-0';
  alert.setAttribute('role', 'alert');
  alert.textContent = message;
  body.replaceChildren(alert);
  return content;
}

function setContent(element, selector, content) {
  const current = $(selector, element);
  if (current) current.replaceWith(content);
}

async function openDiff(slot) {
  const diff = modal('modal-agent-diff');
  if (!diff) return;
  const seq = ++diffSeq;
  const shown = diff.element.classList.contains('show');
  if (!shown) {
    setContent(diff.element, '[data-ac-diff-content]', placeholder('data-ac-diff-content', 'Changes', 'Loading the diff…'));
    diff.instance.show();
  } else {
    $$('[data-ac-diff-content] .ac-mode [data-ac-diff]', diff.element).forEach((button) => { button.disabled = true; });
  }
  let content;
  try {
    content = await getFragment(pageUrl(`/diff?slot=${encodeURIComponent(slot)}`));
  } catch (error) {
    content = failure('data-ac-diff-content', 'Changes', error.message);
  }
  if (seq !== diffSeq || !content) return;
  disposeTooltips($('[data-ac-diff-content]', diff.element));
  setContent(diff.element, '[data-ac-diff-content]', content);
}

async function copyPatch(button, slot) {
  await busy(button, async () => {
    try {
      const answer = await getRequest(runUrl(`/diff?slot=${encodeURIComponent(slot)}`));
      const patch = answer?.patch ?? '';
      if (!patch) toast('Nothing to copy', 'This workspace has no changes.', 'warn');
      else if (await copyText(patch)) toast('Copied', 'The patch is on the clipboard.', 'ok');
      else toast('Not copied', 'The browser did not allow the clipboard.', 'warn');
    } catch (error) {
      toast('Patch not copied', error.message);
    }
  });
}

function jumpToFile(link, event) {
  const id = (link.getAttribute('href') ?? '').slice(1);
  const target = id && document.getElementById(id);
  if (!target) return;
  event.preventDefault();
  target.scrollIntoView({ block: 'start', behavior: 'smooth' });
}

// ---- keep ---------------------------------------------------------------------------------------------------------------------

function showKeepPane(content, pane) {
  content.dataset.pane = pane;
  for (const element of $$('[data-ac-keep-pane]', content)) element.hidden = element.dataset.acKeepPane !== pane;
  for (const element of $$('[data-ac-keep-only]', content)) element.hidden = !element.dataset.acKeepOnly.split(' ').includes(pane);
}

/** Opens modal-keep-result for `slot` (after closing modal-agent-diff when Keep was pressed there). */
function openKeep(slot) {
  const diff = document.getElementById('modal-agent-diff');
  if (diff?.classList.contains('show') && window.bootstrap) {
    diff.addEventListener('hidden.bs.modal', () => loadKeep(slot, true), { once: true });
    hideModal(diff);
    return;
  }
  loadKeep(slot, true);
}

async function loadKeep(slot, show) {
  const keep = modal('modal-keep-result');
  if (!keep) return;
  const seq = ++keepSeq;
  setContent(keep.element, '[data-ac-keep-content]', placeholder('data-ac-keep-content', `Keep ${slot.toUpperCase()}`, 'Checking your project…'));
  if (show) keep.instance.show();
  let content;
  try {
    content = await getFragment(pageUrl(`/keep?slot=${encodeURIComponent(slot)}`));
  } catch (error) {
    content = failure('data-ac-keep-content', `Keep ${slot.toUpperCase()}`, error.message);
  }
  if (seq !== keepSeq || !content) return;
  setContent(keep.element, '[data-ac-keep-content]', content);
}

function showRefusal(content, result, how) {
  const message = $('[data-ac-refused-message]', content);
  if (message) message.textContent = result?.message || 'Nothing was kept.';
  const files = $('[data-ac-refused-files]', content);
  if (files) {
    const conflicts = Array.isArray(result?.conflicts) ? result.conflicts : [];
    files.replaceChildren(...conflicts.map((change) => {
      const row = document.createElement('div');
      row.className = 'kv';
      const path = document.createElement('span');
      path.className = 'k ac-path';
      path.textContent = change?.path ?? String(change ?? '');
      const note = document.createElement('span');
      note.className = 'v text-nowrap';
      note.textContent = 'changed in both';
      row.append(path, note);
      return row;
    }));
    files.hidden = conflicts.length === 0;
  }
  showKeepPane(content, 'refused');
  const instead = $('[data-ac-keep-branch-instead]', content);
  if (instead && how === 'branch') instead.hidden = true;
}

async function submitKeep(button) {
  const content = button.closest('[data-ac-keep-content]');
  if (!content) return;
  const slot = content.dataset.slot;
  const how = $('[data-ac-keep-how]:checked', content)?.value;
  if (!slot || !how) return;
  const discardOthers = $('[data-ac-keep-discard]', content)?.checked === true;
  let answer = null;
  await busy(button, async () => {
    try {
      answer = await postRequest(runUrl(how === 'branch' ? '/keep-branch' : '/apply'), { slot, discardOthers });
    } catch (error) {
      toast('Not kept', error.message);
    }
  });
  const result = answer?.result;
  if (result?.outcome === 'refused') showRefusal(content, result, how);
  else if (result) {
    hideModal(content);
    const keptBack = Array.isArray(answer.keptBack) && answer.keptBack.length > 0
      ? ` Still running, so not discarded: ${answer.keptBack.map((item) => String(item).toUpperCase()).join(', ')}.` : '';
    toast(result.outcome === 'applied' ? 'Applied to your project' : 'Branch kept', `${result.message ?? ''}${keptBack}`.trim(), 'ok');
  }
  await refresh();
}

function branchInstead(button) {
  const content = button.closest('[data-ac-keep-content]');
  if (!content) return;
  const branch = $('[data-ac-keep-how][value="branch"]', content);
  if (!branch || branch.disabled) return;
  branch.checked = true;
  showKeepPane(content, 'options');
  $('[data-ac-keep-submit]', content)?.focus();
}

// ---- wiring -------------------------------------------------------------------------------------------------------------------

function onClick(event) {
  const link = event.target.closest?.('.ac-diff-file-link');
  if (link) {
    jumpToFile(link, event);
    return;
  }
  const target = event.target.closest?.('button');
  if (!target || target.disabled) return;
  const data = target.dataset;
  if (target.matches('[data-ac-stop-all]')) stopAll(target);
  else if (target.matches('[data-ac-stop]')) stop(target, data.acStop);
  else if (target.matches('[data-ac-approve]')) approve(target, { scope: 'call', slot: data.slot, taskId: Number(data.taskId), decision: data.acApprove === 'deny' ? 'deny' : 'allow' });
  else if (target.matches('[data-ac-approve-command]')) approve(target, { scope: 'command', slot: data.slot, taskId: Number(data.taskId), decision: 'allow' });
  else if (target.matches('[data-ac-approve-all]')) approve(target, { scope: 'all', items: listedCalls(data.acItems), decision: data.acApproveAll === 'deny' ? 'deny' : 'allow' });
  else if (target.matches('[data-ac-continue]')) act(target, 'Not continued', () => postRequest(runUrl('/continue'), { slot: data.slot, scope: data.acContinue === 'run' ? 'run' : 'turn' }));
  else if (target.matches('[data-ac-discard]')) discard(target, data.acDiscard);
  else if (target.matches('[data-ac-copy]')) copyPath(data.acCopy);
  else if (target.matches('[data-ac-open-chat]')) openChat(target, data.acOpenChat);
  else if (target.matches('[data-ac-diff]')) openDiff(data.acDiff);
  else if (target.matches('[data-ac-copy-patch]')) copyPatch(target, data.acCopyPatch);
  else if (target.matches('[data-ac-keep]')) openKeep(data.acKeep);
  else if (target.matches('[data-ac-keep-submit]')) submitKeep(target);
  else if (target.matches('[data-ac-keep-retry]')) loadKeep(data.acKeepRetry, false);
  else if (target.matches('[data-ac-keep-branch-instead]')) branchInstead(target);
}

export function initAgentRun(page) {
  root = page;
  grid = $('[data-ac-grid]', root);
  runId = root.dataset.run ?? '';
  if (!grid || !runId) return;
  remember(root);
  document.addEventListener('click', onClick);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && active()) scheduleRefresh(0);
  });
  startTicking();
  follow();
}
