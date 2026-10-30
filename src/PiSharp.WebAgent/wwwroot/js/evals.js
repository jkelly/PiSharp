// Decision 0011 (run W5, E6): the Evals pages (Views/Evals). The pages are server-rendered; this module only sends what the user asks and
// swaps in the server's fragments:
//   Run now / Run again / Run this case / Run now to confirm: POST /api/evals/suites/{id}/run; when the Spend Guard asks, its confirm
//     modal (showSpendConfirm) asks, and the run is sent again with the user's choice and the run id the approval is stored under.
//   Schedule: fetches _SuiteScheduleModal (GET /evals/suites/{id}/schedule); the repeat panes, the runs-a-day line, and the cap: each edit
//     asks the server again (?cap=) whether it fits, so the refusal is SuiteService.Refusal's; Save schedule and Turn off post to
//     /api/evals/suites/{id}/schedule, which refuses a cap below the worst case again.
//   Set baseline / Accept as baseline: fetches _BaselineModal (GET /evals/suites/{id}/baseline?run=&target=), posts .../baseline.
//   A check (or "add"): fetches _CheckEditorModal (GET /evals/cases/{id}/check?check=&kind=); the kind panes, tool arguments, the judge's
//     family and cost (POST /api/evals/checks/judge-preview: nothing is sent to a model), Try (POST /api/evals/checks/try), and Save or
//     Remove: the check is validated (POST /api/evals/checks/validate), then the case's checks are saved (POST /api/evals/cases/{id}).
//   Add and remove a target, rename and delete the suite, remove a case: POST the Suites unit's APIs.
//   Add case and Edit case: E7's _TestCaseModal and pin.js; when it saves a case of this suite (pisharp:pin-saved), the page reloads.
//   A running run: its page polls GET /evals/runs/{id}/live and swaps the fragment in until the run ends; the suite page polls
//     GET /api/evals/runs/{id} for its progress and reloads when the run ends.
// Text is set with textContent only. A change that reloads the page keeps its toast with flashAndReload (sessionStorage, guarded).
import { confirmAction, providerModelValue, showToast } from './shell.js';
import { flashAndReload, showFlash } from './settings-api.js';
import { getFragment, getRequest, postRequest } from './request-api.js';
import { showSpendConfirm } from './spend-confirm.js';

const enc = encodeURIComponent;
const api = {
  suite: (id) => `/api/evals/suites/${enc(id)}`,
  testCase: (id) => `/api/evals/cases/${enc(id)}`,
  run: (id) => `/api/evals/runs/${enc(id)}`,
  checks: '/api/evals/checks',
};
const fragments = {
  schedule: (id, cap) => `/evals/suites/${enc(id)}/schedule${cap === undefined ? '' : `?cap=${enc(cap)}`}`,
  baseline: (id, run, target) => {
    const query = new URLSearchParams();
    if (run) query.set('run', run);
    if (target) query.set('target', target);
    const text = query.toString();
    return `/evals/suites/${enc(id)}/baseline${text ? `?${text}` : ''}`;
  },
  check: (caseId, checkId, kind) => {
    const query = new URLSearchParams();
    if (checkId) query.set('check', checkId);
    if (kind) query.set('kind', kind);
    const text = query.toString();
    return `/evals/cases/${enc(caseId)}/check${text ? `?${text}` : ''}`;
  },
  runLive: (id) => `/evals/runs/${enc(id)}/live`,
};

const page = document.querySelector('[data-evals]');
const loading = new Map();

function failed(title, error) {
  showToast({ title, body: error instanceof Error ? error.message : String(error ?? ''), tone: 'danger' });
}

function plural(count, one, many) {
  return `${count.toLocaleString('en-US')} ${count === 1 ? one : many}`;
}

/** An amount like the server's DriftText.Money: two decimals, four under a cent. */
function money(micros) {
  const places = Math.abs(micros) < 10_000 ? 4 : 2;
  const text = (Math.abs(micros) / 1_000_000).toLocaleString('en-US', { minimumFractionDigits: places, maximumFractionDigits: places });
  return (micros < 0 ? '-$' : '$') + text;
}

/** Dollars the user typed (2, 2.50, $1,000) as micro-USD, or null when it is not an amount. */
function parseUsd(text) {
  let value = String(text ?? '').trim();
  if (value.startsWith('$')) value = value.slice(1).trim();
  value = value.replaceAll(',', '');
  if (!/^(\d+(\.\d*)?|\.\d+)$/.test(value)) return null;
  const usd = Number(value);
  return Number.isFinite(usd) ? Math.round(usd * 1_000_000) : null;
}

