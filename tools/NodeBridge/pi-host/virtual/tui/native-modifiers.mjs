// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/tui/src/native-modifiers.ts.
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
