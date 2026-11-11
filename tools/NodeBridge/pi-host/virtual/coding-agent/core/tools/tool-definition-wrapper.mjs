// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/tools/tool-definition-wrapper.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function wrapToolDefinition(definition, ctxFactory) {
    return {
        name: definition.name,
        label: definition.label,
        description: definition.description,
        parameters: definition.parameters,
        outputSchema: definition.outputSchema,
        constrainedSampling: definition.constrainedSampling,
        prepareArguments: definition.prepareArguments,
        executionMode: definition.executionMode,
        execute: (toolCallId, params, signal, onUpdate, ctx)=>definition.execute(toolCallId, params, signal, onUpdate, ctx ?? ctxFactory?.(toolCallId, signal))
    };
}
export function wrapToolDefinitions(definitions, ctxFactory) {
    return definitions.map((definition)=>wrapToolDefinition(definition, ctxFactory));
}
export function createToolDefinitionFromAgentTool(tool) {
    return {
        name: tool.name,
        label: tool.label,
        description: tool.description,
        parameters: tool.parameters,
        outputSchema: tool.outputSchema,
        constrainedSampling: tool.constrainedSampling,
        prepareArguments: tool.prepareArguments,
        executionMode: tool.executionMode,
        execute: async (toolCallId, params, signal, onUpdate)=>tool.execute(toolCallId, params, signal, onUpdate)
    };
}
