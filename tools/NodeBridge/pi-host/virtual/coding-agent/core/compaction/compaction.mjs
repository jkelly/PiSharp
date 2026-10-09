// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/compaction/compaction.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { contentText, getCurrentSystemMessage, normalizeContext, retryAssistantCall, uuidv7 } from "../../../pi-ai.mjs";
import { completeSimple } from "../../../pi-ai.mjs";
import { convertToLlm } from "../messages.mjs";
import { buildSessionProjection, sessionEntryToContextMessages } from "../session-manager.mjs";
import { combineUsage } from "../usage-totals.mjs";
import { computeFileLists, createFileOps, extractFileOpsFromMessage, formatFileOperations, SUMMARIZATION_SYSTEM_PROMPT, serializeConversation } from "./utils.mjs";
function extractFileOperations(messages, entries, prevCompactionIndex) {
    const fileOps = createFileOps();
    if (prevCompactionIndex >= 0) {
        const prevCompaction = entries[prevCompactionIndex];
        if (!prevCompaction.fromHook && prevCompaction.details) {
            const details = prevCompaction.details;
            if (Array.isArray(details.readFiles)) {
                for (const f of details.readFiles)fileOps.read.add(f);
            }
            if (Array.isArray(details.modifiedFiles)) {
                for (const f of details.modifiedFiles)fileOps.edited.add(f);
            }
        }
    }
    for (const msg of messages){
        extractFileOpsFromMessage(msg, fileOps);
    }
    return fileOps;
}
function getMessagesFromProjectedEntryForCompaction(entry) {
    if (entry.sourceEntry.type === "compaction") return [];
    return entry.messages.filter((message)=>message.role !== "system");
}
export const DEFAULT_COMPACTION_SETTINGS = {
    enabled: true,
    reserveTokens: 16384,
    keepRecentTokens: 20000
};
export function calculateContextTokens(usage) {
    return usage.totalTokens || usage.input + usage.output + usage.cacheRead + usage.cacheWrite;
}
function getAssistantUsage(msg) {
    if (msg.role === "assistant" && "usage" in msg) {
        const assistantMsg = msg;
        if (assistantMsg.stopReason !== "aborted" && assistantMsg.stopReason !== "error" && assistantMsg.usage && calculateContextTokens(assistantMsg.usage) > 0) {
            return assistantMsg.usage;
        }
    }
    return undefined;
}
export function getLastAssistantUsage(entries) {
    for(let i = entries.length - 1; i >= 0; i--){
        const entry = entries[i];
        if (entry.type === "message") {
            const usage = getAssistantUsage(entry.message);
            if (usage) return usage;
        }
    }
    return undefined;
}
function getLastAssistantUsageInfo(messages) {
    for(let i = messages.length - 1; i >= 0; i--){
        const usage = getAssistantUsage(messages[i]);
        if (usage) return {
            usage,
            index: i
        };
    }
    return undefined;
}
export function estimateContextTokens(messages) {
    const usageInfo = getLastAssistantUsageInfo(messages);
    if (!usageInfo) {
        let estimated = 0;
        for (const message of messages){
            estimated += estimateTokens(message);
        }
        return {
            tokens: estimated,
            usageTokens: 0,
            trailingTokens: estimated,
            lastUsageIndex: null
        };
    }
    const usageTokens = calculateContextTokens(usageInfo.usage);
    let trailingTokens = 0;
    for(let i = usageInfo.index + 1; i < messages.length; i++){
        trailingTokens += estimateTokens(messages[i]);
    }
    return {
        tokens: usageTokens + trailingTokens,
        usageTokens,
        trailingTokens,
        lastUsageIndex: usageInfo.index
    };
}
export function estimateProjectedContextTokens(projection, branchEntries) {
    const estimate = estimateContextTokens(projection.messages);
    if (estimate.lastUsageIndex !== null) {
        let projectedMessageIndex = 0;
        let usageEntryId;
        for (const entry of projection.entries){
            const nextMessageIndex = projectedMessageIndex + entry.messages.length;
            if (estimate.lastUsageIndex < nextMessageIndex) {
                usageEntryId = entry.sourceEntry.id;
                break;
            }
            projectedMessageIndex = nextMessageIndex;
        }
        const usageEntryIndex = usageEntryId ? branchEntries.findIndex((entry)=>entry.id === usageEntryId) : -1;
        let latestInvalidatingEntryIndex = -1;
        for(let i = branchEntries.length - 1; i >= 0; i--){
            const entry = branchEntries[i];
            if (entry.type === "context_edit" || entry.type === "compaction") {
                latestInvalidatingEntryIndex = i;
                break;
            }
        }
        if (usageEntryIndex > latestInvalidatingEntryIndex) return estimate;
    }
    const currentSystem = getCurrentSystemMessage(projection.messages);
    let tokens = currentSystem ? estimateTokens(currentSystem) : 0;
    for (const message of projection.messages){
        if (message.role !== "system") tokens += estimateTokens(message);
    }
    return {
        tokens,
        usageTokens: 0,
        trailingTokens: tokens,
        lastUsageIndex: null
    };
}
export function shouldCompact(contextTokens, contextWindow, settings) {
    if (!settings.enabled) return false;
    return contextTokens > contextWindow - settings.reserveTokens;
}
const ESTIMATED_IMAGE_CHARS = 4800;
function estimateTextAndImageContentChars(content) {
    if (typeof content === "string") {
        return content.length;
    }
    let chars = 0;
    for (const block of content){
        if (block.type === "text" && block.text) {
            chars += block.text.length;
        } else if (block.type === "image") {
            chars += ESTIMATED_IMAGE_CHARS;
        }
    }
    return chars;
}
export function estimateTokens(message) {
    let chars = 0;
    switch(message.role){
        case "system":
            {
                const system = message;
                chars = estimateTextAndImageContentChars(system.content);
                if (system.sections) {
                    for (const section of Object.values(system.sections)){
                        if (section) chars += section.length;
                    }
                }
                if (system.toolsAdded) chars += JSON.stringify(system.toolsAdded).length;
                return Math.ceil(chars / 4);
            }
        case "user":
            {
                chars = estimateTextAndImageContentChars(message.content);
                return Math.ceil(chars / 4);
            }
        case "assistant":
            {
                const assistant = message;
                for (const block of assistant.content){
                    if (block.type === "text") {
                        chars += block.text.length;
                    } else if (block.type === "thinking") {
                        chars += block.thinking.length;
                    } else if (block.type === "toolCall") {
                        chars += block.name.length + JSON.stringify(block.arguments).length;
                    }
                }
                return Math.ceil(chars / 4);
            }
        case "custom":
        case "toolResult":
            {
                chars = estimateTextAndImageContentChars(message.content);
                return Math.ceil(chars / 4);
            }
        case "bashExecution":
            {
                chars = message.command.length + message.output.length;
                return Math.ceil(chars / 4);
            }
        case "branchSummary":
        case "compactionSummary":
            {
                chars = message.summary.length;
                return Math.ceil(chars / 4);
            }
    }
    return 0;
}
function isCutPointMessage(message) {
    switch(message.role){
        case "user":
        case "assistant":
        case "bashExecution":
        case "custom":
        case "branchSummary":
        case "compactionSummary":
            return true;
        case "toolResult":
            return false;
    }
    return false;
}
function isTurnStartMessage(message) {
    switch(message.role){
        case "user":
        case "bashExecution":
        case "custom":
        case "branchSummary":
        case "compactionSummary":
            return true;
        case "assistant":
        case "toolResult":
            return false;
    }
    return false;
}
function isTurnStartEntry(entry) {
    if (entry.type === "compaction") {
        return false;
    }
    return sessionEntryToContextMessages(entry).some(isTurnStartMessage);
}
function findValidCutPoints(entries, startIndex, endIndex) {
    const cutPoints = [];
    for(let i = startIndex; i < endIndex; i++){
        const entry = entries[i];
        if (entry.type === "compaction") {
            continue;
        }
        if (sessionEntryToContextMessages(entry).some(isCutPointMessage)) {
            cutPoints.push(i);
        }
    }
    return cutPoints;
}
export function findTurnStartIndex(entries, entryIndex, startIndex) {
    for(let i = entryIndex; i >= startIndex; i--){
        if (isTurnStartEntry(entries[i])) {
            return i;
        }
    }
    return -1;
}
export function findCutPoint(entries, startIndex, endIndex, keepRecentTokens) {
    const cutPoints = findValidCutPoints(entries, startIndex, endIndex);
    if (cutPoints.length === 0) {
        return {
            firstKeptEntryIndex: startIndex,
            turnStartIndex: -1,
            isSplitTurn: false
        };
    }
    let accumulatedTokens = 0;
    let cutIndex = cutPoints[0];
    for(let i = endIndex - 1; i >= startIndex; i--){
        const entry = entries[i];
        const messageTokens = sessionEntryToContextMessages(entry).reduce((sum, message)=>sum + estimateTokens(message), 0);
        if (messageTokens === 0) continue;
        accumulatedTokens += messageTokens;
        if (accumulatedTokens >= keepRecentTokens) {
            cutIndex = cutPoints.find((candidate)=>candidate >= i) ?? cutPoints[cutPoints.length - 1];
            break;
        }
    }
    while(cutIndex > startIndex){
        const prevEntry = entries[cutIndex - 1];
        if (prevEntry.type === "compaction" || sessionEntryToContextMessages(prevEntry).length > 0) {
            break;
        }
        cutIndex--;
    }
    const cutEntry = entries[cutIndex];
    const startsTurn = isTurnStartEntry(cutEntry);
    const turnStartIndex = startsTurn ? -1 : findTurnStartIndex(entries, cutIndex, startIndex);
    return {
        firstKeptEntryIndex: cutIndex,
        turnStartIndex,
        isSplitTurn: !startsTurn && turnStartIndex !== -1
    };
}
const SUMMARIZATION_PROMPT = `The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

Use this EXACT format:

## Goal
[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

## Constraints & Preferences
- [Any constraints, preferences, or requirements mentioned by user]
- [Or "(none)" if none were mentioned]

## Progress
### Done
- [x] [Completed tasks/changes]

### In Progress
- [ ] [Current work]

### Blocked
- [Issues preventing progress, if any]

## Key Decisions
- **[Decision]**: [Brief rationale]

## Next Steps
1. [Ordered list of what should happen next]

## Critical Context
- [Any data, examples, or references needed to continue]
- [Or "(none)" if not applicable]

Keep each section concise. Preserve exact file paths, function names, and error messages.`;
const UPDATE_SUMMARIZATION_INSTRUCTIONS = `Update the existing structured summary with new information. RULES:
- PRESERVE all existing information from the previous summary
- ADD new progress, decisions, and context from the new messages
- UPDATE the Progress section: move items from "In Progress" to "Done" when completed
- UPDATE "Next Steps" based on what was accomplished
- PRESERVE exact file paths, function names, and error messages
- If something is no longer relevant, you may remove it

Use this EXACT format:

## Goal
[Preserve existing goals, add new ones if the task expanded]

## Constraints & Preferences
- [Preserve existing, add new ones discovered]

## Progress
### Done
- [x] [Include previously done items AND newly completed items]

### In Progress
- [ ] [Current work - update based on progress]

### Blocked
- [Current blockers - remove if resolved]

## Key Decisions
- **[Decision]**: [Brief rationale] (preserve all previous, add new)

## Next Steps
1. [Update based on current state]

## Critical Context
- [Preserve important context, add new if needed]

Keep each section concise. Preserve exact file paths, function names, and error messages.`;
const UPDATE_SUMMARIZATION_PROMPT = `The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

${UPDATE_SUMMARIZATION_INSTRUCTIONS}`;
export function getSummarizationFailure(response, label) {
    if (response.stopReason === "error") {
        return `${label} failed: ${response.errorMessage || "Unknown error"}`;
    }
    if (response.stopReason === "length") {
        return `${label} failed: generation hit the token cap and the summary is incomplete`;
    }
    return undefined;
}
function createSummarizationOptions(model, maxTokens, apiKey, headers, env, signal, thinkingLevel, sessionId) {
    const options = {
        maxTokens,
        signal,
        apiKey,
        headers,
        env,
        sessionId
    };
    if (model.reasoning && thinkingLevel && thinkingLevel !== "off") {
        options.reasoning = thinkingLevel;
    }
    return options;
}
export async function completeSummarization(model, context, options, streamFn, retry, callbacks) {
    const requestOptions = {
        ...options,
        cacheRetention: "none",
        sessionId: options.sessionId ?? uuidv7()
    };
    const produce = async ()=>streamFn ? (await streamFn(model, context, requestOptions)).result() : completeSimple(model, context, requestOptions);
    return retryAssistantCall(produce, retry, requestOptions.signal, callbacks);
}
export async function generateSummary(currentMessages, model, reserveTokens, apiKey, headers, signal, customInstructions, previousSummary, thinkingLevel, streamFn, env, retry, callbacks, sessionId) {
    return (await generateSummaryWithUsage(currentMessages, model, reserveTokens, apiKey, headers, signal, customInstructions, previousSummary, thinkingLevel, streamFn, env, retry, callbacks, sessionId)).text;
}
function buildSummarizationContext(promptText) {
    return normalizeContext({
        systemPrompt: SUMMARIZATION_SYSTEM_PROMPT,
        messages: [
            {
                role: "user",
                content: [
                    {
                        type: "text",
                        text: promptText
                    }
                ],
                timestamp: Date.now()
            }
        ]
    });
}
export async function generateSummaryWithUsage(currentMessages, model, reserveTokens, apiKey, headers, signal, customInstructions, previousSummary, thinkingLevel, streamFn, env, retry, callbacks, sessionId) {
    const maxTokens = Math.min(Math.floor(0.8 * reserveTokens), model.maxTokens > 0 ? model.maxTokens : Number.POSITIVE_INFINITY);
    let basePrompt = previousSummary ? UPDATE_SUMMARIZATION_PROMPT : SUMMARIZATION_PROMPT;
    if (customInstructions) {
        basePrompt = `${basePrompt}\n\nAdditional focus: ${customInstructions}`;
    }
    const llmMessages = convertToLlm(currentMessages);
    const conversationText = serializeConversation(llmMessages);
    let promptText = `<conversation>\n${conversationText}\n</conversation>\n\n`;
    if (previousSummary) {
        promptText += `<previous-summary>\n${previousSummary}\n</previous-summary>\n\n`;
    }
    promptText += basePrompt;
    const completionOptions = createSummarizationOptions(model, maxTokens, apiKey, headers, env, signal, thinkingLevel, sessionId);
    const response = await completeSummarization(model, buildSummarizationContext(promptText), completionOptions, streamFn, retry, callbacks);
    const failure = getSummarizationFailure(response, "Summarization");
    if (failure) {
        throw new Error(failure);
    }
    if (response.content.some((block)=>block.type === "toolCall")) {
        throw new Error("Summarization attempted to call a tool");
    }
    const textContent = contentText(response.content);
    return {
        text: textContent,
        usage: response.usage
    };
}
function isProjectedTurnStart(entry) {
    if (entry.sourceEntry.type === "compaction") return false;
    return entry.messages.some(isTurnStartMessage);
}
function findProjectedTurnStartIndex(entries, entryIndex, startIndex) {
    for(let i = entryIndex; i >= startIndex; i--){
        if (isProjectedTurnStart(entries[i])) return i;
    }
    return -1;
}
function findProjectedCutPoint(entries, startIndex, endIndex, keepRecentTokens) {
    const cutPoints = [];
    for(let i = startIndex; i < endIndex; i++){
        const entry = entries[i];
        if (entry.sourceEntry.type !== "compaction" && entry.messages.some(isCutPointMessage)) cutPoints.push(i);
    }
    if (cutPoints.length === 0) {
        return {
            firstKeptEntryIndex: startIndex,
            turnStartIndex: -1,
            isSplitTurn: false
        };
    }
    let accumulatedTokens = 0;
    let exceededBudget = false;
    let cutIndex = cutPoints[0];
    for(let i = endIndex - 1; i >= startIndex; i--){
        const messageTokens = entries[i].messages.reduce((sum, message)=>sum + estimateTokens(message), 0);
        if (messageTokens === 0) continue;
        accumulatedTokens += messageTokens;
        if (accumulatedTokens >= keepRecentTokens) {
            exceededBudget = true;
            cutIndex = cutPoints.find((candidate)=>candidate >= i) ?? cutPoints[cutPoints.length - 1];
            break;
        }
    }
    const suffix = entries.slice(cutIndex + 1, endIndex);
    const isIntrinsicallyVisible = (entry)=>entry.sourceEntry.type !== "context_edit" && sessionEntryToContextMessages(entry.sourceEntry).length > 0;
    const isOmitted = (entry)=>isIntrinsicallyVisible(entry) && entry.messages.length === 0;
    const omittedSuffixIds = new Set(suffix.filter(isOmitted).map((entry)=>entry.sourceEntry.id));
    const hasExternalReplacement = suffix.some((entry)=>entry.sourceEntry.type === "context_edit" && entry.sourceEntry.replacement !== null && !omittedSuffixIds.has(entry.sourceEntry.targetId));
    const isRecoveryOmissionSuffix = exceededBudget && !hasExternalReplacement && suffix.some((entry)=>entry.sourceEntry.type === "message" && entry.sourceEntry.message.role === "assistant" && isOmitted(entry)) && suffix.every((entry)=>entry.sourceEntry.type !== "compaction" && (!isIntrinsicallyVisible(entry) || isOmitted(entry)));
    if (isRecoveryOmissionSuffix) cutIndex++;
    while(cutIndex > startIndex){
        const previous = entries[cutIndex - 1];
        if (previous.sourceEntry.type === "compaction" || previous.messages.length > 0) break;
        cutIndex--;
    }
    const startsTurn = isProjectedTurnStart(entries[cutIndex]);
    const turnStartIndex = startsTurn ? -1 : findProjectedTurnStartIndex(entries, cutIndex, startIndex);
    return {
        firstKeptEntryIndex: cutIndex,
        turnStartIndex,
        isSplitTurn: !startsTurn && turnStartIndex !== -1
    };
}
export function prepareCompaction(pathEntries, settings) {
    if (pathEntries.length > 0 && pathEntries[pathEntries.length - 1].type === "compaction") {
        return undefined;
    }
    const projection = buildSessionProjection(pathEntries);
    const projectedEntries = projection.entries;
    const sourceEntries = projectedEntries.map((entry)=>entry.sourceEntry);
    const prevCompactionIndex = projectedEntries.findIndex((entry)=>entry.sourceEntry.type === "compaction" && entry.messages.length > 0);
    let previousSummary;
    let boundaryStart = 0;
    if (prevCompactionIndex >= 0) {
        previousSummary = projectedEntries[prevCompactionIndex].sourceEntry.summary;
        boundaryStart = prevCompactionIndex + 1;
    }
    const boundaryEnd = projectedEntries.length;
    const tokensBefore = estimateProjectedContextTokens(projection, pathEntries).tokens;
    const cutPoint = findProjectedCutPoint(projectedEntries, boundaryStart, boundaryEnd, settings.keepRecentTokens);
    const firstKeptEntry = projectedEntries[cutPoint.firstKeptEntryIndex]?.sourceEntry;
    if (!firstKeptEntry?.id) return undefined;
    const firstKeptEntryId = firstKeptEntry.id;
    const historyEnd = cutPoint.isSplitTurn ? cutPoint.turnStartIndex : cutPoint.firstKeptEntryIndex;
    const messagesToSummarize = projectedEntries.slice(boundaryStart, historyEnd).flatMap(getMessagesFromProjectedEntryForCompaction);
    const turnPrefixMessages = cutPoint.isSplitTurn ? projectedEntries.slice(cutPoint.turnStartIndex, cutPoint.firstKeptEntryIndex).flatMap(getMessagesFromProjectedEntryForCompaction) : [];
    if (messagesToSummarize.length === 0 && turnPrefixMessages.length === 0) return undefined;
    const fileOps = extractFileOperations(messagesToSummarize, sourceEntries, prevCompactionIndex);
    if (cutPoint.isSplitTurn) {
        for (const msg of turnPrefixMessages){
            extractFileOpsFromMessage(msg, fileOps);
        }
    }
    return {
        firstKeptEntryId,
        messagesToSummarize,
        turnPrefixMessages,
        isSplitTurn: cutPoint.isSplitTurn,
        tokensBefore,
        previousSummary,
        fileOps,
        settings
    };
}
const TURN_PREFIX_SUMMARIZATION_PROMPT = `The messages above are earlier context from an ongoing conversation. Later messages are stored separately and do not need to be reconstructed.

Create a concise checkpoint of the user's request and the progress shown above. This checkpoint will be placed before the later messages so the conversation can continue with the necessary context.

## Original Request
[What did the user ask for?]

## Progress So Far
- [Key decisions and work completed in these messages]

## Context Needed to Continue
- [Information from these messages needed to understand the later work]

Only summarize information explicitly present above. Do not infer or recreate later messages.`;
export async function compact(preparation, model, apiKey, headers, customInstructions, signal, thinkingLevel, streamFn, env, retry, callbacks, sessionId) {
    const { firstKeptEntryId, messagesToSummarize, turnPrefixMessages, isSplitTurn, tokensBefore, previousSummary, fileOps, settings } = preparation;
    let summary;
    let summaryUsage;
    if (isSplitTurn && turnPrefixMessages.length > 0) {
        let historyText = previousSummary ?? "No prior history.";
        let historyUsage;
        if (messagesToSummarize.length > 0) {
            const historyResult = await generateSummaryWithUsage(messagesToSummarize, model, settings.reserveTokens, apiKey, headers, signal, customInstructions, previousSummary, thinkingLevel, streamFn, env, retry, callbacks, sessionId);
            historyText = historyResult.text;
            historyUsage = historyResult.usage;
        }
        const turnPrefixResult = await generateTurnPrefixSummary(turnPrefixMessages, model, settings.reserveTokens, apiKey, headers, env, signal, thinkingLevel, streamFn, retry, callbacks, sessionId);
        summary = `${historyText}\n\n---\n\n**Turn Context (split turn):**\n\n${turnPrefixResult.text}`;
        summaryUsage = historyUsage ? combineUsage(historyUsage, turnPrefixResult.usage) : turnPrefixResult.usage;
    } else {
        const result = await generateSummaryWithUsage(messagesToSummarize, model, settings.reserveTokens, apiKey, headers, signal, customInstructions, previousSummary, thinkingLevel, streamFn, env, retry, callbacks, sessionId);
        summary = result.text;
        summaryUsage = result.usage;
    }
    const { readFiles, modifiedFiles } = computeFileLists(fileOps);
    summary += formatFileOperations(readFiles, modifiedFiles);
    if (!firstKeptEntryId) {
        throw new Error("First kept entry has no UUID - session may need migration");
    }
    return {
        summary,
        firstKeptEntryId,
        tokensBefore,
        usage: summaryUsage,
        details: {
            readFiles,
            modifiedFiles
        }
    };
}
async function generateTurnPrefixSummary(messages, model, reserveTokens, apiKey, headers, env, signal, thinkingLevel, streamFn, retry, callbacks, sessionId) {
    const maxTokens = Math.min(Math.floor(0.5 * reserveTokens), model.maxTokens > 0 ? model.maxTokens : Number.POSITIVE_INFINITY);
    const llmMessages = convertToLlm(messages);
    const conversationText = serializeConversation(llmMessages);
    const promptText = `# Conversation\n${conversationText}\n\n# Instructions\n${TURN_PREFIX_SUMMARIZATION_PROMPT}`;
    const response = await completeSummarization(model, buildSummarizationContext(promptText), createSummarizationOptions(model, maxTokens, apiKey, headers, env, signal, thinkingLevel, sessionId), streamFn, retry, callbacks);
    const failure = getSummarizationFailure(response, "Turn prefix summarization");
    if (failure) {
        throw new Error(failure);
    }
    if (response.content.some((block)=>block.type === "toolCall")) {
        throw new Error("Turn prefix summarization attempted to call a tool");
    }
    return {
        text: contentText(response.content),
        usage: response.usage
    };
}
