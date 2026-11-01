// Decision 0011 (run W4, R6): Settings › Tools › Agent compare (_AgentCompareDefaults, form [data-agent-compare-defaults]). Its own save
// bar tracks the fields ([data-ac-default]) against the values they had when the page loaded (or were last saved); Save posts the changed
// fields to POST /api/settings/agent-compare (a field left out keeps its stored value), Discard puts the saved values back, and Reset to
// defaults posts /api/settings/agent-compare/reset after a confirm. The tool approval's description follows the choice: Allow-list and Auto
// say that workspaces are copies, not sandboxes. Text is set with textContent only. "Also skip .gitignore patterns" (useGitignore) is a
// switch: it is compared and saved by its checked state (W4 fix 2).
import { confirmAction, showToast } from './shell.js';
import { postJson, setText, busy } from './settings-api.js';

const NAMES = {
  isolation: 'agentCompare.isolation',
  extraExcludes: 'agentCompare.extraExcludes',
  useGitignore: 'agentCompare.useGitignore',
  copyWarnGb: 'agentCompare.copyWarnGb',
  maxTurns: 'agentCompare.maxTurns',
  maxMinutes: 'agentCompare.maxMinutes',
  checkTimeoutMinutes: 'agentCompare.checkTimeoutMinutes',
  approval: 'agentCompare.approval',
};

const form = document.querySelector('form[data-agent-compare-defaults]');
if (form) init(form);

function init(root) {
  const endpoint = root.dataset.endpoint || '/api/settings/agent-compare';
  const fields = [...root.querySelectorAll('[data-ac-default]')];
  const bar = root.querySelector('[data-ac-savebar]');
  // A switch's value is its checked state ("true" or "false"); the other fields' is their text.
  const valueOf = (el) => (el.type === 'checkbox' ? String(el.checked) : el.value);
  const setValue = (el, value) => {
    if (el.type === 'checkbox') el.checked = value === true || value === 'true';
    else el.value = value;
  };
  const saved = new Map(fields.map((el) => [el, valueOf(el)]));
  const key = (el) => el.dataset.acDefault;
  const changed = () => fields.filter((el) => valueOf(el) !== saved.get(el));

  function showApproval() {
    const value = root.querySelector('[data-ac-default="approval"]')?.value ?? 'ask';
    root.querySelectorAll('[data-ac-approval-pane]').forEach((pane) => { pane.hidden = pane.dataset.acApprovalPane !== value; });
  }

  function refresh() {
    const list = changed();
    if (bar) bar.hidden = list.length === 0;
    setText(root.querySelector('[data-ac-savebar-count]'), list.length === 1 ? '1 unsaved change' : `${list.length} unsaved changes`);
    setText(root.querySelector('[data-ac-savebar-names]'), list.map((el) => NAMES[key(el)] ?? key(el)).join(' · '));
    for (const el of fields) el.classList.toggle('border-warning', valueOf(el) !== saved.get(el));
    showApproval();
  }

  /** Puts the settings an answer holds into the fields and makes them the saved values. */
  function load(settings) {
    if (!settings) return;
    for (const el of fields) {
      const name = key(el);
      const value = settings[name];
      if (value === undefined || value === null) continue;
      if (name === 'extraExcludes') el.value = Array.isArray(value) ? value.join(', ') : String(value);
      else if (el.type === 'checkbox') setValue(el, value === true);
      else if (el.tagName === 'SELECT' && ![...el.options].some((option) => option.value === String(value))) continue;
      else el.value = String(value);
    }
    for (const el of fields) saved.set(el, valueOf(el));
    refresh();
  }

  function body(list) {
    const answer = {};
    for (const el of list) {
      const name = key(el);
      if (name === 'extraExcludes') answer.extraExcludes = el.value.split(/[,\n]/).map((item) => item.trim()).filter(Boolean);
      else if (name === 'useGitignore') answer.useGitignore = el.checked;
      else if (name === 'copyWarnGb') answer.copyWarnGb = Number.parseFloat(el.value);
      else if (name === 'maxTurns' || name === 'maxMinutes' || name === 'checkTimeoutMinutes') answer[name] = Number.parseInt(el.value, 10);
      else answer[name] = el.value;
    }
    return answer;
  }

  root.addEventListener('input', refresh);
  root.addEventListener('change', refresh);
  root.querySelector('[data-ac-discard]')?.addEventListener('click', () => {
    for (const el of fields) setValue(el, saved.get(el) ?? '');
    refresh();
  });
  root.addEventListener('submit', async (event) => {
    event.preventDefault();
    const list = changed();
    if (list.length === 0) return;
    const invalid = list.find((el) => el.tagName === 'INPUT' && !el.checkValidity());
    if (invalid) {
      invalid.reportValidity();
      return;
    }
    await busy(root.querySelector('[data-ac-save]'), async () => {
      try {
        load(await postJson(endpoint, body(list)));
        showToast({ title: 'Agent compare defaults saved', body: 'New runs start with them. Runs already started keep their own.', tone: 'ok' });
      } catch (error) {
        showToast({ title: 'Agent compare defaults not saved', body: error.message, tone: 'danger' });
      }
    });
  });
  root.querySelector('[data-ac-defaults-reset]')?.addEventListener('click', async (event) => {
    const yes = await confirmAction({
      title: 'Reset agent compare defaults?',
      body: 'Isolation, what copies skip, the copy size warning, the limits, the check timeout and the tool approval go back to their defaults.',
      ok: 'Reset',
    });
    if (!yes) return;
    await busy(event.target.closest('button'), async () => {
      try {
        load(await postJson(`${endpoint}/reset`, {}));
        showToast({ title: 'Agent compare defaults reset', tone: 'ok' });
      } catch (error) {
        showToast({ title: 'Agent compare defaults not reset', body: error.message, tone: 'danger' });
      }
    });
  });
  refresh();
}
