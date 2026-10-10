// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/source-info.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const BUILTIN_PATH_PREFIX = "builtin:";
export function getSyntheticPathSource(path) {
    if (path.startsWith(BUILTIN_PATH_PREFIX)) return "builtin";
    if (path.startsWith("<") && path.endsWith(">")) return path.slice(1, -1).split(":")[0] || "temporary";
    return undefined;
}
export function isSyntheticPath(path) {
    return path.startsWith(BUILTIN_PATH_PREFIX) || path.startsWith("<");
}
export function createSourceInfo(path, metadata) {
    return {
        path,
        source: metadata.source,
        scope: metadata.scope,
        origin: metadata.origin,
        baseDir: metadata.baseDir
    };
}
export function createSyntheticSourceInfo(path, options) {
    return {
        path,
        source: options.source,
        scope: options.scope ?? "temporary",
        origin: options.origin ?? "top-level",
        baseDir: options.baseDir
    };
}
