// Preference profiles (decision 0010, M5). Every change carries the profile's revision, so a save from a stale page conflicts instead of
// overwriting another change. Feedback itself is given on artifact cards.
import { $, $$, deviceApi, initCommon, escapeHtml as esc } from './core.js';

initCommon();
const root = $('[data-automation]');
const device = $('[data-device]', root);
const select = $('[data-profile]', root);
const error = $('[data-error]', root);
const loading = $('[data-loading]', root);
const enc = encodeURIComponent;
const date = value => value == null ? '—' : new Date(value).toLocaleString();
let api, profile = null, busy = false;

function showError(failure) {
  const fields = failure.fields?.length
    ? `<ul class="mb-0 mt-1">${failure.fields.map(field => `<li><code>${esc(field.path || '/')}</code> ${esc(field.message)}</li>`).join('')}</ul>` : '';
  error.innerHTML = esc(failure.message) + (failure.code === 'conflict' ? ' Refresh to see the current profile.' : '') + fields;
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
  if (!device.value) throw new Error('Add a device first.');
  api = deviceApi(device.value);
  const info = await api.get('/device');
  const supported = (info.features || []).some(item => item.id === 'preferences');
  $('[data-unsupported]', root).hidden = supported;
  $('[data-supported]', root).hidden = !supported;
  select.disabled = !supported;
  if (supported) await list();
}

async function list() {
  const chosen = select.value || new URLSearchParams(location.search).get('profile') || 'default';
  const { items } = await api.get('/preferences');
  select.innerHTML = items.map(item => `<option value="${esc(item.id)}">${esc(item.id)} · r${item.revision}</option>`).join('');
  select.value = items.some(item => item.id === chosen) ? chosen : 'default';
  render(items.find(item => item.id === select.value));
}

async function refresh() {
  render(await api.get(`/preferences/${enc(select.value)}`));
}

function render(next) {
  profile = next;
  if (select.selectedOptions[0]) select.selectedOptions[0].textContent = `${profile.id} · r${profile.revision}`;
  const params = new URLSearchParams({ device: device.value, profile: profile.id });
  window.history.replaceState(null, '', `/preferences?${params}`);
  $('[data-summary]', root).innerHTML = `Profile <strong>${esc(profile.id)}</strong> at revision <strong>${profile.revision}</strong> · ${profile.effective} effective of ${profile.events} event(s) on ${profile.targets} artifact(s) · updated ${esc(date(profile.updatedAt))}`;
  const weights = Object.entries(profile.weights || {}).sort(([, a], [, b]) => b - a);
  $('[data-weights]', root).innerHTML = weights.map(([label, weight]) => `<tr><th scope="row"><code>${esc(label)}</code></th>
    <td><span class="${weight < 0 ? 'text-danger' : 'text-success'}">${weight > 0 ? '+' : ''}${weight.toFixed(2)}</span>
      <span class="preference-bar" style="--weight:${Math.abs(weight)}" data-tone="${weight < 0 ? 'less' : 'more'}" aria-hidden="true"></span></td></tr>`).join('')
    || `<tr><td colspan="2" class="text-body-secondary">${profile.effective
      ? 'No weights: the feedback so far names no labels. Judged findings carry their label; for other artifacts, name labels on the card.'
      : 'No weights yet. Give feedback on an artifact to start.'}</td></tr>`;
  $('[data-examples]', root).innerHTML = (profile.examples || []).map(item => `<li class="border rounded p-2" data-example="${esc(item.eventId)}">
    <div class="d-flex justify-content-between gap-2 flex-wrap"><strong>${esc(item.title || item.target?.artifactId || item.eventId)}</strong>
      <button type="button" class="btn btn-sm btn-outline-secondary" data-forget="${esc(item.eventId)}">Forget</button></div>
    <div class="small text-body-secondary">${esc(item.action)}${item.labels?.length ? ` · ${item.labels.map(esc).join(', ')}` : ''} · ${esc(date(item.at))}</div>
    ${item.note ? `<p class="small mb-0" style="white-space: pre-wrap">${esc(item.note)}</p>` : ''}</li>`).join('');
  $('[data-examples-empty]', root).hidden = (profile.examples || []).length !== 0;
  for (const input of $$('[data-setting]', root)) input.value = profile.settings[input.dataset.setting];
  $('[data-export]', root).href = `/api/devices/${enc(device.value)}/v1/preferences/${enc(profile.id)}/export`;
  $('[data-export]', root).setAttribute('download', `preferences-${profile.id}.json`);
  $('[data-reset]', root).disabled = profile.revision === 0;
  $('[data-delete]', root).disabled = profile.revision === 0;
}

device.addEventListener('change', () => run(connect));
select.addEventListener('change', () => run(refresh));
$('[data-refresh]', root).addEventListener('click', () => run(list));
$('[data-settings]', root).addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const settings = Object.fromEntries($$('[data-setting]', root).map(input => [input.dataset.setting, Number(input.value)]));
    render(await api.patch(`/preferences/${enc(profile.id)}`, { settings, expectedRevision: profile.revision }));
  });
});
$('[data-examples]', root).addEventListener('click', event => {
  const button = event.target.closest('[data-forget]');
  if (button) run(async () => render(await api.patch(`/preferences/${enc(profile.id)}`, { forgetExamples: [button.dataset.forget], expectedRevision: profile.revision })));
});
$('[data-reset]', root).addEventListener('click', () => run(async () => {
  if (!confirm(`Reset ${profile.id}? Its weights and examples start over; the history stays for export.`)) return;
  render(await api.post(`/preferences/${enc(profile.id)}/reset`, { expectedRevision: profile.revision }));
}));
$('[data-delete]', root).addEventListener('click', () => run(async () => {
  if (!confirm(`Delete ${profile.id} and all its feedback? This cannot be undone.`)) return;
  await api.del(`/preferences/${enc(profile.id)}?expectedRevision=${profile.revision}`);
  select.value = 'default';
  await list();
}));

const initial = new URLSearchParams(location.search).get('device');
if ([...device.options].some(option => option.value === initial)) device.value = initial;
run(connect);
