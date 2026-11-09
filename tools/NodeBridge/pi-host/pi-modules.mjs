// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/extensions/loader.ts (getAliases, loadExtensionModule),
// packages/coding-agent/src/core/extensions/jiti-loader.ts and packages/coding-agent/src/main.ts (setThemeJsonValidator, initTheme).
//
// The real Pi packages: PiSharp installs the exact Pi 1.5.0 packages into the agent dir (PiNodeRuntime) and names their node_modules
// here. Extensions are then loaded as upstream's unbundled Node build loads them: jiti from Pi's own dependencies, created for the
// installed coding agent's loader.js, `moduleCache: false`, and getAliases (the coding agent's dist index, pi-agent-core, pi-tui, the
// pi-ai compat/oauth/providers entries, typebox, and the @mariozechner/* and @sinclair/typebox names). Pi 1.5.0's getAliases also
// resolves the pi-ai root and compat entry to the coding agent's pi-ai extension view (dist/core/extensions/pi-ai-extension-view.js)
// and adds pi-ai/models; an installed Pi that predates the view keeps its compat entry, and one without pi-ai/models gets no alias.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

function entryOf(packageDir, condition = 'import') {
  const manifest = JSON.parse(fs.readFileSync(path.join(packageDir, 'package.json'), 'utf8'));
  const root = manifest.exports?.['.'] ?? manifest.exports;
  const target = typeof root === 'string' ? root : root?.[condition]?.default ?? root?.[condition] ?? root?.default ?? manifest.module ?? manifest.main;
  return path.join(packageDir, typeof target === 'string' ? target : 'index.js');
}

