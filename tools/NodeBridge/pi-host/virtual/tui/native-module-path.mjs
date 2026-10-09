// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/native-module-path.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
const moduleRequire = createRequire(import.meta.url);
const TUI_PACKAGE_NAME = "@earendil-works/pi-tui";
export function getNativeModuleCandidates(nativePath, options = {}) {
    const moduleDir = dirname(fileURLToPath(options.moduleUrl ?? import.meta.url));
    const candidates = [];
    try {
        const packageEntry = (options.resolvePackage ?? moduleRequire.resolve)(TUI_PACKAGE_NAME);
        candidates.push(join(dirname(packageEntry), "..", nativePath));
    } catch  {}
    candidates.push(join(moduleDir, "..", nativePath), join(moduleDir, nativePath), join(dirname(options.execPath ?? process.execPath), nativePath));
    return Array.from(new Set(candidates));
}
