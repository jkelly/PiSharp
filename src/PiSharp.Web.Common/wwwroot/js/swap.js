// PiSharp.Web.Common (decision 0011), from PiSharp Durable Web: applies server-rendered fragments. A `view` frame upserts, removes and
// orders items by key (each item's root carries data-key); a panel is replaced whole. Open <details> keep their state across a swap
// (matched by data-detail), and a scroller at the bottom stays there. The HTML comes from the server's Razor partials, which encode and
// sanitize it; nothing here builds markup from text.

/** An element from an HTML string with one root. */
export function element(html) {
  const template = document.createElement('template');
  template.innerHTML = html.trim();
  return template.content.firstElementChild;
}

/** The keyed <details> of a subtree, its root included (an item may itself be a <details>, such as a tool card). */
function detailsIn(root) {
  const found = [...root.querySelectorAll('details[data-detail]')];
  if (root.matches && root.matches('details[data-detail]')) found.unshift(root);
  return found;
}

function openDetails(root) {
  const open = new Set();
  detailsIn(root).forEach((d) => {
    open.add(d.getAttribute('data-detail') + (d.open ? ':open' : ':closed'));
  });
  return open;
}

function restoreDetails(root, states) {
  detailsIn(root).forEach((d) => {
    const key = d.getAttribute('data-detail');
    if (states.has(key + ':open')) d.open = true;
    else if (states.has(key + ':closed')) d.open = false;
  });
}

/** Applies a view frame's items ({upsert: [{id, html}], remove: [id], order: [id]}) to a container; returns whether anything changed. */
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
