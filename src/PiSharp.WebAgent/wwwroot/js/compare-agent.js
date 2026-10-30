// Decision 0011 (run W4, R6): Compare's agent mode (GET /compare?mode=agent, _AgentMode). It moves the mode's modals to the body (before
// the layout's #modal-confirm, so a confirm opens on top of them), sets the top bar's subtitle from the page's data-subtitle, shows
// tooltips when first hovered, and hands the page to its module: [data-agent-compare="setup"] (a new run's setup) or "run" (a run, live).
// The Request · Agent switch is compare.js's (it navigates on [data-ac-mode]); nothing here reads that attribute.
import { moveModals, setSubtitle, wireTooltips } from './compare-agent-common.js';
import { initAgentSetup } from './compare-agent-setup.js';
import { initAgentRun } from './compare-agent-run.js';

const root = document.querySelector('[data-agent-compare]');
if (root) {
  moveModals();
  setSubtitle(root.dataset.subtitle);
  wireTooltips();
  if (root.dataset.agentCompare === 'setup') initAgentSetup(root);
  else if (root.dataset.agentCompare === 'run') initAgentRun(root);
}
