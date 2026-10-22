// Decision 0011 (run W2, WU9): the sign-in modals (#modal-chatgpt-signin, #modal-oauth-signin) on WU5's /api/credentials/signin. A sign-in
// runs on the server: the page starts it, opens the provider's authorize page in a new tab (browser method) or shows the device code, polls
// the flow's status, hands over a pasted code or redirect URL once (the field is cleared at once), and cancels the flow when the modal
// closes before it finished. The page never sees a token: the status carries URLs, a user code and, at the end, the account's metadata.
// Every text is set with textContent; links are set only to http(s) URLs.
import { getJson, postJson, setText, setShown, fillLogo, flashAndReload, busy, ApiError } from './settings-api.js';

const POLL_MS = 2000;

/** Wires every sign-in modal of the page. */
export function initSignIn() {
  document.querySelectorAll('[data-signin-modal]').forEach(setup);
}

function setup(modal) {
  const ctx = { modal, device: modal.dataset.signinModal === 'device', provider: null, name: '', account: null, accountLabel: '', flow: null, timer: 0, countdown: 0, tab: null, succeeded: null, step: '' };
  modal.addEventListener('show.bs.modal', (event) => open(ctx, event.relatedTarget));
  modal.addEventListener('hide.bs.modal', () => close(ctx));
  modal.addEventListener('hidden.bs.modal', () => afterClose(ctx));
  modal.addEventListener('click', (event) => onClick(ctx, event));
  modal.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && event.target.matches('[data-signin-code]')) {
      event.preventDefault();
      submitCode(ctx, event.target);
    } else if (event.key === 'Enter' && event.target.matches('[data-signin-domain]')) {
      event.preventDefault();
      start(ctx, 'deviceCode');
    }
  });
}

function open(ctx, trigger) {
  const data = trigger?.dataset ?? {};
  stopTimers(ctx);
  ctx.flow = null;
  ctx.succeeded = null;
  ctx.tab = null;
  ctx.provider = data.signinProvider || (ctx.device ? 'github-copilot' : 'openai-codex');
  ctx.name = data.signinName || ctx.provider;
  ctx.account = data.signinAccount || null;
  ctx.accountLabel = data.signinAccountLabel || '';
  if (ctx.device) {
    setText(ctx.modal.querySelector('[data-signin-name]'), ctx.name);
    setText(ctx.modal.querySelector('[data-signin-provider-id]'), ctx.provider);
    fillLogo(ctx.modal.querySelector('[data-signin-logo] .provider-logo'), ctx.name, data.signinLogo);
  }
  setShown(ctx.modal.querySelector('[data-signin-again]'), Boolean(ctx.account));
  setText(ctx.modal.querySelector('[data-signin-account-label]'), ctx.accountLabel);
  ctx.modal.querySelectorAll('[data-signin-code], [data-signin-domain]').forEach((input) => { input.value = ''; });
  const first = ctx.device ? (ctx.provider === 'github-copilot' ? 'domain' : null) : 'method';
  if (first) showStep(ctx, first);
  const method = data.signinMethod;
  if (!ctx.device && (method === 'browser' || method === 'deviceCode')) start(ctx, method);
  else if (!first) start(ctx, 'deviceCode');
}

function showStep(ctx, step) {
  ctx.step = step;
  ctx.modal.querySelectorAll('[data-signin-step]').forEach((el) => setShown(el, el.dataset.signinStep === step));
  ctx.modal.querySelectorAll('[data-signin-only]').forEach((el) => setShown(el, el.dataset.signinOnly.split(' ').includes(step)));
  const indicators = [...ctx.modal.querySelectorAll('[data-signin-indicator]')];
  const index = indicators.findIndex((el) => el.dataset.signinIndicator.split(' ').includes(step));
  indicators.forEach((el, i) => {
    el.classList.toggle('active', i === index);
    el.classList.toggle('done', index >= 0 && i < index);
  });
}

function safeUrl(url) {
  return typeof url === 'string' && /^https?:\/\//i.test(url) ? url : null;
}

function setLinks(ctx, selector, url) {
  ctx.modal.querySelectorAll(selector).forEach((link) => {
    const safe = safeUrl(url);
    if (safe) link.href = safe; else link.removeAttribute('href');
    if (link.matches('[data-signin-verification]')) setText(link, safe ?? '');
  });
}

