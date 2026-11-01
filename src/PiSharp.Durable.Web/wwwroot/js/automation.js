// Device-hosted schedules and authoritative execution history (decision 0009). Secrets are never stored in browser storage.
import { $, $$, deviceApi, fragment, initCommon, uuid, escapeHtml as esc, cost, count } from './core.js';
import { wireFeedback } from './feedback.js';

initCommon();
const root = $('[data-automation]');
const mode = root.dataset.automation;
const device = $('[data-device]', root);
const session = $('[data-session]', root);
const error = $('[data-error]', root);
const loading = $('[data-loading]', root);
const enc = encodeURIComponent;
const date = value => value == null ? '—' : new Date(value).toLocaleString();
const duration = value => value == null ? 'In progress' : `${(value / 1000).toFixed(1)} s`;
const isLive = status => ['pending', 'running', 'waiting', 'completing'].includes(status);
const auditLabel = item => item.run?.audit?.status || (isLive(item.status) ? 'Pending' : 'Unaudited');
const option = (value, label) => `<option value="${esc(value)}">${esc(label)}</option>`;
let api, tree = [], jobs = [], schedules = [], next = null, selected = null, version = 0, busy = false, features = [];
const supports = id => features.some(item => item.id === id);
const meters = amounts => Object.entries(amounts || {}).map(([meter, value]) => `${meter} ${meter.startsWith('money:') ? (value / 1e6).toFixed(4) : count(value)}`).join(' · ') || 'none';

function clearSecret() {
  const token = $('[data-issued-token]', root);
  if (token) { token.textContent = ''; $('[data-issued]', root).hidden = true; }
}

async function run(action) {
  if (busy) return;
  busy = true; root.inert = true; root.setAttribute('aria-busy', 'true'); error.hidden = true; loading.textContent = 'Loading…';
  try { await action(); }
  catch (failure) { error.textContent = failure.message; error.hidden = false; }
  finally { busy = false; root.inert = false; root.removeAttribute('aria-busy'); loading.textContent = ''; }
}

async function connect() {
  ++version; clearSecret(); selected = null; next = null;
  if (!device.value) { error.textContent = 'Add a device first.'; error.hidden = false; return; }
  api = deviceApi(device.value);
  const generation = version;
  const info = await api.get('/device');
  features = info.features || [];
  if (!info.capabilities?.includes(mode === 'schedules' ? 'schedules' : 'executions'))
    throw new Error('This device does not support this feature. Update its PiSharp Durable host.');
  const response = await api.get('/tree');
  if (generation !== version) return;
  tree = response.sessions || [];
  const previous = new URLSearchParams(location.search).get('session');
  session.innerHTML = tree.map(item => option(item.id, item.name)).join('');
  if (tree.some(item => item.id === previous)) session.value = previous;
  if (mode === 'schedules') {
    await loadAgents(); await refreshSchedules();
  } else {
    $('[data-detail]', root).hidden = true;
    await history();
    const task = new URLSearchParams(location.search).get('task');
    if (task && session.value) await detail(task);
  }
}

async function loadAgents() {
  const agent = $('[data-agent]', root);
  const current = tree.find(item => item.id === session.value);
  agent.innerHTML = (current?.agents || []).filter(item => !item.ownerTaskId).map(item => option(item.id, item.name)).join('');
  await loadJobs();
}

async function loadJobs() {
  const agent = $('[data-agent]', root).value;
  jobs = session.value && agent ? (await api.get(`/sessions/${enc(session.value)}/agents/${enc(agent)}/jobs`)).definitions : [];
  $('[data-job]', root).innerHTML = jobs.map(item => option(item.id, item.title)).join('');
  $('[data-create-button]', root).disabled = !jobs.length;
  jobInputs();
}

function jobInputs() {
  const job = jobs.find(item => item.id === $('[data-job]', root).value);
  $('[data-job-inputs]', root).innerHTML = (job?.inputs || []).map(input => `<div class="col-md-3"><label class="form-label">${esc(input.name)}<input class="form-control" type="number" data-input="${esc(input.name)}" value="${esc(input.default)}" min="${esc(input.minimum)}" max="${esc(input.maximum)}" required /></label></div>`).join('');
}

