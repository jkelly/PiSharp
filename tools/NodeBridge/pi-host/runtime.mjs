// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (createExtensionRuntime,
// createExtensionAPI, createExtension, loadExtension, loadExtensions), packages/coding-agent/src/core/extensions/runner.ts (createContext,
// createToolContext, createCommandContext, emit, emitToolCall, emitToolResult, emitMessageEnd, emitUserBash, emitContext,
// emitBeforeProviderRequest, emitBeforeProviderHeaders, emitBeforeAgentStart, emitResourcesDiscover, emitInput, emitBoundary,
// emitCacheWarmingDecision, emitProjectTrustEvent, withUIPrompt), packages/coding-agent/src/core/event-bus.ts and
// packages/coding-agent/src/core/exec.ts.
//
// The extensions of one PiSharp session run here, in the user's Node. Registrations are reported to the PiSharp host, which owns the
// agent, the session and the UI; the ExtensionAPI's actions and the context's getters call the host (getters synchronously, as the
// upstream API is synchronous). The per-event reductions below are upstream's runner loops over one extension's handlers; the host
// folds the per-extension results across extensions in load order the same way.
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const STALE_MESSAGE = 'This extension ctx is stale after session replacement or reload. Do not use a captured pi or command ctx after ctx.newSession(), ctx.fork(), ctx.switchSession(), or ctx.reload(). For newSession, fork, and switchSession, move post-replacement work into withSession and use the ctx passed to withSession. For reload, do not use the old ctx after await ctx.reload().';

const message = (error) => (error instanceof Error ? error.message : String(error));
const stack = (error) => (error instanceof Error ? error.stack : undefined);

/** Source createEventBus: handlers run asynchronously and their errors are logged, never thrown at the emitter. */
export function createEventBus(onEmit) {
  const emitter = new EventEmitter();
  emitter.setMaxListeners(0);
  return {
    emit: (channel, data) => { emitter.emit(channel, data); onEmit?.(channel, data); },
    deliver: (channel, data) => emitter.emit(channel, data),
    on: (channel, handler) => {
      const safe = async (data) => { try { await handler(data); } catch (error) { console.error(`Event handler error (${channel}):`, error); } };
      emitter.on(channel, safe);
      return () => emitter.off(channel, safe);
    },
    clear: () => emitter.removeAllListeners(),
  };
}

/** Source execCommand: spawn without a shell, collect stdout/stderr, SIGTERM then SIGKILL on abort or timeout. */
export function execCommand(command, args, cwd, options) {
  return new Promise((resolve) => {
    let proc;
    try { proc = spawn(command, args, { cwd, shell: false, stdio: ['ignore', 'pipe', 'pipe'] }); }
    catch { resolve({ stdout: '', stderr: '', code: 1, killed: false }); return; }
    let stdout = '', stderr = '', killed = false, timeoutId;
    const killProcess = () => {
      if (killed) return; killed = true; proc.kill('SIGTERM');
      setTimeout(() => { if (!proc.killed) proc.kill('SIGKILL'); }, 5000).unref();
    };
    if (options?.signal) { if (options.signal.aborted) killProcess(); else options.signal.addEventListener('abort', killProcess, { once: true }); }
    if (options?.timeout && options.timeout > 0) timeoutId = setTimeout(killProcess, options.timeout);
    proc.stdout?.on('data', (data) => { stdout += data.toString(); });
    proc.stderr?.on('data', (data) => { stderr += data.toString(); });
    const done = (code) => {
      if (timeoutId) clearTimeout(timeoutId);
      options?.signal?.removeEventListener('abort', killProcess);
      resolve({ stdout, stderr, code: code ?? 0, killed });
    };
    proc.on('error', () => done(1));
    proc.on('close', (code) => done(code));
  });
}

function isUserBashEventResult(value) {
  if (typeof value !== 'object' || value === null) return false;
  const hasOperations = value.operations !== undefined, hasResult = value.result !== undefined;
  if (hasOperations === hasResult) return false;
  if (hasOperations) return typeof value.operations === 'object' && value.operations !== null && typeof value.operations.exec === 'function';
  const result = value.result;
  if (typeof result !== 'object' || result === null) return false;
  return typeof result.output === 'string' && 'exitCode' in result && (result.exitCode === undefined || typeof result.exitCode === 'number') &&
    typeof result.cancelled === 'boolean' && typeof result.truncated === 'boolean' && (result.fullOutputPath === undefined || typeof result.fullOutputPath === 'string');
}

const sameMessages = (left, right) => left.length === right.length && left.every((m, i) => m === right[i]);
function restoreSystemMessages(current, visible, returned) {
  if (sameMessages(returned, visible)) return current;
  const head = current[0]?.role === 'system' ? current[0] : undefined;
  return head ? [head, ...returned] : returned;
}

/** A JSON-safe copy of a registration's descriptive fields (functions are reported as flags). */
function describeTool(tool) {
  return {
    name: tool.name, label: tool.label, description: tool.description, promptSnippet: tool.promptSnippet, promptGuidelines: tool.promptGuidelines,
    parameters: tool.parameters, exposure: tool.exposure, namespace: tool.namespace, annotations: tool.annotations, defaultActive: tool.defaultActive,
    executionMode: tool.executionMode, renderShell: tool.renderShell, constrainedSampling: tool.constrainedSampling || undefined, outputSchema: tool.outputSchema,
    hasRenderCall: typeof tool.renderCall === 'function', hasRenderResult: typeof tool.renderResult === 'function',
    hasPrepareArguments: typeof tool.prepareArguments === 'function', hasPrepareLoadout: typeof tool.prepareLoadout === 'function',
  };
}

export function describeExtension(ext) {
  return {
    index: ext.index, path: ext.path, resolvedPath: ext.resolvedPath,
    events: [...ext.handlers.entries()].filter(([, list]) => list.length > 0).map(([event]) => event),
    handlerCounts: Object.fromEntries([...ext.handlers.entries()].filter(([, list]) => list.length > 0).map(([event, list]) => [event, list.length])),
    tools: [...ext.tools.values()].map(t => describeTool(t.definition)),
    commands: [...ext.commands.values()].map(c => ({ name: c.name, description: c.description, hasCompletions: typeof c.getArgumentCompletions === 'function' })),
    flags: [...ext.flags.values()].map(f => ({ name: f.name, description: f.description, type: f.type, default: f.default })),
    shortcuts: [...ext.shortcuts.values()].map(s => ({ shortcut: s.shortcut, description: s.description })),
    messageRenderers: [...ext.messageRenderers.keys()], entryRenderers: [...(ext.entryRenderers?.keys() ?? [])],
    toolRenderers: ext.toolRenderers?.length ?? 0, markdownTransformer: typeof ext.markdownTransformer === 'function',
  };
}

/**
 * The runtime of one Node host: every extension of the session, the shared flag values, event bus and pending registrations.
 * `bridge` is the host channel: `sync(method, params)`, `call(method, params)` (Promise) and `notify(method, params)`.
 */
