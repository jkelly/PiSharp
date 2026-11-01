// Decision 0011 (run W3, S4): modal-spend-confirm for the pages that send model requests (S5 hosts it on the chat pages, W4 on Playground
// and Compare). confirmSpend(call) asks POST /api/spend/confirm (a preflight: nothing is reserved or sent) and resolves at once with
// { choice: 'allow' } when the request fits; otherwise it shows the cause's pane (the server's own markup) and resolves with the user's
// choice: 'proceed-once', 'cap-output' (with maxOutputTokens), 'choose-model' (after opening #modal-model-picker, when the page has one)
// or 'cancel'. It never switches a model or an account: the caller sends exactly what the user chose. A page that renders
// _SpendConfirmModal with a null model gets its content filled in; one that does not gets the modal appended.
import { postJson } from './settings-api.js';

/** The guard's answer for `call` ({ provider, model, account, source, run, inputBytes, maxTokens }): verdict, code, pane, estimate, html. */
export function preflight(call) {
  return postJson('/api/spend/confirm', call);
}

/** Shows the confirm modal of a preflight answer that is not 'allow'; resolves with the user's choice. */
export function showSpendConfirm(answer) {
  const fresh = new DOMParser().parseFromString(answer?.html ?? '', 'text/html').getElementById('modal-spend-confirm');
  if (!fresh || !window.bootstrap) return Promise.resolve({ choice: 'cancel', verdict: answer?.verdict, code: answer?.code });
  let host = document.getElementById('modal-spend-confirm');
  if (!host) {
    host = document.importNode(fresh, true);
    document.body.append(host);
  } else {
    host.querySelector('[data-spend-confirm-content]').replaceWith(document.importNode(fresh.querySelector('[data-spend-confirm-content]'), true));
  }
  const picker = document.getElementById('modal-model-picker');
  const content = host.querySelector('[data-spend-confirm-content]');
  const choose = content.querySelector('[data-spend-choice="choose-model"]');
  if (choose) choose.hidden = !picker;
  const modal = window.bootstrap.Modal.getOrCreateInstance(host);
  return new Promise((resolve) => {
    let result = { choice: 'cancel', verdict: answer.verdict, code: answer.code, pane: answer.pane };
    const onClick = (event) => {
      const button = event.target.closest('[data-spend-choice]');
      if (!button || button.disabled) return;
      const tokens = Number(button.dataset.maxOutputTokens);
      result = {
        choice: button.dataset.spendChoice,
        verdict: answer.verdict,
        code: answer.code,
        pane: answer.pane,
        maxOutputTokens: Number.isInteger(tokens) && tokens > 0 ? tokens : undefined,
      };
      modal.hide();
    };
    content.addEventListener('click', onClick);
    host.addEventListener('hidden.bs.modal', () => {
      content.removeEventListener('click', onClick);
      if (result.choice === 'choose-model' && picker) window.bootstrap.Modal.getOrCreateInstance(picker).show();
      resolve(result);
    }, { once: true });
    modal.show();
  });
}

/** Checks `call` against the caps and asks the user when it does not fit. Resolves with { choice, verdict, maxOutputTokens?, estimate }. */
export async function confirmSpend(call) {
  const answer = await preflight(call);
  if (!answer || answer.verdict === 'allow' || !answer.html) return { choice: 'allow', verdict: 'allow', estimate: answer?.estimate };
  const result = await showSpendConfirm(answer);
  return { ...result, estimate: answer.estimate };
}
