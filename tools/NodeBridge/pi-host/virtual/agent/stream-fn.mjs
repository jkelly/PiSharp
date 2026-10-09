// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/stream-fn.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
let defaultStreamFn;
export function setDefaultStreamFn(streamFn) {
    defaultStreamFn = streamFn;
}
export function getDefaultStreamFn() {
    if (!defaultStreamFn) {
        throw new Error("No default stream function configured. Pass streamFn explicitly or call setDefaultStreamFn().");
    }
    return defaultStreamFn;
}
