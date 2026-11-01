// PiSharp Durable Web: dialogs. New session (device, name, a directory browser over GET /v1/fs/dirs, Provider → Model, thinking), the model
// picker, and one prompt dialog for renaming, forking and archiving.
import { $, deviceApi, escapeHtml, modal, tokens, uuid } from './core.js';

const PROVIDER_NAMES = {
  openai: 'OpenAI', 'openai-codex': 'OpenAI Codex', anthropic: 'Anthropic', google: 'Google', 'google-vertex': 'Google Vertex',
  'azure-openai-responses': 'Azure OpenAI', 'amazon-bedrock': 'Amazon Bedrock', 'github-copilot': 'GitHub Copilot', openrouter: 'OpenRouter',
  xai: 'xAI', mistral: 'Mistral', deepseek: 'DeepSeek', lmstudio: 'LM Studio', ollama: 'Ollama', faux: 'Faux (test)',
};
export const providerName = (id) => PROVIDER_NAMES[id] || (id ? id[0].toUpperCase() + id.slice(1) : '');

export function modelLine(model) {
  if (!model) return '';
  const parts = [tokens(model.contextWindow).toUpperCase() + ' context'];
  if (model.reasoning) parts.push('reasoning');
  if (model.cost && (model.cost.input || model.cost.output)) parts.push(`$${model.cost.input} / $${model.cost.output} per 1M tokens`);
  return parts.join(' · ');
}

/** Fills a Provider select and a Model select from a device's models, choosing `current` when given. */
function bindModels(providerSelect, modelSelect, line, models, current) {
  const providers = [...new Set(models.map((m) => m.provider))];
  providerSelect.innerHTML = providers.map((p) => `<option value="${escapeHtml(p)}">${escapeHtml(providerName(p))}</option>`).join('');
  const fill = () => {
    const list = models.filter((m) => m.provider === providerSelect.value);
    modelSelect.innerHTML = list.map((m) => `<option value="${escapeHtml(m.id)}">${escapeHtml(m.name || m.id)}</option>`).join('');
    if (current && current.provider === providerSelect.value) modelSelect.value = current.modelId;
    describe();
  };
  const describe = () => {
    const model = models.find((m) => m.provider === providerSelect.value && m.id === modelSelect.value);
    if (line) line.textContent = modelLine(model);
  };
  providerSelect.onchange = fill;
  modelSelect.onchange = describe;
  if (current && providers.includes(current.provider)) providerSelect.value = current.provider;
  fill();
}

export class Dialogs {
  constructor(app) {
    this.app = app;
    this.initNewSession();
    this.initModelPicker();
    this.initPrompt();
  }

