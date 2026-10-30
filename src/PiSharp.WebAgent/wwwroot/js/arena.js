// Decision 0011 (run W5, E5): a blind battle's page (GET /compare/arena/{id}, Views/Compare/Arena.cshtml). While answers are held it
// follows the battle's event stream (GET /api/evals/arena/{id}/events: counts and the status only, never a name) and, on each frame,
// swaps the body the server renders (GET /compare/arena/{id}/body), keeping the open tab, the vote pane, the tag and the ranking chosen so
// far. The vote bar posts the vote (POST /api/evals/arena/{id}/vote: a pick, Tie, All bad or a ranking, with the tag; New tag… types
// one) and swaps in the revealed body. Stream anyway posts its switch; Stop all stops the round (it is revealed, never counted); New round
// asks the same again with a new shuffle (a Spend Guard refusal opens modal-spend-confirm). Agent battles open each column's changes in
// modal-agent-diff (GET /compare/arena/{id}/diff?letter=, names hidden until the vote) and answer a waiting tool call or a budget Ask by
// letter. Pin as test case is shown only when the page hosts #modal-test-case (E7's modal, which reads data-pin-run). Model text is never
// built here: every fragment is the server's.
import { showToast, confirmAction } from './shell.js';
import { getFragment, postRequest } from './request-api.js';
import { ARENA_API, followStart } from './arena-start.js';

const root = document.querySelector('[data-arena]');
const battleId = root?.dataset.arena ?? '';
let source = null;
let loading = false;
let pending = false;
let timer = 0;

const $ = (selector, scope = document) => scope.querySelector(selector);
const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
const api = (path = '') => `${ARENA_API}/${encodeURIComponent(battleId)}${path}`;
const page = (path = '') => `/compare/arena/${encodeURIComponent(battleId)}${path}`;
const body = () => $('[data-arena-battle]', root);
const toast = (title, text, tone = 'danger') => showToast({ title, body: text, tone });

// ---- the body -----------------------------------------------------------------------------------------------------------------

function tooltips(scope, dispose = false) {
  if (!window.bootstrap?.Tooltip) return;
  for (const element of $$('[data-bs-toggle="tooltip"]', scope)) {
    if (dispose) window.bootstrap.Tooltip.getInstance(element)?.dispose();
    else window.bootstrap.Tooltip.getOrCreateInstance(element);
  }
}

function remember(scope) {
  if (!scope) return {};
  const tag = $('[data-arena-tag]', scope);
  return {
    tab: $('.nav-link.active', scope)?.id?.startsWith('arena-tab-metrics') ? 'metrics' : 'responses',
    pane: $('[data-arena-panes]', scope)?.dataset.pane ?? 'av-pick',
    tag: tag?.value ?? null,
    newTag: $('[data-arena-tag-input]', scope)?.value ?? '',
    ranking: $$('[data-arena-rank]', scope).map((select) => select.value),
    open: $$('details[open]', scope).map((details) => details.closest('[data-arena-col]')?.dataset.arenaCol + '|' + details.className),
  };
}

function restore(scope, state) {
  if (state.tab === 'metrics') {
    const tab = $('[id^="arena-tab-metrics"]', scope);
    if (tab && window.bootstrap?.Tab) window.bootstrap.Tab.getOrCreateInstance(tab).show();
  }
  if (state.pane) showPane(state.pane, scope);
  const tag = $('[data-arena-tag]', scope);
  if (tag && state.tag !== null && [...tag.options].some((option) => option.value === state.tag)) tag.value = state.tag;
  const input = $('[data-arena-tag-input]', scope);
  if (input) {
    input.value = state.newTag ?? '';
    input.hidden = tag?.value !== '';
  }
  const selects = $$('[data-arena-rank]', scope);
  if (state.ranking?.length === selects.length) selects.forEach((select, index) => { select.value = state.ranking[index]; });
  for (const details of $$('details', scope)) {
    const key = details.closest('[data-arena-col]')?.dataset.arenaCol + '|' + details.className;
    if (state.open?.includes(key)) details.open = true;
  }
}

