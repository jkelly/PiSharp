// Decision 0011 (run W2, WU9): what the settings modules share: JSON calls to the app's APIs (/api/credentials of WU5, /api/settings of
// WU9), with the server's {error} message on failure; a toast that survives the reload after a change (sessionStorage, per tab; a page
// without storage just reloads); and small DOM helpers. Text is always set with textContent. No secret is ever stored here: a toast carries
// only names and counts.
import { showToast } from './shell.js';

const FLASH_KEY = 'pisharp.settings.flash';

/** An API answer that was not 2xx: `message` is the server's scrubbed {error}, `status` the HTTP status (0 when unreachable). */
export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

/** Calls `url` with `method`, sending `body` as JSON when given, and resolves to the parsed answer (or null). Throws ApiError. */
export async function api(method, url, body) {
  const init = { method, headers: { Accept: 'application/json' }, credentials: 'same-origin', cache: 'no-store' };
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  let response;
  try {
    response = await fetch(url, init);
  } catch {
    throw new ApiError('The app could not be reached. Is it still running?', 0);
  }
  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }
  if (!response.ok) {
    const message = data && typeof data.error === 'string' ? data.error : `The request failed (${response.status}).`;
    throw new ApiError(message, response.status);
  }
  return data;
}

/** POSTs `body` (an empty object by default) as JSON. */
export function postJson(url, body = {}) {
  return api('POST', url, body);
}

/** GETs JSON. */
export function getJson(url) {
  return api('GET', url);
}

/** Keeps `toast` ({ title, body, tone }) for the next page load of this tab, then reloads (or goes to `url`). */
export function flashAndReload(toast, url) {
  try { sessionStorage.setItem(FLASH_KEY, JSON.stringify(toast)); } catch { /* storage off: the change still shows after the reload */ }
  if (url) window.location.assign(url);
  else window.location.reload();
}

/** Shows the toast a previous page kept with flashAndReload, once. */
export function showFlash() {
  let toast = null;
  try {
    const raw = sessionStorage.getItem(FLASH_KEY);
    sessionStorage.removeItem(FLASH_KEY);
    toast = raw ? JSON.parse(raw) : null;
  } catch {
    toast = null;
  }
  if (toast && typeof toast.title === 'string') showToast({ title: toast.title, body: String(toast.body ?? ''), tone: toast.tone });
}

/** Shows an error toast for `error` (an ApiError or anything else). */
export function showError(title, error) {
  showToast({ title, body: error instanceof Error ? error.message : String(error ?? ''), tone: 'danger' });
}

/** Sets an element's text (never HTML). */
export function setText(element, text) {
  if (element) element.textContent = text ?? '';
}

/** Shows or hides an element with the hidden attribute. */
export function setShown(element, shown) {
  if (element) element.hidden = !shown;
}

/** "1 model", "3 models". */
export function plural(count, one, many) {
  return `${count} ${count === 1 ? one : (many ?? `${one}s`)}`;
}

/** A provider mark (an <img>) or the name's initial, built with DOM calls, into `host` (a .provider-logo). */
export function fillLogo(host, name, url) {
  if (!host) return;
  host.replaceChildren();
  host.setAttribute('aria-label', `${name || 'Provider'} logo`);
  if (url && url.startsWith('/')) {
    const img = document.createElement('img');
    img.className = 'provider-logo-mark';
    img.src = url;
    img.alt = '';
    img.width = 20;
    img.height = 20;
    img.decoding = 'async';
    host.append(img);
  } else {
    const initial = document.createElement('span');
    initial.className = 'provider-logo-initial';
    initial.setAttribute('aria-hidden', 'true');
    initial.textContent = (name || '?').charAt(0).toUpperCase();
    host.append(initial);
  }
}

/** Disables `button` and shows a spinner in it while `work` runs; restores it afterwards. */
export async function busy(button, work) {
  if (!button) return work();
  const before = [...button.childNodes].map((node) => node.cloneNode(true));
  const wasDisabled = button.disabled;
  button.disabled = true;
  const spinner = document.createElement('span');
  spinner.className = 'spinner-border spinner-border-sm me-1';
  spinner.setAttribute('aria-hidden', 'true');
  button.prepend(spinner);
  try {
    return await work();
  } finally {
    if (button.isConnected) {
      button.replaceChildren(...before);
      button.disabled = wasDisabled;
    }
  }
}
