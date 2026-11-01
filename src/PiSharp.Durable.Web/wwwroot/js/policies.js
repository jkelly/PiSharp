// Judgment policies and judgments (decision 0010). Every call goes through the hub's proxy, so the page holds no token. Publishing and
// evaluating send an Idempotency-Key, so a retried click never publishes or starts twice.
import { $, deviceApi, initCommon, uuid, escapeHtml as esc } from './core.js';

initCommon();
const root = $('[data-automation]');
const device = $('[data-device]', root);
const session = $('[data-session]', root);
const error = $('[data-error]', root);
const loading = $('[data-loading]', root);
const enc = encodeURIComponent;
const date = value => value == null ? '—' : new Date(value).toLocaleString();
const percent = value => typeof value === 'number' ? `${Math.round(value * 100)}%` : '—';
const option = (value, label) => `<option value="${esc(value)}">${esc(label)}</option>`;
let api, policies = [], judgments = [], selected = null, busy = false, info = null;

function template() {
  return {
    title: 'Release relevance',
    kind: 'structured',
    model: { provider: info?.defaultProvider || 'faux', id: info?.defaultModel || 'echo' },
    instructions: 'Decide whether the release matters to this team.',
    inputSchema: { type: 'object', additionalProperties: false, required: ['title'], properties: { title: { type: 'string', maxLength: 200 } } },
    labels: [{ id: 'relevant', description: 'It matters to us' }, { id: 'ignore', description: 'It does not' }],
    thresholds: { auto: 0.9, confirm: 0.5 },
  };
}

function showError(failure) {
  const fields = failure.fields?.length
    ? `<ul class="mb-0 mt-1">${failure.fields.map(field => `<li><code>${esc(field.path || '/')}</code> ${esc(field.message)}</li>`).join('')}</ul>` : '';
  error.innerHTML = esc(failure.message) + fields;
  error.hidden = false;
}

async function run(action) {
  if (busy) return;
  busy = true; root.inert = true; root.setAttribute('aria-busy', 'true'); error.hidden = true; loading.textContent = 'Loading…';
  try { await action(); }
  catch (failure) { showError(failure); }
  finally { busy = false; root.inert = false; root.removeAttribute('aria-busy'); loading.textContent = ''; }
}

async function connect() {
  selected = null;
  $('[data-policy-detail]', root).hidden = true;
  if (!device.value) throw new Error('Add a device first.');
  api = deviceApi(device.value);
  info = await api.get('/device');
  const supported = ['policies', 'judgments'].every(id => (info.features || []).some(item => item.id === id));
  $('[data-unsupported]', root).hidden = supported;
  $('[data-supported]', root).hidden = !supported;
  if (!supported) return;
  const tree = await api.get('/tree');
  const previous = new URLSearchParams(location.search).get('session');
  session.innerHTML = (tree.sessions || []).map(item => option(item.id, item.name)).join('');
  if ((tree.sessions || []).some(item => item.id === previous)) session.value = previous;
  if (!$('[data-spec]', root).value) $('[data-spec]', root).value = JSON.stringify(template(), null, 2);
  await refresh();
}

async function refresh() {
  policies = (await api.get('/policies')).items;
  $('[data-policies]', root).innerHTML = policies.map(item => `<tr>
    <td><button type="button" class="btn btn-link p-0" data-policy="${esc(item.id)}">${esc(item.title)}</button><br/><small class="text-body-secondary">${esc(item.id)}</small></td>
    <td>${esc(item.kind)}</td><td>${item.activeVersion == null ? '—' : 'v' + item.activeVersion}</td><td>v${item.latestVersion}</td><td>${esc(date(item.updatedAt))}</td></tr>`).join('');
  $('[data-policies-empty]', root).hidden = policies.length !== 0;
  const chosen = $('[data-evaluate-policy]', root).value;
  $('[data-evaluate-policy]', root).innerHTML = policies.filter(item => item.activeVersion != null).map(item => option(item.id, `${item.title} (v${item.activeVersion})`)).join('');
  if (policies.some(item => item.id === chosen)) $('[data-evaluate-policy]', root).value = chosen;
  if (selected) await showPolicy(selected);
  await refreshJudgments();
}

async function showPolicy(id) {
  const policy = await api.get(`/policies/${enc(id)}`);
  selected = id;
  const panel = $('[data-policy-detail]', root);
  panel.hidden = false;
  panel.innerHTML = `<h2 class="h5" id="policy-detail-title">${esc(policy.title)} <small class="text-body-secondary">${esc(policy.id)}</small></h2>
    <p>Active version ${policy.activeVersion == null ? 'none' : 'v' + policy.activeVersion} · revision ${policy.revision}</p>
    <div class="table-responsive"><table class="table table-sm align-middle"><thead><tr><th>Version</th><th>Hash</th><th>Published</th><th></th></tr></thead><tbody>
    ${policy.versions.map(version => `<tr><td>v${version.version}</td><td><code>${esc(version.hash.slice(0, 19))}…</code></td><td>${esc(date(version.createdAt))}</td>
      <td>${version.version === policy.activeVersion ? '<span class="ps-chip" data-tone="ok">Active</span>'
        : `<button type="button" class="btn btn-sm btn-outline-secondary" data-activate="${version.version}" data-revision="${policy.revision}">Activate</button>`}</td></tr>`).join('')}
    </tbody></table></div>
    ${policy.active ? `<details><summary>Active specification</summary><pre class="small automation-output" data-active-spec>${esc(JSON.stringify(policy.active.spec, null, 2))}</pre></details>
      <button type="button" class="btn btn-sm btn-outline-secondary mt-2" data-edit-spec>Edit as a new version</button>` : ''}`;
  panel.dataset.policyId = policy.id;
  panel.activeSpec = policy.active?.spec;
}