// ----- run a suite -----

async function runSuite(suiteId, caseId, button) {
  if (button) button.disabled = true;
  try {
    let body = caseId ? { caseIds: [caseId] } : {};
    for (;;) {
      const answer = await postRequest(`${api.suite(suiteId)}/run`, body);
      if (answer?.posted) {
        flashAndReload({ title: 'Run started', body: 'Spend Guard reserved the worst case first. Results fill in as answers finish.', tone: 'ok' }, answer.url);
        return;
      }
      if (answer?.refused) {
        showToast({ title: 'Not run', body: answer.refused.message ?? 'The run did not start.', tone: 'warn' });
        return;
      }
      if (!answer?.spend) {
        showToast({ title: 'Not run', body: 'The run did not start.', tone: 'warn' });
        return;
      }
      const choice = await showSpendConfirm(answer.spend);
      if (choice.choice === 'proceed-once') body = { ...body, runId: answer.runId, spend: { choice: 'proceed-once' } };
      else if (choice.choice === 'cap-output' && choice.maxOutputTokens) {
        body = { ...body, runId: answer.runId, spend: { choice: 'cap-output', maxOutputTokens: choice.maxOutputTokens } };
      } else return;
    }
  } catch (error) {
    failed('Run not started', error);
  } finally {
    if (button?.isConnected) button.disabled = false;
  }
}

// ----- modals: an empty shell on the page, its content fetched for the suite, case or run -----

/** Opens the modal `id` and fills it with the fragment at `url`; resolves with its content element, or null. */
async function openModal(id, url) {
  const host = document.getElementById(id);
  if (!host || !window.bootstrap) return null;
  const current = host.querySelector('[data-modal-content]');
  if (!loading.has(id) && current) loading.set(id, current.cloneNode(true));
  const blank = loading.get(id)?.cloneNode(true);
  if (blank && current) current.replaceWith(blank);
  window.bootstrap.Modal.getOrCreateInstance(host).show();
  try {
    const fresh = await getFragment(url);
    const content = fresh?.querySelector('[data-modal-content]');
    if (!content) throw new Error('The dialog could not be loaded.');
    host.querySelector('[data-modal-content]').replaceWith(content);
    return content;
  } catch (error) {
    const body = host.querySelector('[data-modal-content] .modal-body');
    if (body) {
      body.replaceChildren();
      const message = document.createElement('div');
      message.className = 'msg-error';
      message.setAttribute('role', 'alert');
      message.textContent = error instanceof Error ? error.message : 'The dialog could not be loaded.';
      body.append(message);
    }
    return null;
  }
}

function hideModal(content) {
  const host = content?.closest('.modal');
  if (host && window.bootstrap) window.bootstrap.Modal.getOrCreateInstance(host).hide();
}

// ----- schedule -----

let capRequest = 0;
let capTimer = 0;

function scheduleContent(element) {
  return element.closest('#modal-suite-schedule [data-modal-content][data-suite-id]');
}

function showRepeat(content, repeat) {
  content.querySelectorAll('[data-ss-repeat]').forEach((button) => {
    const active = button.dataset.ssRepeat === repeat;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', active ? 'true' : 'false');
  });
  content.querySelectorAll('[data-ss-repeats] [data-ss-pane]').forEach((pane) => {
    pane.hidden = pane.dataset.ssPane !== `ss-${repeat}`;
  });
}

function updateDayText(content) {
  const hours = Number(content.querySelector('#ssHours')?.value);
  const text = content.querySelector('[data-ss-day-text]');
  if (!text) return;
  if (!Number.isInteger(hours) || hours < 1 || hours > 24) {
    text.textContent = 'Run every 1 to 24 hours.';
    return;
  }
  const runs = Math.ceil(24 / hours);
  const worst = Number(content.dataset.worstMicros) || 0;
  text.textContent = `${plural(runs, 'run', 'runs')} a day can reserve up to ${money(worst * runs)}. A run that doesn't fit is skipped, never partly run.`;
}

