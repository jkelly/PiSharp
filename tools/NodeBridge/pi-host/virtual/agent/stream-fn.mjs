// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/agent/src/stream-fn.ts.
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
