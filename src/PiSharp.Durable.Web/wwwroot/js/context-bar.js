// PiSharp Durable Web: the context bar (PLAN.md §7, after durable-pi's ContextBar, MIT). Two markers on the model's window: where a summary
// starts in the background, and where the next request waits for it. Dragging or the arrow keys (1024 tokens; Shift for 8192) move a
// marker; the change is saved for the session as PATCH /v1/sessions/{s}/compaction {reserveTokens, backgroundTokens}.
import { tokens, toast, toastError } from './core.js';

export function initContextBar(bar, { save }) {
  if (!bar) return { setUsed() {} };
  const windowSize = Number(bar.getAttribute('data-window'));
  const markers = { auto: bar.querySelector('.ctx-marker.auto'), wait: bar.querySelector('.ctx-marker.wait') };
  const labels = { auto: bar.querySelector('.ctx-label.above'), wait: bar.querySelector('.ctx-label.below') };
  const reserve = bar.querySelector('.ctx-reserve');
  const fill = bar.querySelector('[data-ctx-fill]');
  const hint = document.querySelector(bar.getAttribute('data-hint'));
  const v = { auto: Number(markers.auto.getAttribute('aria-valuenow')), wait: Number(markers.wait.getAttribute('aria-valuenow')) };
  let timer = null;

  const clamp = (which, value) => {
    value = Math.round(value / 512) * 512;
    if (which === 'auto') return Math.max(4096, Math.min(v.wait - 1024, value));
    return Math.max(v.auto + 1024, Math.min(windowSize - 1024, value));
  };
  const render = () => {
    for (const which of ['auto', 'wait']) {
      const pct = v[which] / windowSize * 100;
      markers[which].style.left = pct + '%';
      markers[which].setAttribute('aria-valuenow', String(v[which]));
      markers[which].setAttribute('aria-valuetext', tokens(v[which]) + ' tokens');
      labels[which].style.left = pct + '%';
      labels[which].classList.toggle('edge-l', pct < 25);
      labels[which].classList.toggle('edge-r', pct > 75);
      labels[which].textContent = (which === 'auto' ? 'Auto-compact at ' : 'Reserve at ') + tokens(v[which]);
    }
    reserve.style.left = (v.wait / windowSize * 100) + '%';
    if (hint) hint.innerHTML = `Compacts in the background at <strong>${tokens(v.auto)}</strong> and waits at <strong>${tokens(v.wait)}</strong>.`;
  };
  const commit = () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try {
        await save({ reserveTokens: windowSize - v.wait, backgroundTokens: v.wait - v.auto });
        toast('Compaction settings saved for this session.');
      } catch (error) {
        toastError(error);
      }
    }, 350);
  };
  for (const which of ['auto', 'wait']) {
    const marker = markers[which];
    marker.addEventListener('keydown', (e) => {
      const step = e.shiftKey ? 8192 : 1024;
      if (e.key === 'ArrowRight' || e.key === 'ArrowUp') { e.preventDefault(); v[which] = clamp(which, v[which] + step); render(); commit(); }
      if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') { e.preventDefault(); v[which] = clamp(which, v[which] - step); render(); commit(); }
    });
    marker.addEventListener('pointerdown', (e) => {
      marker.setPointerCapture(e.pointerId);
      const rect = bar.getBoundingClientRect();
      const move = (ev) => { v[which] = clamp(which, (ev.clientX - rect.left) / rect.width * windowSize); render(); };
      const up = () => { marker.removeEventListener('pointermove', move); marker.removeEventListener('pointerup', up); commit(); };
      marker.addEventListener('pointermove', move);
      marker.addEventListener('pointerup', up);
    });
  }
  render();
  return {
    setUsed(used) {
      if (fill) {
        fill.style.width = Math.min(100, used / windowSize * 100) + '%';
        fill.classList.toggle('hot', used >= v.auto);
      }
    },
  };
}
