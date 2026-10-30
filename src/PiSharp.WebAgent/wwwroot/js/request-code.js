// Decision 0011 (run W4, R1): _ViewCodeModal's behaviour. When the modal opens it sends the current request to POST /api/playground/code
// (the server builds C#, cURL and JSON from the run engine's own plan, so the code is what Run would send, with "••••" where a credential
// goes) and fills the tabs with textContent. The copy buttons copy a tab's text.
import { postRequest, copyText } from './request-api.js';

/** Wires #modal-view-code: `spec()` returns the request to show (it may throw an Error whose message is shown). */
export function initViewCode(spec, { onCopied } = {}) {
  const modal = document.getElementById('modal-view-code');
  if (!modal || modal.dataset.wired === 'true') return;
  modal.dataset.wired = 'true';
  const error = modal.querySelector('[data-code-error]');
  const fill = (name, text) => {
    const target = modal.querySelector(`[data-code="${name}"]`);
    if (target) target.textContent = text ?? '';
  };
  modal.addEventListener('show.bs.modal', async () => {
    error.hidden = true;
    for (const name of ['cs', 'curl', 'json', 'provider']) fill(name, '…');
    let request;
    try {
      request = spec();
    } catch (problem) {
      error.textContent = problem.message;
      error.hidden = false;
      for (const name of ['cs', 'curl', 'json', 'provider']) fill(name, '');
      return;
    }
    try {
      const code = await postRequest('/api/playground/code', request);
      modal.querySelector('[data-code-subtitle]').textContent = code.subtitle ?? '';
      fill('cs', code.cSharp ?? code.csharp);
      fill('curl', code.curl);
      fill('json', code.json);
      fill('provider', code.provider);
      modal.querySelector('[data-code-provider-wrap]').hidden = !code.provider;
      const notes = modal.querySelector('[data-code-notes]');
      notes.replaceChildren(...(code.notes ?? []).map((note) => {
        const item = document.createElement('li');
        item.textContent = note;
        return item;
      }));
      const inspector = modal.querySelector('[data-code-inspector]');
      if (inspector) {
        inspector.hidden = !code.inspectorUrl;
        if (code.inspectorUrl) inspector.href = code.inspectorUrl;
      }
    } catch (problem) {
      error.textContent = problem.message;
      error.hidden = false;
      for (const name of ['cs', 'curl', 'json', 'provider']) fill(name, '');
    }
  });
  modal.addEventListener('click', async (event) => {
    const button = event.target.closest('[data-copy-code]');
    if (!button) return;
    const text = modal.querySelector(`[data-code="${button.dataset.copyCode}"]`)?.textContent ?? '';
    onCopied?.(await copyText(text));
  });
}