function showRefused(content, message) {
  const status = content.querySelector('[data-ss-cap-status]');
  if (!status) return;
  status.querySelector('[data-ss-pane="ss-fits"]').hidden = true;
  const refused = status.querySelector('[data-ss-pane="ss-refused"]');
  refused.hidden = false;
  refused.querySelector('[data-ss-refused-message]').textContent = message;
  content.querySelector('[data-cap-input]')?.classList.add('is-invalid');
  const save = content.querySelector('[data-ss-save]');
  if (save) save.disabled = true;
}

async function checkCap(content) {
  const input = content.querySelector('[data-cap-input]');
  const text = input.value.trim();
  const id = ++capRequest;
  if (!text) {
    showRefused(content, "A schedule needs a suite-run cap: scheduled runs can't ask, so the cap blocks.");
    return;
  }
  const micros = parseUsd(text);
  if (micros === null) {
    showRefused(content, 'Enter the cap in dollars, such as 2.00.');
    return;
  }
  try {
    const fresh = await getFragment(fragments.schedule(content.dataset.suiteId, (micros / 1_000_000).toFixed(6)));
    if (id !== capRequest || !content.isConnected) return;
    const freshContent = fresh?.querySelector('[data-modal-content]');
    const status = freshContent?.querySelector('[data-ss-cap-status]');
    if (!status) return;
    content.querySelector('[data-ss-cap-status]').replaceWith(status);
    const refused = freshContent.dataset.refused ?? '';
    content.dataset.refused = refused;
    input.classList.toggle('is-invalid', refused !== '');
    const save = content.querySelector('[data-ss-save]');
    if (save) save.disabled = refused !== '';
  } catch (error) {
    if (id === capRequest) showRefused(content, error instanceof Error ? error.message : 'The cap could not be checked.');
  }
}

function readSchedule(content) {
  const repeat = content.querySelector('[data-ss-repeat].active')?.dataset.ssRepeat ?? 'daily';
  const body = { enabled: true, repeat };
  if (repeat === 'hours') {
    body.everyHours = Number(content.querySelector('#ssHours').value);
    body.at = content.querySelector('#ssHoursStart').value;
  } else if (repeat === 'weekly') {
    body.day = content.querySelector('#ssWeekDay').value;
    body.at = content.querySelector('#ssWeekTime').value;
  } else {
    body.at = content.querySelector('#ssDailyTime').value;
  }
  if (content.dataset.timeZone) body.timeZone = content.dataset.timeZone;
  return body;
}

async function saveSchedule(content, button) {
  const micros = parseUsd(content.querySelector('[data-cap-input]').value);
  if (micros === null || micros <= 0) {
    showRefused(content, 'Enter the cap in dollars, such as 2.00.');
    return;
  }
  button.disabled = true;
  try {
    const answer = await postRequest(`${api.suite(content.dataset.suiteId)}/schedule`, { ...readSchedule(content), capMicros: micros });
    if (answer?.saved) {
      const label = answer.schedule?.label ?? 'on its schedule';
      hideModal(content);
      flashAndReload({ title: 'Schedule saved', body: `${content.dataset.suiteName} runs ${label.charAt(0).toLowerCase()}${label.slice(1)} with a ${money(micros)} suite-run cap.`, tone: 'ok' });
      return;
    }
    showRefused(content, answer?.refused?.message ?? 'The schedule was not saved.');
  } catch (error) {
    showRefused(content, error instanceof Error ? error.message : 'The schedule was not saved.');
  }
}

async function turnOff(content, button) {
  button.disabled = true;
  try {
    await postRequest(`${api.suite(content.dataset.suiteId)}/schedule`, { enabled: false });
    hideModal(content);
    flashAndReload({ title: 'Schedule turned off', body: `${content.dataset.suiteName} now runs only when you choose Run now.`, tone: 'warn' });
  } catch (error) {
    button.disabled = false;
    failed('Schedule not changed', error);
  }
}

// ----- baseline -----

function baselineRunChanged(content) {
  const option = content.querySelector('[data-bl-run]')?.selectedOptions[0];
  const targets = (option?.dataset.targets ?? '').split(' ').filter(Boolean);
  content.querySelectorAll('[data-bl-target]').forEach((box) => {
    const inRun = targets.includes(box.value);
    box.disabled = !inRun;
    if (!inRun) box.checked = false;
  });
}

