// Decision 0011 (run W4, R3): the Run inspector (GET /runs/{id}). Copy buttons ([data-copy]: "#id" copies that element's text, anything
// else the value itself; View code's [data-copy-code]); "Show response" and "Open Raw SSE" ([data-show-tab]); the Raw SSE filter, its
// event-family toggles and Wrap; Collapse deltas; and the run actions (history-actions.js: Re-run, Pin, Delete, which goes back to
// History). A run still streaming follows /api/runs/{id}/events and reloads when its status frame arrives. View code is rendered by the
// server from the stored request, so request-code.js's fetch is not used here. Text is set with textContent only.
import { showToast } from './shell.js';
import { showFlash } from './settings-api.js';
import { copyText } from './request-api.js';
import { wireRunActions } from './history-actions.js';

const root = document.querySelector('[data-run-inspector]');
if (root) {
  showFlash();
  wireRunActions(root);
  wireCopy();
  wireTabs();
  wireSse();
  wireEvents();
  follow();
}

function copied(ok) {
  showToast(ok ? { title: 'Copied', tone: 'ok', delay: 2000 } : { title: 'Not copied', body: 'The browser blocked the clipboard.', tone: 'warn' });
}

function wireCopy() {
  document.addEventListener('click', async (event) => {
    const button = event.target.closest('[data-copy]');
    if (button) {
      const value = button.dataset.copy ?? '';
      const text = value.startsWith('#') ? document.getElementById(value.slice(1))?.textContent ?? '' : value;
      copied(await copyText(text));
      return;
    }
    const code = event.target.closest('[data-copy-code]');
    if (code) {
      const modal = code.closest('.modal') ?? document;
      copied(await copyText(modal.querySelector(`[data-code="${code.dataset.copyCode}"]`)?.textContent ?? ''));
    }
  });
}

function wireTabs() {
  document.addEventListener('click', (event) => {
    const button = event.target.closest('[data-show-tab]');
    if (!button) return;
    const tab = root.querySelector(`[data-run-tab="${button.dataset.showTab}"]`);
    if (!tab) return;
    if (window.bootstrap) window.bootstrap.Tab.getOrCreateInstance(tab).show();
    tab.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  });
}

function wireSse() {
  const list = root.querySelector('[data-sse-list]');
  if (!list) return;
  const filter = root.querySelector('[data-sse-filter]');
  const shown = root.querySelector('[data-sse-shown]');
  const rows = [...list.querySelectorAll('[data-sse-row]')];
  const apply = () => {
    const needle = (filter?.value ?? '').trim().toLowerCase();
    const groups = new Set([...root.querySelectorAll('[data-sse-group]')].filter((box) => box.checked).map((box) => box.dataset.sseGroup));
    const anyGroups = root.querySelector('[data-sse-group]') !== null;
    let count = 0;
    for (const row of rows) {
      const visible = (!anyGroups || groups.has(row.dataset.group)) && (needle === '' || row.textContent.toLowerCase().includes(needle));
      row.hidden = !visible;
      if (visible) count++;
    }
    if (shown) shown.textContent = count.toLocaleString();
  };
  filter?.addEventListener('input', apply);
  for (const box of root.querySelectorAll('[data-sse-group]')) box.addEventListener('change', apply);
  root.querySelector('[data-sse-wrap]')?.addEventListener('change', (event) => list.classList.toggle('sse-wrap', event.target.checked));
}

function wireEvents() {
  const events = root.querySelector('[data-run-events]');
  const toggle = root.querySelector('[data-events-collapse]');
  if (!events || !toggle) return;
  toggle.addEventListener('change', () => { events.dataset.collapsed = toggle.checked ? 'true' : 'false'; });
}

function follow() {
  if (root.dataset.running !== 'true' || !window.EventSource) return;
  const source = new EventSource(`/api/runs/${encodeURIComponent(root.dataset.runInspector)}/events`);
  let done = false;
  const reload = () => {
    if (done) return;
    done = true;
    source.close();
    window.location.reload();
  };
  source.addEventListener('status', reload);
  source.addEventListener('error', () => {
    if (source.readyState === EventSource.CLOSED) window.setTimeout(reload, 1500);
  });
}
