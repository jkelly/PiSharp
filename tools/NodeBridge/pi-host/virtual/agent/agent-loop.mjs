// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { EventStream, getCurrentTools, getToolStateChanges, normalizeContext, toToolDeclaration, validateToolArguments } from "../pi-ai.mjs";
import { getDefaultStreamFn } from "./stream-fn.mjs";
export function agentLoop(prompts, context, config, signal, streamFn) {
    const stream = createAgentStream();
    void runAgentLoop(prompts, context, config, async (event)=>{
        stream.push(event);
    }, signal, streamFn).then((messages)=>{
        stream.end(messages);
    });
    return stream;
}
export function agentLoopContinue(context, config, signal, streamFn) {
    if (context.messages.length === 0) {
        throw new Error("Cannot continue: no messages in context");
    }
    if (context.messages[context.messages.length - 1].role === "assistant") {
        throw new Error("Cannot continue from message role: assistant");
    }
    const stream = createAgentStream();
    void runAgentLoopContinue(context, config, async (event)=>{
        stream.push(event);
    }, signal, streamFn).then((messages)=>{
        stream.end(messages);
    });
    return stream;
}
export async function runAgentLoop(prompts, context, config, emit, signal, streamFn) {
    const initialMessages = declareToolChanges(context, prompts);
    const newMessages = [
        ...initialMessages
    ];
    const currentContext = {
        ...context,
        messages: [
            ...context.messages,
            ...initialMessages
        ]
    };
    await emit({
        type: "agent_start"
    });
    await emit({
        type: "turn_start"
    });
    for (const message of initialMessages){
        await emit({
            type: "message_start",
            message
        });
        await emit({
            type: "message_end",
            message
        });
    }
    await runLoop(currentContext, newMessages, config, signal, emit, streamFn ?? getDefaultStreamFn());
    return newMessages;
}
export async function runAgentLoopContinue(context, config, emit, signal, streamFn) {
    if (context.messages.length === 0) {
        throw new Error("Cannot continue: no messages in context");
    }
    if (context.messages[context.messages.length - 1].role === "assistant") {
        throw new Error("Cannot continue from message role: assistant");
    }
    const newMessages = [];
    const currentContext = {
        ...context
    };
    await emit({
        type: "agent_start"
    });
    await emit({
        type: "turn_start"
    });
    await runLoop(currentContext, newMessages, config, signal, emit, streamFn ?? getDefaultStreamFn());
    return newMessages;
}
function createAgentStream() {
    return new EventStream((event)=>event.type === "agent_end", (event)=>event.type === "agent_end" ? event.messages : []);
}
async function runLoop(initialContext, newMessages, initialConfig, signal, emit, streamFunction) {
    let currentContext = initialContext;
    let config = initialConfig;
    let lastCompletedTurn;
    let explicitContinuation = false;
    let pendingMessages = await config.getSteeringMessages?.() || [];
    while(true){
        let hasMoreToolCalls = true;
        while(hasMoreToolCalls || pendingMessages.length > 0){
            let preparedMessages = [];
            if (lastCompletedTurn) {
                const nextTurnSnapshot = await config.prepareNextTurn?.(lastCompletedTurn);
                if (nextTurnSnapshot) {
                    currentContext = nextTurnSnapshot.context ?? currentContext;
                    preparedMessages = nextTurnSnapshot.messages ?? [];
                    config = {
                        ...config,
                        model: nextTurnSnapshot.model ?? config.model,
                        reasoning: nextTurnSnapshot.thinkingLevel === undefined ? config.reasoning : nextTurnSnapshot.thinkingLevel === "off" ? undefined : nextTurnSnapshot.thinkingLevel
                    };
                }
                if (pendingMessages.length === 0) {
                    pendingMessages = await config.getSteeringMessages?.() || [];
                }
                await emit({
                    type: "turn_start"
                });
            }
            for (const message of declareToolChanges(currentContext, [
                ...preparedMessages,
                ...pendingMessages
            ])){
                await emit({
                    type: "message_start",
                    message
                });
                await emit({
                    type: "message_end",
                    message
                });
                currentContext.messages.push(message);
                newMessages.push(message);
            }
            pendingMessages = [];
            const requestUpdate = await config.prepareRequest?.({
                context: currentContext,
                model: config.model,
                thinkingLevel: config.reasoning ?? "off"
            }, signal);
            if (requestUpdate) {
                currentContext = requestUpdate.context ?? currentContext;
                config = {
                    ...config,
                    model: requestUpdate.model ?? config.model,
                    reasoning: requestUpdate.thinkingLevel === undefined ? config.reasoning : requestUpdate.thinkingLevel === "off" ? undefined : requestUpdate.thinkingLevel
                };
            }
            const message = await streamAssistantResponse(currentContext, config, signal, emit, streamFunction);
            newMessages.push(message);
            if (message.stopReason === "error" || message.stopReason === "aborted") {
                lastCompletedTurn = {
                    message,
                    toolResults: [],
                    context: currentContext,
                    newMessages
                };
                await config.finishTurn?.(lastCompletedTurn, signal);
                await emit({
                    type: "turn_end",
                    message,
                    toolResults: []
                });
                await emit({
                    type: "agent_end",
                    messages: newMessages
                });
                return;
            }
            const toolCalls = message.content.filter((c)=>c.type === "toolCall");
            const toolResults = [];
            hasMoreToolCalls = false;
            if (toolCalls.length > 0) {
                const executedToolBatch = message.stopReason === "length" ? await failToolCallsFromTruncatedMessage(toolCalls, emit) : await executeToolCalls(currentContext, message, config, signal, emit);
                toolResults.push(...executedToolBatch.messages);
                hasMoreToolCalls = !executedToolBatch.terminate;
                for (const result of toolResults){
                    currentContext.messages.push(result);
                    newMessages.push(result);
                }
            }
            lastCompletedTurn = {
                message,
                toolResults,
                context: currentContext,
                newMessages
            };
            const decision = await config.finishTurn?.(lastCompletedTurn, signal);
            await emit({
                type: "turn_end",
                message,
                toolResults
            });
            if (decision?.action === "end") {
                await emit({
                    type: "agent_end",
                    messages: newMessages
                });
                return;
            }
            explicitContinuation = decision?.action === "continue";
            pendingMessages = await config.getSteeringMessages?.() || [];
            if (hasMoreToolCalls || pendingMessages.length > 0) {
                explicitContinuation = false;
            }
        }
        const followUpMessages = await config.getFollowUpMessages?.() || [];
        if (followUpMessages.length > 0) {
            explicitContinuation = false;
            pendingMessages = followUpMessages;
            continue;
        }
        if (explicitContinuation) {
            explicitContinuation = false;
            continue;
        }
        break;
    }
    await emit({
        type: "agent_end",
        messages: newMessages
    });
}
function declareToolChanges(context, pendingMessages) {
    let systemIndex = -1;
    for(let i = pendingMessages.length - 1; i >= 0; i--){
        if (pendingMessages[i].role === "system") {
            systemIndex = i;
            break;
        }
    }
    const pending = pendingMessages[systemIndex];
    const baseline = pending ? pendingMessages.map((message, index)=>index === systemIndex ? withToolChanges(pending, NO_CHANGES) : message) : pendingMessages;
    const changes = getToolStateChanges(getCurrentTools([
        ...context.messages,
        ...baseline
    ]), (context.tools ?? []).map(toToolDeclaration));
    const unchanged = changes.toolsAdded.length === 0 && changes.toolsRemoved.length === 0;
    if (pending) {
        if (unchanged && !pending.toolsAdded?.length && !pending.toolsRemoved?.length) return pendingMessages;
        return baseline.map((message, index)=>index === systemIndex ? withToolChanges(pending, changes) : message);
    }
    if (unchanged) return pendingMessages;
    const update = withToolChanges({
        role: "system",
        content: "",
        timestamp: Date.now()
    }, changes);
    const insertIndex = pendingMessages.findIndex((message)=>message.role !== "system");
    const index = insertIndex === -1 ? pendingMessages.length : insertIndex;
    return [
        ...pendingMessages.slice(0, index),
        update,
        ...pendingMessages.slice(index)
    ];
}
const NO_CHANGES = {
    toolsAdded: [],
    toolsRemoved: []
};
function withToolChanges(message, { toolsAdded, toolsRemoved }) {
    const { toolsAdded: _added, toolsRemoved: _removed, ...rest } = message;
    return {
        ...rest,
        ...toolsAdded.length > 0 ? {
            toolsAdded
        } : {},
        ...toolsRemoved.length > 0 ? {
            toolsRemoved
        } : {}
    };
}
async function streamAssistantResponse(context, config, signal, emit, streamFunction) {
    let messages = context.messages;
    if (config.transformContext) {
        messages = await config.transformContext(messages, signal);
    }
    const llmMessages = await config.convertToLlm(messages);
    const llmContext = normalizeContext({
        messages: llmMessages
    });
    const resolvedApiKey = (config.getApiKey ? await config.getApiKey(config.model.provider) : undefined) || config.apiKey;
    const response = await streamFunction(config.model, llmContext, {
        ...config,
        apiKey: resolvedApiKey,
        signal
    });
    const result = async ()=>Object.assign(await response.result(), {
            thinkingLevel: config.reasoning ?? "off"
        });
    let partialMessage = null;
    let addedPartial = false;
    for await (const event of response){
        switch(event.type){
            case "start":
                partialMessage = event.partial;
                context.messages.push(partialMessage);
                addedPartial = true;
                await emit({
                    type: "message_start",
                    message: {
                        ...partialMessage
                    }
                });
                break;
            case "text_start":
            case "text_delta":
            case "text_end":
            case "thinking_start":
            case "thinking_delta":
            case "thinking_end":
            case "toolcall_start":
            case "toolcall_delta":
            case "toolcall_end":
                if (partialMessage) {
                    partialMessage = event.partial;
                    context.messages[context.messages.length - 1] = partialMessage;
                    await emit({
                        type: "message_update",
                        assistantMessageEvent: event,
                        message: {
                            ...partialMessage
                        }
                    });
                }
                break;
            case "done":
            case "error":
                {
                    const finalMessage = await result();
                    if (addedPartial) {
                        context.messages[context.messages.length - 1] = finalMessage;
                    } else {
                        context.messages.push(finalMessage);
                    }
                    if (!addedPartial) {
                        await emit({
                            type: "message_start",
                            message: {
                                ...finalMessage
                            }
                        });
                    }
                    await emit({
                        type: "message_end",
                        message: finalMessage
                    });
                    return finalMessage;
                }
        }
    }
    const finalMessage = await result();
    if (addedPartial) {
        context.messages[context.messages.length - 1] = finalMessage;
    } else {
        context.messages.push(finalMessage);
        await emit({
            type: "message_start",
            message: {
                ...finalMessage
            }
        });
    }
    await emit({
        type: "message_end",
        message: finalMessage
    });
    return finalMessage;
}
async function failToolCallsFromTruncatedMessage(toolCalls, emit) {
    const messages = [];
    for (const toolCall of toolCalls){
        await emit({
            type: "tool_execution_start",
            toolCallId: toolCall.id,
            toolName: toolCall.name,
            args: toolCall.arguments
        });
        const finalized = {
            toolCall,
            result: createErrorToolResult(`Tool call "${toolCall.name}" was not executed: the response hit the output token limit, so its arguments may be truncated. Re-issue the tool call with complete arguments.`),
            isError: true
        };
        await emitToolExecutionEnd(finalized, emit);
        const toolResultMessage = createToolResultMessage(finalized);
        await emitToolResultMessage(toolResultMessage, emit);
        messages.push(toolResultMessage);
    }
    return {
        messages,
        terminate: false
    };
}
async function executeToolCalls(currentContext, assistantMessage, config, signal, emit) {
    const toolCalls = assistantMessage.content.filter((c)=>c.type === "toolCall");
    const hasSequentialToolCall = toolCalls.some((tc)=>currentContext.tools?.find((t)=>t.name === tc.name)?.executionMode === "sequential");
    if (config.toolExecution === "sequential" || hasSequentialToolCall) {
        return executeToolCallsSequential(currentContext, assistantMessage, toolCalls, config, signal, emit);
    }
    return executeToolCallsParallel(currentContext, assistantMessage, toolCalls, config, signal, emit);
}
async function executeToolCallsSequential(currentContext, assistantMessage, toolCalls, config, signal, emit) {
    const finalizedCalls = [];
    const messages = [];
    for (const toolCall of toolCalls){
        await emit({
            type: "tool_execution_start",
            toolCallId: toolCall.id,
            toolName: toolCall.name,
            args: toolCall.arguments
        });
        const preparation = await prepareToolCall(currentContext, assistantMessage, toolCall, config, signal);
        let finalized;
        if (preparation.kind === "immediate") {
            finalized = {
                toolCall,
                result: preparation.result,
                isError: preparation.isError
            };
        } else {
            const executed = await executePreparedToolCall(preparation, signal, emitToolExecutionUpdate(toolCall, emit));
            finalized = await finalizeExecutedToolCall(currentContext, assistantMessage, preparation, executed, config, signal);
        }
        await emitToolExecutionEnd(finalized, emit);
        const toolResultMessage = createToolResultMessage(finalized);
        await emitToolResultMessage(toolResultMessage, emit);
        finalizedCalls.push(finalized);
        messages.push(toolResultMessage);
        if (signal?.aborted) {
            break;
        }
    }
    return {
        messages,
        terminate: shouldTerminateToolBatch(finalizedCalls)
    };
}
async function executeToolCallsParallel(currentContext, assistantMessage, toolCalls, config, signal, emit) {
    const finalizedCalls = [];
    for (const toolCall of toolCalls){
        await emit({
            type: "tool_execution_start",
            toolCallId: toolCall.id,
            toolName: toolCall.name,
            args: toolCall.arguments
        });
        const preparation = await prepareToolCall(currentContext, assistantMessage, toolCall, config, signal);
        if (preparation.kind === "immediate") {
            const finalized = {
                toolCall,
                result: preparation.result,
                isError: preparation.isError
            };
            await emitToolExecutionEnd(finalized, emit);
            finalizedCalls.push(finalized);
            if (signal?.aborted) {
                break;
            }
            continue;
        }
        finalizedCalls.push(async ()=>{
            if (signal?.aborted) {
                const finalized = {
                    toolCall,
                    result: createErrorToolResult("Operation aborted"),
                    isError: true
                };
                await emitToolExecutionEnd(finalized, emit);
                return finalized;
            }
            const executed = await executePreparedToolCall(preparation, signal, emitToolExecutionUpdate(toolCall, emit));
            const finalized = await finalizeExecutedToolCall(currentContext, assistantMessage, preparation, executed, config, signal);
            await emitToolExecutionEnd(finalized, emit);
            return finalized;
        });
        if (signal?.aborted) {
            break;
        }
    }
    const orderedFinalizedCalls = await Promise.all(finalizedCalls.map((entry)=>typeof entry === "function" ? entry() : Promise.resolve(entry)));
    const messages = [];
    for (const finalized of orderedFinalizedCalls){
        const toolResultMessage = createToolResultMessage(finalized);
        await emitToolResultMessage(toolResultMessage, emit);
        messages.push(toolResultMessage);
    }
    return {
        messages,
        terminate: shouldTerminateToolBatch(orderedFinalizedCalls)
    };
}
function shouldTerminateToolBatch(finalizedCalls) {
    return finalizedCalls.length > 0 && finalizedCalls.every((finalized)=>finalized.result.terminate === true);
}
function prepareToolCallArguments(tool, toolCall) {
    if (!tool.prepareArguments) {
        return toolCall;
    }
    const preparedArguments = tool.prepareArguments(toolCall.arguments);
    if (preparedArguments === toolCall.arguments) {
        return toolCall;
    }
    return {
        ...toolCall,
        arguments: preparedArguments
    };
}
async function prepareToolCall(currentContext, assistantMessage, toolCall, config, signal, tools = currentContext.tools ?? []) {
    const tool = tools.find((t)=>t.name === toolCall.name);
    if (!tool) {
        return {
            kind: "immediate",
            result: createErrorToolResult(`Tool ${toolCall.name} not found`),
            isError: true
        };
    }
    try {
        const preparedToolCall = prepareToolCallArguments(tool, toolCall);
        const validatedArgs = validateToolArguments(tool, preparedToolCall);
        if (config.beforeToolCall) {
            const beforeResult = await config.beforeToolCall({
                assistantMessage,
                toolCall,
                args: validatedArgs,
                context: currentContext
            }, signal);
            if (signal?.aborted) {
                return {
                    kind: "immediate",
                    result: createErrorToolResult("Operation aborted"),
                    isError: true
                };
            }
            if (beforeResult?.block) {
                const result = createErrorToolResult(beforeResult.reason || "Tool execution was blocked");
                if (beforeResult.terminate === true) {
                    result.terminate = true;
                }
                return {
                    kind: "immediate",
                    result,
                    isError: true
                };
            }
        }
        if (signal?.aborted) {
            return {
                kind: "immediate",
                result: createErrorToolResult("Operation aborted"),
                isError: true
            };
        }
        return {
            kind: "prepared",
            toolCall,
            tool,
            args: validatedArgs
        };
    } catch (error) {
        return {
            kind: "immediate",
            result: createErrorToolResult(error instanceof Error ? error.message : String(error)),
            isError: true
        };
    }
}
function emitToolExecutionUpdate(toolCall, emit) {
    return (partialResult)=>emit({
            type: "tool_execution_update",
            toolCallId: toolCall.id,
            toolName: toolCall.name,
            args: toolCall.arguments,
            partialResult
        });
}
export async function runToolCall(toolCall, options) {
    const { assistantMessage, context, signal } = options;
    const preparation = await prepareToolCall(context, assistantMessage, toolCall, options, signal, options.tools);
    if (preparation.kind === "immediate") {
        return {
            toolCall,
            result: preparation.result,
            isError: preparation.isError
        };
    }
    const executed = await executePreparedToolCall(preparation, signal, options.onUpdate ?? (()=>{}));
    return finalizeExecutedToolCall(context, assistantMessage, preparation, executed, options, signal);
}
async function executePreparedToolCall(prepared, signal, onUpdate) {
    const updateEvents = [];
    let acceptingUpdates = true;
    const startedAt = performance.now();
    const elapsed = ()=>Math.round(performance.now() - startedAt);
    try {
        const result = await prepared.tool.execute(prepared.toolCall.id, prepared.args, signal, (partialResult)=>{
            if (!acceptingUpdates) return;
            updateEvents.push(Promise.resolve(onUpdate(partialResult)));
        });
        const durationMs = elapsed();
        acceptingUpdates = false;
        await Promise.all(updateEvents);
        return {
            result,
            isError: result.isError === true,
            durationMs
        };
    } catch (error) {
        const durationMs = elapsed();
        acceptingUpdates = false;
        await Promise.all(updateEvents);
        return {
            result: createErrorToolResult(error instanceof Error ? error.message : String(error)),
            isError: true,
            durationMs
        };
    } finally{
        acceptingUpdates = false;
    }
}
async function finalizeExecutedToolCall(currentContext, assistantMessage, prepared, executed, config, signal) {
    let result = executed.result;
    let isError = executed.isError;
    if (config.afterToolCall) {
        try {
            const afterResult = await config.afterToolCall({
                assistantMessage,
                toolCall: prepared.toolCall,
                args: prepared.args,
                result,
                isError,
                context: currentContext
            }, signal);
            if (afterResult) {
                const structuredContent = afterResult.structuredContent ?? (afterResult.content ? undefined : result.structuredContent);
                result = {
                    ...result,
                    content: afterResult.content ?? result.content,
                    details: afterResult.details ?? result.details,
                    usage: afterResult.usage ?? result.usage,
                    terminate: afterResult.terminate ?? result.terminate
                };
                if (structuredContent === undefined) delete result.structuredContent;
                else result.structuredContent = structuredContent;
                isError = afterResult.isError ?? isError;
            }
        } catch (error) {
            result = createErrorToolResult(error instanceof Error ? error.message : String(error));
            isError = true;
        }
    }
    return {
        toolCall: prepared.toolCall,
        result,
        isError,
        durationMs: executed.durationMs
    };
}
function createErrorToolResult(message) {
    return {
        content: [
            {
                type: "text",
                text: message
            }
        ],
        details: {}
    };
}
async function emitToolExecutionEnd(finalized, emit) {
    await emit({
        type: "tool_execution_end",
        toolCallId: finalized.toolCall.id,
        toolName: finalized.toolCall.name,
        result: finalized.result,
        isError: finalized.isError,
        ...finalized.durationMs === undefined ? {} : {
            durationMs: finalized.durationMs
        }
    });
}
function createToolResultMessage(finalized) {
    return {
        role: "toolResult",
        toolCallId: finalized.toolCall.id,
        toolName: finalized.toolCall.name,
        content: finalized.result.content ?? [],
        details: finalized.result.details,
        usage: finalized.result.usage,
        isError: finalized.isError,
        ...finalized.durationMs === undefined ? {} : {
            durationMs: finalized.durationMs
        },
        timestamp: Date.now()
    };
}
async function emitToolResultMessage(toolResultMessage, emit) {
    await emit({
        type: "message_start",
        message: toolResultMessage
    });
    await emit({
        type: "message_end",
        message: toolResultMessage
    });
}
