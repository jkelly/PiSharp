// Decision 0011 (run W4, R1): the Playground page. Run (Ctrl+Enter) sends the request to POST /api/runs, the run engine's endpoint (Spend
// Guard, history, events): the page never calls a provider. A refused option marks its row; a run the Spend Guard would ask about opens
// modal-spend-confirm and is sent again only with the user's choice. Then the page follows the run's events into the output pane and, when
// the run ends, swaps in the server's _RunOutput (Markdown rendered by the sanitizing MarkdownRenderer). The URL becomes ?run={id}, so a
// reload (or another tab) reopens the run, live while it runs. Stop (Esc) cancels the run. The estimate by Run follows the draft
// (POST /api/playground/estimate, debounced), and so do the token counts. A tool call is answered with a toolResult the user writes, or
// with the tool's mock response; nothing runs here. Tooltips start lazily on hover or focus (no global init).
import { showToast, confirmAction, providerModelValue } from './shell.js';
import { showSpendConfirm } from './spend-confirm.js';
import { postRequest, getRequest, getFragment, copyText, RequestError } from './request-api.js';
import { collectSpec, chosenModel } from './request-spec.js';
import { initConfig, applyModel, markRefused, resetOptions, capabilities } from './request-config.js';
import { initMessages, setMessages, setTokens, setImagesAllowed, appendMessage } from './request-messages.js';
import { initTools, toolMocks } from './request-tools.js';
import { initEventControls, applyEventView, streamRun } from './request-output.js';
import { initViewCode } from './request-code.js';

const host = document.querySelector('[data-playground]');
const config = host?.querySelector('[data-request-config]');
const list = host?.querySelector('[data-message-list]');
const toolbar = document.querySelector('[data-playground-toolbar]');
let stream = null;
let running = false;
let estimateTimer = 0;
let estimateSeq = 0;
let modelSeq = 0;

function output() {
  return host.querySelector('[data-run-output]');
}

function toast(title, body, tone = 'danger') {
  showToast({ title, body, tone });
}

// Bootstrap tooltips start on first hover or focus (chat.js does the same); the estimate chip is replaced as the draft changes.
function wireTooltips() {
  const show = (event) => {
    const target = event.target.closest?.('[data-bs-toggle="tooltip"]');
    if (!target || !window.bootstrap) return;
    window.bootstrap.Tooltip.getOrCreateInstance(target).show();
  };
  document.addEventListener('mouseover', show);
  document.addEventListener('focusin', show);
}

function disposeTooltips(element) {
  if (!window.bootstrap) return;
  element.querySelectorAll('[data-bs-toggle="tooltip"]').forEach((item) => window.bootstrap.Tooltip.getInstance(item)?.dispose());
}

// ---- toolbar state -------------------------------------------------------------------------------------------------------------

function setToolbar(state, statusLine) {
  toolbar.dataset.state = state;
  const streaming = state === 'streaming';
  toolbar.querySelector('[data-run]').hidden = streaming;
  toolbar.querySelector('[data-running]').hidden = !streaming;
  const stop = toolbar.querySelector('[data-stop]');
  stop.disabled = !streaming;
  stop.classList.toggle('btn-outline-danger', streaming);
  stop.classList.toggle('btn-outline-secondary', !streaming);
  const status = toolbar.querySelector('[data-run-status]');
  status.classList.remove('text-ps-blue', 'text-ps-danger', 'text-ps-faint');
  status.classList.add(streaming ? 'text-ps-blue' : state === 'error' ? 'text-ps-danger' : 'text-ps-faint');
  status.replaceChildren();
  if (streaming) {
    const dot = document.createElement('span');
    dot.className = 'status-dot live me-1';
    status.append(dot);
  }
  const text = document.createElement('span');
  text.dataset.runStatusText = '';
  text.textContent = statusLine ?? (streaming ? 'streaming' : '');
  status.append(text);
  const runId = output()?.dataset.runId;
  const inspector = toolbar.querySelector('[data-open-inspector]');
  if (inspector) {
    inspector.classList.toggle('disabled', !runId);
    if (runId) {
      inspector.href = '/runs/' + encodeURIComponent(runId);
      inspector.removeAttribute('aria-disabled');
    } else {
      inspector.href = '#';
      inspector.setAttribute('aria-disabled', 'true');
    }
  }
}

// ---- output pane ---------------------------------------------------------------------------------------------------------------

