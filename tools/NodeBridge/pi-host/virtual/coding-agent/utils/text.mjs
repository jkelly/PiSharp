// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/utils/text.ts.
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
