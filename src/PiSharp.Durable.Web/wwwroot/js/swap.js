// PiSharp Durable Web: applies the hub's fragments. A `view` frame upserts, removes and orders transcript items by key; a panel is
// replaced whole. Open <details> keep their state across a swap (matched by data-detail), and a thread scrolled to the bottom stays there.
import { element } from './core.js';

function openDetails(root) {
  const open = new Set();
  root.querySelectorAll('details[data-detail]').forEach((d) => {
    open.add(d.getAttribute('data-detail') + (d.open ? ':open' : ':closed'));
  });
  return open;
}

function restoreDetails(root, states) {
  root.querySelectorAll('details[data-detail]').forEach((d) => {
    const key = d.getAttribute('data-detail');
    if (states.has(key + ':open')) d.open = true;
    else if (states.has(key + ':closed')) d.open = false;
  });
}

/** Applies a view frame's items to the thread; returns whether anything changed. */
export function applyItems(thread, items, reset) {
  if (reset) thread.replaceChildren();
  let changed = false;
  for (const { id, html } of items.upsert || []) {
    const fresh = element(html);
    if (!fresh) continue;
    const old = thread.querySelector(`:scope > [data-key="${CSS.escape(id)}"]`);
    if (old) {
      const states = openDetails(old);
      old.replaceWith(fresh);
      restoreDetails(fresh, states);
    } else {
      thread.appendChild(fresh);
    }
    changed = true;
  }
  for (const id of items.remove || []) {
    const old = thread.querySelector(`:scope > [data-key="${CSS.escape(id)}"]`);
    if (old) { old.remove(); changed = true; }
  }
  const order = items.order || [];
  let cursor = thread.firstElementChild;
  for (const id of order) {
    const el = thread.querySelector(`:scope > [data-key="${CSS.escape(id)}"]`);
    if (!el) continue;
    if (el !== cursor) { thread.insertBefore(el, cursor); changed = true; }
    else cursor = cursor.nextElementSibling;
  }
  return changed;
}

/** Replaces a container's content with HTML, keeping open details and focus where it can. */
export function swapHtml(container, html) {
  const states = openDetails(container);
  const focused = document.activeElement && container.contains(document.activeElement) ? document.activeElement.id : null;
  container.innerHTML = html;
  restoreDetails(container, states);
  if (focused) document.getElementById(focused)?.focus();
}

/** Runs a change and keeps a scroller pinned to the bottom when it was there. */
export function keepPinned(scroller, change) {
  const pinned = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 80;
  const result = change();
  if (pinned) scroller.scrollTop = scroller.scrollHeight;
  return result;
}
