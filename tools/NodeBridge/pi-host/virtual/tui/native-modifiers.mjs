// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/tui/src/native-modifiers.ts.
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