async function saveBaseline(content, button) {
  const select = content.querySelector('[data-bl-run]');
  const targets = [...content.querySelectorAll('[data-bl-target]:checked')].map((box) => box.value);
  if (!select?.value) return;
  if (targets.length === 0) {
    showToast({ title: 'Choose a target', body: 'Check at least one target to re-baseline.', tone: 'warn' });
    return;
  }
  button.disabled = true;
  try {
    const replace = content.querySelector('[data-bl-refs]')?.checked === true;
    await postRequest(`${api.suite(content.dataset.suiteId)}/baseline`, { runId: select.value, targets, replaceReferences: replace });
    const run = (select.selectedOptions[0]?.textContent ?? 'the run').split(' · ')[0];
    hideModal(content);
    flashAndReload({
      title: 'Baseline set',
      body: `${plural(targets.length, 'target now compares', 'targets now compare')} against ${run.replace(/^Run/, 'run')}, and their flags are cleared.${replace ? ' The reference outputs are this run\'s answers now.' : ''}`,
      tone: 'ok',
    });
  } catch (error) {
    button.disabled = false;
    failed('Baseline not set', error);
  }
}

// ----- check editor -----

function checkContent(element) {
  return element.closest('#modal-check-editor [data-modal-content][data-case-id]');
}

function showError(content, message) {
  const box = content.querySelector('[data-ce-error]');
  if (!box) return;
  box.textContent = message ?? '';
  box.hidden = !message;
}

function showKind(content) {
  const select = content.querySelector('[data-ce-kind]');
  const pane = select.selectedOptions[0]?.dataset.ceTarget;
  content.querySelectorAll('[data-ce-pane]').forEach((element) => { element.hidden = element.dataset.cePane !== pane; });
  const judge = select.value === 'judge';
  const tryLine = content.querySelector('[data-ce-try]');
  if (tryLine) tryLine.hidden = judge;
  const result = content.querySelector('[data-ce-try-result]');
  if (result) result.replaceChildren();
  showError(content, '');
  if (judge) previewJudge(content);
}

function addArgument(content) {
  const template = content.querySelector('[data-ce-arg-template]');
  const list = content.querySelector('[data-ce-args]');
  if (!template || !list) return;
  const row = template.content.firstElementChild.cloneNode(true);
  list.append(row);
  row.querySelector('[data-arg-name]')?.focus();
}

/** The check the editor describes, as the Checks unit's JSON. Throws an Error the editor shows for a field it cannot read. */
function readCheck(content) {
  const q = (selector) => content.querySelector(selector);
  const kind = q('[data-ce-kind]').value;
  const check = { kind };
  if (content.dataset.checkId) check.id = content.dataset.checkId;
  switch (kind) {
    case 'contains':
      check.text = q('#ceContainsText').value;
      check.scope = q('#ceContainsWhere').value;
      check.matchCase = q('#ceContainsCase').checked;
      check.negate = q('#ceContainsNot').checked;
      break;
    case 'regex':
      check.pattern = q('#ceRegexPattern').value;
      check.scope = q('#ceRegexWhere').value;
      check.negate = q('#ceRegexNot').checked;
      break;
    case 'json-schema':
      try {
        check.schema = JSON.parse(q('#ceSchema').value);
      } catch (error) {
        throw new Error(`The schema is not valid JSON: ${error instanceof Error ? error.message : ''}`.trim());
      }
      break;
    case 'tool':
      check.tool = q('#ceToolName').value.trim();
      check.calls = q('#ceToolCount').value;
      check.arguments = [...content.querySelectorAll('[data-ce-args] [data-ce-arg]')]
        .map((row) => ({
          name: row.querySelector('[data-arg-name]').value.trim(),
          rule: row.querySelector('[data-arg-rule]').value,
          value: row.querySelector('[data-arg-value]').value,
        }))
        .filter((argument) => argument.name)
        .map((argument) => (argument.rule === 'present' ? { name: argument.name, rule: argument.rule } : argument));
      break;
    case 'limits': {
      const cost = q('#ceMaxCost').value.trim();
      const latency = q('#ceMaxLatency').value.trim();
      if (cost) {
        const micros = parseUsd(cost);
        if (micros === null || micros <= 0) throw new Error('Enter the max cost in dollars, such as 0.03.');
        check.maxCostMicros = micros;
      }
      if (latency) {
        const seconds = Number(latency);
        if (!Number.isFinite(seconds) || seconds <= 0) throw new Error('Enter the max latency in seconds, such as 30.');
        check.maxLatencyMs = Math.round(seconds * 1000);
      }
      break;
    }
    case 'judge': {
      const choice = providerModelValue(q('[data-ce-judge-select] [data-provider-model]'));
      if (!choice?.provider || !choice?.model) throw new Error('Choose the judge\'s provider and model.');
      check.judge = { provider: choice.provider, model: choice.model, rubric: q('#ceRubric').value, passAt: Number(q('#cePassAt').value) };
      break;
    }
    default:
      break;
  }
  return check;
}