function replaceOutput(fresh) {
  const current = output();
  if (!fresh || !current) return;
  const activeTab = current.querySelector('.nav-link.active')?.id?.replace(/^.*-tab-/, '');
  const filter = current.querySelector('[data-event-filter][aria-pressed="true"]')?.dataset.eventFilter;
  const collapsed = current.querySelector('[data-events-collapse]')?.checked;
  disposeTooltips(current);
  current.replaceWith(fresh);
  if (activeTab && activeTab !== 'rendered' && window.bootstrap) {
    const tab = fresh.querySelector(`[id$="-tab-${activeTab}"]`);
    if (tab) window.bootstrap.Tab.getOrCreateInstance(tab).show();
  }
  if (filter) fresh.querySelector(`[data-event-filter="${filter}"]`)?.click();
  const collapse = fresh.querySelector('[data-events-collapse]');
  if (collapse && collapsed === false) collapse.checked = false;
  applyEventView(fresh);
  setToolbar(fresh.dataset.state, fresh.dataset.statusLine);
}

async function loadOutput(id) {
  const fresh = await getFragment(id ? `/playground/runs/${encodeURIComponent(id)}/output` : '/playground/output');
  replaceOutput(fresh);
  return fresh;
}

function follow(id) {
  stream?.close();
  const section = output();
  running = true;
  setToolbar('streaming', 'streaming');
  stream = streamRun(section, id, {
    after: Number(section.dataset.lastSeq || 0),
    onEnd: async () => {
      stream = null;
      running = false;
      try {
        await loadOutput(id);
      } catch (error) {
        setToolbar('error', 'last run failed');
        toast('Could not show the run', error.message);
      }
      scheduleEstimate();
    },
  });
}

// ---- run -----------------------------------------------------------------------------------------------------------------------

async function run(spend) {
  if (running) return;
  let spec;
  try {
    spec = collectSpec(host);
  } catch (error) {
    toast('Not run', error.message, 'warn');
    return;
  }
  const button = toolbar.querySelector('[data-run]');
  button.disabled = true;
  running = true;
  try {
    const answer = await postRequest('/api/runs', spend ? { ...spec, spend } : spec);
    if (!answer?.posted) {
      running = false;
      button.disabled = false;
      if (!answer?.spend) {
        toast('Not run', answer?.message ?? 'The Spend Guard refused the run.');
        return;
      }
      const choice = await showSpendConfirm(answer.spend);
      if (choice.choice === 'proceed-once') await run({ choice: 'proceed-once' });
      else if (choice.choice === 'cap-output' && choice.maxOutputTokens) await run({ choice: 'cap-output', maxOutputTokens: choice.maxOutputTokens });
      return;
    }
    history.replaceState(null, '', '/playground?run=' + encodeURIComponent(answer.id));
    await loadOutput(answer.id);
    button.disabled = false;
    running = false;
    if (output().dataset.state === 'streaming') follow(answer.id);
    else scheduleEstimate();
  } catch (error) {
    running = false;
    button.disabled = false;
    if (error instanceof RequestError && error.refused.length > 0) markRefused(config, error.refused);
    toast('Not run', error.message);
  }
}

async function stop() {
  const id = output()?.dataset.runId;
  if (!id || !running) return;
  try {
    await postRequest(`/api/runs/${encodeURIComponent(id)}/cancel`, {});
  } catch (error) {
    toast('Could not stop the run', error.message);
  }
}

// ---- estimate ------------------------------------------------------------------------------------------------------------------

function scheduleEstimate() {
  window.clearTimeout(estimateTimer);
  estimateTimer = window.setTimeout(estimate, 400);
}

async function estimate() {
  const seq = ++estimateSeq;
  const estimateHost = toolbar.querySelector('[data-estimate-host]');
  let spec;
  try {
    spec = collectSpec(host);
  } catch {
    return;
  }
  try {
    const answer = await postRequest('/api/playground/estimate', spec);
    if (seq !== estimateSeq) return;
    disposeTooltips(estimateHost);
    const template = document.createElement('template');
    template.innerHTML = answer.html ?? '';
    estimateHost.replaceChildren(template.content);
    setTokens(list, answer.parts, answer.tokens, capabilities(config)?.contextWindow);
    toolbar.querySelector('[data-run]').title = answer.valid ? 'Run (Ctrl+Enter)' : `Run (Ctrl+Enter) · ${answer.message ?? ''}`;
  } catch {
    if (seq === estimateSeq) estimateHost.replaceChildren();
  }
}

// ---- model ---------------------------------------------------------------------------------------------------------------------

