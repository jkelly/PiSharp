// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/utils/string-enum.ts.
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