function chip(tone, text) {
  const element = document.createElement('span');
  element.className = 'ps-chip';
  if (tone) element.dataset.tone = tone;
  element.textContent = text;
  return element;
}

async function tryCheck(content, button) {
  let check;
  try {
    check = readCheck(content);
  } catch (error) {
    showError(content, error.message);
    return;
  }
  button.disabled = true;
  showError(content, '');
  const result = content.querySelector('[data-ce-try-result]');
  try {
    const answer = await postRequest(`${api.checks}/try`, { check, caseId: content.dataset.caseId });
    result.replaceChildren();
    if (answer?.result) {
      const verdict = answer.result.verdict;
      const tone = verdict === 'passed' ? 'ok' : verdict === 'review' ? 'warn' : 'danger';
      const word = verdict === 'passed' ? 'passes' : verdict === 'failed' ? 'fails' : verdict;
      const badge = chip(tone, word);
      badge.dataset.tryVerdict = verdict;
      const text = document.createElement('span');
      text.className = 'ms-2';
      text.textContent = answer.result.detail ?? (verdict === 'passed' ? 'Passes on the reference output.' : 'Fails on the reference output.');
      result.append(badge, text);
    } else {
      const note = document.createElement('span');
      note.className = 'text-ps-faint';
      note.textContent = answer?.note ?? 'Nothing to try the check on.';
      result.append(note);
    }
  } catch (error) {
    showError(content, error instanceof Error ? error.message : 'The check could not be tried.');
  } finally {
    button.disabled = false;
  }
}

let previewRequest = 0;

async function previewJudge(content) {
  const choice = providerModelValue(content.querySelector('[data-ce-judge-select] [data-provider-model]'));
  const family = content.querySelector('[data-ce-family]');
  const estimate = content.querySelector('[data-ce-judge-estimate]');
  if (!choice?.provider || !choice?.model || !family || !estimate) return;
  const id = ++previewRequest;
  try {
    const answer = await postRequest(`${api.checks}/judge-preview`, { provider: choice.provider, model: choice.model, caseId: content.dataset.caseId });
    if (id !== previewRequest || !content.isConnected) return;
    family.replaceChildren();
    if (answer?.family) {
      const panel = document.createElement('div');
      panel.className = 'ps-panel px-3 py-2 d-flex flex-wrap gap-2 align-items-start fs-7';
      const text = document.createElement('span');
      text.className = 'flex-grow-1 eval-notice-text';
      text.textContent = answer.family.message ?? '';
      panel.append(chip(answer.family.same ? 'warn' : 'ok', answer.family.chip ?? ''), text);
      family.append(panel);
    }
    estimate.textContent = answer?.estimate?.text ?? '';
  } catch (error) {
    if (id !== previewRequest) return;
    family.replaceChildren();
    estimate.textContent = error instanceof Error ? error.message : '';
  }
}

async function caseChecks(caseId) {
  const answer = await getRequest(api.testCase(caseId));
  return Array.isArray(answer?.case?.checks) ? answer.case.checks : [];
}

async function saveCheck(content, button) {
  let check;
  try {
    check = readCheck(content);
  } catch (error) {
    showError(content, error.message);
    return;
  }
  button.disabled = true;
  showError(content, '');
  try {
    const valid = await postRequest(`${api.checks}/validate`, { check });
    const saved = valid?.check ?? check;
    const checks = await caseChecks(content.dataset.caseId);
    const index = saved.id ? checks.findIndex((item) => item.id === saved.id) : -1;
    if (index >= 0) checks[index] = saved;
    else checks.push(saved);
    await postRequest(api.testCase(content.dataset.caseId), { checks });
    hideModal(content);
    flashAndReload({ title: 'Check saved', body: 'The next run uses it. Earlier results are not recalculated.', tone: 'ok' });
  } catch (error) {
    button.disabled = false;
    showError(content, error instanceof Error ? error.message : 'The check was not saved.');
  }
}

