// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/text.ts.
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
