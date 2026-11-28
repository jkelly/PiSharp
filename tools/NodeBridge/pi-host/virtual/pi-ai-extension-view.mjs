// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/coding-agent/src/core/extensions/pi-ai-extension-view.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { Type } from "./typebox.mjs";
export * from "./pi-ai.mjs";
export { Type };
export function StringEnum(values, options) {
    return Type.Unsafe({
        type: "string",
        enum: values,
        ...options?.description && {
            description: options.description
        },
        ...options?.default && {
            default: options.default
        }
    });
}