async function removeCheck(content, button) {
  const id = content.dataset.checkId;
  if (!id) return;
  button.disabled = true;
  try {
    const checks = (await caseChecks(content.dataset.caseId)).filter((item) => item.id !== id);
    await postRequest(api.testCase(content.dataset.caseId), { checks });
    hideModal(content);
    flashAndReload({ title: 'Check removed', body: 'The next run leaves it out. Earlier results are not recalculated.', tone: 'ok' });
  } catch (error) {
    button.disabled = false;
    showError(content, error instanceof Error ? error.message : 'The check was not removed.');
  }
}

// ----- the suite: targets, rename, delete, cases -----

function suiteTargets() {
  const section = document.querySelector('[data-suite-targets]');
  try {
    const list = JSON.parse(section?.dataset.targets ?? '[]');
    return Array.isArray(list) ? list : [];
  } catch {
    return [];
  }
}

function withoutIds(targets) {
  return targets.map((target) => {
    const copy = { ...target };
    delete copy.id;
    return copy;
  });
}

async function saveTargets(suiteId, targets, toast) {
  await postRequest(api.suite(suiteId), { targets: withoutIds(targets) });
  flashAndReload(toast);
}

async function addTarget(form) {
  const suiteId = page?.dataset.suiteId;
  const choice = providerModelValue(form.querySelector('[data-provider-model]'));
  if (!suiteId || !choice?.provider || !choice?.model) {
    showToast({ title: 'Choose a model', body: 'Pick the provider and model to add.', tone: 'warn' });
    return;
  }
  const button = form.querySelector('[type="submit"]');
  if (button) button.disabled = true;
  try {
    await saveTargets(suiteId, [...suiteTargets(), { provider: choice.provider, model: choice.model }],
      { title: 'Target added', body: `${choice.provider}/${choice.model} joins from the next run.`, tone: 'ok' });
  } catch (error) {
    if (button) button.disabled = false;
    failed('Target not added', error);
  }
}

async function removeTarget(button) {
  const suiteId = page?.dataset.suiteId;
  const name = button.dataset.targetName ?? 'this target';
  const ok = await confirmAction({
    title: `Remove ${name}?`,
    body: 'Later runs leave it out. Earlier runs, its results and its baselines stay in history.',
    ok: 'Remove target',
  });
  if (!ok || !suiteId) return;
  try {
    await saveTargets(suiteId, suiteTargets().filter((target) => target.id !== button.dataset.removeTarget),
      { title: 'Target removed', body: `${name} is no longer in this suite.`, tone: 'ok' });
  } catch (error) {
    failed('Target not removed', error);
  }
}

function showRename(show) {
  const form = document.querySelector('[data-rename-form]');
  if (!form) return;
  form.hidden = !show;
  if (show) form.querySelector('input')?.select();
}

async function rename(form) {
  const suiteId = page?.dataset.suiteId;
  const name = form.querySelector('input').value.trim();
  if (!suiteId || !name) return;
  try {
    await postRequest(api.suite(suiteId), { name });
    flashAndReload({ title: 'Suite renamed', body: name, tone: 'ok' });
  } catch (error) {
    failed('Suite not renamed', error);
  }
}

async function deleteSuite(button) {
  const name = button.dataset.suiteTitle ?? 'this suite';
  const ok = await confirmAction({
    title: `Delete ${name}?`,
    body: 'Its cases, runs, results, baselines and drift signals are deleted. The model calls stay in History.',
    ok: 'Delete suite',
  });
  if (!ok) return;
  try {
    await postRequest(`${api.suite(button.dataset.deleteSuite)}/delete`, {});
    flashAndReload({ title: 'Suite deleted', body: name, tone: 'ok' }, '/evals/suites');
  } catch (error) {
    failed('Suite not deleted', error);
  }
}

async function deleteCase(button) {
  const name = button.dataset.caseName ?? 'this case';
  const ok = await confirmAction({
    title: `Remove ${name}?`,
    body: 'The case and its checks leave the suite. Earlier runs keep their results.',
    ok: 'Remove case',
  });
  if (!ok) return;
  try {
    await postRequest(`${api.testCase(button.dataset.deleteCase)}/delete`, {});
    flashAndReload({ title: 'Case removed', body: name, tone: 'ok' });
  } catch (error) {
    failed('Case not removed', error);
  }
}

// ----- runs -----

