// PiSharp Durable Web: shared helpers. Every device call goes through the hub's proxy (/api/devices/{id}/v1/...), which adds the token.
export const $ = (selector, root = document) => root.querySelector(selector);
export const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

export class ApiError extends Error {
  constructor(code, message, status) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

/** A JSON request; an error envelope becomes an ApiError. */
export async function api(method, path, body, { idempotencyKey, signal } = {}) {
  const headers = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
  let response;
  try {
    response = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    throw new ApiError('offline', 'The hub is unreachable. Is PiSharp Durable Web still running?', 0);
  }
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!response.ok) {
    const error = json && json.error;
    throw new ApiError(error ? error.code : 'internal', error ? error.message : `${response.status} ${response.statusText}`, response.status);
  }
  return json;
}

/** A device's v1 API through the proxy. */
export function deviceApi(deviceId) {
  const base = `/api/devices/${encodeURIComponent(deviceId)}/v1`;
  return {
    get: (path) => api('GET', base + path),
    post: (path, body = {}, options) => api('POST', base + path, body, options),
    patch: (path, body, options) => api('PATCH', base + path, body, options),
    del: (path) => api('DELETE', base + path),
  };
}

/** An HTML fragment from the hub. */
export async function fragment(path) {
  const response = await fetch(path, { headers: { Accept: 'text/html' } });
  if (!response.ok) throw new ApiError('internal', `${response.status} ${response.statusText}`, response.status);
  return response.text();
}

export const uiState = {
  async get(key) {
    try { return (await api('GET', `/api/ui-state/${encodeURIComponent(key)}`)).value; } catch { return null; }
  },
  async set(key, value) {
    try { await api('PUT', `/api/ui-state/${encodeURIComponent(key)}`, { value }); } catch { /* best effort */ }
  },
};

export function uuid() {
  if (crypto.randomUUID) return crypto.randomUUID();
  return 'r' + Date.now().toString(36) + Math.random().toString(36).slice(2);
}

export function tokens(n) {
  n = Number(n) || 0;
  return n >= 1e6 ? (n / 1e6).toFixed(2) + 'M' : n >= 1000 ? +(n / 1000).toFixed(1) + 'k' : String(Math.round(n));
}

export const count = (n) => Math.round(Number(n) || 0).toLocaleString('en-US');
export const cost = (n) => '$' + (Number(n) || 0).toFixed(4);

export function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/** An element from an HTML string with one root. */
export function element(html) {
  const template = document.createElement('template');
  template.innerHTML = html.trim();
  return template.content.firstElementChild;
}

export function toast(text, tone = 'ok') {
  const el = document.getElementById('toast');
  if (!el || !window.bootstrap) return;
  el.querySelector('.toast-text').textContent = text;
  const icon = el.querySelector('[data-toast-icon]');
  if (icon) icon.className = 'bi ' + (tone === 'error' ? 'bi-exclamation-octagon text-danger' : 'bi-check2');
  window.bootstrap.Toast.getOrCreateInstance(el, { delay: tone === 'error' ? 7000 : 4500 }).show();
}

export function toastError(error) {
  toast(error instanceof ApiError || error instanceof Error ? error.message : String(error), 'error');
}

