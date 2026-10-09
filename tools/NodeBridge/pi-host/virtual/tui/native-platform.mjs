// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/native-platform.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { createRequire } from "node:module";
import * as path from "node:path";
import { getNativeModuleCandidates } from "./native-module-path.mjs";
const cjsRequire = createRequire(import.meta.url);
const helpers = new Map();
function loadNativePlatformHelper(platform, suffix = "") {
    const arch = process.arch;
    if (arch !== "x64" && arch !== "arm64") return undefined;
    const nativePath = path.join("native", platform, "prebuilds", `${platform}-${arch}`, `${platform}-platform${suffix}.node`);
    if (helpers.has(nativePath)) return helpers.get(nativePath);
    for (const modulePath of getNativeModuleCandidates(nativePath)){
        try {
            const helper = cjsRequire(modulePath);
            if (typeof helper?.getText === "function" && typeof helper.getImage === "function") {
                helpers.set(nativePath, helper);
                return helper;
            }
        } catch  {}
    }
    helpers.set(nativePath, undefined);
    return undefined;
}
export function getNativePlatformHelper() {
    if (process.platform !== "darwin" && process.platform !== "win32") return undefined;
    return loadNativePlatformHelper(process.platform);
}
export function getNativeClipboard() {
    if (process.platform !== "linux") return getNativePlatformHelper();
    if (!process.env.DISPLAY) return undefined;
    return loadNativePlatformHelper("linux", "-x11");
}
