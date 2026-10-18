// PiSharp Durable Web: the devices page (PiSharp addition, PLAN.md §7 item 2). Add a `url` device (its token is checked first), enter a
// token, edit or remove a device added here, forget a changed identity, and import a dpi-network.yaml. The table refreshes from the hub
// whenever a device's status changes.
import { $, api, escapeHtml, fragment, initCommon, modal, toast, toastError } from './core.js';
import { HubStream } from './stream.js';

initCommon();

const stream = new HubStream();
stream.status = (status) => {
  const chip = $('[data-connection]');
  if (chip) { chip.textContent = status === 'connected' ? 'Connected' : 'Reconnecting'; chip.setAttribute('data-tone', status === 'connected' ? 'ok' : 'warn'); }
};
let refresh = null;
stream.on('network', (frame) => {
  const devices = frame.devices || [];
  const online = devices.filter((d) => d.status === 'online').length;
  const summary = $('[data-online-summary]');
  if (summary) summary.textContent = devices.length ? `${online} of ${devices.length} online` : 'No devices';
  clearTimeout(refresh);
  refresh = setTimeout(async () => {
    if (document.querySelector('#deviceTable .dropdown-menu.show')) return;
    try { $('#deviceTable').innerHTML = await fragment('/devices/table'); } catch { /* next change refreshes */ }
  }, 150);
});
stream.start();

// ─── Add and edit ─────────────────────────────────────────────────────────
let editing = null;
const ad = {
  id: $('#ad-id'), url: $('#ad-url'), token: $('#ad-token'), error: $('[data-ad-error]'),
  panes: (name) => document.querySelectorAll('[data-ad-pane]').forEach((p) => { p.hidden = p.getAttribute('data-ad-pane') !== name; }),
};

function openAdd(device) {
  editing = device || null;
  $('[data-ad-title]').textContent = editing ? `Edit ${editing.id}` : 'Add device';
  ad.id.value = editing ? editing.id : '';
  ad.id.disabled = !!editing;
  $('[data-ad-id-hint]').textContent = editing ? "The name can't change. Remove the device and add it again to rename it." : 'Lower-case letters, digits and dashes. Used in addresses like build-box/release-notes/main.';
  ad.url.value = editing ? editing.url : '';
  $('[data-ad-url-hint]').textContent = editing ? "Changing the address forgets the device's identity and checks it again." : '';
  ad.token.value = '';
  ad.token.placeholder = editing ? 'Unchanged' : 'dpi_…';
  $('[data-ad-token-hint]').innerHTML = editing ? 'Leave blank to keep the stored token. It is never shown.' : 'Run <code>pisharp durable token</code> on the device to print it. At least 32 characters.';
  ad.error.hidden = true;
  ad.panes('form');
  $('[data-ad-submit]').hidden = false;
  $('[data-ad-submit]').textContent = editing ? 'Save' : 'Check and add';
  $('[data-ad-cancel]').hidden = false;
  $('[data-ad-done]').hidden = true;
  modal('modal-add-device').show();
  setTimeout(() => (editing ? ad.url : ad.id).focus(), 300);
}

$('#ad-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const submit = $('[data-ad-submit]');
  submit.disabled = true;
  ad.error.hidden = true;
  try {
    if (editing) {
      await api('PATCH', `/api/devices/${encodeURIComponent(editing.id)}`, { url: ad.url.value.trim() || undefined, token: ad.token.value.trim() || undefined });
      modal('modal-add-device').hide();
      toast('Device saved. Checking it again.');
      return;
    }
    const result = await api('POST', '/api/devices', { id: ad.id.value.trim(), url: ad.url.value.trim(), token: ad.token.value.trim() });
    const info = result.info;
    $('[data-ad-ok-title]').textContent = `${ad.id.value.trim()} is online`;
    $('[data-ad-ok-sub]').textContent = `${info.hostname} · ${info.platform} · API version ${info.apiVersion}`;
    $('[data-ad-uid]').textContent = info.uid.slice(0, 4) + '…' + info.uid.slice(-4);
    $('[data-ad-dirs]').textContent = (info.startDirs || []).join(', ');
    $('[data-ad-plain]').hidden = !result.plainHttp;
    ad.panes('ok');
    submit.hidden = true;
    $('[data-ad-cancel]').hidden = true;
    $('[data-ad-done]').hidden = false;
  } catch (error) {
    ad.error.hidden = false;
    ad.error.textContent = error.message;
  } finally {
    submit.disabled = false;
  }
});

// ─── Token ────────────────────────────────────────────────────────────────
let tokenDevice = null;
function openToken(id) {
  tokenDevice = id;
  document.querySelectorAll('[data-tk-device]').forEach((el) => { el.textContent = id; });
  $('#tk-value').value = '';
  $('[data-tk-error]').hidden = true;
  modal('modal-token').show();
  setTimeout(() => $('#tk-value').focus(), 300);
}

