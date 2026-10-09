// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/program-status.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const PROGRAM_STATUS_QUERY = "\x1b]7501;?\x1b\\";
export function isProgramStatusReply(sequence) {
    return /^\x1b\]7501;\?[^\x07\x1b]*(?:\x07|\x1b\\)$/.test(sequence);
}
const APP_PATTERN = /^[A-Za-z0-9_.+-]{1,32}$/;
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f]+/g;
const MAX_MESSAGE_BYTES = 2048;
function truncateUtf8(text, maxBytes) {
    if (Buffer.byteLength(text, "utf8") <= maxBytes) return text;
    let bytes = 0;
    let end = 0;
    for (const char of text){
        const size = Buffer.byteLength(char, "utf8");
        if (bytes + size > maxBytes) break;
        bytes += size;
        end += char.length;
    }
    return text.slice(0, end);
}
export function formatProgramStatus(status) {
    const pairs = [
        `state=${status.state}`
    ];
    if (status.app !== undefined && APP_PATTERN.test(status.app)) pairs.push(`app=${status.app}`);
    if (status.state === "blocked" && status.kind) pairs.push(`kind=${status.kind}`);
    const message = truncateUtf8((status.message ?? "").replace(CONTROL_CHARACTERS, " ").trim(), MAX_MESSAGE_BYTES);
    if (message) pairs.push(`msg=${Buffer.from(message, "utf8").toString("base64")}`);
    return `\x1b]7501;${pairs.join(":")}\x1b\\`;
}
