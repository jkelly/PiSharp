// Decision 0011 (run W2, WU9): the settings forms of Defaults and Tools & workspace ([data-settings-form]). Each form tracks what changed
// against the values it loaded with, shows _SettingsSaveBar with the changed settings' names, and saves the whole section as JSON (POST
// /api/settings/defaults or /api/settings/tools); Discard reloads. Defaults marks the thinking levels the chosen model doesn't support.
// Tools asks before "Never ask", edits the allow list, and runs _FolderBrowser on GET /api/settings/folders. Text is set with textContent.
// W5 integration fix 1: Tools' "Always allowed this session" revokes a chat's grant at once (POST /api/chats/{id}/grants/revoke).
import { confirmAction, providerModelValue } from './shell.js';
import { getJson, postJson, setText, setShown, flashAndReload, showError, busy } from './settings-api.js';

/** Wires every settings form of the page, the Defaults reset and the folder browser. */
export function initForms() {
  document.querySelectorAll('form[data-settings-form]').forEach(setup);
  document.querySelector('[data-defaults-reset]')?.addEventListener('click', resetDefaults);
  const browser = document.querySelector('[data-folder-browser]');
  if (browser) setupFolders(browser);
  document.querySelector('[data-session-grants]')?.addEventListener('click', revokeGrant);
}

// Revokes one session grant; the row goes once the server says so (a grant already gone, after a restart, goes too).
async function revokeGrant(event) {
  const button = event.target.closest('[data-grant-revoke]');
  const row = button?.closest('[data-session-grant]');
  if (!row) return;
  try {
    await busy(button, () => postJson(`/api/chats/${encodeURIComponent(row.dataset.chat)}/grants/revoke`,
      { tool: row.dataset.tool, command: row.dataset.command || null }));
  } catch (error) {
    if (error?.status !== 404) {
      showError('Could not revoke the grant', error);
      return;
    }
  }
  const card = row.closest('[data-session-grants]');
  row.remove();
  setShown(card.querySelector('[data-session-grants-empty]'), !card.querySelector('[data-session-grant]'));
}

const NAMES = {
  provider: 'defaults.provider',
  model: 'defaults.model',
  thinking: 'defaults.thinking',
  workingDirectory: 'tools.workingDirectory',
  approvalMode: 'tools.approvalMode',
  allowList: 'tools.allowList',
};

// The section as the form holds it now.
function read(form) {
  if (form.dataset.settingsForm === 'defaults') {
    const choice = providerModelValue(form.querySelector('[data-provider-model]'));
    const thinking = form.querySelector('input[name="thinking"]:checked')?.value ?? '';
    // With no default saved, the dropdowns show the first model; it becomes the default only once the user picks a model, so saving
    // just the thinking level never sets a model the user did not choose.
    const unset = form.dataset.modelUnset === 'true' && form.dataset.modelTouched !== 'true';
    return { provider: unset ? null : choice?.provider ?? null, model: unset ? null : choice?.model ?? null, thinking: thinking || null };
  }
  const rules = [...form.querySelectorAll('[data-allow-list] [data-allow-rule]')].map((row) => ({ tool: row.dataset.tool, command: row.dataset.command || null }));
  return {
    workingDirectory: form.querySelector('[data-working-directory]')?.value.trim() ?? '',
    approvalMode: form.querySelector('input[name="approvalMode"]:checked')?.value ?? 'ask',
    allowList: rules,
  };
}

function changed(before, now) {
  return Object.keys(now).filter((key) => JSON.stringify(before[key] ?? null) !== JSON.stringify(now[key] ?? null));
}

