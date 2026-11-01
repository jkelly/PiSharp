// Decision 0011 (run W3, unit AP): Settings › About (Views/Settings/About.cshtml), loaded with asp-append-version. Copy buttons
// ([data-copy] names the element whose text to copy), the diagnostics download (a plain link the server answers as an attachment; here only
// its toast), and the provider connectivity check (_AboutConnectivityCheck): it runs only when its button is clicked, as one POST to
// /api/about/connectivity, while the running pane shows a row per provider; the answer's results pane (server-rendered) and refreshed
// diagnostics report replace the old ones. Text is set with textContent; only the server's own fragment is inserted as HTML.
import { showToast } from './shell.js';
import { api, showError, setShown, busy } from './settings-api.js';
import { swapHtml } from '/_content/PiSharp.Web.Common/js/swap.js';

const root = document.querySelector('[data-connectivity]');
let running = false;

document.addEventListener('click', onClick);

async function onClick(event) {
  const copy = event.target.closest('[data-copy]');
  if (copy) {
    await copyText(copy);
    return;
  }
  const download = event.target.closest('[data-diagnostics-download]');
  if (download) {
    showToast({ title: 'Download started', body: download.getAttribute('download') || 'The diagnostics report is being saved.', tone: 'info', delay: 3000 });
    return;
  }
  const run = event.target.closest('[data-connectivity-run]');
  if (run && root?.contains(run)) await check(run);
}

async function copyText(button) {
  const target = document.querySelector(button.dataset.copy);
  const text = target ? (('value' in target && target.value) || target.textContent || '').trim() : '';
  if (!text) return;
  try {
    await navigator.clipboard.writeText(text);
    showToast({ title: 'Copied', body: 'The diagnostics report is on the clipboard.', tone: 'ok', delay: 2000 });
  } catch {
    showToast({ title: 'Copy failed', body: 'Select the text and copy it instead.', tone: 'warn' });
  }
}

function showPane(name) {
  if (!root) return;
  root.dataset.state = name;
  for (const pane of root.querySelectorAll('[data-pane]')) setShown(pane, pane.dataset.pane === name);
}

async function check(button) {
  if (running) return;
  running = true;
  const before = root.dataset.state || 'idle';
  showPane('running');
  try {
    const answer = await busy(button, () => api('POST', root.dataset.endpoint));
    const results = root.querySelector('[data-connectivity-results]');
    if (results && typeof answer?.html === 'string') swapHtml(results, answer.html);
    const report = document.querySelector('[data-diagnostics-report]');
    if (report && typeof answer?.report === 'string') report.textContent = answer.report;
    showPane('results');
    const last = answer?.last;
    if (last?.offline) showToast({ title: 'Nothing was checked', body: last.message || 'The app is offline.', tone: 'info' });
    else if (last && last.tested > 0) {
      const tone = last.reachable === last.tested ? 'ok' : 'warn';
      showToast({ title: 'Connectivity checked', body: last.summary, tone });
    } else if (last) showToast({ title: 'Nothing to check', body: last.message || last.summary, tone: 'info' });
  } catch (error) {
    showPane(before);
    showError('Could not check connectivity', error);
  } finally {
    running = false;
  }
}