export class ExtensionRuntime {
  constructor(bridge, options) {
    this.bridge = bridge;
    this.options = options;
    /** Reload generation: the compatibility loader imports a reloaded module afresh (jiti's moduleCache:false does in Pi mode). */
    this.generation = options.generation ?? 0;
    this.cwd = options.cwd;
    this.mode = options.mode ?? 'print';
    this.hasUI = options.hasUI === true;
    this.flagValues = new Map(Object.entries(options.flagValues ?? {}));
    this.extensions = [];
    this.staleMessage = undefined;
    this.bound = false;
    this.pendingProviders = []; this.pendingVirtualModels = []; this.pendingMcp = [];
    this.mcpServers = new Map();
    this.virtualModels = new Map();
    this.providers = new Map();
    this.eventBus = createEventBus((channel, data) => {
      try { this.bridge.notify('events.emit', { channel, data: JSON.parse(JSON.stringify(data ?? null)) }); } catch { /* not JSON */ }
    });
    this.uiPromptDepth = 0; this.activeUIPrompt = undefined;
    this.components = new Map(); this.nextComponent = 0;
    this.themeFactory = options.themeFactory;
    this.importExtension = options.importExtension;
    this.createEventStream = options.createEventStream;
    this.themeName = options.theme ?? 'dark';
    this.rendererState = new Map();
    this.callbacks = new Map();
  }

  assertActive() { if (this.staleMessage) throw new Error(this.staleMessage); }
  invalidate(text = STALE_MESSAGE) { if (!this.staleMessage) { this.staleMessage = text; this.eventBus.clear(); } }

  // ---------------------------------------------------------------------------------------------------------------- loading

  createExtension(extensionPath, resolvedPath) {
    return {
      index: this.extensions.length, path: extensionPath, resolvedPath, handlers: new Map(), tools: new Map(), messageRenderers: new Map(),
      entryRenderers: new Map(), commands: new Map(), flags: new Map(), shortcuts: new Map(), toolRenderers: [], markdownTransformer: undefined,
    };
  }

  /** Source createExtensionAPI: registrations write to the extension; changes to shared state wait for a successful factory. */
  createExtensionAPI(extension) {
    const runtime = this, bridge = this.bridge, cwd = this.cwd, eventBus = this.eventBus;
    const pendingFlagValues = new Map(); const pendingRuntimeChanges = []; const loadingUnsubscribers = [];
    let state = 'loading';
    const assertActive = () => {
      if (state === 'failed') throw new Error(`Extension "${extension.path}" failed to load and its API is no longer active.`);
      runtime.assertActive();
    };
    const applyRuntimeChange = (change) => { if (state === 'loading') pendingRuntimeChanges.push(change); else change(); };
    const changed = (kind, detail) => { if (state === 'active') bridge.notify('registrations.changed', { ext: extension.index, kind, ...detail, extension: describeExtension(extension) }); };
    const api = {
      on(event, handler) {
        assertActive();
        const registered = (...args) => handler(...args);
        const list = extension.handlers.get(event) ?? [];
        const first = list.length === 0;
        list.push(registered); extension.handlers.set(event, list);
        if (first) changed('event', { event });
        return () => {
          const handlers = extension.handlers.get(event); if (!handlers) return;
          const index = handlers.indexOf(registered); if (index === -1) return;
          handlers.splice(index, 1); if (handlers.length === 0) extension.handlers.delete(event);
        };
      },
      registerTool(tool) {
        assertActive();
        if (typeof tool.parameters !== 'object' || tool.parameters === null || Array.isArray(tool.parameters))
          throw new Error(`Tool "${tool.name}" registered by extension "${extension.path}" must define an object parameter schema.`);
        extension.tools.set(tool.name, { definition: tool });
        changed('tool', { name: tool.name });
      },
      registerCommand(name, options) {
        assertActive();
        if (typeof name !== 'string' || name.length === 0)
          throw new Error(`Command registered by extension "${extension.path}" must have a non-empty string name. Use pi.registerCommand("name", { description, handler }).`);
        if (typeof options?.handler !== 'function') throw new Error(`Command "/${name}" registered by extension "${extension.path}" must define handler().`);
        extension.commands.set(name, { name, ...options });
        changed('command', { name });
      },
      registerShortcut(shortcut, options) { assertActive(); extension.shortcuts.set(shortcut, { shortcut, extensionPath: extension.path, ...options }); changed('shortcut', { shortcut }); },
      registerFlag(name, options) {
        assertActive();
        if (options.default !== undefined && typeof options.default !== options.type)
          throw new Error(`Invalid default for flag "${name}": expected ${options.type}, got ${typeof options.default}`);
        extension.flags.set(name, { name, extensionPath: extension.path, ...options });
        if (options.default !== undefined && !runtime.flagValues.has(name)) {
          if (state === 'loading') { if (!pendingFlagValues.has(name)) pendingFlagValues.set(name, options.default); }
          else runtime.flagValues.set(name, options.default);
        }
        changed('flag', { name });
      },
      registerMessageRenderer(customType, renderer) { assertActive(); extension.messageRenderers.set(customType, renderer); changed('messageRenderer', { customType }); },
      registerMarkdownTransformer(transformer) { assertActive(); extension.markdownTransformer = transformer; changed('markdownTransformer', {}); },
      registerEntryRenderer(customType, renderer) { assertActive(); extension.entryRenderers.set(customType, renderer); changed('entryRenderer', { customType }); },
      registerToolRenderer(resolver) { assertActive(); extension.toolRenderers.push(resolver); changed('toolRenderer', {}); },
      getFlag(name) {
        assertActive();
        if (!extension.flags.has(name)) return undefined;
        return runtime.flagValues.has(name) ? runtime.flagValues.get(name) : pendingFlagValues.get(name);
      },
      sendMessage(msg, options) { assertActive(); runtime.requireBound(); bridge.notify('pi.sendMessage', { message: msg, options }); },
      sendUserMessage(content, options) { assertActive(); runtime.requireBound(); bridge.notify('pi.sendUserMessage', { content, options }); },
      appendEntry(customType, data) { assertActive(); runtime.requireBound(); bridge.notify('pi.appendEntry', { customType, data }); },
      setSessionName(name) { assertActive(); runtime.requireBound(); bridge.notify('pi.setSessionName', { name }); },
      getSessionName() { assertActive(); runtime.requireBound(); return bridge.sync('pi.read', { op: 'getSessionName' }) ?? undefined; },
      setLabel(entryId, label) { assertActive(); runtime.requireBound(); bridge.notify('pi.setLabel', { entryId, label }); },
      exec(command, args, options) { assertActive(); return execCommand(command, args, options?.cwd ?? cwd, options); },
      getActiveTools() { assertActive(); runtime.requireBound(); return bridge.sync('pi.read', { op: 'getActiveTools' }); },
      getAllTools() { assertActive(); runtime.requireBound(); return bridge.sync('pi.read', { op: 'getAllTools' }); },
      getSettings() { assertActive(); runtime.requireBound(); return bridge.sync('pi.read', { op: 'getSettings' }); },
      setActiveTools(toolNames) { assertActive(); runtime.requireBound(); bridge.sync('pi.setActiveTools', { toolNames }); },
      getCommands() { assertActive(); runtime.requireBound(); return bridge.sync('pi.read', { op: 'getCommands' }); },
      setModel(model) {
        assertActive();
        if (!runtime.bound) return Promise.reject(new Error('Extension runtime not initialized'));
        return bridge.call('pi.setModel', { model });
      },
      getThinkingLevel() { assertActive(); runtime.requireBound(); return bridge.sync('pi.read', { op: 'getThinkingLevel' }); },
      setThinkingLevel(level) { assertActive(); runtime.requireBound(); bridge.sync('pi.setThinkingLevel', { level }); },
      registerProvider(providerOrName, config) {
        assertActive();
        if (typeof providerOrName === 'string') {
          if (!config) throw new Error('Provider config is required when registering by name');
          applyRuntimeChange(() => runtime.registerProvider(providerOrName, config, extension));
          return;
        }
        applyRuntimeChange(() => runtime.registerNativeProvider(providerOrName, extension));
      },
      unregisterProvider(name) { assertActive(); applyRuntimeChange(() => runtime.unregisterProvider(name, extension)); },
      registerMcpServer(name, config) {
        assertActive();
        const owner = runtime.mcpServers.get(name)?.extensionPath;
        if (owner !== undefined && owner !== extension.path) throw new Error(`MCP server "${name}" is already registered by extension "${owner}"`);
        const namespace = (value) => 'mcp__' + value.replace(/[-_]+/g, '_');
        const clash = [...runtime.mcpServers.values()].find(s => s.name !== name && namespace(s.name) === namespace(name));
        if (clash) throw new Error(`MCP server "${name}" conflicts with registered server "${clash.name}"`);
        const server = { name, config: structuredClone(config), extensionPath: extension.path };
        applyRuntimeChange(() => runtime.registerMcpServer(server));
      },
      unregisterMcpServer(name) { assertActive(); applyRuntimeChange(() => runtime.unregisterMcpServer(name, extension.path)); },
      getMcpServers() { assertActive(); return [...runtime.mcpServers.values()].map(s => structuredClone(s)); },
      registerVirtualModel(model) { assertActive(); applyRuntimeChange(() => runtime.registerVirtualModel(model, extension)); },
      unregisterVirtualModel(provider, id) { assertActive(); applyRuntimeChange(() => runtime.unregisterVirtualModel(provider, id)); },
      events: {
        emit(channel, data) { assertActive(); eventBus.emit(channel, data); },
        on(channel, handler) {
          assertActive();
          const unsubscribe = eventBus.on(channel, handler);
          if (state === 'loading') loadingUnsubscribers.push(unsubscribe);
          return unsubscribe;
        },
      },
    };
    return {
      api,
      commit: () => {
        if (state !== 'loading') return;
        runtime.assertActive();
        for (const [name, value] of pendingFlagValues) if (!runtime.flagValues.has(name)) runtime.flagValues.set(name, value);
        for (const apply of pendingRuntimeChanges) apply();
        state = 'active';
        pendingFlagValues.clear(); pendingRuntimeChanges.length = 0; loadingUnsubscribers.length = 0;
      },
      discard: () => {
        if (state !== 'loading') return;
        state = 'failed';
        for (const unsubscribe of loadingUnsubscribers) unsubscribe();
        pendingFlagValues.clear(); pendingRuntimeChanges.length = 0; loadingUnsubscribers.length = 0;
      },
    };
  }

