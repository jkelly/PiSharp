// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/tui/src/program-resume.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { createHash } from "node:crypto";
import { isAbsolute } from "node:path";
const PROGRAM_RESUME_TOKEN_ENV = "OSC7577_TOKEN";
export function takeProgramResumeToken(env = process.env) {
    const token = env[PROGRAM_RESUME_TOKEN_ENV];
    delete env[PROGRAM_RESUME_TOKEN_ENV];
    return token || undefined;
}
const MAX_SEQUENCE_BYTES = 2048;
const MAX_ARGV_ELEMENTS = 64;
export function formatProgramResume(command, sender) {
    if (!isReportableCommand(command)) return undefined;
    const pairs = [
        `argv=${command.argv.map(encodeText).join(",")}`
    ];
    if (command.cwd !== undefined) pairs.push(`cwd=${encodeText(command.cwd)}`);
    const sequence = formatReport(pairs, sender);
    return Buffer.byteLength(sequence, "utf8") <= MAX_SEQUENCE_BYTES ? sequence : undefined;
}
export function formatProgramResumeClear(sender) {
    return formatReport([
        "clear=1"
    ], sender);
}
function formatReport(pairs, sender) {
    const body = [
        `pid=${sender.pid}`,
        `time=${sender.time}`,
        ...pairs
    ].join(":");
    const mac = createHash("sha256").update(`${body}:${sender.token}`, "utf8").digest("hex");
    return `\x1b]7577;${body}:mac=${mac}\x1b\\`;
}
const CONTROL_CHARACTER = /[\u0000-\u001f\u007f-\u009f]/;
function isReportableText(text) {
    return text.isWellFormed() && !CONTROL_CHARACTER.test(text);
}
function isReportableCommand(command) {
    const [program] = command.argv;
    if (program === undefined || command.argv.length > MAX_ARGV_ELEMENTS) return false;
    const isBareName = program.length > 0 && !program.startsWith("-") && !/[\\/]/.test(program);
    if (!isBareName && !isAbsolute(program)) return false;
    if (command.cwd !== undefined && !(isAbsolute(command.cwd) && isReportableText(command.cwd))) return false;
    return command.argv.every(isReportableText);
}
function encodeText(text) {
    return Buffer.from(text, "utf8").toString("base64");
}
