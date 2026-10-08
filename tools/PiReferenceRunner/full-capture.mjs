import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const fixture = parseJsonSupported(readFileSync(process.argv[2], 'utf8'));
const { runAgentLoop } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/agent/src/agent-loop.ts')).href);
const { createAssistantMessageEventStream } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/event-stream.ts')).href);
function gate() { let release; const promise = new Promise(resolve => { release = resolve; }); return { promise, release }; }
const barrierEntered = gate();
const barrierRelease = gate();
const allStarted = gate();
const completions = new Map(fixture.toolCalls.map(call => [call.id, gate()]));
const observedEnds = new Map(fixture.toolCalls.map(call => [call.id, gate()]));
const requests = [];
const events = [];
const providerEmissionSnapshots = [];
const toolTrace = [];
const controls = [];
let active = 0;
let maxActive = 0;
let startedCount = 0;
let requestCount = 0;
const originalNow = Date.now;
Date.now = () => fixture.clock.unixMilliseconds;
const finalAssistant = structuredClone(fixture.assistantMessage);
const tools = fixture.toolCalls.map(call => ({
  name: call.name, label: call.name, description: `Synthetic tool ${call.name}`,
  parameters: structuredClone(fixture.toolParameters),
  async execute(id, args) {
    assert.equal(id, call.id);
    toolTrace.push({ kind: 'execute_start', id, args: structuredClone(args) });
    active++; maxActive = Math.max(maxActive, active);
    if (++startedCount === fixture.toolCalls.length) allStarted.release();
    await completions.get(id).promise;
    active--;
    toolTrace.push({ kind: 'execute_finish', id });
    return { content: [{ type: 'text', text: `result:${id}` }], details: { value: args.value }, terminate: true };
  },
}));
const emit = async event => {
  // Clone synchronously at the awaited emission boundary, before any later mutation.
  events.push(structuredClone(event));
  if (event.type === 'message_end' && event.message.role === 'assistant') {
    controls.push({ kind: 'assistant_barrier_enter' });
    barrierEntered.release();
    await barrierRelease.promise;
    controls.push({ kind: 'assistant_barrier_release' });
  }
  if (event.type === 'tool_execution_end') observedEnds.get(event.toolCallId).release();
};
const streamFn = (model, context, options) => {
  assert.equal(++requestCount, 1, 'All-terminate scenario must make exactly one provider request');
  assert.equal(options.apiKey, undefined, 'No credential may enter fake stream');
  requests.push({ model: structuredClone(model), context: structuredClone(context) });
  const stream = createAssistantMessageEventStream();
  const push = event => { providerEmissionSnapshots.push(structuredClone(event)); stream.push(event); };
  const partial = structuredClone(finalAssistant);
  partial.content = [];
  partial.stopReason = 'pending';
  push({ type: 'start', partial: structuredClone(partial) });
  for (let contentIndex = 0; contentIndex < finalAssistant.content.length; contentIndex++) {
    const toolCall = finalAssistant.content[contentIndex];
    partial.content.push({ type: 'toolCall', id: toolCall.id, name: toolCall.name, arguments: {} });
    push({ type: 'toolcall_start', contentIndex, partial: structuredClone(partial) });
    push({ type: 'toolcall_delta', contentIndex, delta: JSON.stringify(toolCall.arguments), partial: structuredClone(partial) });
    partial.content[contentIndex] = structuredClone(toolCall);
    push({ type: 'toolcall_end', contentIndex, toolCall: structuredClone(toolCall), partial: structuredClone(partial) });
  }
  push({ type: 'done', reason: 'toolUse', message: finalAssistant });
  return stream;
};
try {
  const running = runAgentLoop(structuredClone(fixture.prompts), { messages: [], tools }, {
    model: structuredClone(fixture.model), convertToLlm: messages => messages,
    async beforeToolCall(context) { toolTrace.push({ kind: 'preflight', id: context.toolCall.id, args: structuredClone(context.args) }); },
    async afterToolCall(context) { toolTrace.push({ kind: 'after', id: context.toolCall.id }); },
  }, emit, new AbortController().signal, streamFn);
  await barrierEntered.promise;
  assert.deepEqual(toolTrace, [], 'No tool preflight/execution while assistant message_end is awaited');
  controls.push({ kind: 'barrier_probe', preflightCount: 0, executionCount: 0 });
  barrierRelease.release();
  await allStarted.promise;
  for (const id of fixture.completionOrder) {
    controls.push({ kind: 'release_tool', id });
    completions.get(id).release();
    await observedEnds.get(id).promise;
  }
  const finalResult = await running;
  const preflightOrder = toolTrace.filter(entry => entry.kind === 'preflight').map(entry => entry.id);
  const completionOrder = events.filter(event => event.type === 'tool_execution_end').map(event => event.toolCallId);
  const resultOrder = finalResult.filter(message => message.role === 'toolResult').map(message => message.toolCallId);
  assert.deepEqual(preflightOrder, fixture.toolCalls.map(call => call.id));
  assert.deepEqual(completionOrder, fixture.completionOrder);
  assert.deepEqual(resultOrder, fixture.toolCalls.map(call => call.id));
  assert.equal(maxActive, fixture.toolCalls.length);
  assert.equal(events.filter(event => event.type === 'agent_end').length, 1);
  assert.equal(requests.length, 1);
  console.log(JSON.stringify({ observations: { requests, events, providerEmissionSnapshots, toolTrace, controls, finalResult, effects: [], checks: { barrierBlockedTools: true, preflightOrder, completionOrder, resultOrder, maxConcurrentTools: maxActive, providerRequestCount: requestCount, terminalAgentEndCount: 1 } }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }));
} finally { Date.now = originalNow; }