  requireBound() { if (!this.bound) throw new Error('Extension runtime not initialized. Action methods cannot be called during extension loading.'); }

  /** Source loadExtension: import (jiti semantics, `{ default: true }`), then await the factory with a fresh API. */
  async load(extensionPath) {
    const resolvedPath = path.resolve(this.cwd, extensionPath);
    try {
      let factory;
      if (this.importExtension) factory = await this.importExtension(resolvedPath);
      else {
        const url = pathToFileURL(resolvedPath).href + (this.generation > 0 ? `?pisharp-reload=${this.generation}` : '');
        const imported = await import(url);
        factory = imported && 'default' in imported ? imported.default : imported;
        if (factory && typeof factory === 'object' && typeof factory.default === 'function') factory = factory.default;
      }
      if (typeof factory !== 'function') return { error: `Extension does not export a valid factory function: ${extensionPath}` };
      const extension = this.createExtension(extensionPath, resolvedPath);
      const load = this.createExtensionAPI(extension);
      try { await factory(load.api); load.commit(); }
      catch (error) { load.discard(); throw error; }
      this.extensions.push(extension);
      return { extension };
    } catch (error) {
      return { error: `Failed to load extension: ${message(error)}` };
    }
  }

  // ---------------------------------------------------------------------------------------------------------------- shared registrations

  registerProvider(name, config, extension) {
    const record = { name, config, extensionPath: extension.path };
    this.providers.set(name, record);
    this.bridge.notify('provider.register', { name, extensionPath: extension.path, config: this.describeProviderConfig(name, config) });
  }
  registerNativeProvider(provider, extension) {
    this.providers.set(provider.id, { name: provider.id, native: provider, extensionPath: extension.path });
    this.bridge.notify('provider.register', { name: provider.id, extensionPath: extension.path, native: JSON.parse(JSON.stringify(provider, (k, v) => typeof v === 'function' ? undefined : v)) });
  }
  unregisterProvider(name) { this.providers.delete(name); this.bridge.notify('provider.unregister', { name }); }
  describeProviderConfig(name, config) {
    const plain = JSON.parse(JSON.stringify(config, (key, value) => typeof value === 'function' ? undefined : value));
    return {
      ...plain,
      hasStreamSimple: typeof config.streamSimple === 'function', hasRefreshModels: typeof config.refreshModels === 'function',
      oauth: config.oauth ? { name: config.oauth.name, isSubscription: config.oauth.isSubscription, usesCallbackServer: config.oauth.usesCallbackServer,
        hasModifyModels: typeof config.oauth.modifyModels === 'function' } : undefined,
      images: config.images ? Object.fromEntries(Object.keys(config.images).map(api => [api, { hasGenerate: true }])) : undefined,
      classifiers: config.classifiers ? Object.fromEntries(Object.keys(config.classifiers).map(api => [api, { hasClassify: true }])) : undefined,
    };
  }
  registerMcpServer(server) { this.mcpServers.set(server.name, server); this.bridge.notify('mcp.register', server); }
  unregisterMcpServer(name, extensionPath) {
    const server = this.mcpServers.get(name);
    if (!server || server.extensionPath !== extensionPath) return;
    this.mcpServers.delete(name); this.bridge.notify('mcp.unregister', { name });
  }
  registerVirtualModel(model, extension) {
    const key = model.provider + '/' + model.id;
    this.virtualModels.set(key, { model, extensionPath: extension.path });
    const plain = JSON.parse(JSON.stringify(model, (k, v) => typeof v === 'function' ? undefined : v));
    this.bridge.notify('virtualModel.register', { definition: plain, extensionPath: extension.path });
  }
  unregisterVirtualModel(provider, id) { this.virtualModels.delete(provider + '/' + id); this.bridge.notify('virtualModel.unregister', { provider, id }); }

