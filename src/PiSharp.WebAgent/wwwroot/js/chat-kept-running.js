// Decision 0011 (run W2, WU8): the mockups' "kept running while you were away" toast. The layout renders it as a template
// (_ChatKeptRunning) when chats are running; this shows it once per tab, and only when no page of the app was open in this browser for a
// while (the runs went on while you were away), so moving between pages never repeats it. Storage may be blocked (private windows,
// cleared site data): then it shows once per page load at most and nothing else changes.
const LAST_SEEN = 'pisharp.last-seen';
const SHOWN = 'pisharp.kept-running-shown';
const AWAY_MS = 60_000;

function read(store, key) {
  try { return window[store].getItem(key); } catch { return null; }
}

function write(store, key, value) {
  try { window[store].setItem(key, value); } catch { /* Blocked storage: nothing to remember. */ }
}

const lastSeen = Number(read('localStorage', LAST_SEEN)) || 0;
const away = Date.now() - lastSeen > AWAY_MS;
const markSeen = () => write('localStorage', LAST_SEEN, String(Date.now()));
markSeen();
window.addEventListener('pagehide', markSeen);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') markSeen(); });
window.setInterval(() => { if (document.visibilityState === 'visible') markSeen(); }, 20_000);

const template = document.getElementById('toast-kept-running-template');
const host = document.getElementById('toasts');
if (template && host && away && read('sessionStorage', SHOWN) !== template.dataset.keptRunning) {
  const toast = template.content.firstElementChild.cloneNode(true);
  host.append(toast);
  write('sessionStorage', SHOWN, template.dataset.keptRunning ?? '');
  toast.addEventListener('hidden.bs.toast', () => toast.remove(), { once: true });
  if (window.bootstrap) window.bootstrap.Toast.getOrCreateInstance(toast, { autohide: false }).show();
  else toast.classList.add('show');
}
