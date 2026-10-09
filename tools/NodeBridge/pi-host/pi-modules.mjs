// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (getAliases, loadExtensionModule)
// and packages/coding-agent/src/core/extensions/jiti-loader.ts.
//
// The real Pi packages: PiSharp installs the exact Pi 1.1.0 packages into the agent dir (PiNodeRuntime) and names their node_modules
// here. Extensions are then loaded as upstream's unbundled Node build loads them: jiti from Pi's own dependencies, created for the
// installed coding agent's loader.js, `moduleCache: false`, and getAliases (the coding agent's dist index, pi-agent-core, pi-tui, the
// pi-ai compat/oauth/providers entries, typebox, and the @mariozechner/* and @sinclair/typebox names).
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
  const piTuiEntry = resolve('@earendil-works/pi-tui');
  const piAiCompatEntry = resolve('@earendil-works/pi-ai/compat');
  const piAiOauthEntry = resolve('@earendil-works/pi-ai/oauth');
  const piAiProvidersEntry = resolve('@earendil-works/pi-ai/providers/all');
  const alias = {
    '@earendil-works/pi-coding-agent': packageIndex,
    '@earendil-works/pi-agent-core': piAgentCoreEntry,
    '@earendil-works/pi-tui': piTuiEntry,
    '@earendil-works/pi-ai/providers/all': piAiProvidersEntry,
    '@earendil-works/pi-ai/compat': piAiCompatEntry,
    '@earendil-works/pi-ai/oauth': piAiOauthEntry,
    '@earendil-works/pi-ai': piAiCompatEntry,
    '@mariozechner/pi-coding-agent': packageIndex,
    '@mariozechner/pi-agent-core': piAgentCoreEntry,
    '@mariozechner/pi-tui': piTuiEntry,
    '@mariozechner/pi-ai/providers/all': piAiProvidersEntry,
    '@mariozechner/pi-ai/compat': piAiCompatEntry,
    '@mariozechner/pi-ai/oauth': piAiOauthEntry,
    '@mariozechner/pi-ai': piAiCompatEntry,
    typebox: typeboxEntry,
    'typebox/compile': typeboxCompileEntry,
    'typebox/value': typeboxValueEntry,
    '@sinclair/typebox': typeboxEntry,
    '@sinclair/typebox/compile': typeboxCompileEntry,
    '@sinclair/typebox/value': typeboxValueEntry,
  };

  // The coding agent's theme (main.ts calls initTheme before extensions run; ctx.ui.theme is that global theme).
  const themes = await import(pathToFileURL(path.join(codingAgent, 'dist', 'modes', 'interactive', 'theme', 'theme.js')).href);
  try { themes.initTheme(themeName, false); } catch { /* the system theme stays unset only if Pi itself cannot load one */ }
  const ai = await import(pathToFileURL(piAiCompatEntry).href);
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
    themeFactory: (name) => (name === undefined ? themes.theme : themes.getThemeByName(name)),
    currentTheme: () => themes.theme,
    themes,
  };
}