  // ---------------------------------------------------------------------------------------------------------------- contexts

  theme() {
    if (!this.themeObject && this.themeFactory) { try { this.themeObject = this.themeFactory(this.themeName); } catch { this.themeObject = undefined; } }
    return this.themeObject;
  }

  /** Source createContext. `ctx` names the host invocation whose context answers the getters. */
  createContext(ctx, extras = {}) {
    const runtime = this, bridge = this.bridge;
    const read = (op, args) => { runtime.assertActive(); return bridge.sync('ctx.read', { ctx, op, args }); };
    const session = (op, args) => { runtime.assertActive(); return bridge.sync('session.read', { ctx, op, args }); };
    const sessionManager = {};
    for (const op of ['getCwd', 'getSessionDir', 'getSessionId', 'getSessionFile', 'getLeafId', 'getLeafEntry', 'getEntry', 'getLabel', 'getBranch',
      'buildContextEntries', 'buildSessionProjection', 'getHeader', 'getEntries', 'getTree', 'getSessionName', 'buildSessionContext', 'getChildren'])
      sessionManager[op] = (...args) => { const value = session(op, args); return value === null ? undefined : value; };
    const modelRegistry = this.createModelRegistry(ctx);
    const context = {
      get ui() { runtime.assertActive(); return runtime.createUIContext(ctx); },
      get mode() { runtime.assertActive(); return runtime.mode; },
      get hasUI() { runtime.assertActive(); return runtime.hasUI; },
      get cwd() { runtime.assertActive(); return runtime.cwd; },
      get sessionManager() { runtime.assertActive(); return sessionManager; },
      get modelRegistry() { runtime.assertActive(); return modelRegistry; },
      get model() { return read('model') ?? undefined; },
      get scopedModels() { return read('scopedModels') ?? []; },
      get thinkingLevel() { return read('thinkingLevel') ?? undefined; },
      isIdle: () => read('isIdle'),
      isProjectTrusted: () => read('isProjectTrusted'),
      get signal() { runtime.assertActive(); return extras.signal; },
      abort: () => { runtime.assertActive(); bridge.notify('ctx.action', { ctx, op: 'abort' }); },
      hasPendingMessages: () => read('hasPendingMessages'),
      shutdown: () => { runtime.assertActive(); bridge.notify('ctx.action', { ctx, op: 'shutdown' }); },
      getContextUsage: () => read('contextUsage') ?? undefined,
      compact: (options) => {
        runtime.assertActive();
        bridge.call('ctx.compact', { ctx, customInstructions: options?.customInstructions }).then(
          (result) => options?.onComplete?.(result), (error) => options?.onError?.(error instanceof Error ? error : new Error(String(error))));
      },
      getSystemPrompt: () => extras.getSystemPrompt ? (runtime.assertActive(), extras.getSystemPrompt()) : read('systemPrompt'),
    };
    Object.defineProperty(context, '__pisharpCtx', { value: ctx, enumerable: false });
    return context;
  }

  createToolContext(ctx, toolCallId, signal) {
    const runtime = this, bridge = this.bridge;
    return Object.defineProperties(this.createContext(ctx, { signal }), {
      tools: { get() { runtime.assertActive(); return bridge.sync('ctx.read', { ctx, op: 'callableTools' }) ?? []; } },
      executeTool: {
        value: async (name, args, options = {}) => {
          runtime.assertActive();
          const progress = typeof options.onUpdate === 'function' ? options.onUpdate : undefined;
          return bridge.call('ctx.executeTool', { ctx, toolCallId, name, args }, { signal: options.signal ?? signal, onProgress: progress });
        },
      },
    });
  }

  createCommandContext(ctx) {
    const runtime = this, bridge = this.bridge;
    const context = Object.defineProperties({}, Object.getOwnPropertyDescriptors(this.createContext(ctx)));
    const withSession = (options, op, params) => {
      runtime.assertActive();
      const callback = options?.withSession; const setup = options?.setup;
      const callbackId = callback ? runtime.registerCallback(async (replacementCtx) => callback(runtime.createReplacedContext(replacementCtx))) : undefined;
      const setupId = setup ? runtime.registerCallback(async (setupCtx) => setup(runtime.createSetupSessionManager(setupCtx))) : undefined;
      return bridge.call('command.session', { ctx, op, ...params, withSession: callbackId, setup: setupId }).finally(() => {
        if (callbackId) runtime.callbacks.delete(callbackId); if (setupId) runtime.callbacks.delete(setupId);
      });
    };
    context.getSystemPromptOptions = () => { runtime.assertActive(); return bridge.sync('ctx.read', { ctx, op: 'systemPromptOptions' }); };
    context.waitForIdle = () => { runtime.assertActive(); return bridge.call('command.waitForIdle', { ctx }); };
    context.newSession = (options) => withSession(options, 'newSession', { parentSession: options?.parentSession });
    context.fork = (entryId, options) => withSession(options, 'fork', { entryId, position: options?.position });
    context.navigateTree = (targetId, options) => { runtime.assertActive(); return bridge.call('command.session', { ctx, op: 'navigateTree', targetId, options }); };
    context.switchSession = (sessionPath, options) => withSession(options, 'switchSession', { sessionPath });
    context.reload = () => { runtime.assertActive(); return bridge.call('command.reload', { ctx }); };
    return context;
  }

  /** Source ReplacedSessionContext: a command context of the replacement session plus awaited sendMessage/sendUserMessage. */
  createReplacedContext(ctx) {
    const context = this.createCommandContext(ctx);
    context.sendMessage = (msg, options) => this.bridge.call('pi.sendMessage', { ctx, message: msg, options, awaited: true });
    context.sendUserMessage = (content, options) => this.bridge.call('pi.sendUserMessage', { ctx, content, options, awaited: true });
    return context;
  }

  createSetupSessionManager(ctx) {
    const bridge = this.bridge; const manager = {};
    for (const op of ['appendMessage', 'appendCustomEntry', 'appendCustomMessageEntry', 'appendSessionInfo', 'appendLabelChange', 'appendModelChange', 'appendThinkingLevelChange'])
      manager[op] = (...args) => bridge.sync('setup.write', { ctx, op, args });
    for (const op of ['getEntries', 'getLeafId', 'getHeader', 'getSessionFile', 'getSessionId', 'getCwd', 'getBranch', 'getEntry'])
      manager[op] = (...args) => { const value = bridge.sync('setup.read', { ctx, op, args }); return value === null ? undefined : value; };
    return manager;
  }

