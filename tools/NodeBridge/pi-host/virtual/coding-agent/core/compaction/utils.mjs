// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/compaction/utils.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { contentText } from "../../../pi-ai.mjs";
export function createFileOps() {
    return {
        read: new Set(),
        written: new Set(),
        edited: new Set()
    };
}
export function extractFileOpsFromMessage(message, fileOps) {
    if (message.role === "toolResult") {
        for (const call of message.nestedCalls?.calls ?? [])addFileOp(call.name, call.arguments, fileOps);
        return;
    }
    if (message.role !== "assistant") return;
    if (!("content" in message) || !Array.isArray(message.content)) return;
    for (const block of message.content){
        if (typeof block !== "object" || block === null) continue;
        if (!("type" in block) || block.type !== "toolCall") continue;
        if (!("arguments" in block) || !("name" in block)) continue;
        addFileOp(block.name, block.arguments, fileOps);
    }
}
function addFileOp(toolName, args, fileOps) {
    const path = typeof args?.path === "string" ? args.path : undefined;
    if (!path) return;
    switch(toolName){
        case "read":
            fileOps.read.add(path);
            break;
        case "write":
            fileOps.written.add(path);
            break;
        case "edit":
            fileOps.edited.add(path);
            break;
    }
}
export function computeFileLists(fileOps) {
    const modified = new Set([
        ...fileOps.edited,
        ...fileOps.written
    ]);
    const readOnly = [
        ...fileOps.read
    ].filter((f)=>!modified.has(f)).sort();
    const modifiedFiles = [
        ...modified
    ].sort();
    return {
        readFiles: readOnly,
        modifiedFiles
    };
}
export function formatFileOperations(readFiles, modifiedFiles) {
    const sections = [];
    if (readFiles.length > 0) {
        sections.push(`<read-files>\n${readFiles.join("\n")}\n</read-files>`);
    }
    if (modifiedFiles.length > 0) {
        sections.push(`<modified-files>\n${modifiedFiles.join("\n")}\n</modified-files>`);
    }
    if (sections.length === 0) return "";
    return `\n\n${sections.join("\n\n")}`;
}
const TOOL_RESULT_MAX_CHARS = 2000;
function truncateForSummary(text, maxChars) {
    if (text.length <= maxChars) return text;
    const truncatedChars = text.length - maxChars;
    return `${text.slice(0, maxChars)}\n\n[... ${truncatedChars} more characters truncated]`;
}
export function serializeConversation(messages) {
    const parts = [];
    for (const msg of messages){
        if (msg.role === "user") {
            const content = contentText(msg.content, "");
            if (content) parts.push(`[User]: ${content}`);
        } else if (msg.role === "assistant") {
            const thinkingParts = [];
            const toolCalls = [];
            for (const block of msg.content){
                if (block.type === "thinking") {
                    thinkingParts.push(block.thinking);
                } else if (block.type === "toolCall") {
                    const args = block.arguments;
                    const argsStr = Object.entries(args).map(([k, v])=>`${k}=${JSON.stringify(v)}`).join(", ");
                    toolCalls.push(`${block.name}(${argsStr})`);
                }
            }
            if (thinkingParts.length > 0) {
                parts.push(`[Assistant thinking]: ${thinkingParts.join("\n")}`);
            }
            if (msg.content.some((block)=>block.type === "text")) {
                parts.push(`[Assistant]: ${contentText(msg.content)}`);
            }
            if (toolCalls.length > 0) {
                parts.push(`[Assistant tool calls]: ${toolCalls.join("; ")}`);
            }
        } else if (msg.role === "toolResult") {
            const content = contentText(msg.content, "");
            if (content) {
                parts.push(`[Tool result]: ${truncateForSummary(content, TOOL_RESULT_MAX_CHARS)}`);
            }
        }
    }
    return parts.join("\n\n");
}
export const SUMMARIZATION_SYSTEM_PROMPT = `You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.

Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.`;
