// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/wrapper.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { wrapToolDefinition } from "../tools/tool-definition-wrapper.mjs";
export function wrapRegisteredTool(registeredTool, runner) {
    return wrapToolDefinition(registeredTool.definition, (toolCallId, signal)=>runner.createToolContext(toolCallId, signal));
}
export function wrapRegisteredTools(registeredTools, runner) {
    return registeredTools.map((tool)=>wrapRegisteredTool(tool, runner));
}
