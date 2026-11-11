// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/native-modifiers.ts.
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
