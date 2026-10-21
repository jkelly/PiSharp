// Supervised research (decision 0010, M4). Every call goes through the hub's proxy. Starting a run sends an Idempotency-Key; an open run is
// polled until it ends, showing each angle's progress from the run record's `branches`, the findings kept so far and the run's spend.
import { $, deviceApi, initCommon, uuid, escapeHtml as esc } from './core.js';

initCommon();
const root = $('[data-automation]');
const device = $('[data-device]', root);
const session = $('[data-session]', root);
const error = $('[data-error]', root);
const loading = $('[data-loading]', root);
const panel = $('[data-run]', root);
const enc = encodeURIComponent;
const date = value => value == null ? '—' : new Date(value).toLocaleString();
const option = (value, label) => `<option value="${esc(value)}">${esc(label)}</option>`;
const levels = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh'];
const tones = { completed: 'ok', running: 'info', waiting: 'accent', planned: undefined, failed: 'danger', aborted: 'warn', cancelled: 'warn', declined: 'warn', expired: 'warn', limited: 'warn' };
const chip = status => `<span class="ps-chip"${tones[status] ? ` data-tone="${tones[status]}"` : ''}>${esc(status)}</span>`;
let api, models = [], defaultModel = '', shown = null, timer = null, busy = false;

function showError(failure) {
  const list = failure.fields?.length
    ? `<ul class="mb-0 mt-1">${failure.fields.map(field => `<li><code>${esc(field.path || '/')}</code> ${esc(field.message)}</li>`).join('')}</ul>` : '';
  error.innerHTML = esc(failure.message) + list;
  error.hidden = false;
}

async function run(action) {
  if (busy) return;
  busy = true; root.inert = true; root.setAttribute('aria-busy', 'true'); error.hidden = true; loading.textContent = 'Loading…';
  try { await action(); }
  catch (failure) { showError(failure); }
  finally { busy = false; root.inert = false; root.removeAttribute('aria-busy'); loading.textContent = ''; }
}

function remember() {
  const query = new URLSearchParams({ device: device.value, session: session.value });
  if (shown) query.set('task', shown);
  history.replaceState(null, '', `${location.pathname}?${query}`);
}

async function connect() {
  stop();
  if (!device.value) throw new Error('Add a device first.');
  api = deviceApi(device.value);
  const info = await api.get('/device');
  const features = new Set((info.features || []).map(item => item.id));
  $('[data-unsupported]', root).hidden = features.has('research');
  $('[data-supported]', root).hidden = !features.has('research');
  if (!features.has('research')) return;
  models = info.models || [];
  defaultModel = info.defaultModel || '';
  for (const select of root.querySelectorAll('[data-model]'))
    select.innerHTML = option('', "The agent's model") + models.map(model => option(`${model.provider}/${model.id}`, `${model.name} (${model.provider})`)).join('');
  for (const stage of ['research', 'synthesis']) thinking(stage);
  const tree = await api.get('/tree');
  const query = new URLSearchParams(location.search);
  session.innerHTML = (tree.sessions || []).map(item => option(item.id, item.name)).join('');
  if ((tree.sessions || []).some(item => item.id === query.get('session'))) session.value = query.get('session');
  const policies = features.has('policies') ? (await api.get('/policies')).items.filter(item => item.activeVersion != null) : [];
  $('[data-policy]', root).innerHTML = option('', 'Publish without judgment') + policies.map(item => option(item.id, `${item.title} (v${item.activeVersion})`)).join('');
  const sources = features.has('sources') ? (await api.get('/sources')).items : [];
  $('[data-sources]', root).innerHTML = sources.map(item => `<div class="form-check"><input class="form-check-input" type="checkbox" id="material-${esc(item.id)}"
    value="${esc(item.id)}" data-source /><label class="form-check-label" for="material-${esc(item.id)}">${esc(item.title || item.id)}</label></div>`).join('');
  $('[data-sources-field]', root).hidden = sources.length === 0;
  await recent();
  const task = Number(query.get('task'));
  if (task > 0 && session.value) await show(task);
  else { shown = null; panel.hidden = true; remember(); }
}

/** Offers the thinking levels the stage's model supports: a model that does not reason only thinks "off". */
function thinking(stage) {
  const chosen = $(`[data-model="${stage}"]`, root).value || defaultModel;
  const model = models.find(item => `${item.provider}/${item.id}` === chosen || item.id === chosen);
  const select = $(`[data-thinking="${stage}"]`, root);
  const allowed = model?.reasoning ? levels : ['off'];
  const previous = select.value;
  select.innerHTML = allowed.map(level => option(level, level)).join('');
  select.value = allowed.includes(previous) ? previous : 'off';
}

