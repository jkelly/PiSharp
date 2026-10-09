// PiSharp Node extension host (main thread). Started by PiSharp with the user's Node when a session loads TypeScript or JavaScript
// extensions; it is a separate process, and the host protocol on standard input/output is its only channel to PiSharp:
//   {"type":"request","id":n,"method":m,"params":p[,"sync":true]}   {"type":"response","id":n,"result":r | "error":{message,stack}}
//   {"type":"notify","method":m,"params":p}   {"type":"progress","id":n,"value":v}   {"type":"cancel","id":n}
// Extension output written to standard output goes to standard error so it cannot corrupt the protocol.
import { Worker, MessageChannel, receiveMessageOnPort } from 'node:worker_threads';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import util from 'node:util';
import { Readable } from 'node:stream';
import { installHooks } from './loader-hooks.mjs';
import { loadPiModules } from './pi-modules.mjs';
import { ExtensionRuntime, describeExtension } from './runtime.mjs';
import { validateToolArguments } from './validate.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

// ------------------------------------------------------------------------------------------------ standard output stays protocol-only
const writeErr = (text) => process.stderr.write(text);
for (const name of ['log', 'info', 'debug']) console[name] = (...args) => writeErr(util.format(...args) + '\n');
process.stdout.write = (chunk, encoding, callback) => {
  writeErr(typeof chunk === 'string' ? chunk : Buffer.from(chunk).toString(typeof encoding === 'string' ? encoding : 'utf8'));
  if (typeof encoding === 'function') encoding(); else if (typeof callback === 'function') callback();
  return true;
};
// Standard input is the protocol, read by the I/O thread. On Windows, opening it again here (the lazy process.stdin, which Pi's own
// modules touch) blocks until the I/O thread's pending read completes, so extensions get an empty, never-ending stream instead, as
// an extension would see stdin in Pi's RPC mode.
const isolatedStdin = new Readable({ read() {} });
isolatedStdin.isTTY = false;
Object.defineProperty(process, 'stdin', { configurable: true, enumerable: true, get: () => isolatedStdin });

// ------------------------------------------------------------------------------------------------ channel to the I/O thread
const flag = new SharedArrayBuffer(4);
const signal = new Int32Array(flag);
const sync = new MessageChannel();
const main = new MessageChannel();
const io = new Worker(path.join(here, 'io-worker.mjs'), {
  workerData: { flag, syncPort: sync.port2, mainPort: main.port2 }, transferList: [sync.port2, main.port2], stdout: false, stderr: false,
});
io.on('error', (error) => { writeErr(`pisharp-node-host: I/O thread failed: ${error?.stack ?? error}\n`); process.exit(32); });
io.on('exit', (code) => process.exit(shuttingDown ? 0 : (code || 33)));
let shuttingDown = false;
const port = main.port1;
const send = (frame) => port.postMessage({ kind: 'out', frame });

let nextToken = 0;
const waiting = new Map(); // token -> { resolve, reject, onProgress }

class HostError extends Error { constructor(error) { super(error?.message ?? 'PiSharp host request failed'); if (error?.code) this.code = error.code; } }

const bridge = {
  /** Synchronous host call: blocks this thread until the I/O thread has the host's answer. */
  sync(method, params) {
    Atomics.store(signal, 0, 0);
    port.postMessage({ kind: 'sync', method, params: plain(params) });
    for (;;) {
      const result = Atomics.wait(signal, 0, 0, 120_000);
      if (result === 'timed-out') throw new Error(`PiSharp host did not answer ${method}`);
      if (Atomics.load(signal, 0) === 1) break;
    }
    const received = receiveMessageOnPort(sync.port1);
    const message = received?.message;
    if (!message) throw new Error(`PiSharp host answer missing for ${method}`);
    if (message.error) throw new HostError(message.error);
    return message.result;
  },
  /** Asynchronous host call. `options.signal` cancels it; `options.onProgress` receives progress values. */
  call(method, params, options = {}) {
    return new Promise((resolve, reject) => {
      const token = ++nextToken;
      waiting.set(token, { resolve, reject, onProgress: options.onProgress });
      port.postMessage({ kind: 'async', token, method, params: plain(params) });
      if (options.signal) {
        const abort = () => port.postMessage({ kind: 'cancelOwn', token });
        if (options.signal.aborted) abort(); else options.signal.addEventListener('abort', abort, { once: true });
      }
    });
  },
  notify(method, params) { send({ type: 'notify', method, params: plain(params) }); },
};
// The virtual modules' host hook: call(name, ...args), where a trailing { signal, onUpdate, ctx } carries the caller's context.
globalThis.__pisharpBridge = {
  call: (name, ...args) => {
    const last = args.at(-1);
    const extras = last && typeof last === 'object' && ('signal' in last || 'onUpdate' in last || 'ctx' in last) ? args.pop() : undefined;
    return bridge.call('bridge.call', { name, args, ctx: extras?.ctx?.__pisharpCtx }, { signal: extras?.signal, onProgress: extras?.onUpdate });
  },
};

