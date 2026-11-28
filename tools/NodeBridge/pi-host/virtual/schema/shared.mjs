// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/schema/src/shared.ts.
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