async function cancelRun(button) {
  const ok = await confirmAction({
    title: 'Cancel this run?',
    body: 'Queued answers are released. Answers already streaming settle at their estimate.',
    ok: 'Cancel run',
  });
  if (!ok) return;
  button.disabled = true;
  try {
    await postRequest(`${api.run(button.dataset.cancelRun)}/cancel`, {});
    showToast({ title: 'Run cancelled', body: 'Queued answers were released.', tone: 'warn' });
    refreshRun();
  } catch (error) {
    button.disabled = false;
    failed('Run not cancelled', error);
  }
}

const POLL_MS = 3000;
const SETTLE_MS = 1500;
let pollTimer = 0;
let polling = false;

async function refreshRun() {
  const live = document.querySelector('[data-eval-run-live]');
  const runId = page?.dataset.runId;
  if (!live || !runId || polling) return;
  polling = true;
  try {
    const fresh = await getFragment(fragments.runLive(runId));
    if (fresh?.matches('[data-eval-run-live]')) {
      const wasRunning = live.dataset.running === 'true';
      live.replaceWith(fresh);
      if (wasRunning && fresh.dataset.running !== 'true') {
        showToast({ title: 'Run finished', body: `The run is ${fresh.dataset.runStatus ?? 'over'}.`, tone: fresh.dataset.runStatus === 'complete' ? 'ok' : 'warn' });
        // A complete run's drift signals are stored just after it is marked complete: read it once more to show them.
        if (fresh.dataset.runStatus === 'complete') window.setTimeout(settleRun, SETTLE_MS);
      }
    }
  } catch {
    // A missed poll is retried on the next tick.
  } finally {
    polling = false;
    schedulePoll();
  }
}

async function settleRun() {
  const live = document.querySelector('[data-eval-run-live]');
  const runId = page?.dataset.runId;
  if (!live || !runId) return;
  try {
    const fresh = await getFragment(fragments.runLive(runId));
    if (fresh?.matches('[data-eval-run-live]')) live.replaceWith(fresh);
  } catch {
    // The page shows the run as it ended; a reload shows its signals.
  }
}

function schedulePoll() {
  window.clearTimeout(pollTimer);
  if (document.querySelector('[data-eval-run-live]')?.dataset.running !== 'true') return;
  if (document.hidden) return;
  pollTimer = window.setTimeout(refreshRun, POLL_MS);
}

let suiteTimer = 0;

function scheduleSuitePoll(delay) {
  window.clearTimeout(suiteTimer);
  if (page?.dataset.evals !== 'suite' || !page.dataset.activeRun || document.hidden) return;
  suiteTimer = window.setTimeout(refreshSuiteRun, delay);
}

async function refreshSuiteRun() {
  const runId = page?.dataset.activeRun;
  const card = document.querySelector('[data-suite-running]');
  if (!runId || !card) return;
  try {
    const answer = await getRequest(api.run(runId));
    const status = answer?.run?.status;
    if (status && status !== 'queued' && status !== 'running') {
      // Reloaded a moment later, so the drift signals stored just after the run is marked complete show.
      window.setTimeout(() => flashAndReload({ title: `Run #${answer.run.number} finished`, body: `The run is ${status}.`, tone: status === 'complete' ? 'ok' : 'warn' }),
        status === 'complete' ? SETTLE_MS : 0);
      return;
    }
    const total = Number(answer?.answers) || 0;
    const finished = (answer?.results ?? []).filter((result) => result.status === 'done' || result.status === 'error').length;
    const percent = total ? Math.round((100 * finished) / total) : 0;
    const bar = card.querySelector('.progress');
    if (bar) {
      bar.setAttribute('aria-valuenow', String(percent));
      bar.querySelector('.progress-bar').style.width = `${percent}%`;
    }
  } catch {
    // A missed poll is retried on the next tick.
  }
  scheduleSuitePoll(POLL_MS + 1000);
}

// ----- wiring -----