function setPicker(root, provider, model) {
  const providerSelect = root?.querySelector('[data-provider-select]');
  if (!providerSelect || ![...providerSelect.options].some((option) => option.value === provider)) return false;
  if (providerSelect.value !== provider) {
    providerSelect.value = provider;
    providerSelect.dispatchEvent(new Event('change', { bubbles: true }));
  }
  const pane = [...root.querySelectorAll('[data-provider]')].find((element) => element.dataset.provider === provider);
  const modelSelect = pane?.querySelector('[data-model-select]');
  if (!modelSelect || ![...modelSelect.options].some((option) => option.value === model)) return false;
  if (modelSelect.value !== model) {
    modelSelect.value = model;
    modelSelect.dispatchEvent(new Event('change', { bubbles: true }));
  }
  return true;
}

async function modelChanged() {
  const { provider, model } = chosenModel(config);
  if (!provider || !model) return;
  const seq = ++modelSeq;
  try {
    const answer = await getRequest(`/api/playground/model?provider=${encodeURIComponent(provider)}&model=${encodeURIComponent(model)}`);
    if (seq !== modelSeq) return;
    applyModel(config, answer);
    const caps = answer.capabilities;
    setImagesAllowed(list, caps?.images === true);
    list.dataset.contextWindow = caps?.contextWindow ?? '';
    toolbar.querySelector('[data-chip-provider]').textContent = provider;
    toolbar.querySelector('[data-chip-name]').textContent = caps?.name ?? model;
    const dot = toolbar.querySelector('[data-chip-dot]');
    dot.classList.toggle('ok', true);
    dot.classList.toggle('warn', false);
    toolbar.querySelector('[data-run]').disabled = false;
    setPicker(document.querySelector('[data-model-picker] [data-provider-model]'), provider, model);
  } catch (error) {
    if (seq === modelSeq) toast('Model not loaded', error.message);
  }
  scheduleEstimate();
}

function wirePicker() {
  const form = document.querySelector('[data-model-picker]');
  form?.addEventListener('submit', (event) => {
    event.preventDefault();
    const choice = providerModelValue(form.querySelector('[data-provider-model]'));
    if (choice?.provider && choice?.model) setPicker(config.querySelector('[data-provider-model]'), choice.provider, choice.model);
    window.bootstrap?.Modal.getOrCreateInstance(form.closest('.modal')).hide();
  });
  config.addEventListener('provider-model-change', (event) => {
    if (event.target.closest('[data-request-config]') === config) modelChanged();
  });
}

// ---- tool calls in the output --------------------------------------------------------------------------------------------------

function outputCalls(section) {
  try { return JSON.parse(section.dataset.toolCalls || '[]'); } catch { return []; }
}

function editors() {
  return [...list.querySelectorAll('[data-msg-list] > [data-msg]')];
}

// The output's assistant message, appended once (a second click finds it by its tool call ids).
function ensureReply(section) {
  const calls = outputCalls(section);
  const text = section.querySelector('[data-output-plain]')?.textContent ?? '';
  const ids = new Set(calls.map((call) => call.id));
  const existing = editors().reverse().find((editor) => editor.dataset.role === 'assistant'
    && [...editor.querySelectorAll('[data-call-id]')].some((id) => ids.has(id.textContent.trim())));
  if (existing) return existing;
  const editor = appendMessage(list, { role: 'assistant', text, toolCalls: calls });
  list.dispatchEvent(new CustomEvent('request-change', { bubbles: true }));
  return editor;
}

function answerCalls(section, callId) {
  const calls = outputCalls(section);
  ensureReply(section);
  const sequential = config.querySelector('[data-exec]:checked')?.value === 'sequential';
  const chosen = sequential ? calls.filter((call) => call.id === callId) : calls;
  const mocks = toolMocks(config);
  const answered = new Set(editors().filter((editor) => editor.dataset.role === 'toolResult').map((editor) => editor.querySelector('[data-result-call]').value));
  let firstEmpty = null;
  for (const call of chosen) {
    if (answered.has(call.id)) continue;
    const mock = mocks.get(call.name);
    const editor = appendMessage(list, { role: 'toolResult', toolCallId: call.id, toolName: call.name, text: mock?.text ?? '', isError: mock?.isError === true });
    if (!mock && !firstEmpty) firstEmpty = editor;
  }
  list.dispatchEvent(new CustomEvent('request-change', { bubbles: true }));
  if (firstEmpty) {
    const textarea = firstEmpty.querySelector('[data-msg-text]');
    textarea.scrollIntoView({ block: 'center' });
    textarea.focus();
    showToast({ title: 'Write the tool result', body: 'The Playground doesn\'t run tools: type the result, then press Run.', tone: 'info' });
    return;
  }
  const pending = editors().some((editor) => editor.dataset.role === 'toolResult' && editor.querySelector('[data-msg-text]').value.trim() === '');
  if (!pending) run();
}