/** Theme, copy, reveal, toggle-class and pane switching: the page-independent behaviour. */
export function initCommon() {
  const THEME_KEY = 'pisharp-durable-theme';
  const applyTheme = (theme) => {
    const resolved = theme === 'light' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-bs-theme', resolved);
    $$('[data-theme-icon]').forEach((icon) => {
      icon.classList.toggle('bi-moon-stars', resolved === 'dark');
      icon.classList.toggle('bi-sun', resolved === 'light');
    });
  };
  let stored = null;
  try { stored = localStorage.getItem(THEME_KEY); } catch { /* storage unavailable */ }
  applyTheme(stored || 'dark');

  document.addEventListener('click', (e) => {
    if (e.target.closest('[data-theme-toggle]')) {
      const next = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem(THEME_KEY, next); } catch { /* storage unavailable */ }
      applyTheme(next);
    }
    const copy = e.target.closest('[data-copy]');
    if (copy) {
      const source = copy.getAttribute('data-copy');
      const text = source.startsWith('#') ? ($(source)?.innerText ?? '') : source;
      navigator.clipboard?.writeText(text).catch(() => {});
      toast('Copied.');
    }
    const reveal = e.target.closest('[data-reveal]');
    if (reveal) {
      const input = $(reveal.getAttribute('data-reveal'));
      if (input) {
        const show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        const icon = reveal.querySelector('.bi');
        if (icon) { icon.classList.toggle('bi-eye', !show); icon.classList.toggle('bi-eye-slash', show); }
      }
    }
    const toggle = e.target.closest('[data-toggle-class]');
    if (toggle) {
      const target = $(toggle.getAttribute('data-target'));
      if (target) {
        target.classList.toggle(toggle.getAttribute('data-toggle-class'));
        savePanels();
      }
    }
    const pane = e.target.closest('[data-show-pane]');
    if (pane && pane.tagName !== 'SELECT') {
      const container = pane.getAttribute('data-panes-target') ? $(pane.getAttribute('data-panes-target')) : pane.closest('[data-panes]');
      if (container) {
        if (pane.tagName === 'A') e.preventDefault();
        showPane(container, pane.getAttribute('data-show-pane'));
        if (pane.hasAttribute('data-expand')) { $('#workspace')?.classList.remove(pane.getAttribute('data-expand')); savePanels(); }
      }
    }
    if (e.target.closest('a[href="#"]')) e.preventDefault();
  });
  $$('[data-panes]').forEach((container) => {
    const panes = own(container, '[data-pane]');
    if (!panes.length) return;
    const fallback = panes.find((p) => p.hasAttribute('data-pane-default')) || panes[0];
    showPane(container, fallback.getAttribute('data-pane'));
  });
}

const own = (container, selector) => $$(selector, container).filter((el) => el.closest('[data-panes]') === container);

/** Shows one pane of a [data-panes] group and marks its tab. */
export function showPane(container, name) {
  const panes = own(container, '[data-pane]');
  if (!panes.some((p) => p.getAttribute('data-pane') === name)) return false;
  panes.forEach((p) => { p.hidden = p.getAttribute('data-pane') !== name; });
  own(container, '[data-pane-indicator]').forEach((indicator) => {
    const on = indicator.getAttribute('data-pane-indicator').split(/\s+/).includes(name);
    indicator.classList.toggle('active', on);
    if (indicator.getAttribute('role') === 'tab') indicator.setAttribute('aria-selected', on ? 'true' : 'false');
  });
  container.setAttribute('data-active-pane', name);
  container.dispatchEvent(new CustomEvent('pane', { detail: name }));
  return true;
}

/** Panel collapse state, kept per browser. */
export function savePanels() {
  const ws = $('#workspace');
  if (!ws) return;
  try {
    localStorage.setItem('pisharp-durable-panels', JSON.stringify({
      tree: ws.classList.contains('tree-collapsed'),
      inspector: ws.classList.contains('ins-collapsed'),
      width: document.documentElement.style.getPropertyValue('--dn-tree-w') || null,
    }));
  } catch { /* storage unavailable */ }
}

export function restorePanels() {
  const ws = $('#workspace');
  if (!ws) return;
  try {
    const saved = JSON.parse(localStorage.getItem('pisharp-durable-panels') || 'null');
    if (!saved) return;
    ws.classList.toggle('tree-collapsed', !!saved.tree);
    ws.classList.toggle('ins-collapsed', !!saved.inspector);
    if (saved.width) document.documentElement.style.setProperty('--dn-tree-w', saved.width);
  } catch { /* storage unavailable */ }
}

export function modal(id) {
  return window.bootstrap.Modal.getOrCreateInstance(document.getElementById(id));
}