$('#tk-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    const result = await api('PATCH', `/api/devices/${encodeURIComponent(tokenDevice)}`, { token: $('#tk-value').value.trim() });
    modal('modal-token').hide();
    toast(`${tokenDevice} is ${result.device?.status || 'checking'}.`);
  } catch (error) {
    const alert = $('[data-tk-error]');
    alert.hidden = false;
    alert.textContent = error.message;
  }
});

// ─── Remove ───────────────────────────────────────────────────────────────
let removing = null;
$('[data-rm-confirm]').addEventListener('click', async () => {
  try {
    await api('DELETE', `/api/devices/${encodeURIComponent(removing)}`);
    modal('modal-remove').hide();
    toast(`${removing} removed from this hub.`);
  } catch (error) {
    toastError(error);
  }
});

// ─── Import ───────────────────────────────────────────────────────────────
const im = {
  yaml: $('#im-yaml'), error: $('[data-im-error]'),
  pane(name) {
    document.querySelectorAll('[data-im-pane]').forEach((p) => { p.hidden = p.getAttribute('data-im-pane') !== name; });
    $('[data-im-cancel]').hidden = name !== 'paste';
    $('[data-im-back]').hidden = name !== 'preview';
    $('[data-im-preview]').hidden = name !== 'paste';
    $('[data-im-apply]').hidden = name !== 'preview';
  },
};
$('[data-im-file]').addEventListener('change', async (e) => {
  const file = e.target.files[0];
  if (file) im.yaml.value = await file.text();
});
$('[data-im-back]').addEventListener('click', () => im.pane('paste'));
$('[data-im-preview]').addEventListener('click', async () => {
  im.error.hidden = true;
  try {
    const result = await api('POST', '/api/devices/import', { yaml: im.yaml.value, apply: false });
    const words = { add: ['Will be added', 'ok', 'needs a token'], same: ['Already added', '', 'from this file'], update: ['Will be updated', 'ok', 'new address'], conflict: ['Skipped', 'warn', 'a device added here has this name'] };
    $('[data-im-rows]').innerHTML = result.devices.map((d) => {
      const [text, tone, note] = words[d.result];
      return `<tr><td>${escapeHtml(d.id)}</td><td><span class="ps-mono fs-7">${escapeHtml(d.url)}</span></td><td><span class="ps-chip" data-tone="${tone}">${text}</span> <span class="fs-8 text-ps-faint">${note}</span></td></tr>`;
    }).join('');
    const count = result.devices.filter((d) => d.result === 'add' || d.result === 'update').length;
    $('[data-im-apply]').textContent = `Import ${count} device${count === 1 ? '' : 's'}`;
    $('[data-im-apply]').disabled = count === 0;
    im.pane('preview');
  } catch (error) {
    im.error.hidden = false;
    im.error.textContent = error.message;
  }
});
$('[data-im-apply]').addEventListener('click', async () => {
  try {
    const result = await api('POST', '/api/devices/import', { yaml: im.yaml.value, apply: true });
    modal('modal-import').hide();
    const count = result.devices.filter((d) => d.result === 'add' || d.result === 'update').length;
    toast(`${count} device${count === 1 ? '' : 's'} imported. Enter a token to connect.`);
  } catch (error) {
    im.error.hidden = false;
    im.error.textContent = error.message;
  }
});

// ─── Actions ──────────────────────────────────────────────────────────────
document.addEventListener('click', async (e) => {
  const action = e.target.closest('[data-action]');
  if (!action) return;
  const id = action.dataset.device;
  try {
    switch (action.dataset.action) {
      case 'add-device': openAdd(null); break;
      case 'import': im.pane('paste'); im.error.hidden = true; modal('modal-import').show(); break;
      case 'edit': openAdd({ id, url: action.dataset.url }); break;
      case 'token': openToken(id); break;
      case 'remove':
        removing = id;
        document.querySelectorAll('[data-rm-device]').forEach((el) => { el.textContent = id; });
        modal('modal-remove').show();
        break;
      case 'check': {
        const result = await api('POST', `/api/devices/${encodeURIComponent(id)}/check`);
        toast(`${id} is ${result?.status}.`, result?.status === 'online' ? 'ok' : 'error');
        break;
      }
      case 'forget':
        await api('POST', `/api/devices/${encodeURIComponent(id)}/forget-identity`);
        toast(`${id}'s identity forgotten. Checking again.`);
        break;
    }
  } catch (error) {
    toastError(error);
  }
});

const params = new URLSearchParams(location.search);
if (params.get('add')) openAdd(null);
if (params.get('token')) openToken(params.get('token'));
if (params.get('edit')) {
  const row = document.querySelector(`[data-action="edit"][data-device="${CSS.escape(params.get('edit'))}"]`);
  openAdd({ id: params.get('edit'), url: row?.dataset.url || '' });
}