async function showErrorDetails(id) {
  try {
    const fresh = await getFragment(`/playground/runs/${encodeURIComponent(id)}/error`);
    const current = document.getElementById('modal-error-details');
    if (current && fresh) {
      window.bootstrap?.Modal.getInstance(current)?.dispose();
      current.replaceWith(fresh);
    }
    window.bootstrap?.Modal.getOrCreateInstance(fresh ?? current).show();
  } catch (error) {
    toast('Details not available', error.message);
  }
}

function wireOutput() {
  initEventControls(host);
  document.addEventListener('click', async (event) => {
    const button = event.target.closest('button');
    if (!button || button.disabled) return;
    const section = output();
    if (button.matches('[data-copy-output]') && section) {
      const which = button.dataset.copyOutput;
      const text = (which === 'json' ? section.querySelector('[data-output-json]') : section.querySelector('[data-output-plain]'))?.textContent ?? '';
      showToast({ title: (await copyText(text)) ? 'Copied' : 'Copy failed', tone: 'ok' });
    } else if (button.matches('[data-run-retry]')) {
      run();
    } else if (button.matches('[data-error-details]')) {
      showErrorDetails(button.dataset.errorDetails);
    } else if (button.matches('[data-add-tool-result]') && section) {
      answerCalls(section, button.dataset.callId);
    } else if (button.matches('[data-append-reply]') && section) {
      ensureReply(section).scrollIntoView({ block: 'center' });
    } else if (button.matches('[data-copy-error]')) {
      const body = document.querySelector('#modal-error-details [data-error-body]');
      showToast({ title: (await copyText(body?.innerText ?? '')) ? 'Copied' : 'Copy failed', tone: 'ok' });
    }
  });
}

// ---- toolbar -------------------------------------------------------------------------------------------------------------------

function wireToolbar() {
  toolbar.querySelector('[data-run]').addEventListener('click', () => run());
  toolbar.querySelector('[data-stop]').addEventListener('click', () => stop());
  toolbar.addEventListener('click', async (event) => {
    const item = event.target.closest('[data-reset]');
    if (!item) return;
    switch (item.dataset.reset) {
      case 'parameters':
        resetOptions(config);
        break;
      case 'messages': {
        const prompt = list.querySelector('[data-msg-prompt="true"] [data-msg-text]')?.value ?? '';
        setMessages(list, prompt, [{ role: 'user', text: '' }]);
        break;
      }
      case 'output':
        if (running) return;
        history.replaceState(null, '', '/playground');
        await loadOutput(null).catch((error) => toast('Could not clear the output', error.message));
        break;
      case 'everything': {
        const ok = await confirmAction({ title: 'Reset everything?', body: 'The options, messages, tools and output go back to a new request on this model.', ok: 'Reset' });
        if (!ok) return;
        const { provider, model } = chosenModel(config);
        window.location.assign(`/playground?provider=${encodeURIComponent(provider)}&model=${encodeURIComponent(model)}`);
        break;
      }
      default:
        break;
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.defaultPrevented || event.isComposing) return;
    const ctrl = event.ctrlKey || event.metaKey;
    if (ctrl && event.key === 'Enter' && !document.querySelector('.modal.show')) {
      event.preventDefault();
      run();
    } else if (event.key === 'Escape' && running && !document.querySelector('.modal.show, .dropdown-menu.show')) {
      stop();
    }
  });
}

function start() {
  if (!host || !config || !list || !toolbar) return;
  wireTooltips();
  initConfig(config);
  initTools(config);
  initMessages(list);
  wirePicker();
  wireOutput();
  wireToolbar();
  initViewCode(() => collectSpec(host), { onCopied: (ok) => showToast({ title: ok ? 'Copied' : 'Copy failed', tone: ok ? 'ok' : 'danger' }) });
  host.addEventListener('request-change', scheduleEstimate);
  host.addEventListener('request-error', (event) => toast(event.detail?.title ?? 'Error', event.detail?.message ?? ''));
  const section = output();
  applyEventView(section);
  if (section.dataset.state === 'streaming' && section.dataset.runId) follow(section.dataset.runId);
  scheduleEstimate();
}

start();
