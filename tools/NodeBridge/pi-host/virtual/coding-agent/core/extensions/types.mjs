// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function defineTool(tool) {
    return tool;
}
export function isBashToolResult(e) {
    return e.toolName === "bash";
}
export function isPowerShellToolResult(e) {
    return e.toolName === "powershell";
}
export function isReadToolResult(e) {
    return e.toolName === "read";
}
export function isEditToolResult(e) {
    return e.toolName === "edit";
}
export function isWriteToolResult(e) {
    return e.toolName === "write";
}
export function isGrepToolResult(e) {
    return e.toolName === "grep";
}
export function isFindToolResult(e) {
    return e.toolName === "find";
}
export function isLsToolResult(e) {
    return e.toolName === "ls";
}
export function isToolCallEventType(toolName, event) {
    return event.toolName === toolName;
}
