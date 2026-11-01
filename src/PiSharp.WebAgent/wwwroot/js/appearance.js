// Decision 0011 (run W3, unit AP): Settings › Appearance in every page. A classic script in <head> (_Layout, right after Web.Common's
// theme-boot.js), so it runs before the body is parsed: it reads this browser's preferences from localStorage (nothing reaches the server)
// and sets them on <html> before the first paint, so there is no flash of the wrong theme or size. The theme choice is kept under
// Web.Common's theme key (<html data-theme-key>, "pisharp-theme": dark, light or system); "system" follows prefers-color-scheme and
// switches when it changes, and the top bar's moon (shell.js) sets dark or light through window.PiSharpAppearance. Every other preference
// is one JSON object under "pisharp.appearance" (AppearancePrefs lists them) and becomes an attribute that appearance.css reads:
// data-font-size, data-density, data-code-theme, data-code-ligatures, data-usage-footer, data-user-markdown (W3 fix: which of the two
// renderings of a user message the chat shows), data-reduce-motion (its "system" follows prefers-reduced-motion), data-expand-thinking
// and data-expand-tools; the last two open each chat's thinking blocks and tool cards the
// first time they appear (a MutationObserver, so streamed fragments too), and keep the user's own open or close for the page's life.
// Another tab's change applies here at once (the storage event). After any change it dispatches "pisharp:appearance" on document. When the
// browser keeps no site data, the preferences last for the page only. On the Appearance page it also wires the form: the theme cards
// apply at once; the rest is previewed in [data-appearance-preview], marked per row, and saved or discarded with the save bar.
(() => {
  'use strict';

  const root = document.documentElement;
  const THEME_KEY = root.getAttribute('data-theme-key') || 'pisharp-theme';
  const PREFS_KEY = 'pisharp.appearance';
  const EVENT = 'pisharp:appearance';
  const THEMES = ['dark', 'light', 'system'];
  const SWITCH = ['on', 'off'];
  // Each preference's values (the first one is not special: `fallback` is the default), and the <html> attribute it sets.
  const PREFS = {
    fontSize: { values: ['small', 'default', 'large'], fallback: 'default', attribute: 'data-font-size' },
    density: { values: ['comfortable', 'compact'], fallback: 'comfortable', attribute: 'data-density' },
    codeTheme: { values: ['default', 'github', 'solarized'], fallback: 'default', attribute: 'data-code-theme' },
    codeLigatures: { values: SWITCH, fallback: 'off', attribute: 'data-code-ligatures' },
    expandThinking: { values: SWITCH, fallback: 'off', attribute: 'data-expand-thinking' },
    showUsage: { values: SWITCH, fallback: 'on', attribute: 'data-usage-footer' },
    expandToolCalls: { values: SWITCH, fallback: 'off', attribute: 'data-expand-tools' },
    userMarkdown: { values: SWITCH, fallback: 'off', attribute: 'data-user-markdown' },
    reduceMotion: { values: [...SWITCH, 'system'], fallback: 'system', attribute: 'data-reduce-motion' },
  };
  // The body text size of each font size (rem-based sizes scale with the root): 14, 15 and 17 px.
  const SCALE = { small: 14 / 15, default: 1, large: 17 / 15 };

  const query = (text) => (window.matchMedia ? window.matchMedia(text) : null);
  const lightScheme = query('(prefers-color-scheme: light)');
  const reducedMotion = query('(prefers-reduced-motion: reduce)');

  // What this page keeps when the browser keeps no site data (a private window with storage off): the choice lasts until the page closes.
  const memory = new Map();
  const storageWorks = (() => {
    try {
      localStorage.setItem('pisharp.appearance.probe', '1');
      localStorage.removeItem('pisharp.appearance.probe');
      return true;
    } catch {
      return false;
    }
  })();

  function load(key) {
    try {
      const value = localStorage.getItem(key);
      if (value !== null) return value;
    } catch { /* storage unavailable */ }
    return memory.has(key) ? memory.get(key) : null;
  }

  function keep(key, value) {
    memory.set(key, value);
    try { localStorage.setItem(key, value); } catch { /* storage unavailable: the page keeps it */ }
  }

  function themeChoice() {
    const value = load(THEME_KEY);
    return THEMES.includes(value) ? value : 'dark';
  }

  function resolve(choice) {
    return choice === 'system' ? (lightScheme?.matches ? 'light' : 'dark') : choice;
  }

  function normalize(raw) {
    const prefs = {};
    for (const [key, pref] of Object.entries(PREFS)) {
      const value = raw && typeof raw === 'object' ? raw[key] : undefined;
      prefs[key] = pref.values.includes(value) ? value : pref.fallback;
    }
    return prefs;
  }

  function stored() {
    let raw = null;
    try { raw = JSON.parse(load(PREFS_KEY) ?? 'null'); } catch { raw = null; }
    return normalize(raw);
  }

  // The value an attribute shows: reduceMotion's "system" is the system's answer now.
  function effective(key, value) {
    return key === 'reduceMotion' && value === 'system' ? (reducedMotion?.matches ? 'on' : 'off') : value;
  }

  // ── Thinking blocks and tool cards opened by default ──
  // A keyed <details> (data-detail, as the chat renders them) is opened the first time it appears; after that it keeps what the user (or
  // Web.Common's swap.js, which restores open states across a re-render) last set, even when a stream resets the transcript.
  const opened = new Map();
  const expand = { thinking: false, tools: false };
  let observer = null;

  function wanted(details) {
    if (!details.hasAttribute('data-detail') || details.closest('[data-appearance-preview]')) return false;
    if (details.classList.contains('thinking')) return expand.thinking;
    if (details.classList.contains('tool-card')) return expand.tools;
    return false;
  }

  function openIn(node) {
    const found = node.matches?.('details[data-detail]') ? [node] : [];
    if (node.querySelectorAll) found.push(...node.querySelectorAll('details[data-detail]'));
    for (const details of found) {
      if (!wanted(details)) continue;
      const key = details.getAttribute('data-detail');
      if (opened.has(key)) {
        details.open = opened.get(key);
      } else {
        details.open = true;
        opened.set(key, true);
      }
    }
  }

  function onToggle(event) {
    const details = event.target;
    if (details instanceof HTMLDetailsElement && wanted(details)) opened.set(details.getAttribute('data-detail'), details.open);
  }

  function watchDetails(prefs) {
    expand.thinking = prefs.expandThinking === 'on';
    expand.tools = prefs.expandToolCalls === 'on';
    const on = expand.thinking || expand.tools;
    if (on && !observer && window.MutationObserver) {
      observer = new MutationObserver((records) => {
        for (const record of records) for (const node of record.addedNodes) if (node.nodeType === 1) openIn(node);
      });
      observer.observe(root, { childList: true, subtree: true });
      document.addEventListener('toggle', onToggle, true);
      if (document.body) openIn(document.body);
    } else if (!on && observer) {
      observer.disconnect();
      observer = null;
      document.removeEventListener('toggle', onToggle, true);
    }
  }

  // ── Applying ──
  function apply() {
    const choice = themeChoice();
    const theme = resolve(choice);
    const prefs = stored();
    root.setAttribute('data-theme-choice', choice);
    root.setAttribute('data-bs-theme', theme);
    for (const [key, pref] of Object.entries(PREFS)) root.setAttribute(pref.attribute, effective(key, prefs[key]));
    watchDetails(prefs);
    document.dispatchEvent(new CustomEvent(EVENT, { detail: { choice, theme, prefs } }));
  }

  /** The page's appearance: what is stored in this browser and how to change it. */
  const api = Object.freeze({
    /** The theme choices: dark (the default), light, system. */
    themes: Object.freeze([...THEMES]),
    /** The other preferences' keys, in page order. */
    keys: Object.freeze(Object.keys(PREFS)),
    /** The defaults of the other preferences. */
    defaults: () => normalize(null),
    /** The theme choice: 'dark', 'light' or 'system'. */
    choice: themeChoice,
    /** The theme shown: 'dark' or 'light'. */
    theme: () => resolve(themeChoice()),
    /** The other preferences as stored. */
    prefs: stored,
    /** False when the browser keeps no site data (the preferences then last for this page only). */
    persistent: () => storageWorks,
    /** The text scale of a font size (1 for the default). */
    scale: (fontSize) => SCALE[fontSize] ?? 1,
    /** The value of preference `key` as applied (reduceMotion's 'system' as the system answers now). */
    effective,
    /** Stores and applies a theme choice; returns the theme shown. */
    setTheme(choice) {
      if (THEMES.includes(choice)) {
        keep(THEME_KEY, choice);
        apply();
      }
      return resolve(themeChoice());
    },
    /** Switches between dark and light (from System, away from what the system shows); returns the theme shown. */
    toggleTheme() {
      return api.setTheme(resolve(themeChoice()) === 'dark' ? 'light' : 'dark');
    },
    /** Stores and applies the other preferences (unknown values become defaults); returns them as stored. */
    save(prefs) {
      keep(PREFS_KEY, JSON.stringify(normalize(prefs)));
      apply();
      return stored();
    },
  });
  window.PiSharpAppearance = api;

  lightScheme?.addEventListener?.('change', () => { if (themeChoice() === 'system') apply(); });
  reducedMotion?.addEventListener?.('change', () => { if (stored().reduceMotion === 'system') apply(); });
  window.addEventListener('storage', (event) => {
    if (event.key === null || event.key === THEME_KEY || event.key === PREFS_KEY) apply();
  });
  apply();

  // ── Settings › Appearance ──
  function toast(options) {
    import('/js/shell.js').then((shell) => shell.showToast(options)).catch(() => { /* no toast area */ });
  }

  function initPage() {
    const form = document.querySelector('form[data-appearance-form]');
    if (!form) return;
    const preview = document.querySelector('[data-appearance-preview]');
    const themes = document.querySelector('[data-appearance-themes]');
    const storageNote = document.querySelector('[data-appearance-storage-off]');
    if (storageNote) storageNote.hidden = storageWorks;
    let saved = stored();
    // reduceMotion follows the system until the user moves its switch.
    let motionSet = saved.reduceMotion !== 'system';
    const previewDetails = {};

    const field = (key) => form.elements.namedItem(key);
    const isSwitch = (element) => element instanceof HTMLInputElement && element.type === 'checkbox';

    function readValue(key) {
      const element = field(key);
      if (!element) return PREFS[key].fallback;
      if (isSwitch(element)) return key === 'reduceMotion' && !motionSet ? 'system' : element.checked ? 'on' : 'off';
      return PREFS[key].values.includes(element.value) ? element.value : PREFS[key].fallback;
    }

    function writeValue(key, value) {
      const element = field(key);
      if (!element) return;
      if (isSwitch(element)) element.checked = effective(key, value) === 'on';
      else element.value = value;
    }

    const read = () => Object.fromEntries(api.keys.map((key) => [key, readValue(key)]));
    const write = (prefs) => api.keys.forEach((key) => writeValue(key, prefs[key]));
    const changed = (draft) => api.keys.filter((key) => draft[key] !== saved[key]);

    function labelOf(key, value) {
      const element = field(key);
      if (element instanceof HTMLSelectElement) return [...element.options].find((option) => option.value === value)?.textContent ?? value;
      if (isSwitch(element)) return value === 'system' ? `following the system (${effective(key, value)})` : value;
      const radio = [...form.querySelectorAll(`input[name="${key}"]`)].find((input) => input.value === value);
      return (radio && form.querySelector(`label[for="${radio.id}"]`)?.textContent) || value;
    }

    function showPreview(draft) {
      if (!preview) return;
      for (const key of ['density', 'codeTheme', 'codeLigatures', 'showUsage', 'userMarkdown', 'reduceMotion']) {
        preview.setAttribute(PREFS[key].attribute, effective(key, draft[key]));
      }
      // The page already shows the saved font size; the sample is zoomed by the ratio, so it shows the unsaved one.
      const scale = preview.querySelector('[data-preview-scale]');
      if (scale) scale.style.zoom = String(api.scale(draft.fontSize) / api.scale(saved.fontSize));
      preview.querySelectorAll('details[data-preview-detail]').forEach((details) => {
        const key = details.dataset.previewDetail;
        if (previewDetails[key] === draft[key]) return;
        previewDetails[key] = draft[key];
        details.open = draft[key] === 'on';
      });
    }

    function update() {
      const draft = read();
      const keys = changed(draft);
      form.querySelectorAll('[data-pref-row]').forEach((row) => {
        const key = row.dataset.prefRow;
        const modified = keys.includes(key);
        row.classList.toggle('is-modified', modified);
        const chip = row.querySelector('[data-modified-chip]');
        if (chip) {
          chip.hidden = !modified;
          chip.textContent = modified ? `modified · was ${labelOf(key, saved[key]).toLowerCase()}` : 'modified';
        }
      });
      const bar = form.querySelector('[data-savebar]');
      if (bar) bar.hidden = keys.length === 0;
      const count = form.querySelector('[data-savebar-count]');
      if (count) count.textContent = keys.length === 1 ? '1 unsaved change' : `${keys.length} unsaved changes`;
      const names = form.querySelector('[data-savebar-names]');
      // As on the other settings pages, the save bar names the settings by key: where each is kept (the rows' key notes).
      if (names) names.textContent = keys.map((key) => `${PREFS_KEY}.${key}`).join(' · ');
      showPreview(draft);
    }

    function syncThemes() {
      if (!themes) return;
      const choice = themeChoice();
      themes.querySelectorAll('input[name="theme"]').forEach((radio) => { radio.checked = radio.value === choice; });
      const now = themes.querySelector('[data-system-now]');
      if (now) {
        now.textContent = `now ${resolve(choice)}`;
        now.hidden = choice !== 'system';
      }
    }

    themes?.addEventListener('change', (event) => {
      if (event.target.matches('input[name="theme"]')) api.setTheme(event.target.value);
    });
    form.addEventListener('change', (event) => {
      if (event.target.name === 'reduceMotion') motionSet = true;
      update();
    });
    form.addEventListener('input', update);
    form.addEventListener('click', (event) => {
      const reset = event.target.closest('[data-appearance-reset]');
      if (reset) {
        const section = reset.closest('.settings-section');
        const defaults = api.defaults();
        section?.querySelectorAll('[data-pref-row]').forEach((row) => {
          const key = row.dataset.prefRow;
          if (key === 'reduceMotion') motionSet = false;
          writeValue(key, defaults[key]);
        });
        update();
        toast({ title: 'Reset to defaults', body: 'The fields were set back to their defaults. Select Save changes to keep them.', tone: 'info' });
      } else if (event.target.closest('[data-discard]')) {
        motionSet = saved.reduceMotion !== 'system';
        write(saved);
        update();
      }
    });
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const draft = read();
      const keys = changed(draft);
      if (keys.length === 0) return;
      const labels = keys.map((key) => form.querySelector(`[data-pref-row="${key}"]`)?.dataset.prefLabel ?? key);
      saved = api.save(draft);
      motionSet = saved.reduceMotion !== 'system';
      update();
      toast({
        title: 'Appearance saved',
        body: `${labels.join(', ')}: now on every page in this browser.${storageWorks ? '' : ' This browser keeps no site data, so it lasts until the page is closed.'}`,
        tone: 'ok',
      });
    });
    // A change from elsewhere (the top bar, another tab, the system): the theme cards follow; an untouched form follows the stored values.
    document.addEventListener(EVENT, () => {
      syncThemes();
      const fresh = stored();
      if (api.keys.every((key) => fresh[key] === saved[key])) {
        update();
        return;
      }
      const untouched = changed(read()).length === 0;
      saved = fresh;
      if (untouched) {
        motionSet = saved.reduceMotion !== 'system';
        write(saved);
      }
      update();
    });

    write(saved);
    syncThemes();
    update();
    form.dataset.appearanceReady = 'true';
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initPage, { once: true });
  else initPage();
})();