function sync(scope) {
  const status = scope?.dataset.status ?? root.dataset.status;
  root.dataset.status = status;
  const held = status === 'held';
  const stop = $('[data-arena-stop]', root);
  if (stop) stop.hidden = !held;
  const again = $('[data-arena-again]', root);
  if (again) again.hidden = held;
  const subtitle = document.querySelector('[data-arena-subtitle]');
  if (subtitle && scope?.dataset.subtitle) subtitle.textContent = scope.dataset.subtitle;
  // Pin as test case needs E7's modal on this page; without it the button stays hidden.
  const modal = document.getElementById('modal-test-case');
  for (const pin of $$('[data-arena-pin]', root)) {
    pin.hidden = !modal;
    if (modal) {
      pin.dataset.bsToggle = 'modal';
      pin.dataset.bsTarget = '#modal-test-case';
    }
  }
  tooltips(scope);
  if (held) follow();
  else stopFollowing();
}

async function reload() {
  if (loading) {
    pending = true;
    return;
  }
  loading = true;
  try {
    const fresh = await getFragment(page('/body'));
    const current = body();
    if (fresh && current) {
      const state = remember(current);
      tooltips(current, true);
      current.replaceWith(fresh);
      restore(fresh, state);
      sync(fresh);
    }
  } catch (error) {
    if (error?.status === 404) stopFollowing();
  } finally {
    loading = false;
    if (pending) {
      pending = false;
      reload();
    }
  }
}

function schedule(delay = 120) {
  clearTimeout(timer);
  timer = setTimeout(reload, delay);
}

function follow() {
  if (source || !window.EventSource) return;
  source = new EventSource(api('/events'));
  source.addEventListener('state', (event) => {
    let frame = null;
    try { frame = JSON.parse(event.data); } catch { return; }
    const current = body();
    if (frame.status !== 'held') stopFollowing();
    if (!current || current.dataset.status !== frame.status || current.dataset.finished !== String(frame.finished) || frame.streamAnyway
      || root.dataset.arenaMode === 'agent') schedule();
  });
  source.addEventListener('error', () => {
    // The stream ends once the battle is no longer held; the browser would reconnect, so stop and read the body once.
    if (source?.readyState === EventSource.CLOSED || root.dataset.status !== 'held') {
      stopFollowing();
      schedule(0);
    }
  });
}

function stopFollowing() {
  source?.close();
  source = null;
}

// ---- voting -------------------------------------------------------------------------------------------------------------------

function showPane(pane, scope = body()) {
  const panes = $('[data-arena-panes]', scope);
  if (!panes) return;
  const target = $(`[data-pane-body="${pane}"]`, panes) ? pane : 'av-pick';
  panes.dataset.pane = target;
  for (const element of $$('[data-pane-body]', panes)) element.hidden = element.dataset.paneBody !== target;
  for (const element of $$('[data-pane-only]', panes)) element.hidden = element.dataset.paneOnly !== target;
  for (const button of $$('[data-arena-show-pane]', panes)) {
    const on = button.dataset.arenaShowPane === target;
    button.classList.toggle('active', on);
    button.setAttribute('aria-pressed', on ? 'true' : 'false');
  }
}

function voteError(message) {
  const element = $('[data-arena-vote-error]', body());
  if (!element) {
    if (message) toast('Not saved', message);
    return;
  }
  element.textContent = message ?? '';
  element.hidden = !message;
}

function chosenTag() {
  const select = $('[data-arena-tag]', body());
  if (!select) return null;
  if (select.value !== '') return select.value;
  const typed = ($('[data-arena-tag-input]', body())?.value ?? '').trim();
  if (!typed) throw new Error('Name the new tag first.');
  return typed;
}

