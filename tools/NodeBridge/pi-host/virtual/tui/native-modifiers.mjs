// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/tui/src/native-modifiers.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { getNativePlatformHelper } from "./native-platform.mjs";
export function isNativeModifierPressed(key) {
    const helper = getNativePlatformHelper();
    if (!helper?.isModifierPressed) return false;
    try {
        return helper.isModifierPressed(key) === true;
    } catch  {
        return false;
    }
}
