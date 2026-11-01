// Monitored sources and source checks (decision 0010). Every call goes through the hub's proxy, so the page holds no token and never sees a
// source's credential. Adding a source and starting a check send an Idempotency-Key; edits and deletes send the revision they were made from.
import { $, deviceApi, initCommon, uuid, escapeHtml as esc } from './core.js';

initCommon();
const root = $('[data-automation]');
const device = $('[data-device]', root);
const session = $('[data-session]', root);
const error = $('[data-error]', root);
const loading = $('[data-loading]', root);
const form = $('[data-edit]', root);
const enc = encodeURIComponent;
const date = value => value == null ? '—' : new Date(value).toLocaleString();
const option = (value, label) => `<option value="${esc(value)}">${esc(label)}</option>`;
const tones = { ok: 'ok', degraded: 'warn', new: undefined };
const fields = ['title', 'url', 'repository', 'maxItems', 'maxPages', 'credentialRef', 'enabled', 'includePrereleases', 'allowFallback'];
let api, sources = [], selected = null, editing = null, busy = false;

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

async function connect() {
  selected = null;
  $('[data-source-detail]', root).hidden = true;
  stopEditing();
  if (!device.value) throw new Error('Add a device first.');
  api = deviceApi(device.value);
  const info = await api.get('/device');
  const supported = (info.features || []).some(item => item.id === 'sources');
  const judging = (info.features || []).some(item => item.id === 'policies');
  $('[data-unsupported]', root).hidden = supported;
  $('[data-supported]', root).hidden = !supported;
  if (!supported) return;
  const tree = await api.get('/tree');
  const previous = new URLSearchParams(location.search).get('session');
  session.innerHTML = (tree.sessions || []).map(item => option(item.id, item.name)).join('');
  if ((tree.sessions || []).some(item => item.id === previous)) session.value = previous;
  const policies = judging ? (await api.get('/policies')).items.filter(item => item.activeVersion != null) : [];
  $('[data-check-policy]', root).innerHTML = option('', 'No judgment') + policies.map(item => option(item.id, `${item.title} (v${item.activeVersion})`)).join('');
  await refresh();
}

function status(state) {
  const label = { ok: 'OK', degraded: `Failing (${state.failures})`, new: 'Not checked yet' }[state.status] || state.status;
  const tone = tones[state.status];
  return `<span class="ps-chip"${tone ? ` data-tone="${tone}"` : ''}>${esc(label)}</span>`
    + (state.lastError ? `<br/><small class="text-danger" title="${esc(date(state.lastErrorAt))}">${esc(state.lastError)}</small>` : '');
}

async function refresh() {
  sources = (await api.get('/sources')).items;
  $('[data-sources]', root).innerHTML = sources.map(item => `<tr data-source-row="${esc(item.id)}">
    <td><button type="button" class="btn btn-link p-0" data-source="${esc(item.id)}">${esc(item.title || item.id)}</button><br/>
      <small class="text-body-secondary">${esc(item.kind)} · ${esc(item.repository || item.url || '')}${item.enabled ? '' : ' · <strong>disabled</strong>'}</small></td>
    <td>${status(item.state)}</td><td>${esc(date(item.state.lastCheckedAt))}</td><td>${esc(date(item.state.lastChangedAt))}</td><td>${item.state.items}</td>
    <td>${item.state.nextEligibleAt && item.state.nextEligibleAt > Date.now() ? esc(date(item.state.nextEligibleAt)) : '—'}</td>
    <td class="text-nowrap"><button type="button" class="btn btn-sm btn-outline-secondary" data-edit-source="${esc(item.id)}">Edit</button>
      <button type="button" class="btn btn-sm btn-outline-secondary" data-toggle="${esc(item.id)}">${item.enabled ? 'Disable' : 'Enable'}</button>
      <button type="button" class="btn btn-sm btn-outline-danger" data-delete="${esc(item.id)}" aria-label="Delete ${esc(item.title || item.id)}">Delete</button></td></tr>`).join('');
  $('[data-sources-empty]', root).hidden = sources.length !== 0;
  const ticked = new Set([...root.querySelectorAll('[data-check-source]:checked')].map(box => box.value));
  $('[data-check-sources]', root).innerHTML = sources.map(item => `<div class="form-check"><input class="form-check-input" type="checkbox" id="check-${esc(item.id)}"
    value="${esc(item.id)}" data-check-source ${ticked.has(item.id) ? 'checked' : ''} /><label class="form-check-label" for="check-${esc(item.id)}">${esc(item.title || item.id)}</label></div>`).join('');
  const health = (await api.get('/retrieval/health')).items;
  $('[data-health]', root).innerHTML = health.map(item => `<tr><td>${esc(item.provider)}</td><td><code>${esc(item.origin)}</code></td><td>${item.failures}</td>
    <td>${esc(item.lastFailure || '—')}${item.lastStatus ? ` (HTTP ${item.lastStatus})` : ''}</td>
    <td>${esc(date(Math.max(item.cooldownUntil || 0, item.retryAfterUntil || 0) || null))}${item.retryAfterUntil ? ' <small class="text-body-secondary">Retry-After</small>' : ''}</td></tr>`).join('');
  $('[data-health-empty]', root).hidden = health.length !== 0;
  if (selected && sources.some(item => item.id === selected)) await showSource(selected);
  else { selected = null; $('[data-source-detail]', root).hidden = true; }
}