/** JSON-safe copy (functions and symbols dropped, undefined object members omitted, as JSON.stringify does). */
function plain(value) { return value === undefined ? undefined : JSON.parse(JSON.stringify(value, (k, v) => typeof v === 'bigint' ? Number(v) : v) ?? 'null'); }
function errorOf(error) { return { message: error instanceof Error ? error.message : String(error), stack: error instanceof Error ? error.stack : undefined, name: error?.name }; }

// ------------------------------------------------------------------------------------------------ requests from PiSharp
let runtime;
const active = new Map(); // host request id -> AbortController

async function handle(method, params, id) {
  switch (method) {
    case 'init': {
      if (params.agentDir && !process.env.PI_CODING_AGENT_DIR) process.env.PI_CODING_AGENT_DIR = params.agentDir;
      globalThis.__pisharpHost = { agentDir: params.agentDir, version: params.version, cwd: params.cwd };
      let themeFactory, importExtension, createEventStream, tuiModule, editorTheme, modules = 'compatibility';
      if (params.piModules) {
        // The installed Pi packages, loaded with Pi's own jiti and aliases.
        const pi = await loadPiModules(params.piModules, params.theme);
        themeFactory = pi.themeFactory; importExtension = pi.importExtension; createEventStream = pi.createEventStream; modules = 'pi@' + pi.version;
        tuiModule = pi.tui; editorTheme = () => pi.themes.getEditorTheme();
      } else {
        // Offline fallback: PiSharp's compatibility modules and loader hooks.
        await installHooks();
        try { const themes = await import(pathToFileURL(path.join(here, 'virtual', 'theme.mjs')).href); themeFactory = (name) => themes.createTheme(name); } catch { themeFactory = undefined; }
        const ai = await import(pathToFileURL(path.join(here, 'virtual', 'pi-ai.mjs')).href);
        createEventStream = () => ai.createAssistantMessageEventStream();
        try { tuiModule = await import(pathToFileURL(path.join(here, 'virtual', 'pi-tui.mjs')).href); } catch { tuiModule = undefined; }
        try { const themes = await import(pathToFileURL(path.join(here, 'virtual', 'theme.mjs')).href); editorTheme = typeof themes.getEditorTheme === 'function' ? () => themes.getEditorTheme() : undefined; } catch { editorTheme = undefined; }
      }
      runtime = new ExtensionRuntime(bridge, { ...params, themeFactory, importExtension, createEventStream, tuiModule, editorTheme });
      return { node: process.version, pid: process.pid, modules };
    }
    case 'load': {
      const results = [];
      for (const entry of params.paths) {
        const loaded = await runtime.load(entry);
        results.push(loaded.error ? { path: entry, error: loaded.error } : { path: entry, extension: describeExtension(loaded.extension) });
      }
      return { results, flagValues: Object.fromEntries(runtime.flagValues) };
    }
    case 'bind': runtime.bound = true; return { flagValues: Object.fromEntries(runtime.flagValues) };
    case 'reload': {
      // agent-session.ts reload(): the old runtime is invalidated (its pi and ctx objects are stale) and the extensions load again
      // into a fresh runtime with fresh modules, keeping the flag values.
      const previous = runtime;
      previous.invalidate();
      runtime = new ExtensionRuntime(bridge, { ...previous.options, flagValues: Object.fromEntries(previous.flagValues), generation: previous.generation + 1 });
      runtime.themeName = previous.themeName;
      const results = [];
      for (const entry of params.paths) {
        const loaded = await runtime.load(entry);
        results.push(loaded.error ? { path: entry, error: loaded.error } : { path: entry, extension: describeExtension(loaded.extension) });
      }
      runtime.bound = previous.bound;
      return { results, flagValues: Object.fromEntries(runtime.flagValues) };
    }
    case 'flags.set': for (const [name, value] of Object.entries(params.values ?? {})) runtime.flagValues.set(name, value); return true;
    case 'emit': return runtime.emit(runtime.extensions[params.ext], params.event, params.payload, params.ctx, params.handler);
    case 'tool.execute': {
      const controller = new AbortController(); active.set(id, controller);
      try {
        const onUpdate = (partial) => send({ type: 'progress', id, value: plain(partial) });
        return await runtime.executeTool(params.ext, params.name, params.toolCallId, params.params, params.ctx, controller.signal, onUpdate);
      } finally { active.delete(id); }
    }
    case 'tool.prepareArguments': {
      // agent-core prepareToolCall: the tool's prepareArguments shim, then validateToolArguments against its schema.
      const tool = runtime.extensions[params.ext]?.tools.get(params.name)?.definition;
      if (!tool) throw new Error(`Tool ${params.name} not found`);
      const prepared = tool.prepareArguments ? tool.prepareArguments(params.args) : params.args;
      return validateToolArguments(tool, { name: params.name, arguments: prepared });
    }
    case 'tool.prepareLoadout': {
      const tool = runtime.extensions[params.ext]?.tools.get(params.name)?.definition;
      if (!tool?.prepareLoadout) return null;
      const loadout = params.loadout;
      const view = { declared: loadout.declared, callable: loadout.callable, registered: loadout.registered,
        getExposure: (name) => loadout.exposures?.[name] ?? 'direct', getNamespace: (name) => loadout.namespaces?.[name], getPromptGuidelines: (name) => loadout.guidelines?.[name] ?? [] };
      return tool.prepareLoadout(view) ?? null;
    }
    case 'command.execute': await runtime.executeCommand(params.ext, params.name, params.args, params.ctx); return null;
    case 'command.complete': return runtime.completeCommand(params.ext, params.name, params.prefix);
    case 'shortcut.run': await runtime.runShortcut(params.ext, params.shortcut, params.ctx); return null;
    case 'callback.invoke': {
      const fn = runtime.callbacks.get(params.id);
      if (!fn) throw new Error(`Unknown extension callback ${params.id}`);
      return fn(params.args);
    }
    case 'userBash.abort': runtime.shellAborts?.get(params.callId)?.abort(); return null;
    case 'component.render': return runtime.renderComponent(params.id, params.width);
    case 'component.input': runtime.inputComponent(params.id, params.data); return null;
    case 'component.dispose': runtime.disposeComponent(params.id); return null;
    case 'component.method': return plain(runtime.callComponent(params.id, params.method, params.args)) ?? null;
    case 'editor.configure': runtime.configureEditor(params.keybindings, params.actions); return null;
    case 'editor.autocomplete': runtime.setEditorAutocomplete(params.id, params.token); return null;
    case 'autocomplete.suggest': {
      const controller = new AbortController(); active.set(id, controller);
      try {
        const provider = runtime.autocompleteProvider(params.wrapper, params.token);
        return plain(await provider.getSuggestions(params.lines, params.cursorLine, params.cursorCol, { force: params.force === true, signal: controller.signal })) ?? null;
      } finally { active.delete(id); }
    }
    case 'autocomplete.apply': return plain(runtime.autocompleteProvider(params.wrapper, params.token).applyCompletion(params.lines, params.cursorLine, params.cursorCol, params.item, params.prefix)) ?? null;
    case 'autocomplete.triggers': return plain(runtime.autocompleteProvider(params.wrapper, params.token).triggerCharacters ?? []);
    case 'autocomplete.file': {
      const provider = runtime.autocompleteProvider(params.wrapper, params.token);
      return typeof provider.shouldTriggerFileCompletion === 'function' ? provider.shouldTriggerFileCompletion(params.lines, params.cursorLine, params.cursorCol) !== false : true;
    }
    case 'render.tool': return runtime.renderTool(params);
    case 'render.resolve': {
      const renderers = runtime.resolveToolRenderers(params.toolName);
      return renderers ? { handled: true, renderShell: renderers.renderShell ?? 'default', hasRenderCall: typeof renderers.renderCall === 'function',
        hasRenderResult: typeof renderers.renderResult === 'function' } : { handled: false };
    }
    case 'render.message': return runtime.renderMessage(params);
    case 'render.entry': return runtime.renderEntry(params);
    case 'markdown.transform': return runtime.transformMarkdown(params.markdown, params.context);
    case 'virtualModel.route': return runtime.routeVirtualModel(params.provider, params.id, params.request, params.ctx);
    case 'provider.call': {
      const record = runtime.providers.get(params.provider);
      const config = record?.config;
      if (!config) throw new Error(`Provider ${params.provider} is not registered by an extension`);
      if (params.op === 'classify') return config.classifiers[params.api].classify(...params.args);
      if (params.op === 'generateImages') return config.images[params.api].generateImages(...params.args);
      if (params.op === 'refreshModels') return config.refreshModels(params.args?.[0] ?? {});
      throw new Error(`Unsupported provider operation ${params.op}`);
    }
    case 'provider.stream': {
      // An extension provider's streamSimple (registerProvider with api + streamSimple): its AssistantMessageEvents stream back as
      // progress (pi-ai source events with their partial message), the final message as the result.
      const record = [...runtime.providers.values()].find(item => item.config?.api === params.api && typeof item.config?.streamSimple === 'function')
        ?? runtime.providers.get(params.provider);
      const streamSimple = record?.config?.streamSimple;
      if (typeof streamSimple !== 'function') throw new Error(`No extension provider streams the ${params.api} API`);
      const controller = new AbortController(); active.set(id, controller);
      try {
        const stream = await streamSimple(params.model, params.context, { ...(params.options ?? {}), signal: controller.signal });
        let final;
        for await (const event of stream) {
          send({ type: 'progress', id, value: plain(event) });
          if (event.type === 'done') final = event.message; else if (event.type === 'error') final = event.error;
        }
        if (!final && typeof stream.result === 'function') final = await stream.result();
        return plain(final ?? null);
      } finally { active.delete(id); }
    }
    case 'events.deliver': runtime.eventBus.deliver(params.channel, params.data); return null;
    case 'invalidate': runtime.invalidate(params.message); return null;
    case 'shutdown': shuttingDown = true; setImmediate(() => port.postMessage({ kind: 'stop' })); return null;
    default: throw new Error(`Unsupported PiSharp Node host method: ${method}`);
  }
}