  registerCallback(fn) { const id = `cb${++this.nextComponent}`; this.callbacks.set(id, fn); return id; }

  createModelRegistry(ctx) {
    const bridge = this.bridge;
    const read = (op, args) => { const value = bridge.sync('models.read', { ctx, op, args }); return value === null ? undefined : value; };
    return {
      getAll: () => read('getAll'), getAvailable: () => read('getAvailable'), find: (provider, modelId) => read('find', [provider, modelId]),
      findOfType: (type, provider, id) => read('findOfType', [type, provider, id]), hasConfiguredAuth: (model) => read('hasConfiguredAuth', [model]),
      getModelsOfType: (type, provider) => read('getModelsOfType', [type, provider]), getModelOfType: (type, provider, id) => read('getModelOfType', [type, provider, id]),
      getAvailableOfType: (type, provider) => bridge.call('models.call', { ctx, op: 'getAvailableOfType', args: [type, provider] }),
      getError: () => read('getError'), getProviderDisplayName: (provider) => read('getProviderDisplayName', [provider]),
      getProviderAuthStatus: (provider) => read('getProviderAuthStatus', [provider]), isUsingOAuth: (model) => read('isUsingOAuth', [model]),
      getRegisteredProviderIds: () => read('getRegisteredProviderIds'),
      getApiKeyAndHeaders: (model) => bridge.call('models.call', { ctx, op: 'getApiKeyAndHeaders', args: [model] }),
      getApiKeyForProvider: (provider) => bridge.call('models.call', { ctx, op: 'getApiKeyForProvider', args: [provider] }),
      getProviderAuth: (provider) => bridge.call('models.call', { ctx, op: 'getProviderAuth', args: [provider] }),
      refresh: (options) => bridge.call('models.call', { ctx, op: 'refresh', args: [options ?? {}] }),
      classify: (model, context, options) => bridge.call('models.call', { ctx, op: 'classify', args: [model, context, plainOptions(options)] }, { signal: options?.signal }),
      generateImages: (model, context, options) => bridge.call('models.call', { ctx, op: 'generateImages', args: [model, context, plainOptions(options)] }, { signal: options?.signal }),
      // model-registry.ts stream/streamSimple/complete: request-time authentication and PiSharp's live route for the model.
      stream: (model, context, options) => this.modelStream('stream', model, context, options, ctx),
      streamSimple: (model, context, options) => this.modelStream('streamSimple', model, context, options, ctx),
      complete: (model, context, options) => this.modelStream('complete', model, context, options, ctx).result(),
      completeSimple: (model, context, options) => this.modelStream('completeSimple', model, context, options, ctx).result(),
      registerProvider: (providerOrName, config) => typeof providerOrName === 'string'
        ? this.registerProvider(providerOrName, config, { path: '<modelRegistry>' }) : this.registerNativeProvider(providerOrName, { path: '<modelRegistry>' }),
      unregisterProvider: (name) => this.unregisterProvider(name),
      registerVirtualModel: (definition) => this.registerVirtualModel(definition, { path: '<modelRegistry>' }),
      unregisterVirtualModel: (provider, id) => this.unregisterVirtualModel(provider, id),
    };
  }

  /** An AssistantMessageEventStream (the installed pi-ai's, or the compatibility module's) over the host's answer for the model. */
  modelStream(name, model, context, options, ctx) {
    const stream = this.createEventStream();
    const failed = (error) => ({
      role: 'assistant', content: [], api: model?.api ?? 'unknown', provider: model?.provider ?? 'unknown', model: model?.id ?? 'unknown',
      usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
      stopReason: options?.signal?.aborted ? 'aborted' : 'error', errorMessage: message(error), timestamp: Date.now(),
    });
    const finish = (result) => {
      if (result.stopReason === 'error' || result.stopReason === 'aborted') stream.push({ type: 'error', reason: result.stopReason, error: result });
      else { stream.push({ type: 'start', partial: result }); stream.push({ type: 'done', reason: result.stopReason ?? 'stop', message: result }); }
      stream.end(result);
    };
    this.bridge.call('bridge.call', { name, args: [model, context, plainOptions(options)], ctx }, { signal: options?.signal })
      .then((result) => finish(result && typeof result === 'object' && result.role === 'assistant' ? result : failed(new Error('PiSharp host returned no message'))),
        (error) => finish(failed(error)));
    return stream;
  }

