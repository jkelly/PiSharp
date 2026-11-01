// Decision 0011 (run W4, R1): the JSON and fragment calls of the request pages (Playground; Compare, Tool Bench and Presets reuse it). The
// run endpoints answer {error: {code, message, refused?}} and the older ones {error: "message"}; RequestError carries either, with the
// HTTP status and the parsed body, so a page can mark the options a run refused. Nothing here stores or logs a request.

/** A call that did not answer 2xx. `code` and `refused` come from the server's {error}; `data` is the parsed body (or null). */
export class RequestError extends Error {
  constructor(message, status, data) {
    super(message);
    this.name = 'RequestError';
    this.status = status;
    this.data = data;
    const error = data && typeof data.error === 'object' ? data.error : null;
    this.code = error?.code ?? null;
    this.refused = Array.isArray(error?.refused) ? error.refused : [];
  }
}

function errorMessage(data, status) {
  if (data && typeof data.error === 'string') return data.error;
  if (data && typeof data.error?.message === 'string') return data.error.message;
  return status === 0 ? 'The app could not be reached. Is it still running?' : `The request failed (${status}).`;
}

/** Calls `url` with `method`, sending `body` as JSON when given; resolves to the parsed answer (or null). Throws RequestError. */
export async function requestJson(method, url, body, signal) {
  const init = { method, headers: { Accept: 'application/json' }, credentials: 'same-origin', cache: 'no-store', signal };
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  let response;
  try {
    response = await fetch(url, init);
  } catch (error) {
    if (error?.name === 'AbortError') throw error;
    throw new RequestError(errorMessage(null, 0), 0, null);
  }
  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }
  if (!response.ok) throw new RequestError(errorMessage(data, response.status), response.status, data);
  return data;
}

/** POSTs `body` as JSON. */
export function postRequest(url, body, signal) {
  return requestJson('POST', url, body, signal);
}

/** GETs JSON. */
export function getRequest(url, signal) {
  return requestJson('GET', url, undefined, signal);
}

/** GETs a server-rendered fragment (HTML) and returns its one root element, or null when there is none. Throws RequestError. */
export async function getFragment(url) {
  let response;
  try {
    response = await fetch(url, { headers: { Accept: 'text/html' }, credentials: 'same-origin', cache: 'no-store' });
  } catch {
    throw new RequestError(errorMessage(null, 0), 0, null);
  }
  if (!response.ok) throw new RequestError(`The request failed (${response.status}).`, response.status, null);
  const html = await response.text();
  const template = document.createElement('template');
  template.innerHTML = html.trim();
  return template.content.firstElementChild;
}

/** Copies `text` to the clipboard; resolves to whether it worked. */
export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