async function recent() {
  if (!session.value) {
    $('[data-recent]', root).innerHTML = '';
    $('[data-recent-empty]', root).hidden = false;
    return;
  }
  const items = (await api.get(`/sessions/${enc(session.value)}/executions?kind=job&q=pisharp-research&limit=20`)).items
    .filter(item => item.run?.job === 'pisharp-research');
  $('[data-recent]', root).innerHTML = items.map(item => `<tr><td><button type="button" class="btn btn-link p-0" data-show="${item.id}">#${item.id}</button></td>
    <td>${esc(item.run.input?.question || '')}</td><td>${chip(item.run.status)}</td><td>${esc(date(item.startedAt))}</td></tr>`).join('');
  $('[data-recent-empty]', root).hidden = items.length !== 0;
}

function stop() {
  clearTimeout(timer);
  timer = null;
}

async function show(task) {
  stop();
  shown = task;
  remember();
  const base = `/sessions/${enc(session.value)}/executions/${task}`;
  const [execution, artifacts, usage] = await Promise.all([api.get(base), api.get(`${base}/artifacts`), api.get(`${base}/usage`)]);
  if (shown !== task) return;
  render(execution, artifacts.items || [], usage);
  if (['running', 'waiting'].includes(execution.run?.status)) timer = setTimeout(() => poll(task), 2000);
  else await recent();
}

async function poll(task) {
  if (busy || shown !== task) return;
  try { await show(task); }
  catch (failure) { showError(failure); }
}

function render(execution, artifacts, usage) {
  const record = execution.run || {};
  const input = record.input || {};
  const branches = record.branches || [];
  const angles = branches.filter(row => row.phase === 'research');
  const helpers = branches.filter(row => row.phase !== 'research');
  const findings = new Map(artifacts.map(item => [item.id, item]));
  const phase = (record.phases || []).find(item => item.id === record.phase);
  const totals = usage.totals || {};
  const open = ['running', 'waiting'].includes(record.status);
  const review = record.status === 'waiting' && record.phase === 'review';
  const row = item => {
    const attempts = item.attempts || [];
    const last = attempts[attempts.length - 1];
    const finding = item.artifactId && findings.get(item.artifactId);
    return `<tr><td>${esc(item.title)}<br/><small class="text-body-secondary"><code>${esc(item.id)}</code></small></td>
      <td>${esc(item.model)}${item.thinking && item.thinking !== 'off' ? ` <small class="text-body-secondary">· ${esc(item.thinking)}</small>` : ''}</td>
      <td>${chip(item.status)}</td><td>${attempts.length}${last?.status === 'running' && last.deadlineAt ? `<br/><small class="text-body-secondary">until ${esc(date(last.deadlineAt))}</small>` : ''}</td>
      <td>${item.error ? `<small class="text-danger">${esc(item.error)}</small>` : item.answerChars != null ? `${item.answerChars} characters` : '—'}</td>
      <td>${finding ? esc(finding.title) : '—'}</td></tr>`;
  };
  panel.innerHTML = `<div class="d-flex justify-content-between align-items-start flex-wrap gap-2">
      <div><h2 class="h5 mb-1" id="run-title">Research #${execution.id}: ${esc(input.question || '')}</h2>
        <p class="mb-0">${chip(record.status)} ${phase ? `<span class="text-body-secondary">${esc(phase.label)}${phase.summary ? ` — ${esc(phase.summary)}` : ''}</span>` : ''}</p></div>
      <div class="d-flex gap-2 flex-wrap">
        ${review ? `<button type="button" class="btn btn-sm btn-primary" data-decide="true">Publish</button><button type="button" class="btn btn-sm btn-outline-secondary" data-decide="false">Decline</button>` : ''}
        ${open ? `<button type="button" class="btn btn-sm btn-outline-danger" data-cancel>Cancel run</button>` : ''}
        <a class="btn btn-sm btn-outline-secondary" href="/executions?device=${enc(device.value)}&session=${enc(session.value)}&task=${execution.id}">Execution details</a>
      </div></div>
    ${record.error ? `<div class="alert alert-danger mt-3 mb-0" role="alert">${esc(record.error)}</div>` : ''}
    <div class="table-responsive mt-3"><table class="table table-sm align-middle"><thead><tr><th>Angle</th><th>Model</th><th>Status</th><th>Attempts</th><th>Answer</th><th>Finding</th></tr></thead>
      <tbody>${angles.map(row).join('') || '<tr><td colspan="6" class="text-body-secondary">The angles are being planned.</td></tr>'}</tbody></table></div>
    ${helpers.length ? `<p class="small text-body-secondary mb-2">${helpers.map(item => `${esc(item.title)}: ${esc(item.status)}${item.error ? ` (${esc(item.error)})` : ''}`).join(' · ')}</p>` : ''}
    <p class="small mb-3">Spent: ${totals.requests ?? 0} request(s), ${totals.tokens ?? 0} tokens${totals['money:USD'] != null ? `, $${(totals['money:USD'] / 1e6).toFixed(4)}` : ''}${usage.costKnown === false ? ' <span class="text-body-secondary">(some cost is not known)</span>' : ''}</p>
    <h3 class="h6">Findings so far</h3>
    ${artifacts.length ? `<ul class="list-unstyled d-grid gap-2">${artifacts.map(item => `<li class="border rounded p-2"><strong>${esc(item.title)}</strong>
      <div class="small text-body-secondary" style="white-space: pre-wrap">${esc((item.props?.summary || '').slice(0, 1200))}</div></li>`).join('')}</ul>`
      : '<p class="text-body-secondary">No angle has answered yet.</p>'}`;
  panel.hidden = false;
}

