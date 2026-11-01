// Feedback controls on artifact cards (decision 0010, M5). Each choice is one feedback event in the device's default profile. A write that
// failed keeps its request ID, so Retry cannot record the choice twice; Undo appends a reversal and shows the state it reverts to.
import { $, $$, uuid } from './core.js';

const enc = encodeURIComponent;
const names = { more: 'More like this', less: 'Less like this', dismiss: 'Dismiss' };

/** Wires every feedback group under `container`; `api` is the device API and `sessionId()` the session the cards belong to. */
export function wireFeedback(container, api, sessionId) {
  container.addEventListener('click', event => {
    const button = event.target.closest('[data-feedback-action], [data-feedback-retry]');
    const group = button?.closest('[data-feedback]');
    if (!group) return;
    const pending = group.dataset.pending ? JSON.parse(group.dataset.pending) : null;
    if (button.hasAttribute('data-feedback-retry')) { if (pending) send(group, pending); return; }
    const action = button.dataset.feedbackAction;
    const example = action !== 'undo' && action !== 'dismiss' && $('[data-feedback-example]', group)?.checked === true;
    const labels = action === 'undo' ? [] : ($('[data-feedback-labels]', group)?.value || '').split(',').map(label => label.trim().toLowerCase()).filter(Boolean);
    if (action === 'label' && !labels.length) { note(group, 'Name at least one label first.'); return; }
    const same = pending?.action === action && pending.example === example && pending.labels.join() === labels.join();
    send(group, same ? pending : { action, example, labels, requestId: uuid() });
  });

  async function send(group, request) {
    const status = $('[data-feedback-state]', group);
    const retry = $('[data-feedback-retry]', group);
    group.dataset.pending = JSON.stringify(request);
    retry.hidden = true;
    group.setAttribute('aria-busy', 'true');
    $$('button', group).forEach(button => { button.disabled = true; });
    status.textContent = 'Saving…';
    const path = `/sessions/${enc(sessionId())}/artifacts/${enc(group.dataset.feedback)}/feedback`;
    try {
      const body = { action: request.action };
      if (request.labels.length) body.labels = request.labels;
      if (request.example) body.example = true;
      await api.post(path, body, { idempotencyKey: request.requestId });
      delete group.dataset.pending;
      show(group, await api.get(path), request.action === 'undo');
    } catch (failure) {
      note(group, `Not saved: ${failure.message}${failure.fields?.length ? ` (${failure.fields.map(field => `${field.path} ${field.message}`).join('; ')})` : ''}`);
      retry.hidden = false;
    } finally {
      group.removeAttribute('aria-busy');
      $$('button', group).forEach(button => { button.disabled = false; });
    }
  }
}

function note(group, text) {
  const status = $('[data-feedback-state]', group);
  status.textContent = text;
  status.classList.add('text-danger');
}

function show(group, state, reverted) {
  const status = $('[data-feedback-state]', group);
  status.classList.remove('text-danger');
  $$('[data-feedback-action]', group).filter(button => button.dataset.feedbackAction in names)
    .forEach(button => button.setAttribute('aria-pressed', String(button.dataset.feedbackAction === state.vote)));
  $('[data-feedback-action="undo"]', group).hidden = !state.undo;
  const example = $('[data-feedback-example]', group);
  if (example) example.checked = state.example === true;
  const labelled = state.labels?.length ? `labels ${state.labels.join(', ')}` : '';
  const now = [state.vote ? names[state.vote] : '', labelled].filter(Boolean).join(' · ') || 'no feedback';
  status.textContent = `${reverted ? 'Reverted to' : 'Saved:'} ${now} · profile ${state.profileId} r${state.revision}`;
}
