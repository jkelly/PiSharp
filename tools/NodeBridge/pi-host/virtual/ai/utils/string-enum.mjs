// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/utils/string-enum.ts.
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