function setup(form) {
  const state = { before: read(form), mode: form.querySelector('input[name="approvalMode"]:checked')?.value ?? null };
  const update = () => {
    const keys = changed(state.before, read(form));
    const bar = form.querySelector('[data-savebar]');
    setShown(bar, keys.length > 0);
    setText(form.querySelector('[data-savebar-count]'), keys.length === 1 ? '1 unsaved change' : `${keys.length} unsaved changes`);
    setText(form.querySelector('[data-savebar-names]'), keys.map((key) => NAMES[key] ?? key).join(' · '));
    form.querySelectorAll('[data-setting-row]').forEach((row) => {
      const names = row.dataset.settingRow === 'model' ? ['provider', 'model'] : [row.dataset.settingRow];
      const modified = names.some((name) => keys.includes(name));
      row.classList.toggle('is-modified', modified);
      setShown(row.querySelector('[data-modified-chip]'), modified);
    });
    setText(form.querySelector('[data-mode-key]'), read(form).approvalMode ?? '');
  };
  form.addEventListener('input', update);
  form.addEventListener('change', async (event) => {
    if (event.target.matches('input[name="approvalMode"]')) await onMode(form, state, event.target);
    if (event.target.matches('[data-allow-tool]')) syncPattern(form);
    update();
  });
  form.addEventListener('provider-model-change', () => {
    form.dataset.modelTouched = 'true';
    markThinking(form);
    update();
  });
  form.addEventListener('click', (event) => {
    if (event.target.closest('[data-discard]')) {
      window.location.reload();
    } else if (event.target.closest('[data-allow-add]')) {
      addRule(form);
      update();
    } else if (event.target.closest('[data-allow-remove]')) {
      event.target.closest('[data-allow-rule]')?.remove();
      setShown(form.querySelector('[data-allow-empty]'), !form.querySelector('[data-allow-list] [data-allow-rule]'));
      update();
    }
  });
  form.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && event.target.matches('[data-allow-pattern]')) {
      event.preventDefault();
      addRule(form);
      update();
    }
  });
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const body = read(form);
    try {
      await busy(form.querySelector('[data-save]'), () => postJson(form.dataset.endpoint, body));
      flashAndReload({ title: form.dataset.savedTitle || 'Saved', body: '', tone: 'ok' });
    } catch (error) {
      showError('Could not save', error);
    }
  });
  markThinking(form);
  syncPattern(form);
  update();
}

// "Never ask" asks first; cancelling goes back to the mode chosen before.
async function onMode(form, state, radio) {
  if (radio.value !== 'auto') {
    state.mode = radio.value;
    return;
  }
  const ok = await confirmAction({
    title: 'Auto-approve every tool call?',
    body: 'Every tool call runs immediately, including commands and file changes, with your account\'s permissions. Only use this in a disposable folder or a container.',
    ok: 'Turn off approvals',
  });
  if (ok) {
    state.mode = 'auto';
    return;
  }
  const previous = form.querySelector(`input[name="approvalMode"][value="${state.mode === 'auto' ? 'ask' : state.mode ?? 'ask'}"]`);
  if (previous) previous.checked = true;
}

// Marks the thinking levels the chosen default model doesn't support (Defaults).
function markThinking(form) {
  const group = form.querySelector('[data-thinking-levels]');
  if (!group) return;
  const choice = providerModelValue(form.querySelector('[data-provider-model]'));
  const supported = choice?.thinking ?? [];
  const unsupported = [];
  group.querySelectorAll('[data-thinking-level]').forEach((input) => {
    const level = input.dataset.thinkingLevel;
    const no = supported.length > 0 && !supported.includes(level);
    form.querySelector(`label[for="${input.id}"]`)?.classList.toggle('is-unsupported', no);
    if (no) unsupported.push(level);
  });
  setText(form.querySelector('[data-thinking-note]'), unsupported.length && choice?.model
    ? `${choice.model} doesn't support ${unsupported.join(', ')}; those are clamped to its nearest level.` : '');
}

function syncPattern(form) {
  const tool = form.querySelector('[data-allow-tool]');
  const pattern = form.querySelector('[data-allow-pattern]');
  if (!tool || !pattern) return;
  const bash = tool.value === 'bash';
  pattern.disabled = !bash;
  pattern.placeholder = bash ? 'e.g. dotnet test *  (blank: every command)' : `every ${tool.value} call`;
  if (!bash) pattern.value = '';
}

function addRule(form) {
  const tool = form.querySelector('[data-allow-tool]')?.value;
  const pattern = form.querySelector('[data-allow-pattern]');
  if (!tool) return;
  const command = tool === 'bash' ? (pattern?.value.trim() || null) : null;
  const list = form.querySelector('[data-allow-list]');
  const exists = [...list.querySelectorAll('[data-allow-rule]')].some((row) => row.dataset.tool === tool && (row.dataset.command || null) === command);
  if (!exists) {
    const row = form.querySelector('template[data-allow-template]').content.firstElementChild.cloneNode(true);
    row.dataset.tool = tool;
    if (command) row.dataset.command = command;
    setText(row.querySelector('[data-rule-tool]'), tool);
    setText(row.querySelector('[data-rule-command]'), command ?? 'every call');
    row.querySelector('[data-allow-remove]').setAttribute('aria-label', `Remove ${tool} ${command ?? 'rule'}`);
    list.append(row);
  }
  if (pattern) pattern.value = '';
  setShown(form.querySelector('[data-allow-empty]'), false);
}

