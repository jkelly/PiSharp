// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/extensions/wrapper.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { wrapToolDefinition } from "../tools/tool-definition-wrapper.mjs";
export function wrapRegisteredTool(registeredTool, runner) {
    return wrapToolDefinition(registeredTool.definition, (toolCallId, signal)=>runner.createToolContext(toolCallId, signal));
}
export function wrapRegisteredTools(registeredTools, runner) {
    return registeredTools.map((tool)=>wrapRegisteredTool(tool, runner));
}
