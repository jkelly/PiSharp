// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/key-id-schema.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { Key } from "../../pi-tui.mjs";
import { Type } from "../../typebox.mjs";
const modifiers = [
    "ctrl",
    "shift",
    "alt",
    "super"
];
const baseKeyPattern = `(?:[a-z0-9]|${Object.values(Key).flatMap((value)=>typeof value === "string" ? [
        value.replace(/[\\^$.*+?()[\]{}|]/g, "\\$&")
    ] : []).join("|")})`;
const duplicateModifierPattern = modifiers.map((modifier)=>`${modifier}\\+.*${modifier}\\+`).join("|");
export const KeyIdSchema = Type.Unsafe(Type.String({
    pattern: `^(?!.*(?:${duplicateModifierPattern}))(?:(?:${modifiers.join("|")})\\+){0,4}${baseKeyPattern}$`,
    description: "Key identifier with optional ctrl, shift, alt, or super modifiers."
}));
export function keybindingValueSchema(options) {
    return Type.Union([
        KeyIdSchema,
        Type.Array(KeyIdSchema)
    ], options);
}
export const KeybindingValueSchema = keybindingValueSchema();