async function resetDefaults(event) {
  const ok = await confirmAction({
    title: 'Reset all agent defaults?',
    body: 'The default model and thinking level go back to "not set". Running chats keep their own settings.',
    ok: 'Reset defaults',
  });
  if (!ok) return;
  try {
    await busy(event.target.closest('button'), () => postJson('/api/settings/defaults/reset'));
    flashAndReload({ title: 'Defaults reset', body: '', tone: 'ok' });
  } catch (error) {
    showError('Could not reset the defaults', error);
  }
}

function setupFolders(browser) {
  const input = document.querySelector('[data-working-directory]');
  let listing = null;
  const load = async (path) => {
    const list = browser.querySelector('[data-folder-list]');
    setShown(browser.querySelector('[data-folder-error]'), false);
    try {
      listing = await getJson(`/api/settings/folders?path=${encodeURIComponent(path ?? '')}`);
      render(browser, listing, load);
    } catch (error) {
      setText(browser.querySelector('[data-folder-error]'), error.message);
      setShown(browser.querySelector('[data-folder-error]'), true);
      if (path) {
        await load('');
      } else {
        list.replaceChildren();
      }
    }
  };
  browser.addEventListener('show.bs.collapse', () => load(input?.value.trim() ?? ''));
  browser.querySelector('[data-folder-use]')?.addEventListener('click', () => {
    if (!listing?.path || !input) return;
    input.value = listing.path;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    window.bootstrap?.Collapse.getOrCreateInstance(browser).hide();
  });
}

function render(browser, listing, load) {
  const crumbs = browser.querySelector('[data-folder-crumbs]');
  crumbs.replaceChildren();
  listing.crumbs.forEach((crumb, index) => {
    if (index > 0) {
      const chevron = document.createElement('i');
      chevron.className = 'bi bi-chevron-right text-ps-faint';
      chevron.setAttribute('aria-hidden', 'true');
      crumbs.append(chevron);
    }
    if (index === listing.crumbs.length - 1) {
      const span = document.createElement('span');
      span.textContent = crumb.name;
      crumbs.append(span);
    } else {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'btn btn-link btn-sm p-0 ps-mono fs-8';
      button.textContent = crumb.name;
      button.addEventListener('click', () => load(crumb.path ?? ''));
      crumbs.append(button);
    }
  });
  const list = browser.querySelector('[data-folder-list]');
  list.replaceChildren();
  const row = (icon, name, onOpen, chips = []) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'list-group-item list-group-item-action d-flex align-items-center gap-2 bg-transparent';
    const i = document.createElement('i');
    i.className = `bi ${icon}`;
    i.setAttribute('aria-hidden', 'true');
    const label = document.createElement('span');
    label.className = 'text-break';
    label.textContent = name;
    button.append(i, label);
    if (chips.length) {
      const holder = document.createElement('span');
      holder.className = 'ms-auto d-flex gap-1';
      for (const text of chips) {
        const chip = document.createElement('span');
        chip.className = 'ps-chip';
        chip.dataset.tone = 'accent';
        chip.textContent = text;
        holder.append(chip);
      }
      button.append(holder);
    }
    button.addEventListener('click', onOpen);
    list.append(button);
  };
  if (listing.parent !== null && listing.parent !== undefined) row('bi-arrow-90deg-up text-ps-faint', '..', () => load(listing.parent));
  for (const folder of listing.folders) {
    row('bi-folder', folder.name, () => load(folder.path), [folder.git ? 'git' : null, folder.agents ? 'AGENTS.md' : null].filter(Boolean));
  }
  if (listing.folders.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'list-group-item bg-transparent text-ps-faint';
    empty.textContent = 'No folders here.';
    list.append(empty);
  }
  if (listing.truncated) {
    const more = document.createElement('div');
    more.className = 'list-group-item bg-transparent fs-8 text-ps-faint';
    more.textContent = 'Only the first 500 folders are listed. Type the path instead.';
    list.append(more);
  }
  setText(browser.querySelector('[data-folder-current]'), listing.path ?? 'Choose a drive');
  const use = browser.querySelector('[data-folder-use]');
  if (use) use.disabled = !listing.path;
}