async function start(ctx, method) {
  // Open the tab first, while the click still counts as the user's: the authorize URL is only known once the server answers.
  if (method === 'browser') {
    try {
      ctx.tab = window.open('about:blank', '_blank');
      if (ctx.tab) ctx.tab.opener = null;
    } catch {
      ctx.tab = null;
    }
  }
  // Starting a sign-in cancels the provider's running one on the server; stop polling the old one here.
  stopTimers(ctx);
  ctx.flow = null;
  showStep(ctx, method === 'browser' ? 'browser' : 'device');
  setText(ctx.modal.querySelector('[data-signin-user-code]'), '…');
  setLinks(ctx, '[data-signin-verification], [data-signin-verification-button]', null);
  setShown(ctx.modal.querySelector('[data-signin-expiry]'), false);
  const domainInput = ctx.modal.querySelector('[data-signin-domain]');
  const body = {
    provider: ctx.provider,
    method,
    accountId: ctx.account,
    domain: ctx.device && domainInput ? domainInput.value.trim() || null : null,
  };
  try {
    const status = await busy(ctx.modal.querySelector(`[data-signin-start="${method}"]:not([hidden])`), () => postJson('/api/credentials/signin', body));
    handle(ctx, status);
  } catch (error) {
    closeTab(ctx);
    fail(ctx, error.message);
  }
}

function closeTab(ctx) {
  try { ctx.tab?.close(); } catch { /* already closed */ }
  ctx.tab = null;
}

function handle(ctx, status) {
  if (!status || !status.flowId) return;
  ctx.flow = status;
  if (status.method === 'browser') {
    const url = safeUrl(status.authorizeUrl);
    setLinks(ctx, '[data-signin-authorize]', url);
    setText(ctx.modal.querySelector('[data-signin-callback]'), status.callbackUrl ?? '');
    // The blank tab opened on the click goes to the authorize page once; without it (a blocked pop-up) the "Open sign-in page" link does.
    if (url && ctx.tab && !ctx.tab.closed) {
      try { ctx.tab.location.href = url; } catch { /* the user closed it */ }
      ctx.tab = null;
    }
  } else {
    setText(ctx.modal.querySelector('[data-signin-user-code]'), status.userCode ?? '…');
    setLinks(ctx, '[data-signin-verification], [data-signin-verification-button]', status.verificationUrl);
    startCountdown(ctx, status.expiresAt);
  }
  switch (status.state) {
    case 'succeeded':
      succeed(ctx, status);
      return;
    case 'failed':
    case 'cancelled':
      closeTab(ctx);
      fail(ctx, status.message || (status.state === 'cancelled' ? 'The sign-in was cancelled or timed out.' : 'The sign-in failed.'));
      return;
    case 'working':
      if (ctx.device) {
        setText(ctx.modal.querySelector('[data-signin-progress]'), status.message || 'exchanging for a Copilot token and enabling models');
        showStep(ctx, 'enabling');
      }
      break;
    case 'waiting':
      if (status.method === 'browser') showStep(ctx, status.callbackListening ? 'browser' : 'port-busy');
      else showStep(ctx, 'device');
      break;
    default:
      break;
  }
  schedule(ctx);
}

function schedule(ctx) {
  clearTimeout(ctx.timer);
  const flowId = ctx.flow?.flowId;
  if (!flowId) return;
  ctx.timer = setTimeout(async () => {
    if (ctx.flow?.flowId !== flowId) return;
    try {
      handle(ctx, await getJson(`/api/credentials/signin/${encodeURIComponent(flowId)}`));
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) fail(ctx, error.message);
      else schedule(ctx);
    }
  }, POLL_MS);
}

function startCountdown(ctx, expiresAt) {
  clearInterval(ctx.countdown);
  const element = ctx.modal.querySelector('[data-signin-countdown]');
  const end = expiresAt ? Date.parse(expiresAt) : NaN;
  setShown(ctx.modal.querySelector('[data-signin-expiry]'), !Number.isNaN(end));
  if (Number.isNaN(end)) return;
  const tick = () => {
    const left = Math.max(0, Math.round((end - Date.now()) / 1000));
    setText(element, `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`);
    if (left === 0) clearInterval(ctx.countdown);
  };
  tick();
  ctx.countdown = setInterval(tick, 1000);
}

