// Decision 0011 (run W4, R3): the run actions History › Runs and the Run inspector share. Re-run asks GET /api/history/runs/{id}/spec
// for the run's stored request, posts it to /api/runs (when the Spend Guard refuses, its confirm modal asks and the run is sent again only
// with the user's choice) and opens the new run's inspector. Delete confirms, then POST /api/runs/delete; Pin posts /api/runs/{id}/pin.
// Text is set with textContent only; nothing here stores a request.
import { confirmAction, showToast } from './shell.js';
import { flashAndReload } from './settings-api.js';
import { getRequest, postRequest } from './request-api.js';
import { showSpendConfirm } from './spend-confirm.js';

const runPath = (id) => `/api/runs/${encodeURIComponent(id)}`;

/** Shows a danger toast for a failed call. */
export function failed(title, error) {
  showToast({ title, body: error instanceof Error ? error.message : String(error ?? ''), tone: 'danger' });
}

/** Sends run `id`'s stored request again; opens the new run's inspector. `button` (optional) is disabled meanwhile. */
export async function rerun(id, button) {
  if (button) button.disabled = true;
  try {
    const answer = await getRequest(`/api/history/runs/${encodeURIComponent(id)}/spec`);
    const spec = answer?.spec;
    if (!spec) throw new Error('This run has no stored request to send again.');
    let spend;
    for (;;) {
      const started = await postRequest('/api/runs', spend ? { ...spec, spend } : spec);
      if (started?.posted) {
        flashAndReload({ title: 'Re-run started', body: `Run ${started.id}`, tone: 'ok' }, `/runs/${encodeURIComponent(started.id)}`);
        return;
      }
      if (!started?.spend) {
        showToast({ title: 'Not run', body: started?.message ?? 'The Spend Guard refused the run.', tone: 'warn' });
        return;
      }
      const choice = await showSpendConfirm(started.spend);
      if (choice.choice === 'proceed-once') spend = { choice: 'proceed-once' };
      else if (choice.choice === 'cap-output' && choice.maxOutputTokens) spend = { choice: 'cap-output', maxOutputTokens: choice.maxOutputTokens };
      else return;
    }
  } catch (error) {
    failed('Not run', error);
  } finally {
    if (button && button.isConnected) button.disabled = false;
  }
}

/** Asks, then deletes `ids` (with what they group). `label` names a single run; `after` is where to go next (this page by default). */
export async function deleteRuns(ids, { label = '', after } = {}) {
  if (!ids.length) return;
  const one = ids.length === 1;
  const ok = await confirmAction({
    title: one ? 'Delete this run?' : `Delete ${ids.length} runs?`,
    body: (one ? 'The run, its stream events and its raw payloads are' : 'These runs, their stream events and their raw payloads are')
      + " deleted from runs.db, with the models and turns a grouped run holds. Chats keep their transcripts. This can't be undone.",
    detail: one ? label : '',
    ok: one ? 'Delete run' : `Delete ${ids.length} runs`,
    icon: 'bi-trash3',
  });
  if (!ok) return;
  try {
    const answer = await postRequest('/api/runs/delete', { ids });
    const deleted = Number(answer?.deleted ?? 0);
    flashAndReload({ title: one ? 'Run deleted' : `${ids.length} runs deleted`, body: deleted > ids.length ? `${deleted} rows with what they grouped.` : '', tone: 'ok' }, after);
  } catch (error) {
    failed('Not deleted', error);
  }
}

/** Pins or unpins run `id`, then reloads. */
export async function pinRun(id, pinned) {
  try {
    await postRequest(`${runPath(id)}/pin`, { pinned });
    flashAndReload({ title: pinned ? 'Run pinned' : 'Run unpinned', body: pinned ? 'It never expires.' : 'It expires with retention.', tone: 'ok' });
  } catch (error) {
    failed(pinned ? 'Not pinned' : 'Not unpinned', error);
  }
}

/** Wires the delegated row and header actions under `root`: [data-run-rerun], [data-run-delete], [data-run-pin]. */
export function wireRunActions(root) {
  root.addEventListener('click', (event) => {
    const rerunButton = event.target.closest('[data-run-rerun]');
    if (rerunButton) {
      rerun(rerunButton.dataset.runRerun, rerunButton);
      return;
    }
    const deleteButton = event.target.closest('[data-run-delete]');
    if (deleteButton) {
      deleteRuns([deleteButton.dataset.runDelete], { label: deleteButton.dataset.runLabel ?? '', after: deleteButton.dataset.runAfterDelete });
      return;
    }
    const pinButton = event.target.closest('[data-run-pin]');
    if (pinButton) pinRun(pinButton.dataset.runPin, pinButton.dataset.pinned !== 'true');
  });
}
