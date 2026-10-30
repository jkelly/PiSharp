// Decision 0011 (run W4, R2): the Compare page's entry module. The Request · Agent switch (_CompareModeSwitch, in both modes) opens a new
// comparison in the chosen mode: /compare (request) or /compare?mode=agent (R6's agent mode); it is disabled while a column streams.
// Request mode (_ComparePrompt, the columns and the Metrics tab) is compare-request.js's. Agent mode loads its own module from _AgentMode.
import { initCompareRequest } from './compare-request.js';

document.addEventListener('change', (event) => {
  const radio = event.target.closest?.('[data-ac-mode]');
  if (!radio || !radio.checked) return;
  window.location.assign(radio.value === 'agent' ? '/compare?mode=agent' : '/compare');
});

const host = document.querySelector('[data-compare]');
if (host) initCompareRequest(host);