async function refreshSchedules() {
  schedules = await api.get('/schedules');
  $('[data-schedules]', root).innerHTML = schedules.map(item => `<tr data-schedule="${esc(item.id)}">
    <td><strong>${esc(item.jobId)}</strong><br/><small>${esc(tree.find(s => s.id === item.sessionId)?.name || item.sessionId)} · Agent ${item.agentId}</small></td>
    <td><label class="visually-hidden" for="interval-${esc(item.id)}">Interval for ${esc(item.jobId)}</label><input id="interval-${esc(item.id)}" data-row-interval class="form-control form-control-sm" type="number" min="1" max="525600" value="${item.everyMinutes}" style="width:7rem" /><button type="button" class="btn btn-sm btn-link" data-schedule-action="interval">Update</button></td>
    <td>${item.enabled ? 'Enabled' : 'Paused'}</td><td>${esc(date(item.nextAt))}</td>
    <td>${esc(item.lastStatus || 'Not run yet')}${item.lastError ? `<br/><small>${esc(item.lastError)}</small>` : ''}${item.lastTaskId ? `<br/><a href="/executions?device=${enc(device.value)}&session=${enc(item.sessionId)}&task=${item.lastTaskId}">View run #${item.lastTaskId}</a>` : ''}</td>
    <td><div class="d-flex gap-1 flex-wrap"><button type="button" class="btn btn-sm btn-outline-secondary" data-schedule-action="toggle">${item.enabled ? 'Pause' : 'Resume'}</button><button type="button" class="btn btn-sm btn-outline-secondary" data-schedule-action="tick">Check now</button><button type="button" class="btn btn-sm btn-outline-danger" data-schedule-action="delete">Delete</button></div></td></tr>`).join('');
  $('[data-schedules-empty]', root).hidden = schedules.length !== 0;
  const scope = $('[data-token-schedule]', root), previous = scope.value;
  scope.innerHTML = schedules.map(item => option(item.id, `${item.jobId} · ${item.id.slice(0, 8)}`)).join('') + option('', 'All schedules');
  if (schedules.some(item => item.id === previous)) scope.value = previous;
  const credentials = await api.get('/trigger-credentials');
  $('[data-credentials]', root).innerHTML = credentials.map(item => `<tr><td>${esc(item.label)}</td><td>${esc(item.scheduleId ? item.scheduleId.slice(0, 8) : 'All schedules')} · trigger only</td><td>${esc(date(item.expiresAt))}${item.expiresAt <= Date.now() ? ' (expired)' : ''}</td><td><button type="button" class="btn btn-sm btn-outline-danger" data-revoke="${esc(item.id)}">Revoke</button></td></tr>`).join('');
}

async function history(append = false) {
  if (!session.value) { $('[data-executions]', root).innerHTML = ''; $('[data-history-empty]', root).hidden = false; return; }
  const params = new URLSearchParams({ kind: $('[data-kind]', root).value, status: $('[data-status]', root).value, q: $('[data-query]', root).value });
  if (append && next) params.set('cursor', JSON.stringify(next));
  const page = await api.get(`/sessions/${enc(session.value)}/executions?${params}`);
  next = page.next;
  const rows = page.items.map(item => `<tr><td><button type="button" class="btn btn-link p-0" data-execution="${item.id}">${esc(item.title)} #${item.id}</button><br/><small>${esc(item.kind)}${item.run?.startedBy ? ' · ' + esc(item.run.startedBy) : ''}</small></td><td>${item.agentId}</td><td>${esc(item.status)}</td><td>${esc(date(item.startedAt))}</td><td>${esc(duration(item.durationMs))}</td><td>${esc(item.kind === 'job' ? auditLabel(item) : '—')}</td></tr>`).join('');
  if (append) $('[data-executions]', root).insertAdjacentHTML('beforeend', rows); else $('[data-executions]', root).innerHTML = rows;
  $('[data-history-empty]', root).hidden = !!$('[data-executions]', root).children.length;
  $('[data-more]', root).hidden = !next;
}

async function detail(id) {
  const result = await api.get(`/sessions/${enc(session.value)}/executions/${enc(id)}`);
  selected = id;
  historyUrl(id);
  const audit = result.run?.audit;
  const panel = $('[data-detail]', root);
  panel.hidden = false;
  const missing = audit?.missing || [];
  const failure = result.run?.error || result.task?.state?.outcome?.error?.message;
  const phases = (result.run?.phases || []).map(phase => `<li class="mb-2"><strong>${esc(phase.label)}</strong> · ${esc(phase.status)}${phase.summary ? `<p class="mb-1 text-body-secondary">${esc(phase.summary)}</p>` : ''}${phase.steps?.length ? `<ul>${phase.steps.map(step => `<li><details><summary>${esc(step.label)} · ${esc(step.status)} · ${esc(duration(step.ms))}</summary>${step.detail ? `<pre class="small automation-output">${esc(step.detail)}</pre>` : ''}</details></li>`).join('')}</ul>` : ''}</li>`).join('');
  const taskRows = (result.tasks || []).map(task => `<li class="mb-2"><details><summary>#${task.id} ${esc(task.kind)} · ${esc(task.state.outcome?.status || task.state.status)} · ${esc(duration(task.startedAt != null && task.endedAt != null ? task.endedAt - task.startedAt : null))}</summary><pre class="small automation-output">${esc(JSON.stringify(task, null, 2))}</pre></details></li>`).join('');
  panel.innerHTML = `<h2 class="h4">${esc(result.title)} #${result.id}</h2><p>${esc(result.status)} · ${esc(duration(result.durationMs))} · ${count(result.usage.totalTokens)} recorded tokens · ${cost(result.usage.cost)}</p>
    ${failure ? `<p class="text-danger">${esc(failure)}</p>` : ''}
    ${result.kind === 'job' ? `<h3 class="h6">Completion audit: ${esc(auditLabel(result))}</h3><p>${audit ? `${audit.done.length} of ${audit.expected.length} requirements satisfied.` : isLive(result.status) ? 'The audit runs when the job completes.' : 'This older run has no completion audit.'}</p>${missing.length ? `<p class="text-danger">Missing: ${missing.map(esc).join(', ')}</p>` : ''}<ul>${Object.entries(result.run?.checks || {}).map(([name, check]) => `<li>${esc(name)}: ${check.passed ? 'passed' : 'failed'}${check.detail ? ' — ' + esc(check.detail) : ''}</li>`).join('')}</ul><h3 class="h6">Phases and steps</h3><ol>${phases}</ol>` : ''}
    ${result.kind === 'job' && supports('artifacts') ? '<h3 class="h6">Artifacts</h3><div data-artifacts><p class="text-body-secondary">Loading artifacts…</p></div>' : ''}
    ${result.ledger ? `<h3 class="h6">Budget ledger</h3><p data-ledger>${result.ledger.reservations.length} reservation(s) · ${esc(meters(result.ledger.totals))} · ${result.ledger.costKnown ? 'cost known' : 'cost not fully known'}${result.ledger.uncertain ? ` · <span class="text-danger">${result.ledger.uncertain} uncertain</span> (<a href="/budgets?device=${enc(device.value)}">reconcile</a>)` : ''}</p>` : ''}
    <h3 class="h6">Tasks and subagents</h3><ul class="list-unstyled">${taskRows}</ul><details><summary>Recorded messages and tool results (${result.entries.length})</summary><pre class="small automation-output">${esc(JSON.stringify(result.entries, null, 2))}</pre></details>`;
  const cards = $('[data-artifacts]', panel);
  if (cards) {
    cards.innerHTML = await fragment(`/ui/${enc(device.value)}/${enc(session.value)}/executions/${enc(id)}/artifacts`);
    if (location.hash.startsWith('#artifact-')) document.getElementById(location.hash.slice(1))?.scrollIntoView();
  }
}

async function artifactAction(button) {
  const label = button.dataset.label || button.dataset.artifactAction;
  if (!confirm(`${label}? This starts a new execution on the device.`)) return;
  const started = await api.post(`/sessions/${enc(session.value)}/artifacts/${enc(button.dataset.artifactId)}/actions/${enc(button.dataset.artifactAction)}`,
    { confirm: true }, { idempotencyKey: uuid() });
  await history();
  await detail(String(started.taskId));
}

function historyUrl(id) {
  const params = new URLSearchParams({ device: device.value, session: session.value });
  if (id) params.set('task', id);
  window.history.replaceState(null, '', `/executions?${params}`);
}

device.addEventListener('change', () => run(connect));
session.addEventListener('change', () => run(async () => {
  selected = null; next = null;
  if (mode === 'schedules') await loadAgents();
  else { $('[data-detail]', root).hidden = true; historyUrl(); await history(); }
}));

if (mode === 'schedules') {
  $('[data-agent]', root).addEventListener('change', () => run(loadJobs));
  $('[data-job]', root).addEventListener('change', jobInputs);
  $('[data-refresh]', root).addEventListener('click', () => run(refreshSchedules));
  $('[data-dismiss-token]', root).addEventListener('click', clearSecret);
  $('[data-create-schedule]', root).addEventListener('submit', event => { event.preventDefault(); run(async () => {
    const input = Object.fromEntries($$('[data-input]', root).map(element => [element.dataset.input, Number(element.value)]));
    await api.post('/schedules', { sessionId: session.value, agentId: Number($('[data-agent]', root).value), jobId: $('[data-job]', root).value, everyMinutes: Number($('[data-interval]', root).value), input });
    await refreshSchedules();
  }); });
  $('[data-schedules]', root).addEventListener('click', event => {
    const button = event.target.closest('[data-schedule-action]');
    if (!button) return;
    const row = button.closest('[data-schedule]'), item = schedules.find(item => item.id === row.dataset.schedule);
    run(async () => {
      const path = `/schedules/${enc(item.id)}`;
      switch (button.dataset.scheduleAction) {
        case 'toggle': await api.patch(path, { enabled: !item.enabled }); break;
        case 'interval': { const input = $('[data-row-interval]', row); if (!input.reportValidity()) return; await api.patch(path, { everyMinutes: Number(input.value) }); break; }
        case 'tick': await api.post(path + '/tick'); break;
        case 'delete': if (!confirm('Delete this schedule and revoke tokens restricted to it? Existing runs will continue.')) return; await api.del(path); break;
      }
      await refreshSchedules();
    });
  });
  $('[data-create-credential]', root).addEventListener('submit', event => { event.preventDefault(); run(async () => {
    clearSecret();
    const result = await api.post('/trigger-credentials', { label: $('[data-token-label]', root).value, scheduleId: $('[data-token-schedule]', root).value || null, expiresInHours: Number($('[data-token-hours]', root).value) });
    $('[data-issued-token]', root).textContent = result.token; $('[data-issued]', root).hidden = false;
    await refreshSchedules();
  }); });
  $('[data-credentials]', root).addEventListener('click', event => {
    const button = event.target.closest('[data-revoke]');
    if (button) run(async () => { await api.del(`/trigger-credentials/${enc(button.dataset.revoke)}`); clearSecret(); await refreshSchedules(); });
  });
} else {
  $('[data-history-filters]', root).addEventListener('submit', event => { event.preventDefault(); run(async () => { await history(); if (selected) await detail(selected); }); });
  $('[data-more]', root).addEventListener('click', () => run(() => history(true)));
  $('[data-executions]', root).addEventListener('click', event => { const button = event.target.closest('[data-execution]'); if (button) run(() => detail(button.dataset.execution)); });
  $('[data-detail]', root).addEventListener('click', event => { const button = event.target.closest('[data-artifact-action]'); if (button) run(() => artifactAction(button)); });
  wireFeedback($('[data-detail]', root), { get: path => api.get(path), post: (path, body, options) => api.post(path, body, options) }, () => session.value);
}
window.addEventListener('pagehide', clearSecret);
const initial = new URLSearchParams(location.search).get('device');
if ([...device.options].some(option => option.value === initial)) device.value = initial;
run(connect);
