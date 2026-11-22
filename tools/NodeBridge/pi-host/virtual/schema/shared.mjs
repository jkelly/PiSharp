// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/schema/src/shared.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function sharedRegex(source) {
    const hit = regexes.get(source)?.deref();
    if (hit !== undefined) return hit;
    let re;
    try {
        re = new RegExp(source, "u");
    } catch  {
        re = new RegExp(source);
    }
    regexes.set(source, new WeakRef(re));
    regexCleanup.register(re, source);
    return re;
}
const regexes = new Map();
const regexCleanup = new FinalizationRegistry((source)=>{
    if (regexes.get(source)?.deref() === undefined) regexes.delete(source);
});
export function sharedRegexCount() {
    return regexes.size;
}