function stopTimers(ctx) {
  clearTimeout(ctx.timer);
  clearInterval(ctx.countdown);
  ctx.timer = 0;
  ctx.countdown = 0;
}

function succeed(ctx, status) {
  stopTimers(ctx);
  ctx.succeeded = status;
  const account = status.account ?? {};
  const details = account.details ?? {};
  const plan = details.plan ? `ChatGPT ${details.plan.charAt(0).toUpperCase()}${details.plan.slice(1)}` : null;
  setText(ctx.modal.querySelector('[data-signin-success-title]'), ctx.device ? `${ctx.name} connected` : `Signed in to ${plan ?? 'ChatGPT'}`);
  setText(ctx.modal.querySelector('[data-signin-success-text]'), status.reauthenticated
    ? 'The account was signed in again. Its chats keep using it.'
    : status.becameActive ? `New ${ctx.name} chats use this account.` : `Make it active on the provider's page to use it for new chats.`);
  const result = (name) => ctx.modal.querySelector(`[data-signin-result="${name}"]`);
  const row = (name) => ctx.modal.querySelector(`[data-signin-result-row="${name}"]`);
  setText(result('provider'), status.provider);
  setText(result('label'), account.label ?? '');
  const detail = plan ?? (details.host ? details.host : null);
  setText(result('detail-name'), plan ? 'Plan' : 'Host');
  setText(result('detail'), detail ?? '');
  setShown(row('detail'), Boolean(detail));
  setText(result('identity'), account.identity ?? '');
  setShown(row('identity'), Boolean(account.identity));
  const expires = account.expiresAt ? new Date(account.expiresAt) : null;
  setText(result('expires'), expires && !Number.isNaN(expires.getTime()) ? `expires ${expires.toLocaleString()} · refreshes automatically` : '');
  setShown(row('expires'), Boolean(expires));
  showStep(ctx, 'success');
}

function fail(ctx, message) {
  stopTimers(ctx);
  ctx.flow = null;
  setText(ctx.modal.querySelector('[data-signin-error-text]'), message);
  showStep(ctx, 'error');
}

async function cancelFlow(ctx) {
  const flow = ctx.flow;
  stopTimers(ctx);
  ctx.flow = null;
  if (!flow || ctx.succeeded) return;
  try { await postJson(`/api/credentials/signin/${encodeURIComponent(flow.flowId)}/cancel`); } catch { /* already over */ }
}

async function submitCode(ctx, input) {
  if (!ctx.flow || !input) return;
  const code = input.value.trim();
  input.value = '';
  if (!code) return;
  const button = ctx.modal.querySelector(`[data-signin-step="${ctx.step}"] [data-signin-submit-code]`) ?? ctx.modal.querySelector('.modal-footer [data-signin-submit-code]');
  try {
    handle(ctx, await busy(button, () => postJson(`/api/credentials/signin/${encodeURIComponent(ctx.flow.flowId)}/code`, { code })));
  } catch (error) {
    fail(ctx, error.message);
  }
}

function onClick(ctx, event) {
  const startButton = event.target.closest('[data-signin-start]');
  if (startButton && !startButton.disabled) {
    start(ctx, startButton.dataset.signinStart);
    return;
  }
  if (event.target.closest('[data-signin-submit-code]')) {
    submitCode(ctx, ctx.modal.querySelector(`[data-signin-step="${ctx.step}"] [data-signin-code]`));
    return;
  }
  if (event.target.closest('[data-signin-back], [data-signin-retry]')) {
    cancelFlow(ctx);
    closeTab(ctx);
    showStep(ctx, ctx.device ? 'domain' : 'method');
    return;
  }
  if (event.target.closest('[data-signin-done]')) {
    window.bootstrap?.Modal.getInstance(ctx.modal)?.hide();
  }
}

function close(ctx) {
  closeTab(ctx);
  if (ctx.flow && !ctx.succeeded) cancelFlow(ctx);
  stopTimers(ctx);
  ctx.modal.querySelectorAll('[data-signin-code]').forEach((input) => { input.value = ''; });
}

function afterClose(ctx) {
  const status = ctx.succeeded;
  ctx.succeeded = null;
  if (!status) return;
  const label = status.account?.label ?? ctx.name;
  flashAndReload({ title: status.reauthenticated ? `${label} signed in again` : `${label} connected`, body: status.becameActive ? `New ${ctx.name} chats use it.` : '', tone: 'ok' });
}