/** The installed Pi: jiti, the aliases, and the coding agent's theme module (one instance shared through globalThis, as in Pi). */
export async function loadPiModules(nodeModules, themeName) {
  // The installed SDK derives its own request identity from install-telemetry settings. Disable that default in this Node
  // child as well; an explicitly configured environment value remains the user's choice.
  process.env.PI_TELEMETRY ??= '0';
  const codingAgent = path.join(nodeModules, '@earendil-works', 'pi-coding-agent');
  const loaderFile = path.join(codingAgent, 'dist', 'core', 'extensions', 'loader.js');
  const loaderUrl = pathToFileURL(loaderFile).href;
  // jiti-loader.ts: `export { createJiti } from "jiti"`, resolved from the coding agent's own dependencies.
  const jitiDir = [path.join(codingAgent, 'node_modules', 'jiti'), path.join(nodeModules, 'jiti')].find(dir => fs.existsSync(path.join(dir, 'package.json')));
  if (!jitiDir) throw new Error(`jiti is not installed in ${nodeModules}`);
  const { createJiti } = await import(pathToFileURL(entryOf(jitiDir)).href);
  const resolver = createJiti(loaderUrl);
  const resolve = (specifier) => fileURLToPath(resolver.esmResolve(specifier, { parentURL: loaderUrl }));

  // getAliases
  const packageIndex = path.resolve(path.dirname(loaderFile), '../..', 'index.js');
  const typeboxEntry = resolve('typebox'), typeboxCompileEntry = resolve('typebox/compile'), typeboxValueEntry = resolve('typebox/value');
  const piAgentCoreEntry = resolve('@earendil-works/pi-agent-core');
  // Suppress Pi's default provider attribution in the actual shared module before any extension/coding-agent import. Mutating
  // the exported defaults object preserves Agent, TypeBox and jiti module identity, including transitive/default import forms.
  // Explicit identity supplied by an extension still overrides this default in upstream's mergeClientIdentity.
  const agentCore = await import(pathToFileURL(piAgentCoreEntry).href);
  const defaults = agentCore.DEFAULT_AGENT_IDENTITY;
  const attribution = defaults && Object.getOwnPropertyDescriptor(defaults, 'attribution');
  if (!defaults || typeof defaults !== 'object' || typeof agentCore.Agent !== 'function' || !attribution?.writable)
    throw new Error('Installed Pi agent-core has no supported default identity contract');
  defaults.attribution = null;
  if (defaults.attribution !== null)
    throw new Error('Installed Pi agent-core default attribution could not be disabled');
  const piTuiEntry = resolve('@earendil-works/pi-tui');
  const piAiCompatEntry = resolve('@earendil-works/pi-ai/compat');
  const piAiExtensionView = path.join(path.dirname(loaderFile), 'pi-ai-extension-view.js');
  const piAiExtensionEntry = fs.existsSync(piAiExtensionView) ? piAiExtensionView : piAiCompatEntry;
  let piAiModelsEntry;
  try { piAiModelsEntry = resolve('@earendil-works/pi-ai/models'); } catch { /* pi-ai without a ./models entry point */ }
  const piAiOauthEntry = resolve('@earendil-works/pi-ai/oauth');
  const piAiProvidersEntry = resolve('@earendil-works/pi-ai/providers/all');
  const alias = {
    '@earendil-works/pi-coding-agent': packageIndex,
    '@earendil-works/pi-agent-core': piAgentCoreEntry,
    '@earendil-works/pi-tui': piTuiEntry,
    '@earendil-works/pi-ai/providers/all': piAiProvidersEntry,
    '@earendil-works/pi-ai/compat': piAiExtensionEntry,
    ...(piAiModelsEntry ? { '@earendil-works/pi-ai/models': piAiModelsEntry } : {}),
    '@earendil-works/pi-ai/oauth': piAiOauthEntry,
    '@earendil-works/pi-ai': piAiExtensionEntry,
    '@mariozechner/pi-coding-agent': packageIndex,
    '@mariozechner/pi-agent-core': piAgentCoreEntry,
    '@mariozechner/pi-tui': piTuiEntry,
    '@mariozechner/pi-ai/providers/all': piAiProvidersEntry,
    '@mariozechner/pi-ai/compat': piAiExtensionEntry,
    ...(piAiModelsEntry ? { '@mariozechner/pi-ai/models': piAiModelsEntry } : {}),
    '@mariozechner/pi-ai/oauth': piAiOauthEntry,
    '@mariozechner/pi-ai': piAiExtensionEntry,
    typebox: typeboxEntry,
    'typebox/compile': typeboxCompileEntry,
    'typebox/value': typeboxValueEntry,
    '@sinclair/typebox': typeboxEntry,
    '@sinclair/typebox/compile': typeboxCompileEntry,
    '@sinclair/typebox/value': typeboxValueEntry,
  };

  // The coding agent's theme (main.ts installs the theme JSON validator and calls initTheme before extensions run; ctx.ui.theme is that
  // global theme).
  const themeDir = path.join(codingAgent, 'dist', 'modes', 'interactive', 'theme');
  const themes = await import(pathToFileURL(path.join(themeDir, 'theme.js')).href);
  const themeSchema = await import(pathToFileURL(path.join(themeDir, 'theme-schema.js')).href);
  themes.setThemeJsonValidator(themeSchema.validateThemeJson);
  try { themes.initTheme(themeName, false); } catch { /* the system theme stays unset only if Pi itself cannot load one */ }
  const ai = await import(pathToFileURL(piAiCompatEntry).href);
  const tui = await import(pathToFileURL(piTuiEntry).href);
  const version = JSON.parse(fs.readFileSync(path.join(codingAgent, 'package.json'), 'utf8')).version;

  return {
    version,
    nodeModules,
    alias,
    /** loadExtensionModule: a fresh jiti per load, `{ default: true }`. */
    importExtension: async (extensionPath) => {
      const jiti = createJiti(loaderUrl, { moduleCache: false, alias });
      return jiti.import(extensionPath, { default: true });
    },
    createEventStream: () => ai.createAssistantMessageEventStream(),
    tui,
    themeFactory: (name) => (name === undefined ? themes.theme : themes.getThemeByName(name)),
    currentTheme: () => themes.theme,
    themes,
  };
}
