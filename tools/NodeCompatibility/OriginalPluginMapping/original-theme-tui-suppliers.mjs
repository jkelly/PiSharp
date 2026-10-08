// Genuine targeted module references used by unchanged original Theme/system-theme
// and app keybindings imports. No namespace barrel or terminal capability probe.
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
const oracle = process.env.PISHARP_REAL_EXTENSION_ORACLE;
assert(typeof oracle === 'string' && oracle.length > 0);
const original = path => import(pathToFileURL(join(oracle, 'upstream', path)).href);
const colors = await original('packages/tui/src/colors.ts');
const oklab = await original('packages/tui/src/oklab.ts');
const colorMode = await original('packages/tui/src/terminal-image.ts');
const keybindings = await original('packages/tui/src/keybindings.ts');
export const { backgroundAnsi, colorToHex, colorToOklch, foregroundAnsi, indexedColor, mixColors,
  parseColor, rgbColor, styleTextWithAnsi, colorToOkhsl, okhslColor } = colors;
export const { oklabToOkhslLightness } = oklab;
export const { getTerminalColorMode } = colorMode;
export const { KeybindingsManager, TUI_KEYBINDINGS } = keybindings;
export const ORIGINAL_THEME_TUI_MODULES = Object.freeze({ colors, oklab, colorMode, keybindings });
