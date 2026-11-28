// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/coding-agent/src/utils/text.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function splitBom(content) {
    return content.startsWith("\uFEFF") ? {
        bom: "\uFEFF",
        text: content.slice(1)
    } : {
        bom: "",
        text: content
    };
}
export function stripBom(content) {
    return splitBom(content).text;
}