async function refreshJudgments() {
  if (!session.value) { judgments = []; $('[data-judgments]', root).innerHTML = ''; $('[data-judgments-empty]', root).hidden = false; return; }
  judgments = (await api.get(`/sessions/${enc(session.value)}/judgments?limit=50`)).items;
  $('[data-judgments]', root).innerHTML = judgments.map(item => {
    const result = item.result || {};
    const waiting = item.decision === 'pending';
    const last = (item.decisions || []).at(-1);
    return `<tr data-judgment="${esc(item.id)}">
      <td>${esc(item.input?.title || item.policy?.title || item.id)}<br/><small class="text-body-secondary">${esc(item.policy?.id)} v${item.policy?.version} · <a href="/executions?device=${enc(device.value)}&session=${enc(session.value)}&task=${item.runTaskId}">run #${item.runTaskId}</a></small></td>
      <td>${esc(result.label || item.status)}${result.confidence != null ? ` · ${percent(result.confidence)}` : ''}${item.error ? `<br/><small class="text-danger">${esc(item.error)}</small>` : ''}</td>
      <td>${esc(item.route || '—')}${item.routeReason ? `<br/><small class="text-body-secondary">${esc(item.routeReason)}</small>` : ''}</td>
      <td>${esc(item.decision || '—')}${last ? `<br/><small class="text-body-secondary">${esc(last.actor)}${last.reason ? ': ' + esc(last.reason) : ''}</small>` : ''}</td>
      <td>${waiting ? `<label class="visually-hidden" for="reason-${esc(item.id)}">Reason</label><input id="reason-${esc(item.id)}" class="form-control form-control-sm mb-1" maxlength="1000" placeholder="Reason (optional)" data-reason />
        <div class="d-flex gap-1"><button type="button" class="btn btn-sm btn-primary" data-decide="approve">Approve</button><button type="button" class="btn btn-sm btn-outline-secondary" data-decide="reject">Reject</button></div>` : ''}</td></tr>`;
  }).join('');
  $('[data-judgments-empty]', root).hidden = judgments.length !== 0;
}

function parse(text, what) {
  try { return JSON.parse(text); }
  catch (failure) { throw new Error(`${what} is not valid JSON: ${failure.message}`); }
}

device.addEventListener('change', () => run(connect));
session.addEventListener('change', () => run(refreshJudgments));
$('[data-refresh]', root).addEventListener('click', () => run(refresh));
$('[data-policies]', root).addEventListener('click', event => {
  const button = event.target.closest('[data-policy]');
  if (button) run(() => showPolicy(button.dataset.policy));
});
$('[data-policy-detail]', root).addEventListener('click', event => {
  const panel = $('[data-policy-detail]', root);
  const activate = event.target.closest('[data-activate]');
  if (activate) run(async () => {
    await api.put(`/policies/${enc(panel.dataset.policyId)}/active`, { version: Number(activate.dataset.activate), expectedRevision: Number(activate.dataset.revision) });
    await refresh();
  });
  if (event.target.closest('[data-edit-spec]')) {
    $('[data-policy-id]', root).value = panel.dataset.policyId;
    $('[data-spec]', root).value = JSON.stringify(panel.activeSpec, null, 2);
    $('[data-spec]', root).focus();
  }
});
$('[data-publish]', root).addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const id = $('[data-policy-id]', root).value.trim();
    const published = await api.post(`/policies/${enc(id)}/versions`, parse($('[data-spec]', root).value, 'The specification'), { idempotencyKey: uuid() });
    selected = id;
    await refresh();
    $('[data-published]', root).textContent = `Published ${id} v${published.version}.`;
  });
});
$('[data-judgments]', root).addEventListener('click', event => {
  const button = event.target.closest('[data-decide]');
  if (!button) return;
  const row = button.closest('[data-judgment]');
  run(async () => {
    const reason = $('[data-reason]', row).value.trim();
    await api.post(`/sessions/${enc(session.value)}/judgments/${enc(row.dataset.judgment)}/decision`, { choice: button.dataset.decide, reason: reason || undefined });
    await refreshJudgments();
  });
});
$('[data-evaluate]', root).addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    if (!session.value) throw new Error('Choose a session first.');
    const title = $('[data-evaluate-title]', root).value.trim();
    const started = await api.post(`/sessions/${enc(session.value)}/judgments`, {
      policyId: $('[data-evaluate-policy]', root).value,
      title: title || undefined,
      input: parse($('[data-evaluate-input]', root).value, 'The item'),
    }, { idempotencyKey: uuid() });
    $('[data-evaluated]', root).innerHTML = `Started <a href="/executions?device=${enc(device.value)}&session=${enc(session.value)}&task=${started.taskId}">run #${started.taskId}</a>.`;
    await refreshJudgments();
  });
});
const initial = new URLSearchParams(location.search).get('device');
if ([...device.options].some(item => item.value === initial)) device.value = initial;
run(connect);
