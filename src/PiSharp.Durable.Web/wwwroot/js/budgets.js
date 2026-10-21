// Device budgets and reservation reconciliation (decision 0010). Limits are revisioned: a save from a stale page conflicts instead of
// overwriting another change. Money meters are integer micro-units on the wire and currency units on this page.
import { $, $$, deviceApi, initCommon, escapeHtml as esc, count } from './core.js';

initCommon();
const root = $('[data-automation]');
const device = $('[data-device]', root);
const error = $('[data-error]', root);
const loading = $('[data-loading]', root);
const enc = encodeURIComponent;
const isMoney = meter => meter.startsWith('money:');
const show = (meter, value) => isMoney(meter) ? (value / 1e6).toFixed(4) : count(value);
const amounts = values => Object.entries(values || {}).map(([meter, value]) => `${esc(meter)} ${esc(show(meter, value))}`).join('<br/>') || '—';
let api, budgets = null, busy = false;

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
  if (!device.value) throw new Error('Add a device first.');
  api = deviceApi(device.value);
  const info = await api.get('/device');
  const supported = (info.features || []).some(item => item.id === 'budgets');
  $('[data-unsupported]', root).hidden = supported;
  $('[data-supported]', root).hidden = !supported;
  if (supported) await refresh();
}

async function refresh() {
  budgets = await api.get('/budgets');
  $('[data-day]', root).textContent = budgets.day;
  const meters = [...new Set([...Object.keys(budgets.limits.perDay), ...Object.keys(budgets.used)])].sort();
  $('[data-meters]', root).innerHTML = meters.map(meter => {
    const limit = budgets.limits.perDay[meter];
    const value = limit == null ? '' : isMoney(meter) ? limit / 1e6 : limit;
    return `<tr><th scope="row">${esc(meter)}</th><td>${esc(show(meter, budgets.used[meter] || 0))}</td>
      <td><label class="visually-hidden" for="limit-${esc(meter)}">Daily limit for ${esc(meter)}</label><input id="limit-${esc(meter)}" class="form-control form-control-sm" type="number" min="0" step="any" data-limit="${esc(meter)}" value="${esc(value)}" style="max-width:12rem" /></td></tr>`;
  }).join('') || '<tr><td colspan="3" class="text-body-secondary">No limits and no use today.</td></tr>';
  $('[data-reservations]', root).innerHTML = budgets.reservations.map(item => `<tr data-operation="${esc(item.operationId)}">
    <td><code>${esc(item.operationId)}</code><br/><small class="text-body-secondary">${esc(item.provider)}</small></td>
    <td>${esc(item.runKey)}</td>
    <td>${esc(item.status)}${item.detail ? `<br/><small class="text-body-secondary">${esc(item.detail)}</small>` : ''}</td>
    <td>${amounts(item.requested)}</td>
    <td>${item.status === 'uncertain' ? `<div class="d-flex gap-1 flex-wrap"><button type="button" class="btn btn-sm btn-outline-secondary" data-resolve="settled">Charged as reserved</button><button type="button" class="btn btn-sm btn-outline-secondary" data-resolve="released">Not charged</button></div>` : '<small class="text-body-secondary">In progress</small>'}</td></tr>`).join('');
  $('[data-reservations-empty]', root).hidden = budgets.reservations.length !== 0;
}

function wire(meter, text) {
  if (text === '') return null;
  const value = Number(text);
  if (!Number.isFinite(value) || value < 0) throw new Error(`The limit for ${meter} must be a non-negative number.`);
  return isMoney(meter) ? Math.round(value * 1e6) : Math.round(value);
}

device.addEventListener('change', () => run(connect));
$('[data-refresh]', root).addEventListener('click', () => run(refresh));
$('[data-limits]', root).addEventListener('submit', event => {
  event.preventDefault();
  run(async () => {
    const perDay = {};
    for (const input of $$('[data-limit]', root)) {
      const meter = input.dataset.limit;
      const value = wire(meter, input.value.trim());
      if (value !== (budgets.limits.perDay[meter] ?? null)) perDay[meter] = value;
    }
    const added = $('[data-new-meter]', root).value.trim();
    if (added) perDay[added] = wire(added, $('[data-new-limit]', root).value.trim());
    if (Object.keys(perDay).length) await api.patch('/budgets', { perDay, expectedRevision: budgets.limits.revision });
    $('[data-new-meter]', root).value = ''; $('[data-new-limit]', root).value = '';
    await refresh();
  });
});
$('[data-reservations]', root).addEventListener('click', event => {
  const button = event.target.closest('[data-resolve]');
  if (!button) return;
  const operation = button.closest('[data-operation]').dataset.operation;
  const item = budgets.reservations.find(reservation => reservation.operationId === operation);
  const charged = button.dataset.resolve === 'settled';
  if (!confirm(charged ? 'Record this call as charged at its reserved amounts?' : 'Record that this call was not charged? Its amounts stop counting.')) return;
  run(async () => {
    await api.post(`/budgets/reservations/${enc(operation)}/resolve`, charged
      ? { outcome: 'settled', actual: item.requested, detail: 'Reconciled on the Budgets page' }
      : { outcome: 'released', detail: 'Reconciled on the Budgets page' });
    await refresh();
  });
});
const initial = new URLSearchParams(location.search).get('device');
if ([...device.options].some(item => item.value === initial)) device.value = initial;
run(connect);