  // ─── New session ──────────────────────────────────────────────────────
  initNewSession() {
    const form = $('#ns-form');
    if (!form) return;
    this.ns = {
      device: $('#ns-device'), name: $('#ns-name'), cwd: $('#ns-cwd'), provider: $('#ns-provider'), model: $('#ns-model'),
      thinking: $('#ns-thinking'), list: $('[data-dir-list]'), error: $('[data-ns-error]'),
    };
    this.ns.device.addEventListener('change', () => this.loadDevice());
    $('[data-ns-go]').addEventListener('click', () => this.browse(this.ns.cwd.value));
    this.ns.cwd.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); this.browse(this.ns.cwd.value); } });
    this.ns.list.addEventListener('click', (e) => {
      const item = e.target.closest('[data-path]');
      if (item) this.browse(item.getAttribute('data-path'));
    });
    form.addEventListener('submit', (e) => { e.preventDefault(); this.createSession(); });
  }

  newSession(deviceId) {
    const online = this.app.devices.filter((d) => d.status === 'online');
    $('[data-ns-form]').hidden = online.length === 0;
    $('[data-ns-nodevice]').hidden = online.length > 0;
    $('[data-ns-create]').hidden = online.length === 0;
    this.ns.error.hidden = true;
    this.ns.device.innerHTML = online.map((d) => `<option value="${escapeHtml(d.id)}">${escapeHtml(d.id)}${d.info ? ' · ' + escapeHtml(d.info.hostname) : ''}</option>`).join('');
    const preferred = deviceId || this.app.selection.device;
    if (preferred && online.some((d) => d.id === preferred)) this.ns.device.value = preferred;
    this.ns.name.value = '';
    this.requestId = uuid();
    if (online.length) this.loadDevice();
    modal('modal-new-session').show();
    setTimeout(() => this.ns.name.focus(), 300);
  }

  loadDevice() {
    const device = this.app.devices.find((d) => d.id === this.ns.device.value);
    if (!device || !device.info) return;
    $('[data-ns-device-name]').textContent = device.id;
    $('[data-ns-start-dirs]').textContent = (device.info.startDirs || []).join(', ');
    const models = device.info.models || [];
    const current = device.info.defaultModel ? { provider: device.info.defaultProvider || models.find((m) => m.id === device.info.defaultModel)?.provider, modelId: device.info.defaultModel } : null;
    bindModels(this.ns.provider, this.ns.model, $('[data-ns-model-line]'), models, current);
    this.ns.cwd.value = (device.info.startDirs || [])[0] || '';
    this.browse(this.ns.cwd.value);
  }

  async browse(path) {
    const deviceId = this.ns.device.value;
    if (!deviceId) return;
    try {
      const listing = await deviceApi(deviceId).get('/fs/dirs?path=' + encodeURIComponent(path || ''));
      if (listing.path) this.ns.cwd.value = listing.path;
      const rows = [];
      rows.push(`<button type="button" class="dir-item" role="option" data-path=""><i class="bi bi-house"></i>Start directories</button>`);
      if (listing.parent) rows.push(`<button type="button" class="dir-item" role="option" data-path="${escapeHtml(listing.parent)}"><i class="bi bi-arrow-90deg-up"></i>..</button>`);
      for (const dir of listing.dirs) rows.push(`<button type="button" class="dir-item" role="option" data-path="${escapeHtml(dir.path)}"><i class="bi bi-folder2"></i>${escapeHtml(listing.path ? dir.name : dir.path)}</button>`);
      this.ns.list.innerHTML = rows.join('');
      this.ns.error.hidden = true;
    } catch (error) {
      this.ns.error.hidden = false;
      this.ns.error.textContent = error.message;
    }
  }

  async createSession() {
    const deviceId = this.ns.device.value;
    const button = $('[data-ns-create]');
    button.disabled = true;
    try {
      const session = await deviceApi(deviceId).post('/sessions', {
        name: this.ns.name.value.trim() || undefined,
        cwd: this.ns.cwd.value.trim(),
        model: this.ns.model.value ? { provider: this.ns.provider.value, modelId: this.ns.model.value } : undefined,
        thinkingLevel: this.ns.thinking.value,
      }, { idempotencyKey: this.requestId });
      modal('modal-new-session').hide();
      this.app.toast(`Session ${session.name} created on ${deviceId}.`);
      this.app.select({ device: deviceId, session: session.id, agent: 1 });
    } catch (error) {
      this.ns.error.hidden = false;
      this.ns.error.textContent = error.message;
    } finally {
      button.disabled = false;
    }
  }

  // ─── Model picker ─────────────────────────────────────────────────────
  initModelPicker() {
    const form = $('#mp-form');
    if (!form) return;
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const { device, session, agent } = this.app.selection;
      const provider = $('#mp-provider').value;
      const modelId = $('#mp-model').value;
      try {
        const config = await deviceApi(device).patch(`/sessions/${encodeURIComponent(session)}/agents/${agent}/config`, { model: { provider, modelId } });
        modal('modal-model-picker').hide();
        const model = this.app.device()?.info?.models?.find((m) => m.provider === provider && m.id === modelId);
        this.app.toast(`Model saved: ${model?.name || modelId}. Thinking ${config.thinkingLevel}. Used on the next turn.`);
        this.app.reloadPanels(['session', 'tools']);
      } catch (error) {
        this.app.toastError(error);
      }
    });
  }

  modelPicker() {
    const device = this.app.device();
    if (!device?.info) return;
    const tab = $('[data-session-tab]');
    $('[data-mp-agent]').textContent = `${tab?.getAttribute('data-agent-name') || 'this agent'} on ${device.id}`;
    bindModels($('#mp-provider'), $('#mp-model'), $('[data-mp-model-line]'), device.info.models || [],
      tab ? { provider: tab.getAttribute('data-provider'), modelId: tab.getAttribute('data-model') } : null);
    modal('modal-model-picker').show();
  }

  // ─── Prompt (rename, fork, archive) ───────────────────────────────────
  initPrompt() {
    const form = $('#pr-form');
    if (!form) return;
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      if (!this.promptSubmit) return;
      const button = $('[data-pr-submit]');
      button.disabled = true;
      try {
        await this.promptSubmit($('#pr-input').value.trim());
        modal('modal-prompt').hide();
      } catch (error) {
        const alert = $('[data-pr-error]');
        alert.hidden = false;
        alert.textContent = error.message;
      } finally {
        button.disabled = false;
      }
    });
  }

  /** Opens the prompt dialog. Without `label` it is a confirmation. */
  prompt({ title, text, label, value = '', placeholder = '', hint = '', submit, danger = false, onSubmit }) {
    $('[data-pr-title]').textContent = title;
    const p = $('[data-pr-text]');
    p.hidden = !text;
    p.textContent = text || '';
    $('[data-pr-field]').hidden = !label;
    $('[data-pr-label]').textContent = label || '';
    const input = $('#pr-input');
    input.value = value;
    input.placeholder = placeholder;
    $('[data-pr-hint]').textContent = hint;
    $('[data-pr-error]').hidden = true;
    const button = $('[data-pr-submit]');
    button.textContent = submit;
    button.className = 'btn ' + (danger ? 'btn-danger' : 'btn-primary');
    this.promptSubmit = onSubmit;
    modal('modal-prompt').show();
    if (label) setTimeout(() => { input.focus(); input.select(); }, 300);
  }

  path(label, path) {
    $('[data-path-label]').textContent = label;
    $('#full-path').textContent = path;
    modal('modal-path').show();
  }
}