port.on('message', (message) => {
  switch (message.kind) {
    case 'incoming': {
      const frame = message.message;
      if (frame.type === 'request') {
        Promise.resolve().then(() => handle(frame.method, frame.params ?? {}, frame.id)).then(
          (result) => send(result === undefined ? { type: 'response', id: frame.id } : { type: 'response', id: frame.id, result: plain(result) }),
          (error) => send({ type: 'response', id: frame.id, error: errorOf(error) }));
      } else if (frame.type === 'cancel') active.get(frame.id)?.abort();
      else if (frame.type === 'notify' && frame.method === 'events.deliver') runtime?.eventBus.deliver(frame.params.channel, frame.params.data);
      break;
    }
    case 'response': {
      const entry = waiting.get(message.token); if (!entry) break;
      waiting.delete(message.token);
      if (message.message.error) entry.reject(new HostError(message.message.error)); else entry.resolve(message.message.result);
      break;
    }
    case 'progress': waiting.get(message.token)?.onProgress?.(message.value); break;
    case 'eof': shuttingDown = true; process.exit(0);
  }
});

process.on('unhandledRejection', (reason) => writeErr(`pisharp-node-host: unhandled rejection: ${reason?.stack ?? reason}\n`));
process.on('uncaughtException', (error) => writeErr(`pisharp-node-host: uncaught exception: ${error?.stack ?? error}\n`));
send({ type: 'notify', method: 'ready', params: { node: process.version, pid: process.pid } });