  /** Source ExtensionUIContext over the host UI, with the ui_prompt_start/ui_prompt_end wrapping of withUIPrompt. */
  createUIContext(ctx) {
    const runtime = this, bridge = this.bridge;
    if (!this.hasUI) return this.noOpUIContext();
    const dialog = (kind, title, op, args) => runtime.withUIPrompt(kind, title, () => bridge.call('ui.dialog', { ctx, op, args }, { signal: args?.[args.length - 1]?.signal }));
    const opts = (o) => o ? { timeout: o.timeout } : undefined;
    const ui = {
      select: (title, options, o) => dialog('select', title, 'select', [title, options, opts(o)]).then(v => v ?? undefined),
      confirm: (title, msg, o) => dialog('confirm', title, 'confirm', [title, msg, opts(o)]).then(v => v === true),
      input: (title, placeholder, o) => dialog('input', title, 'input', [title, placeholder, opts(o)]).then(v => v ?? undefined),
      editor: (title, prefill) => dialog('editor', title, 'editor', [title, prefill]).then(v => v ?? undefined),
      notify: (text, type) => bridge.notify('ui.publish', { ctx, op: 'notify', args: [text, type ?? 'info'] }),
      onTerminalInput: (handler) => {
        const id = runtime.registerCallback(async (data) => handler(data));
        bridge.notify('ui.publish', { ctx, op: 'onTerminalInput', args: [id] });
        return () => { runtime.callbacks.delete(id); bridge.notify('ui.publish', { ctx, op: 'offTerminalInput', args: [id] }); };
      },
      setStatus: (key, text) => bridge.notify('ui.publish', { ctx, op: 'setStatus', args: [key, text ?? null] }),
      setWorkingMessage: (text) => bridge.notify('ui.publish', { ctx, op: 'setWorkingMessage', args: [text ?? null] }),
      setWorkingVisible: (visible) => bridge.notify('ui.publish', { ctx, op: 'setWorkingVisible', args: [visible] }),
      setWorkingIndicator: (options) => bridge.notify('ui.publish', { ctx, op: 'setWorkingIndicator', args: [options ?? null] }),
      setHiddenThinkingLabel: (label) => bridge.notify('ui.publish', { ctx, op: 'setHiddenThinkingLabel', args: [label ?? null] }),
      setWidget: (key, content, options) => {
        if (typeof content === 'function') {
          const id = runtime.createComponent((tui, theme) => content(tui, theme));
          bridge.notify('ui.publish', { ctx, op: 'setWidget', args: [key, { component: id }, options ?? null] });
        } else bridge.notify('ui.publish', { ctx, op: 'setWidget', args: [key, content ?? null, options ?? null] });
      },
      setFooter: (factory) => bridge.notify('ui.publish', { ctx, op: 'setFooter', args: [factory ? { component: runtime.createComponent((tui, theme) => factory(tui, theme, runtime.footerData(ctx))) } : null] }),
      setHeader: (factory) => bridge.notify('ui.publish', { ctx, op: 'setHeader', args: [factory ? { component: runtime.createComponent((tui, theme) => factory(tui, theme)) } : null] }),
      setTitle: (title) => bridge.notify('ui.publish', { ctx, op: 'setTitle', args: [title] }),
      custom: (factory, options) => runtime.withUIPrompt('custom', undefined, () => new Promise((resolve, reject) => {
        let id;
        const done = (result) => { bridge.notify('component.done', { id }); runtime.disposeComponent(id); resolve(result); };
        try { id = runtime.createComponent((tui, theme, keybindings) => factory(tui, theme, keybindings, done)); }
        catch (error) { reject(error); return; }
        const overlayOptions = typeof options?.overlayOptions === 'function' ? options.overlayOptions() : options?.overlayOptions;
        bridge.call('ui.custom', { ctx, component: id, overlay: options?.overlay === true, overlayOptions: overlayOptions ?? null })
          .catch((error) => { runtime.disposeComponent(id); reject(error); });
      })),
      pasteToEditor: (text) => bridge.notify('ui.publish', { ctx, op: 'pasteToEditor', args: [text] }),
      setEditorText: (text) => bridge.notify('ui.publish', { ctx, op: 'setEditorText', args: [text] }),
      getEditorText: () => bridge.sync('ui.read', { ctx, op: 'getEditorText' }) ?? '',
      addAutocompleteProvider: () => bridge.notify('ui.publish', { ctx, op: 'addAutocompleteProvider', args: [] }),
      setEditorComponent: (factory) => { runtime.editorFactory = factory; bridge.notify('ui.publish', { ctx, op: 'setEditorComponent', args: [factory ? { component: runtime.createComponent((tui, theme, keybindings) => factory(tui, theme, keybindings)) } : null] }); },
      getEditorComponent: () => runtime.editorFactory,
      get theme() { return runtime.theme(); },
      getAllThemes: () => bridge.sync('ui.read', { ctx, op: 'getAllThemes' }) ?? [],
      getTheme: (name) => { const known = (bridge.sync('ui.read', { ctx, op: 'getAllThemes' }) ?? []).some(t => t.name === name); return known && runtime.themeFactory ? runtime.themeFactory(name) : undefined; },
      setTheme: (theme) => {
        const name = typeof theme === 'string' ? theme : theme?.name;
        const result = bridge.sync('ui.setTheme', { ctx, name });
        if (result?.success) { runtime.themeName = name; runtime.themeObject = undefined; }
        return result;
      },
      getToolsExpanded: () => bridge.sync('ui.read', { ctx, op: 'getToolsExpanded' }) === true,
      setToolsExpanded: (expanded) => bridge.notify('ui.publish', { ctx, op: 'setToolsExpanded', args: [expanded] }),
    };
    return ui;
  }

  noOpUIContext() {
    const runtime = this;
    return {
      select: async () => undefined, confirm: async () => false, input: async () => undefined, notify: () => {}, onTerminalInput: () => () => {},
      setStatus: () => {}, setWorkingMessage: () => {}, setWorkingVisible: () => {}, setWorkingIndicator: () => {}, setHiddenThinkingLabel: () => {},
      setWidget: () => {}, setFooter: () => {}, setHeader: () => {}, setTitle: () => {}, custom: async () => undefined, pasteToEditor: () => {},
      setEditorText: () => {}, getEditorText: () => '', editor: async () => undefined, addAutocompleteProvider: () => {}, setEditorComponent: () => {},
      getEditorComponent: () => undefined, get theme() { return runtime.theme(); }, getAllThemes: () => [], getTheme: () => undefined,
      setTheme: () => ({ success: false, error: 'UI not available' }), getToolsExpanded: () => false, setToolsExpanded: () => {},
    };
  }

  footerData(ctx) {
    const bridge = this.bridge;
    return {
      getGitBranch: () => bridge.sync('ui.read', { ctx, op: 'getGitBranch' }) ?? null,
      getExtensionStatuses: () => new Map(Object.entries(bridge.sync('ui.read', { ctx, op: 'getExtensionStatuses' }) ?? {})),
      getAvailableProviderCount: () => bridge.sync('ui.read', { ctx, op: 'getAvailableProviderCount' }) ?? 0,
      onBranchChange: () => () => {},
    };
  }

  withUIPrompt(kind, title, run) {
    const outer = this.uiPromptDepth++ === 0;
    if (outer) {
      this.activeUIPrompt = { kind, title };
      queueMicrotask(() => this.bridge.notify('ui.prompt', { type: 'ui_prompt_start', reason: 'ui_prompt', kind, ...(title ? { title } : {}) }));
    }
    const finish = () => {
      if (--this.uiPromptDepth > 0) return;
      this.uiPromptDepth = 0;
      const prompt = this.activeUIPrompt ?? { kind, title }; this.activeUIPrompt = undefined;
      queueMicrotask(() => this.bridge.notify('ui.prompt', { type: 'ui_prompt_end', reason: 'ui_prompt', kind: prompt.kind, ...(prompt.title ? { title: prompt.title } : {}) }));
    };
    try { return run().finally(finish); } catch (error) { finish(); throw error; }
  }

  // ---------------------------------------------------------------------------------------------------------------- components

  /** A component lives here; the host renders it with component.render(width) and feeds keys with component.input(data). */
  createComponent(factory) {
    const id = `c${++this.nextComponent}`;
    const runtime = this, bridge = this.bridge;
    const tui = {
      requestRender: () => bridge.notify('component.invalidate', { id }),
      get terminal() { const size = bridge.sync('ui.read', { op: 'terminalSize' }) ?? { columns: 80, rows: 24 }; return { columns: size.columns, rows: size.rows }; },
      get columns() { return this.terminal.columns; }, get rows() { return this.terminal.rows; },
    };
    const keybindings = { matches: () => false, getKeys: () => [], get: () => undefined };
    const record = { id, component: undefined, pending: undefined };
    this.components.set(id, record);
    const created = factory(tui, runtime.theme(), keybindings);
    if (created && typeof created.then === 'function') record.pending = created.then(component => { record.component = component; bridge.notify('component.invalidate', { id }); }, () => {});
    else record.component = created;
    return id;
  }
  renderComponent(id, width) {
    const record = this.components.get(id);
    if (!record?.component) return [];
    return (record.component.render(width) ?? []).map(String);
  }
  inputComponent(id, data) { const component = this.components.get(id)?.component; component?.handleInput?.(data); }
  disposeComponent(id) { const record = this.components.get(id); if (!record) return; this.components.delete(id); try { record.component?.dispose?.(); } catch { /* ignore */ } }