document.addEventListener('click', (event) => {
  const target = event.target instanceof Element ? event.target : null;
  if (!target) return;
  let button;
  if ((button = target.closest('[data-run-suite]'))) {
    if (!button.disabled) runSuite(button.dataset.runSuite, button.dataset.caseId, button);
  } else if ((button = target.closest('[data-open-schedule]'))) {
    openModal('modal-suite-schedule', fragments.schedule(button.dataset.openSchedule)).then((content) => { if (content) updateDayText(content); });
  } else if ((button = target.closest('[data-open-baseline]'))) {
    openModal('modal-baseline', fragments.baseline(button.dataset.openBaseline, button.dataset.runId, button.dataset.targetId));
  } else if ((button = target.closest('[data-edit-check]'))) {
    openModal('modal-check-editor', fragments.check(button.dataset.caseId, button.dataset.checkId)).then((content) => {
      if (content?.querySelector('[data-ce-kind]')?.value === 'judge') previewJudge(content);
    });
  } else if ((button = target.closest('[data-ss-repeat]'))) {
    const content = scheduleContent(button);
    if (content) showRepeat(content, button.dataset.ssRepeat);
  } else if ((button = target.closest('[data-ss-save]'))) {
    const content = scheduleContent(button);
    if (content && !button.disabled) saveSchedule(content, button);
  } else if ((button = target.closest('[data-ss-off]'))) {
    const content = scheduleContent(button);
    if (content) turnOff(content, button);
  } else if ((button = target.closest('[data-bl-save]'))) {
    const content = button.closest('#modal-baseline [data-modal-content]');
    if (content && !button.disabled) saveBaseline(content, button);
  } else if ((button = target.closest('[data-ce-add-arg]'))) {
    const content = checkContent(button);
    if (content) addArgument(content);
  } else if ((button = target.closest('[data-ce-remove-arg]'))) {
    button.closest('[data-ce-arg]')?.remove();
  } else if ((button = target.closest('[data-ce-try-button]'))) {
    const content = checkContent(button);
    if (content) tryCheck(content, button);
  } else if ((button = target.closest('[data-ce-save]'))) {
    const content = checkContent(button);
    if (content) saveCheck(content, button);
  } else if ((button = target.closest('[data-ce-remove]'))) {
    const content = checkContent(button);
    if (content) removeCheck(content, button);
  } else if ((button = target.closest('[data-remove-target]'))) {
    removeTarget(button);
  } else if ((button = target.closest('[data-rename-suite]'))) {
    showRename(true);
  } else if ((button = target.closest('[data-rename-cancel]'))) {
    showRename(false);
  } else if ((button = target.closest('[data-delete-suite]'))) {
    deleteSuite(button);
  } else if ((button = target.closest('[data-delete-case]'))) {
    deleteCase(button);
  } else if ((button = target.closest('[data-cancel-run]'))) {
    cancelRun(button);
  }
});

document.addEventListener('input', (event) => {
  const target = event.target instanceof Element ? event.target : null;
  if (!target) return;
  const content = scheduleContent(target);
  if (!content) return;
  if (target.matches('#ssHours')) updateDayText(content);
  else if (target.matches('[data-cap-input]')) {
    window.clearTimeout(capTimer);
    capTimer = window.setTimeout(() => checkCap(content), 300);
  }
});

document.addEventListener('change', (event) => {
  const target = event.target instanceof Element ? event.target : null;
  if (!target) return;
  if (target.matches('[data-bl-run]')) {
    const content = target.closest('#modal-baseline [data-modal-content]');
    if (content) baselineRunChanged(content);
  } else if (target.matches('[data-ce-kind]')) {
    const content = checkContent(target);
    if (content) showKind(content);
  }
});

document.addEventListener('provider-model-change', (event) => {
  const target = event.target instanceof Element ? event.target : null;
  const content = target?.closest('[data-ce-judge-select]') ? checkContent(target) : null;
  if (content) previewJudge(content);
});

document.addEventListener('submit', (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) return;
  if (form.matches('[data-add-target]')) {
    event.preventDefault();
    addTarget(form);
  } else if (form.matches('[data-rename-form]')) {
    event.preventDefault();
    rename(form);
  }
});

document.addEventListener('pisharp:pin-saved', (event) => {
  const detail = event instanceof CustomEvent ? event.detail : null;
  if (page?.dataset.evals !== 'suite' || !detail?.suiteId || detail.suiteId !== page.dataset.suiteId) return;
  flashAndReload({ title: detail.created ? 'Test case added' : 'Test case saved', body: '', tone: 'ok' });
});

document.addEventListener('visibilitychange', () => {
  if (!document.hidden) {
    schedulePoll();
    scheduleSuitePoll(0);
  }
});

showFlash();
schedulePoll();
scheduleSuitePoll(POLL_MS);
