// PiSharp.Web.Common (decision 0011), from the hub's core.js: the dark/light toggle. Dark is the default. A [data-theme-toggle] button
// switches and stores the theme; every [data-theme-icon] shows a moon in dark and a sun in light. The storage key is the one theme-boot.js
// left on <html data-theme-key>, unless initTheme names another.

const DEFAULT_KEY = 'pisharp-theme';
let wired = false;
let storageKey = DEFAULT_KEY;

/** The theme on the page: 'dark' or 'light'. */
export function currentTheme() {
  return document.documentElement.getAttribute('data-bs-theme') === 'light' ? 'light' : 'dark';
}

/** Shows a theme ('light', or anything else for dark) and updates the toggle icons; `store` also remembers it. */
export function applyTheme(theme, { store = false } = {}) {
  const resolved = theme === 'light' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-bs-theme', resolved);
  document.querySelectorAll('[data-theme-icon]').forEach((icon) => {
    icon.classList.toggle('bi-moon-stars', resolved === 'dark');
    icon.classList.toggle('bi-sun', resolved === 'light');
  });
  if (store) {
    try { localStorage.setItem(storageKey, resolved); } catch { /* storage unavailable */ }
  }
  return resolved;
}

/** Applies the stored theme and wires [data-theme-toggle] once per page. */
export function initTheme({ key } = {}) {
  storageKey = key || document.documentElement.getAttribute('data-theme-key') || DEFAULT_KEY;
  let stored = null;
  try { stored = localStorage.getItem(storageKey); } catch { /* storage unavailable */ }
  applyTheme(stored || 'dark');
  if (wired) return;
  wired = true;
  document.addEventListener('click', (e) => {
    if (e.target.closest('[data-theme-toggle]')) applyTheme(currentTheme() === 'dark' ? 'light' : 'dark', { store: true });
  });
}
