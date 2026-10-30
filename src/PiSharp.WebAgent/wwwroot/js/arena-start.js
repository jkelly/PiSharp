// Decision 0011 (run W5, E5): starting a blind round from Compare's setup. The Blind switch ([data-arena-blind]: request in
// _ComparePrompt, agent in _AgentSetup) is remembered in this browser only (a per-viewer convenience; it reads back off when storage is
// unavailable). With it on, Run all (or Run agents) posts the round to POST /api/evals/arena instead of Compare's own endpoints: the server
// shuffles the models into columns A to D and the page opens the battle (GET /compare/arena/{id}), which names no model before the vote. A
// request round the Spend Guard refuses shows modal-spend-confirm (the server's markup) and is started again with the user's choice
// (POST /api/evals/arena/{id}/start); Cancel discards the round that never started.
import { postRequest } from './request-api.js';
import { showSpendConfirm } from './spend-confirm.js';

export const ARENA_API = '/api/evals/arena';
const STORAGE_KEY = 'ps.compare.blind';

/** The Blind switch on this page, or null. */
export function blindSwitch() {
  return document.querySelector('[data-arena-blind]');
}

/** Whether the Blind switch is on. */
export function blindOn() {
  const input = blindSwitch();
  return Boolean(input && input.checked && !input.disabled);
}

/** Restores the switch and calls `onChange(on)` now and whenever it changes. */
export function initBlindSwitch(onChange) {
  const input = blindSwitch();
  if (!input) return;
  try {
    input.checked = window.localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    input.checked = false;
  }
  input.addEventListener('change', () => {
    try { window.localStorage.setItem(STORAGE_KEY, input.checked ? '1' : '0'); } catch { /* Not kept: storage is unavailable. */ }
    onChange?.(input.checked);
  });
  onChange?.(input.checked);
}

/** Opens a started round, or asks the Spend Guard's question and starts it again with the answer. Resolves true when a battle opens. */
export async function followStart(answer, { onRefused } = {}) {
  let current = answer;
  for (;;) {
    if (current?.posted && current.url) {
      window.location.assign(current.url);
      return true;
    }
    if (!current?.battleId || !current?.spend) throw new Error(current?.message ?? 'The Spend Guard refused the round.');
    onRefused?.(current);
    const choice = await showSpendConfirm(current.spend);
    if (choice?.choice !== 'proceed-once' && choice?.choice !== 'cap-output') {
      try { await postRequest(`${ARENA_API}/${encodeURIComponent(current.battleId)}/discard`, {}); } catch { /* It stays until it expires. */ }
      return false;
    }
    const spend = choice.choice === 'cap-output' ? { choice: 'cap-output', maxOutputTokens: choice.maxOutputTokens } : { choice: 'proceed-once' };
    current = await postRequest(`${ARENA_API}/${encodeURIComponent(current.battleId)}/start`, { spend });
  }
}

/** Starts a blind request round from Compare's draft (overrides are cleared on the server). */
export async function startRequestRound(draft, options) {
  return followStart(await postRequest(ARENA_API, { mode: 'request', draft }), options);
}

/** Starts a blind agent round from the agent setup's create body (with its request id). */
export async function startAgentRound(agent) {
  const answer = await postRequest(ARENA_API, { mode: 'agent', agent });
  if (answer?.url) window.location.assign(answer.url);
  return answer;
}
