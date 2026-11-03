// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/validation.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { compile } from "../../schema/index.mjs";
import { formats } from "../../schema/formats/index.mjs";
const validatorCache = new WeakMap();
function getValidator(schema) {
    const cached = validatorCache.get(schema);
    if (cached) {
        return cached;
    }
    const validator = compile(schema, {
        formats,
        coerce: true,
        unresolvedRefs: "reject"
    });
    validatorCache.set(schema, validator);
    return validator;
}
function formatValidationPath(pointer) {
    if (pointer === "") return "root";
    return pointer.slice(1).split("/").map((segment)=>segment.replace(/~1/g, "/").replace(/~0/g, "~")).join(".");
}
export function validateToolCall(tools, toolCall) {
    const tool = tools.find((t)=>t.name === toolCall.name);
    if (!tool) {
        throw new Error(`Tool "${toolCall.name}" not found`);
    }
    return validateToolArguments(tool, toolCall);
}
export function validateToolArguments(tool, toolCall) {
    if (toolCall.argumentsError) {
        throw new Error(`Invalid arguments for tool "${toolCall.name}": ${toolCall.argumentsError}`);
    }
    const result = getValidator(tool.parameters).parse(structuredClone(toolCall.arguments));
    if (result.ok) {
        return result.value;
    }
    const errors = result.errors.map((error)=>`  - ${formatValidationPath(error.path)}: ${error.message}`).join("\n");
    const errorMessage = `Validation failed for tool "${toolCall.name}":\n${errors}\n\nReceived arguments:\n${JSON.stringify(toolCall.arguments, null, 2)}`;
    throw new Error(errorMessage);
}
