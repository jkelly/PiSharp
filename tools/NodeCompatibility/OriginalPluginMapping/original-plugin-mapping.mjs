// An explicitly supplied, narrow original-plugin module map. No loading or admission occurs here.
export function createOriginalPluginMapping({ Type, defineTool, StringEnum, truncation, tui, withFileMutationQueue }) {
  if (Type === null || typeof Type !== 'object' || typeof Type.Object !== 'function' || typeof Type.String !== 'function')
    throw new TypeError('An explicitly supplied Type namespace with Object/String is required.');
  if (typeof defineTool !== 'function') throw new TypeError('An explicitly supplied defineTool export is required.');
  if (StringEnum !== undefined && typeof StringEnum !== 'function')
    throw new TypeError('StringEnum must be an explicitly supplied original function.');
  const tools = { defineTool };
  if (withFileMutationQueue !== undefined) {
    if (typeof withFileMutationQueue !== 'function') throw new TypeError('An explicitly supplied original withFileMutationQueue function is required.');
    tools.withFileMutationQueue = withFileMutationQueue;
  }
  if (truncation !== undefined) {
    if (truncation === null || typeof truncation !== 'object') throw new TypeError('An explicitly supplied original truncation namespace is required.');
    for (const [name, value] of [['DEFAULT_MAX_BYTES', 50 * 1024], ['DEFAULT_MAX_LINES', 2000]]) {
      if (!Object.hasOwn(truncation, name)) throw new TypeError('Original truncation constant required: ' + name);
      if (truncation[name] !== value) throw new TypeError('Original truncation constant required: ' + name);
      tools[name] = value;
    }
    for (const name of ['formatSize', 'truncateHead', 'truncateTail', 'truncateLine']) {
      if (!Object.hasOwn(truncation, name)) throw new TypeError('Original truncation function required: ' + name);
      const original = truncation[name];
      if (typeof original !== 'function') throw new TypeError('Original truncation function required: ' + name);
      tools[name] = original;
    }
  }
  // Each alias pair shares the same namespace, just as pinned VIRTUAL_MODULES does.
  // These are bounded slices, not substitutes for complete upstream namespaces.
  const typebox = Object.freeze({ Type });
  const ai = Object.freeze({ Type, ...(StringEnum === undefined ? {} : { StringEnum }) });
  const codingAgent = Object.freeze(tools);
  let originalTui;
  if (tui !== undefined) {
    if (tui === null || typeof tui !== 'object' || Array.isArray(tui))
      throw new TypeError('An explicitly supplied original TUI namespace is required.');
    const exports = {};
    for (const name of ['Key', 'matchesKey', 'Text', 'Box', 'visibleWidth', 'truncateToWidth']) {
      if (!Object.hasOwn(tui, name)) throw new TypeError('Original TUI export required: ' + name);
      const original = tui[name];
      if (name === 'Key' ? original === null || typeof original !== 'object' || Array.isArray(original) : typeof original !== 'function')
        throw new TypeError('Original TUI export required: ' + name);
      exports[name] = original;
    }
    // Freeze the bounded namespace only; caller-owned constructors, keyboard
    // state and algorithms remain original references and are never invoked.
    originalTui = Object.freeze(exports);
  }
  const modules = Object.freeze({
    typebox,
    '@sinclair/typebox': typebox,
    '@earendil-works/pi-ai': ai,
    '@mariozechner/pi-ai': ai,
    '@earendil-works/pi-coding-agent': codingAgent,
    '@mariozechner/pi-coding-agent': codingAgent,
    ...(originalTui === undefined ? {} : {
      '@earendil-works/pi-tui': originalTui,
      '@mariozechner/pi-tui': originalTui,
    }),
  });
  const resolve = specifier => {
    if (typeof specifier !== 'string' || !Object.hasOwn(modules, specifier))
      throw new TypeError('The requested module is outside this explicit mapping.');
    return modules[specifier];
  };
  const named = (specifier, name) => {
    const namespace = resolve(specifier);
    if (typeof name !== 'string' || !Object.hasOwn(namespace, name))
      throw new TypeError('The requested export is outside this explicit mapping.');
    return namespace[name];
  };
  return Object.freeze({ modules, resolve, named });
}

// Selects an explicitly supplied ESM default, as exported by the pinned hello example.
// This does not emulate Jiti's { default:true } import or the loader's transactional
// initializeExtension/commit/rollback. No Promise wrapping or detached work occurs.
export function activateDefaultPlugin(module, api) {
  if (module === null || (typeof module !== 'object' && typeof module !== 'function') ||
      !Object.hasOwn(module, 'default')) throw new TypeError('An explicit default plugin export is required.');
  const factory = module.default;
  if (typeof factory !== 'function') throw new TypeError('The default plugin export must be a function.');
  return factory(api);
}
