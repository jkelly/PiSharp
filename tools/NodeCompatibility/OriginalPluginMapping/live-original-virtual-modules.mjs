// Exact successor provider for the already admitted original source/package
// closure. Imported only through the reviewed bounded-source loader route.
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createOriginalVirtualModuleInjection } from './original-virtual-module-injection.mjs';

const oracle = process.env.PISHARP_REAL_EXTENSION_ORACLE;
assert(typeof oracle === 'string' && oracle.length > 0, 'Explicit qualified oracle environment required');
const original = path => import(pathToFileURL(join(oracle, 'upstream', path)).href);
const typebox = await import(pathToFileURL(join(oracle, 'node_modules/typebox/build/index.mjs')).href);
const typeboxCompile = await import(pathToFileURL(join(oracle, 'node_modules/typebox/build/compile/index.mjs')).href);
const typeboxValue = await import(pathToFileURL(join(oracle, 'node_modules/typebox/build/value/index.mjs')).href);
const originalAi = await original('packages/ai/src/index.ts');
const originalTypes = await original('packages/coding-agent/src/core/extensions/types.ts');
const originalTruncation = await original('packages/coding-agent/src/core/tools/truncate.ts');
const originalMutations = await original('packages/coding-agent/src/core/tools/file-mutation-queue.ts');
const keys = await original('packages/tui/src/keys.ts');
const utils = await original('packages/tui/src/utils.ts');
const text = await original('packages/tui/src/components/text.ts');
const box = await original('packages/tui/src/components/box.ts');
const themeTui = await import('./original-theme-tui-suppliers.mjs');
const originalTheme = await original('packages/coding-agent/src/modes/interactive/theme/theme.ts');
const originalKeybindings = await original('packages/coding-agent/src/core/keybindings.ts');

// Direct references from targeted original modules; do not load the full TUI
// barrel, recreate constructors, or invoke lazy terminal capability probing.
const originalTui = { Key: keys.Key, matchesKey: keys.matchesKey, Text: text.Text,
  Box: box.Box, visibleWidth: utils.visibleWidth, truncateToWidth: utils.truncateToWidth };
export const ORIGINAL_SUPPLIERS = Object.freeze({ typebox, typeboxCompile, typeboxValue,
  originalAi, originalTypes, originalTruncation, originalTui, originalTheme, originalKeybindings, themeTui,
  withFileMutationQueue: originalMutations.withFileMutationQueue });
assert.equal(typeof ORIGINAL_SUPPLIERS.withFileMutationQueue, 'function', 'Original mutation queue export required');
export const VIRTUAL_MODULES = createOriginalVirtualModuleInjection(ORIGINAL_SUPPLIERS);
globalThis.pisharpRealExtensionVirtualMap = {
  authority: 'bounded original supplied StringEnum/public truncation/TUI slices and frozen R874 source renderer transport; native qualification remains OPEN',
  exports: Object.fromEntries(Object.entries(VIRTUAL_MODULES).map(([name, module]) => [name, Object.keys(module)])),
  actualTypeIdentityWithPublicAiCore: originalAi.Type === typebox.Type,
  actualDefineToolReferenceFromUnchangedTypes: VIRTUAL_MODULES['@earendil-works/pi-coding-agent'].defineTool === originalTypes.defineTool,
  originalTuiReferences: Object.fromEntries(Object.entries(originalTui).map(([name, value]) => [name, VIRTUAL_MODULES['@earendil-works/pi-tui'][name] === value])),
  rendererTransportImplemented: true,
  rendererRuntimeQualified: false,
};

// Entire original parser and constructors; no renderer/theme/keybinding facsimile,
// settings reads, watchers or terminal detection. This profile fixes dark/truecolor.
export function createOriginalRendererEnvironment() {
  const theme = originalTheme.loadThemeFromPath(join(oracle, 'upstream/packages/coding-agent/src/modes/interactive/theme/dark.json'), 'truecolor');
  const keybindings = new originalKeybindings.KeybindingsManager();
  assert(theme instanceof originalTheme.Theme);
  assert(keybindings instanceof originalKeybindings.KeybindingsManager);
  return { theme, keybindings };
}
