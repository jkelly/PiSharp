// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/utils/text.ts.
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
