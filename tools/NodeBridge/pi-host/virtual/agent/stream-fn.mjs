// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/agent/src/stream-fn.ts.
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