async function showSource(id) {
  const source = await api.get(`/sources/${enc(id)}`);
  const items = (await api.get(`/sources/${enc(id)}/items?limit=50`)).items;
  selected = id;
  const panel = $('[data-source-detail]', root);
  panel.hidden = false;
  panel.innerHTML = `<h2 class="h5" id="source-detail-title">${esc(source.title || source.id)} <small class="text-body-secondary">${esc(source.id)} · revision ${source.revision}</small></h2>
    <p>${status(source.state)} · origin <code>${esc(source.origin)}</code>${source.credentialRef ? ` · credential <code>${esc(source.credentialRef)}</code>` : ''}
      · last success ${esc(date(source.state.lastSuccessAt))}</p>
    ${source.health.length ? `<p class="text-body-secondary">This origin: ${source.health.map(item => `${esc(item.provider)} failed ${item.failures}×${item.cooldownUntil ? `, cooling down until ${esc(date(item.cooldownUntil))}` : ''}`).join('; ')}</p>` : ''}
    <h3 class="h6">Items</h3>
    <div class="table-responsive"><table class="table table-sm align-middle"><thead><tr><th>Item</th><th>Published</th><th>Changed</th><th>Version</th></tr></thead><tbody>
    ${items.map(item => `<tr${item.absentAt ? ' class="text-body-secondary"' : ''}><td>${/^https?:\/\//i.test(item.url || '') ? `<a href="${esc(item.url)}" rel="noopener noreferrer" target="_blank">${esc(item.title)}</a>` : esc(item.title)}
      ${item.absentAt ? '<br/><small>Gone from the source</small>' : ''}</td><td>${esc(date(item.publishedAt))}</td><td>${esc(date(item.changedAt))}</td><td>${item.version}</td></tr>`).join('')}
    </tbody></table></div>
    ${items.length ? '' : '<p class="text-body-secondary">No items yet. Run a check.</p>'}
    <details><summary>Recent fetches</summary><div class="table-responsive"><table class="table table-sm align-middle"><thead><tr><th>When</th><th>Provider</th><th>Result</th><th>Run</th></tr></thead><tbody>
    ${source.receipts.map(item => `<tr><td>${esc(date(item.createdAt))}</td><td>${esc(item.provider)}</td>
      <td>${esc(item.status)}${item.httpStatus ? ` · HTTP ${item.httpStatus}` : ''}${item.error ? `<br/><small class="text-danger">${esc(item.error)}</small>` : ''}</td><td><code>${esc(item.runKey)}</code></td></tr>`).join('')}
    </tbody></table></div></details>`;
}

function showKind() {
  const kind = $('[data-field="kind"]', form).value;
  for (const element of form.querySelectorAll('[data-for-kind]')) element.hidden = !element.dataset.forKind.split(' ').includes(kind);
}

function read() {
  const spec = { kind: $('[data-field="kind"]', form).value };
  const kind = spec.kind;
  for (const name of fields) {
    const input = $(`[data-field="${name}"]`, form);
    if (input.closest('[data-for-kind]')?.hidden) { spec[name] = null; continue; }
    if (input.type === 'checkbox') spec[name] = input.checked;
    else if (input.type === 'number') spec[name] = input.value === '' ? null : Number(input.value);
    else spec[name] = input.value.trim() || null;
  }
  if (kind !== 'github-releases') spec.includePrereleases = null;
  return spec;
}

function edit(id) {
  const source = sources.find(item => item.id === id);
  if (!source) return;
  editing = source;
  $('[data-field="id"]', form).value = source.id;
  $('[data-field="id"]', form).readOnly = true;
  $('[data-field="kind"]', form).value = source.kind;
  $('[data-field="kind"]', form).disabled = true;
  for (const name of fields) {
    const input = $(`[data-field="${name}"]`, form);
    if (input.type === 'checkbox') input.checked = name === 'enabled' ? source.enabled !== false : source[name] === true;
    else input.value = source[name] ?? '';
  }
  showKind();
  $('[data-edit-title]', root).textContent = `Edit ${source.title || source.id}`;
  $('[data-edit-submit]', root).textContent = 'Save changes';
  $('[data-edit-cancel]', root).hidden = false;
  $('[data-field="title"]', form).focus();
}

function stopEditing() {
  editing = null;
  form.reset();
  $('[data-field="id"]', form).readOnly = false;
  $('[data-field="kind"]', form).disabled = false;
  showKind();
  $('[data-edit-title]', root).textContent = 'Add a source';
  $('[data-edit-submit]', root).textContent = 'Add source';
  $('[data-edit-cancel]', root).hidden = true;
}

device.addEventListener('change', () => run(connect));
$('[data-refresh]', root).addEventListener('click', () => run(refresh));
$('[data-field="kind"]', form).addEventListener('change', showKind);
$('[data-edit-cancel]', root).addEventListener('click', stopEditing);
$('[data-sources]', root).addEventListener('click', event => {
  const show = event.target.closest('[data-source]');
  if (show) return run(() => showSource(show.dataset.source));
  const change = event.target.closest('[data-edit-source]');
  if (change) return edit(change.dataset.editSource);
  const toggle = event.target.closest('[data-toggle]');
  if (toggle) return run(async () => {
    const source = sources.find(item => item.id === toggle.dataset.toggle);
    await api.patch(`/sources/${enc(source.id)}`, { enabled: !source.enabled, expectedRevision: source.revision });
    await refresh();
  });
  const remove = event.target.closest('[data-delete]');
  if (remove) {
    const source = sources.find(item => item.id === remove.dataset.delete);
    if (!confirm(`Delete ${source.title || source.id}? Its items and fetch history stay on the device; checks stop.`)) return;
    run(async () => {
      await api.del(`/sources/${enc(source.id)}?expectedRevision=${source.revision}`);
      if (editing?.id === source.id) stopEditing();
      await refresh();
    });
  }
});
form.addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const spec = read();
    if (editing) {
      const { kind, ...changes } = spec;
      const saved = await api.patch(`/sources/${enc(editing.id)}`, { ...changes, expectedRevision: editing.revision });
      selected = saved.id;
      $('[data-saved]', root).textContent = `Saved ${saved.id} (revision ${saved.revision}).`;
    } else {
      const created = await api.post('/sources', Object.fromEntries(Object.entries({ id: $('[data-field="id"]', form).value.trim(), ...spec })
        .filter(([, value]) => value !== null)), { idempotencyKey: uuid() });
      selected = created.id;
      $('[data-saved]', root).textContent = `Added ${created.id}.`;
    }
    stopEditing();
    await refresh();
  });
});
$('[data-check]', root).addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    if (!session.value) throw new Error('Choose a session first.');
    const policyId = $('[data-check-policy]', root).value;
    const reevaluate = $('[data-check-reevaluate]', root).checked;
    if (reevaluate && !policyId) throw new Error('Choose a policy to re-judge items with.');
    const requests = $('[data-check-requests]', root).value;
    const sourceIds = [...root.querySelectorAll('[data-check-source]:checked')].map(box => box.value);
    const started = await api.post(`/sessions/${enc(session.value)}/source-checks`, {
      sourceIds: sourceIds.length ? sourceIds : undefined,
      policyId: policyId || undefined,
      reevaluate: reevaluate || undefined,
      force: $('[data-check-force]', root).checked || undefined,
      budget: requests ? { requests: Number(requests) } : undefined,
    }, { idempotencyKey: uuid() });
    $('[data-checked]', root).innerHTML = `Started <a href="/executions?device=${enc(device.value)}&session=${enc(session.value)}&task=${started.taskId}">run #${started.taskId}</a>.`;
  });
});
showKind();
const initial = new URLSearchParams(location.search).get('device');
if ([...device.options].some(item => item.value === initial)) device.value = initial;
run(connect);
