// PiSharp Durable Web: the network tree. The hub renders it (network frames carry its HTML); this module swaps it in, keeps rows the user
// collapsed collapsed, marks the selection, and handles the keyboard (arrows move, right and left expand and collapse, Enter opens) and
// the 220-560 px resizer.
import { $, $$, element, savePanels } from './core.js';

const COLLAPSED_KEY = 'pisharp-durable-collapsed';

function collapsedSet() {
  try { return new Set(JSON.parse(sessionStorage.getItem(COLLAPSED_KEY) || '[]')); } catch { return new Set(); }
}

function saveCollapsed(set) {
  try { sessionStorage.setItem(COLLAPSED_KEY, JSON.stringify([...set])); } catch { /* storage unavailable */ }
}

const rows = () => $$('.tree-row');
const visible = (row) => !row.hidden && !row.classList.contains('tree-hidden');

export class Tree {
  constructor({ onSelect }) {
    this.onSelect = onSelect;
    this.selectedKey = null;
    this.collapsed = collapsedSet();
    document.addEventListener('click', (e) => this.click(e));
    document.addEventListener('keydown', (e) => this.keydown(e));
    this.initResizer();
    this.applyRail();
    this.sync();
  }

  /** Swaps in a network frame's tree HTML. */
  render(html) {
    const template = document.createElement('template');
    template.innerHTML = html;
    const body = template.content.querySelector('#treeBody');
    const rail = template.content.querySelector('#railTemplate');
    const focused = document.activeElement?.closest?.('.tree-row')?.getAttribute('data-key');
    if (body) $('#treeBody')?.replaceWith(body);
    if (rail) $('#railTemplate')?.replaceWith(rail);
    this.applyRail();
    this.sync();
    if (focused) {
      const row = $(`.tree-row[data-key="${CSS.escape(focused)}"]`);
      if (row) this.focus(row, false);
    }
  }

  applyRail() {
    const slot = $('#railDevicesSlot');
    const template = $('#railTemplate');
    if (slot && template) slot.replaceChildren(template.content.cloneNode(true));
  }

  select(key) {
    this.selectedKey = key;
    if (key) {
      // Open the path to the selection.
      const row = $(`.tree-row[data-key="${CSS.escape(key)}"]`);
      for (const ancestor of (row?.getAttribute('data-under') || '').split(/\s+/).filter(Boolean)) this.collapsed.delete(ancestor);
      saveCollapsed(this.collapsed);
    }
    this.sync();
  }

  sync() {
    const all = rows();
    for (const row of all) {
      const key = row.getAttribute('data-key');
      const kids = all.filter((r) => r.getAttribute('data-parent') === key);
      row.querySelector('.chev')?.classList.toggle('leaf', kids.length === 0);
      if (!kids.length) row.removeAttribute('aria-expanded');
      else row.setAttribute('aria-expanded', this.collapsed.has(key) ? 'false' : 'true');
      const selected = key === this.selectedKey;
      row.classList.toggle('sel', selected);
      if (selected) row.setAttribute('aria-selected', 'true'); else row.removeAttribute('aria-selected');
    }
    for (const row of all) {
      const under = (row.getAttribute('data-under') || '').split(/\s+/).filter(Boolean);
      row.classList.toggle('tree-hidden', under.some((key) => this.collapsed.has(key)));
    }
  }

  toggle(row, open) {
    if (!row.hasAttribute('aria-expanded')) return false;
    const key = row.getAttribute('data-key');
    const isOpen = !this.collapsed.has(key);
    const want = open === undefined ? !isOpen : open;
    if (want === isOpen) return false;
    if (want) this.collapsed.delete(key); else this.collapsed.add(key);
    saveCollapsed(this.collapsed);
    this.sync();
    return true;
  }

  focus(row, scroll = true) {
    rows().forEach((r) => { r.classList.remove('focus'); r.tabIndex = -1; });
    row.classList.add('focus');
    row.tabIndex = 0;
    row.focus({ preventScroll: !scroll });
  }

  open(row) {
    const device = row.getAttribute('data-device');
    const session = row.getAttribute('data-session');
    const agent = row.getAttribute('data-agent');
    if (row.classList.contains('agent')) this.onSelect({ device, session, agent: Number(agent) });
    else if (row.classList.contains('session')) this.onSelect({ device, session, agent: 1 });
    else this.onSelect({ device, session: null, agent: null });
  }

  click(e) {
    if (e.target.closest('.row-actions')) return;
    const row = e.target.closest('.tree-row');
    if (!row) return;
    this.focus(row, false);
    if (e.target.closest('.chev') && row.hasAttribute('aria-expanded')) { this.toggle(row); return; }
    if (row.classList.contains('agent')) this.open(row);
    else if (row.classList.contains('session')) {
      if (row.getAttribute('aria-expanded') === 'false') this.toggle(row, true);
      this.open(row);
    } else {
      this.toggle(row);
      if (row.getAttribute('data-status') !== 'online') this.open(row);
    }
    if (window.innerWidth < 768 && row.classList.contains('agent')) window.bootstrap?.Offcanvas.getInstance($('#netTree'))?.hide();
  }

  keydown(e) {
    const row = e.target.closest?.('.tree-row');
    const list = e.target.closest?.('.tree-list');
    if (!row && !list) return;
    const shown = rows().filter(visible);
    if (!row) {
      if (e.key === 'ArrowDown' && shown[0]) { e.preventDefault(); this.focus(shown[0]); }
      return;
    }
    const i = shown.indexOf(row);
    if (e.key === 'ArrowDown' && shown[i + 1]) { e.preventDefault(); this.focus(shown[i + 1]); }
    else if (e.key === 'ArrowUp' && shown[i - 1]) { e.preventDefault(); this.focus(shown[i - 1]); }
    else if (e.key === 'ArrowRight') {
      e.preventDefault();
      if (!this.toggle(row, true)) {
        const child = shown.find((r) => r.getAttribute('data-parent') === row.getAttribute('data-key'));
        if (child) this.focus(child);
      }
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      if (!this.toggle(row, false)) {
        const parent = $(`.tree-row[data-key="${CSS.escape(row.getAttribute('data-parent') || '')}"]`);
        if (parent) this.focus(parent);
      }
    } else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      this.open(row);
    }
  }

  initResizer() {
    const handle = $('.tree-resizer');
    if (!handle) return;
    const root = document.documentElement;
    const width = () => parseInt(getComputedStyle(root).getPropertyValue('--dn-tree-w'), 10) || 280;
    const set = (w) => root.style.setProperty('--dn-tree-w', Math.max(220, Math.min(560, w)) + 'px');
    handle.addEventListener('pointerdown', (e) => {
      const startX = e.clientX;
      const startW = width();
      handle.setPointerCapture(e.pointerId);
      handle.classList.add('dragging');
      const move = (ev) => set(startW + ev.clientX - startX);
      const up = () => {
        handle.classList.remove('dragging');
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', up);
        savePanels();
      };
      handle.addEventListener('pointermove', move);
      handle.addEventListener('pointerup', up);
    });
    handle.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowLeft') { e.preventDefault(); set(width() - 16); savePanels(); }
      if (e.key === 'ArrowRight') { e.preventDefault(); set(width() + 16); savePanels(); }
    });
  }
}

export { element };