function read() {
  const question = $('[data-question]', root).value.trim();
  if (!question) throw new Error('Ask a question first.');
  const angles = [...root.querySelectorAll('[data-angle]')].map(row => ({
    title: $('[data-angle-title]', row).value.trim(),
    prompt: $('[data-angle-prompt]', row).value.trim() || undefined,
  })).filter(angle => angle.title);
  const stage = name => {
    const model = $(`[data-model="${name}"]`, root).value;
    const level = $(`[data-thinking="${name}"]`, root).value;
    return model || level !== 'off' ? { model: model || undefined, thinking: level } : undefined;
  };
  const sourceIds = [...root.querySelectorAll('[data-source]:checked')].map(box => box.value);
  const requests = $('[data-requests]', root).value;
  const pages = $('[data-pages]', root).value;
  return {
    question,
    angles: angles.length ? angles : undefined,
    sourceIds: sourceIds.length ? sourceIds : undefined,
    research: stage('research'),
    synthesis: stage('synthesis'),
    concurrency: Number($('[data-concurrency]', root).value) || undefined,
    maxAttempts: Number($('[data-attempts]', root).value) || undefined,
    angleDeadlineSeconds: Number($('[data-deadline]', root).value) || undefined,
    requireAll: $('[data-require-all]', root).checked || undefined,
    retrieval: pages === '' ? undefined : { maxFetches: Number(pages) },
    policyId: $('[data-policy]', root).value || undefined,
    budget: requests ? { requests: Number(requests) } : undefined,
  };
}

$('[data-angles]', root).innerHTML = [1, 2, 3, 4, 5].map(number => `<div class="row g-2" data-angle>
  <div class="col-md-4"><label class="visually-hidden" for="angle-title-${number}">Angle ${number} title</label>
    <input id="angle-title-${number}" data-angle-title class="form-control" maxlength="200" placeholder="Angle ${number} title" /></div>
  <div class="col-md-8"><label class="visually-hidden" for="angle-prompt-${number}">Angle ${number} question</label>
    <input id="angle-prompt-${number}" data-angle-prompt class="form-control" maxlength="4000" placeholder="What to find out (defaults to the title)" /></div></div>`).join('');

device.addEventListener('change', () => { shown = null; run(connect); });
session.addEventListener('change', () => run(async () => { stop(); shown = null; panel.hidden = true; remember(); await recent(); }));
$('[data-refresh]', root).addEventListener('click', () => run(async () => { await recent(); if (shown) await show(shown); }));
for (const stage of ['research', 'synthesis']) $(`[data-model="${stage}"]`, root).addEventListener('change', () => thinking(stage));
$('[data-recent]', root).addEventListener('click', event => {
  const pick = event.target.closest('[data-show]');
  if (pick) run(() => show(Number(pick.dataset.show)));
});
panel.addEventListener('click', event => {
  const decide = event.target.closest('[data-decide]');
  if (decide) return run(async () => {
    const execution = await api.get(`/sessions/${enc(session.value)}/executions/${shown}`);
    await api.post(`/sessions/${enc(session.value)}/agents/${execution.agentId}/jobs/${shown}/approval`, { approved: decide.dataset.decide === 'true' });
    await show(shown);
  });
  if (event.target.closest('[data-cancel]')) {
    if (!confirm('Cancel this research? Angles still running are stopped; findings already kept stay.')) return;
    run(async () => {
      await api.post(`/sessions/${enc(session.value)}/tasks/${shown}/abort`);
      await show(shown);
    });
  }
});
$('[data-start]', root).addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    if (!session.value) throw new Error('Choose a session first.');
    const started = await api.post(`/sessions/${enc(session.value)}/research`, read(), { idempotencyKey: uuid() });
    $('[data-started]', root).textContent = `Started run #${started.taskId}.`;
    await recent();
    await show(started.taskId);
    panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
});
const initial = new URLSearchParams(location.search).get('device');
if ([...device.options].some(item => item.value === initial)) device.value = initial;
run(connect);
