// Decision 0011 (run W4, R6): what agent compare's modules share: selectors, toasts, the R5 API and R6 fragment prefixes, lazy tooltips,
// the modals' move to the body (the page renders them inside main; they go before the layout's #modal-confirm so a confirm opens on top),
// the top bar's subtitle, and the clocks ([data-ac-elapsed][data-since]: ms since the epoch) that tick between server renders.
import { showToast } from './shell.js';

export const API = '/api/compare/agent';
export const PAGES = '/compare/agent';

export const $ = (selector, root = document) => root.querySelector(selector);
export const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
export const toast = (title, body, tone = 'danger') => showToast({ title, body, tone });

/** "45s", "1m 30s", "1h 05m" (as the server writes them). */
export function duration(ms) {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${String(seconds % 60).padStart(2, '0')}s`;
  return `${Math.floor(seconds / 3600)}h ${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}m`;
}

/** Escapes text for a tooltip that Bootstrap renders as HTML. */
export function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

export function moveModals() {
  const anchor = document.getElementById('modal-confirm');
  for (const modal of [...document.querySelectorAll('[data-ac-modal]'), document.getElementById('modal-model-picker')]) {
    if (!modal || modal.parentElement === document.body) continue;
    if (anchor && anchor.parentElement === document.body) anchor.before(modal);
    else document.body.append(modal);
  }
}

export function setSubtitle(text) {
  const subtitle = document.querySelector('[data-compare-subtitle]');
  if (subtitle && text) subtitle.textContent = text;
}

export function wireTooltips() {
  const show = (event) => {
    const target = event.target.closest?.('[data-bs-toggle="tooltip"]');
    if (!target || !window.bootstrap) return;
    window.bootstrap.Tooltip.getOrCreateInstance(target).show();
  };
  document.addEventListener('mouseover', show);
  document.addEventListener('focusin', show);
}

export function disposeTooltips(element) {
  if (!window.bootstrap || !element) return;
  const items = element.matches?.('[data-bs-toggle="tooltip"]') ? [element] : [];
  items.push(...element.querySelectorAll('[data-bs-toggle="tooltip"]'));
  items.forEach((item) => window.bootstrap.Tooltip.getInstance(item)?.dispose());
}

/** Replaces a tooltip's (HTML) text; the next hover shows it. */
export function setTooltip(element, html) {
  if (!element) return;
  window.bootstrap?.Tooltip.getInstance(element)?.dispose();
  element.setAttribute('title', html);
}

export function tick() {
  const now = Date.now();
  for (const clock of document.querySelectorAll('[data-ac-elapsed][data-since]')) {
    const since = Number(clock.dataset.since);
    if (Number.isFinite(since) && since > 0) clock.textContent = duration(now - since);
  }
}

export function startTicking() {
  window.setInterval(tick, 1000);
}

/** Runs `action` with `button` disabled; resolves to its result. */
export async function busy(button, action) {
  const was = button?.disabled ?? false;
  if (button) button.disabled = true;
  try {
    return await action();
  } finally {
    if (button && button.isConnected) button.disabled = was;
  }
}

/** Hides the modal around `element`, moving focus out of it first (a focused button inside a hidden modal trips aria-hidden). */
export function hideModal(element) {
  const shell = element?.closest?.('.modal') ?? element;
  if (!shell || !window.bootstrap) return;
  if (shell.contains(document.activeElement)) document.activeElement.blur();
  window.bootstrap.Modal.getInstance(shell)?.hide();
}

export function modal(id) {
  const element = document.getElementById(id);
  return element && window.bootstrap ? { element, instance: window.bootstrap.Modal.getOrCreateInstance(element) } : null;
}

/** A fresh request id for a create (R5 answers a repeated create with the run it made). */
export function requestId() {
  if (window.crypto?.randomUUID) return window.crypto.randomUUID();
  return `ac-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}
