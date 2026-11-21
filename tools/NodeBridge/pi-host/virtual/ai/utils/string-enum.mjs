// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/src/utils/string-enum.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function StringEnum(values, options) {
    return {
        type: "string",
        enum: values,
        ...options?.description && {
            description: options.description
        },
        ...options?.default && {
            default: options.default
        }
    };
}
