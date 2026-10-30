// Decision 0011 (run W4, R7): the Tool Bench's setup in the page, for bench.js, bench-tools.js and Presets (R8). The page keeps the
// session's setup and user messages in [data-bench] (data-setup, data-messages, refreshed on every update); every change goes to the
// server (POST /api/bench/sessions/{id}/setup), one at a time, and the answer (the page's parts) is dispatched as a "bench-view" event on
// [data-bench], which bench.js renders. Presets read a bench with readSetup, load one into the open session with applySetup, or open a
// new session with openSession (setup and messages; the server checks both). Nothing here runs a turn.
import { postRequest } from './request-api.js';

let queue = Promise.resolve();

function rootOf(root) {
  return root ?? document.querySelector('[data-bench]');
}

function parse(text, fallback) {
  try { return text ? JSON.parse(text) : fallback; } catch { return fallback; }
}

/** The page's session, setup and user messages: { session, setup, messages }. */
export function readSetup(root) {
  const page = rootOf(root);
  if (!page) return null;
  return {
    session: page.dataset.session,
    setup: parse(page.dataset.setup, {}),
    messages: parse(page.dataset.messages, []),
  };
}

/** Dispatches the server's answer (the page's parts) to bench.js. */
export function showView(view, root) {
  const page = rootOf(root);
  if (page && view) page.dispatchEvent(new CustomEvent('bench-view', { detail: view }));
  return view;
}

/** Saves `setup` as the open session's setup; resolves with the page's parts (also rendered). Throws RequestError (400: not valid). */
export function applySetup(setup, root) {
  const page = rootOf(root);
  if (!page) return Promise.reject(new Error('There is no Tool Bench on this page.'));
  const run = queue.then(async () => {
    const view = await postRequest(`/api/bench/sessions/${encodeURIComponent(page.dataset.session)}/setup`, { setup });
    return showView(view, page);
  });
  queue = run.catch(() => undefined);
  return run;
}

/** Changes the open session's setup with `change(setup)` (a copy of the latest one, changed in place or returned) and saves it. */
export function changeSetup(change, root) {
  const page = rootOf(root);
  if (!page) return Promise.reject(new Error('There is no Tool Bench on this page.'));
  const run = queue.then(async () => {
    const current = structuredClone(parse(page.dataset.setup, {}));
    const next = change(current) ?? current;
    const view = await postRequest(`/api/bench/sessions/${encodeURIComponent(page.dataset.session)}/setup`, { setup: next });
    return showView(view, page);
  });
  queue = run.catch(() => undefined);
  return run;
}

/** Opens a new bench session with `setup` and `messages` (user messages) and goes to it. Throws RequestError. */
export async function openSession(setup, messages = []) {
  const answer = await postRequest('/api/bench/sessions', { setup, messages });
  if (answer?.url) window.location.assign(answer.url);
  return answer;
}