  // ---------------------------------------------------------------------------------------------------------------- dispatch

  /** One extension's handlers for one event, reduced as upstream's runner reduces them. Returns { result?, errors }. */
  async emit(extension, event, payload, ctx, handlerIndex) {
    if (!extension) throw new Error('Unknown extension');
    let handlers = extension.handlers.get(event)?.slice() ?? [];
    if (handlerIndex !== undefined && handlerIndex !== null) handlers = handlers[handlerIndex] ? [handlers[handlerIndex]] : [];
    const errors = [];
    const fail = (error, text) => errors.push({ event, error: text ?? message(error), stack: stack(error) });
    const context = () => this.createContext(ctx, event === 'before_agent_start' ? { getSystemPrompt: () => current.systemPrompt } : {});
    const current = { ...payload };
    const sessionBefore = event === 'session_before_switch' || event === 'session_before_fork' || event === 'session_before_compact' || event === 'session_before_tree';
    switch (event) {
      case 'project_trust': {
        for (const handler of handlers) {
          try { const r = await handler(payload, { cwd: payload.cwd, ui: this.createUIContext(ctx), hasUI: this.hasUI, mode: this.mode }); if (r?.trusted === 'undecided') continue; return { result: r, errors }; }
          catch (error) { fail(error); }
        }
        return { errors };
      }
      case 'tool_call': {
        // Handlers may edit event.input in place; a throwing handler fails the call (upstream does not catch here).
        const ctxObject = context(); let result;
        for (const handler of handlers) {
          const r = await handler(current, ctxObject);
          if (r) { result = r; if (r.block) break; }
        }
        return { result, input: current.input, errors };
      }
      case 'tool_result': {
        const ctxObject = context(); let modified = false;
        for (const handler of handlers) {
          try {
            const r = await handler(current, ctxObject); if (!r) continue;
            if (r.content !== undefined) { current.content = r.content; if (r.structuredContent === undefined) delete current.structuredContent; modified = true; }
            if (r.details !== undefined) { current.details = r.details; modified = true; }
            if (r.structuredContent !== undefined) { current.structuredContent = r.structuredContent; modified = true; }
            if (r.isError !== undefined) { current.isError = r.isError; modified = true; }
            if (r.usage !== undefined) { current.usage = r.usage; modified = true; }
          } catch (error) { fail(error); }
        }
        return { result: modified ? { content: current.content, details: current.details, structuredContent: current.structuredContent, isError: current.isError, usage: current.usage } : undefined, errors };
      }
      case 'message_end': {
        const ctxObject = context(); let msg = payload.message, modified = false;
        for (const handler of handlers) {
          try {
            const r = await handler({ ...payload, message: msg }, ctxObject);
            if (!r?.message) continue;
            if (r.message.role !== msg.role) { fail(null, 'message_end handlers must return a message with the same role'); continue; }
            msg = r.message; modified = true;
          } catch (error) { fail(error); }
        }
        return { result: modified ? { message: msg } : undefined, errors };
      }
      case 'user_bash': {
        const ctxObject = context();
        for (const handler of handlers) {
          try {
            const r = await handler(payload, ctxObject);
            if (r === undefined) continue;
            if (!isUserBashEventResult(r)) throw new Error('Invalid user_bash handler result: return undefined for local execution or exactly one valid { operations } or { result } object');
            if (r.operations) { const id = this.registerCallback((args) => this.runShellOperation(r.operations, args)); return { result: { operations: id }, errors }; }
            return { result: r, errors };
          } catch (error) { fail(error); return { failed: true, errors }; }
        }
        return { errors };
      }
      case 'context': {
        const ctxObject = context(); let messages = structuredClone(payload.messages); let changed = false;
        for (const handler of handlers) {
          try {
            const visible = messages.filter(m => m.role !== 'system'); const snapshot = visible.slice();
            const r = await handler({ type: 'context', messages: visible }, ctxObject);
            const returned = r?.messages ?? (sameMessages(visible, snapshot) ? undefined : visible);
            if (!returned) continue;
            messages = restoreSystemMessages(messages, snapshot, returned); changed = true;
          } catch (error) { fail(error); }
        }
        return { result: changed ? { messages } : undefined, errors };
      }
      case 'context_with_system': {
        const ctxObject = context(); let messages = payload.messages; let changed = false;
        for (const handler of handlers) {
          try {
            const hadSystem = messages[0]?.role === 'system';
            const r = await handler({ type: 'context_with_system', messages }, ctxObject);
            if (r?.messages) { messages = r.messages; changed = true; }
            if (hadSystem && messages[0]?.role !== 'system')
              fail(null, 'Handler removed the leading system message; the request has no prompt or initial tool declarations. Keep it at index 0 or replace a dropped prefix with getCurrentSystemMessage().');
          } catch (error) { fail(error); }
        }
        return { result: changed ? { messages } : undefined, errors };
      }
      case 'before_provider_request': {
        const ctxObject = context(); let current2 = payload.payload; let changed = false;
        for (const handler of handlers) {
          try { const r = await handler({ type: event, payload: current2 }, ctxObject); if (r !== undefined) { current2 = r; changed = true; } }
          catch (error) { fail(error); }
        }
        return { result: changed ? current2 : undefined, errors };
      }
      case 'before_provider_headers': {
        // Handlers edit the headers in place; deleted names are reported as null so the host removes them.
        const ctxObject = context(); const headers = { ...payload.headers }; const before = Object.keys(headers);
        for (const handler of handlers) { try { await handler({ type: event, headers }, ctxObject); } catch (error) { fail(error); } }
        const edited = { ...headers }; for (const name of before) if (!(name in headers) || headers[name] === undefined) edited[name] = null;
        return { result: edited, errors };
      }
      case 'before_agent_start': {
        const ctxObject = context(); const messages = []; let systemPrompt;
        for (const handler of handlers) {
          try {
            const r = await handler({ type: event, prompt: payload.prompt, images: payload.images, systemPrompt: current.systemPrompt, systemPromptOptions: payload.systemPromptOptions }, ctxObject);
            if (r) { if (r.message) messages.push(r.message); if (r.systemPrompt !== undefined) { systemPrompt = r.systemPrompt; current.systemPrompt = r.systemPrompt; } }
          } catch (error) { fail(error); }
        }
        return { result: messages.length || systemPrompt !== undefined ? { messages, systemPrompt } : undefined, errors };
      }
      case 'resources_discover': {
        const ctxObject = context(); const out = { skillPaths: [], promptPaths: [], themePaths: [] }; let any = false;
        for (const handler of handlers) {
          try {
            const r = await handler(payload, ctxObject);
            for (const key of ['skillPaths', 'promptPaths', 'themePaths']) if (r?.[key]?.length) { out[key].push(...r[key]); any = true; }
          } catch (error) { fail(error); }
        }
        return { result: any ? out : undefined, errors };
      }
      case 'input': {
        const ctxObject = context(); let text = payload.text, images = payload.images;
        for (const handler of handlers) {
          try {
            const r = await handler({ type: 'input', text, images, source: payload.source, streamingBehavior: payload.streamingBehavior }, ctxObject);
            if (r?.action === 'handled') return { result: r, errors };
            if (r?.action === 'transform') { text = r.text; images = r.images ?? images; }
          } catch (error) { fail(error); }
        }
        return { result: text !== payload.text || images !== payload.images ? { action: 'transform', text, images } : { action: 'continue' }, errors };
      }
      case 'cache_warming_decision': {
        const ctxObject = context(); let action;
        for (const handler of handlers) { try { const r = await handler(payload, ctxObject); if (r?.action !== undefined) action = r.action; } catch (error) { fail(error); } }
        return { result: action !== undefined ? { action } : undefined, errors };
      }
      case 'turn_end': case 'agent_before_settle': {
        const ctxObject = context(); let entries = payload.entries ?? [], cont = payload.continue === true, any = false;
        for (const handler of handlers) {
          try {
            const r = await handler({ ...payload, entries, continue: cont }, ctxObject);
            if (r?.entries !== undefined) { entries = r.entries; any = true; }
            if (r?.continue !== undefined) { cont = r.continue; any = true; }
          } catch (error) { fail(error); }
        }
        return { result: any ? { entries, continue: cont } : undefined, errors };
      }
      default: {
        const ctxObject = context(); let result;
        for (const handler of handlers) {
          try {
            const r = await handler(payload, ctxObject);
            if (sessionBefore && r) { result = r; if (r.cancel) return { result, errors }; }
          } catch (error) { fail(error); }
        }
        return { result, errors };
      }
    }
  }

