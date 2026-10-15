// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/utils/text.ts.
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
