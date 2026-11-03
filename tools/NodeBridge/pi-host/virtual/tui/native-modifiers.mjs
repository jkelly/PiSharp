// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/tui/src/native-modifiers.ts.
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