async function vote(button) {
  voteError('');
  const kind = button.dataset.arenaVote;
  const payload = { kind };
  try {
    payload.tag = chosenTag();
  } catch (error) {
    voteError(error.message);
    $('[data-arena-tag-input]', body())?.focus();
    return;
  }
  if (kind === 'pick') payload.pick = button.dataset.letter;
  if (kind === 'rank') {
    payload.ranking = $$('[data-arena-rank]', body()).map((select) => select.value);
    if (new Set(payload.ranking).size !== payload.ranking.length) {
      voteError('Rank every answer once: each place needs a different letter.');
      return;
    }
  }
  const buttons = $$('[data-arena-vote]', body());
  buttons.forEach((item) => { item.disabled = true; });
  try {
    await postRequest(api('/vote'), payload);
    await reload();
  } catch (error) {
    voteError(error.message);
    buttons.forEach((item) => { item.disabled = false; });
  }
}

// ---- actions ------------------------------------------------------------------------------------------------------------------

async function act(button, action) {
  if (button) button.disabled = true;
  try {
    await action();
  } catch (error) {
    toast('Not done', error.message);
  } finally {
    if (button) button.disabled = false;
  }
}

async function stop(button) {
  const yes = await confirmAction({
    title: 'Stop this round?',
    body: 'Every answer still running stops now. A stopped round is revealed and never counted.',
    ok: 'Stop all',
  });
  if (!yes) return;
  await act(button, async () => {
    await postRequest(api('/stop'), {});
    await reload();
  });
}

async function againRound(button) {
  await act(button, async () => {
    await followStart(await postRequest(api('/again'), {}));
  });
}

async function streamAnyway(input) {
  try {
    await postRequest(api('/stream-anyway'), { on: input.checked });
    await reload();
  } catch (error) {
    input.checked = !input.checked;
    toast('Not changed', error.message);
  }
}

async function openDiff(letter) {
  const modal = document.getElementById('modal-agent-diff');
  if (!modal || !window.bootstrap?.Modal) return;
  const instance = window.bootstrap.Modal.getOrCreateInstance(modal);
  const content = $('[data-ac-diff-content]', modal);
  if (!modal.classList.contains('show')) instance.show();
  try {
    const fresh = await getFragment(page(`/diff?letter=${encodeURIComponent(letter)}`));
    const current = $('[data-ac-diff-content]', modal);
    if (fresh && current) current.replaceWith(fresh);
  } catch (error) {
    if (content) {
      const body = $('.modal-body', content);
      if (body) body.textContent = error.message;
    }
  }
}

function onClick(event) {
  const target = event.target.closest('button, a');
  if (!target || target.disabled) return;
  if (target.matches('[data-arena-vote]')) vote(target);
  else if (target.matches('[data-arena-show-pane]')) showPane(target.dataset.arenaShowPane);
  else if (target.matches('[data-arena-stop]')) stop(target);
  else if (target.matches('[data-arena-again]')) againRound(target);
  else if (target.matches('[data-arena-diff]')) {
    event.preventDefault();
    openDiff(target.dataset.arenaDiff);
  } else if (target.matches('[data-arena-approve]')) {
    act(target, async () => {
      await postRequest(api('/approve'), { letter: target.dataset.letter, taskId: Number(target.dataset.taskId), decision: target.dataset.arenaApprove });
      await reload();
    });
  } else if (target.matches('[data-arena-continue]')) {
    act(target, async () => {
      await postRequest(api('/continue'), { letter: target.dataset.letter, scope: target.dataset.arenaContinue });
      await reload();
    });
  }
}

function onChange(event) {
  const target = event.target;
  if (target.matches('[data-arena-stream-anyway]')) streamAnyway(target);
  else if (target.matches('[data-arena-tag]')) {
    const input = $('[data-arena-tag-input]', body());
    if (input) {
      input.hidden = target.value !== '';
      if (!input.hidden) input.focus();
    }
    voteError('');
  }
}

if (root && battleId) {
  root.addEventListener('click', onClick);
  root.addEventListener('change', onChange);
  document.getElementById('modal-agent-diff')?.addEventListener('click', onClick);
  sync(body());
}
