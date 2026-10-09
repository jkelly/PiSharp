// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/auth/context.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
const importNodeModule = (specifier)=>import(specifier);
function getProcessEnv() {
    const proc = globalThis.process;
    return proc?.env;
}
export function defaultProviderAuthContext() {
    return {
        async env (name) {
            const value = getProcessEnv()?.[name];
            return typeof value === "string" && value.trim().length > 0 ? value : undefined;
        },
        async fileExists (path) {
            try {
                const fs = await importNodeModule("node:fs/promises");
                let resolved = path;
                if (resolved.startsWith("~")) {
                    const os = await importNodeModule("node:os");
                    resolved = os.homedir() + resolved.slice(1);
                }
                await fs.access(resolved);
                return true;
            } catch  {
                return false;
            }
        }
    };
}
