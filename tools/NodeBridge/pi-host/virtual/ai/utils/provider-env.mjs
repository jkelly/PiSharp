// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/provider-env.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
let procEnvCache = null;
function getBunSandboxEnvValue(name) {
    if (typeof process === "undefined" || !process.versions?.bun || Object.keys(process.env).length > 0) {
        return undefined;
    }
    if (procEnvCache === null) {
        procEnvCache = new Map();
        try {
            const { readFileSync } = require("node:fs");
            const data = readFileSync("/proc/self/environ", "utf-8");
            for (const entry of data.split("\0")){
                const idx = entry.indexOf("=");
                if (idx > 0) {
                    procEnvCache.set(entry.slice(0, idx), entry.slice(idx + 1));
                }
            }
        } catch  {}
    }
    return procEnvCache.get(name);
}
export function getProviderEnvValue(name, env) {
    return env?.[name] || (typeof process !== "undefined" ? process.env[name] : undefined) || getBunSandboxEnvValue(name) || undefined;
}