  async runShellOperation(operations, args) {
    const { command, cwd, timeout, callId } = args;
    const controller = new AbortController(); this.shellAborts ??= new Map(); this.shellAborts.set(callId, controller);
    try {
      return await operations.exec(command, cwd, {
        onData: (data) => this.bridge.notify('userBash.data', { callId, data: Buffer.from(data).toString('base64') }),
        signal: controller.signal, timeout, env: process.env,
      });
    } finally { this.shellAborts.delete(callId); }
  }

  findTool(name) {
    for (const ext of this.extensions) { const tool = ext.tools.get(name); if (tool) return { ext, tool: tool.definition }; }
    return undefined;
  }

  /** Source wrapToolDefinition: execute(toolCallId, params, signal, onUpdate, ctx). */
  async executeTool(extIndex, name, toolCallId, params, ctx, signal, onUpdate) {
    const tool = this.extensions[extIndex]?.tools.get(name)?.definition;
    if (!tool) throw new Error(`Tool ${name} is not registered`);
    const result = await tool.execute(toolCallId, params, signal, onUpdate, this.createToolContext(ctx, toolCallId, signal));
    return result;
  }

  async executeCommand(extIndex, name, args, ctx) {
    const command = this.extensions[extIndex]?.commands.get(name);
    if (!command) throw new Error(`Command /${name} is not registered`);
    await command.handler(args, this.createCommandContext(ctx));
  }

  async completeCommand(extIndex, name, prefix) {
    const command = this.extensions[extIndex]?.commands.get(name);
    if (!command?.getArgumentCompletions) return null;
    return (await command.getArgumentCompletions(prefix)) ?? null;
  }

  async runShortcut(extIndex, shortcut, ctx) {
    const entry = this.extensions[extIndex]?.shortcuts.get(shortcut);
    if (!entry) throw new Error(`Shortcut ${shortcut} is not registered`);
    await entry.handler(this.createContext(ctx));
  }

  /** Source resolveToolRenderers for one extension's resolvers, then the tool's own renderers. */
  resolveToolRenderers(toolName) {
    const resolvers = this.extensions.flatMap(ext => ext.toolRenderers);
    const base = () => { const found = this.findTool(toolName)?.tool; return found ? { renderShell: found.renderShell, renderCall: found.renderCall, renderResult: found.renderResult } : undefined; };
    const resolve = (index) => index < resolvers.length ? resolvers[index](toolName, () => resolve(index + 1)) : base();
    return resolve(0);
  }

  renderTool(params) {
    const renderers = this.resolveToolRenderers(params.toolName);
    if (!renderers) return { handled: false };
    const key = params.toolCallId;
    let state = this.rendererState.get(key);
    if (!state) { state = { state: {}, call: undefined, result: undefined }; this.rendererState.set(key, state); }
    const theme = this.theme();
    const context = {
      args: params.args, toolCallId: key, invalidate: () => this.bridge.notify('render.invalidate', { toolCallId: key }), state: state.state, cwd: this.cwd,
      executionStarted: params.executionStarted === true, argsComplete: params.argsComplete !== false, isPartial: params.isPartial === true,
      expanded: params.expanded === true, showImages: params.showImages === true, isError: params.isError === true, durationMs: params.durationMs ?? undefined,
      outputPad: params.outputPad ?? 0,
    };
    const out = { handled: true, renderShell: renderers.renderShell ?? 'default' };
    if (params.slot === 'call') {
      if (typeof renderers.renderCall !== 'function') return { handled: false, renderShell: out.renderShell };
      state.call = renderers.renderCall(params.args, theme, { ...context, lastComponent: state.call });
      out.lines = state.call ? state.call.render(params.width) : [];
    } else {
      if (typeof renderers.renderResult !== 'function') return { handled: false, renderShell: out.renderShell };
      state.result = renderers.renderResult(params.result, { expanded: context.expanded, isPartial: context.isPartial }, theme, { ...context, lastComponent: state.result });
      out.lines = state.result ? state.result.render(params.width) : [];
    }
    return out;
  }

  renderMessage(params) {
    for (const ext of this.extensions) {
      const renderer = ext.messageRenderers.get(params.customType);
      if (!renderer) continue;
      const component = renderer(params.message, { expanded: params.expanded === true }, this.theme());
      return { handled: true, lines: component ? component.render(params.width) : [] };
    }
    return { handled: false };
  }

  renderEntry(params) {
    for (const ext of this.extensions) {
      const renderer = ext.entryRenderers.get(params.customType);
      if (!renderer) continue;
      const component = renderer(params.entry, { expanded: params.expanded === true }, this.theme());
      return { handled: true, lines: component ? component.render(params.width) : [] };
    }
    return { handled: false };
  }

  transformMarkdown(markdown, context) {
    let text = markdown;
    for (const ext of this.extensions) if (ext.markdownTransformer) text = ext.markdownTransformer(text, context ?? {});
    return text;
  }

  async routeVirtualModel(provider, id, request, ctx) {
    const entry = this.virtualModels.get(provider + '/' + id);
    if (!entry) throw new Error(`Virtual model ${provider}/${id} is not registered`);
    return entry.model.route(request, this.createContext(ctx));
  }
}

function plainOptions(options) {
  if (!options) return undefined;
  return JSON.parse(JSON.stringify(options, (key, value) => (key === 'signal' || typeof value === 'function') ? undefined : value));
}
