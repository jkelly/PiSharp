import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const fixture = parseJsonSupported(readFileSync(process.argv[2], 'utf8'));
const { runAgentLoop } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/agent/src/agent-loop.ts')).href);
const { createAssistantMessageEventStream } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/event-stream.ts')).href);

function gate() {
  let release;
  const promise = new Promise(resolve => { release = resolve; });
  return { promise, release };
}

async function captureScenario(scenario) {
  const assistantBarrierEntered = gate();
  const assistantBarrierRelease = gate();
  const toolEntered = gate();
  const toolRelease = gate();
  const requests = [];
  const events = [];
  const providerEmissionSnapshots = [];
  const hookTrace = [];
  const toolTrace = [];
  const queueTrace = [];
  const controls = [];
  const order = [];
  const steering = [];
  const followUp = [];
  let requestCount = 0;
  let toolExecutionCount = 0;
  let barrierBlockedTools = false;
  let lastContextMessages;
  const clone = value => structuredClone(value);
  const record = (list, kind, entry) => {
    const index = list.length;
    list.push(clone(entry));
    order.push({ kind, index });
  };
  const tool = {
    name: fixture.tool.name,
    label: fixture.tool.name,
    description: fixture.tool.description,
    parameters: clone(fixture.tool.parameters),
    async execute(id, args, signal) {
      assert.equal(id, scenario.providerTurns[0].content[0].id);
      assert.equal(signal.aborted, false);
      toolExecutionCount++;
      record(toolTrace, 'tool', { kind: 'execute_start', id, args });
      toolEntered.release();
      await toolRelease.promise;
      record(toolTrace, 'tool', { kind: 'execute_finish', id });
      return clone(scenario.toolResult);
    },
  };
  const emit = async event => {
    // Snapshot synchronously at the actual awaited sink, before subsequent mutation.
    record(events, 'event', event);
    if (event.type === 'message_end' && event.message.role === 'assistant' && requestCount === 1) {
      record(controls, 'control', { kind: 'assistant_barrier_enter' });
      assistantBarrierEntered.release();
      await assistantBarrierRelease.promise;
      record(controls, 'control', { kind: 'assistant_barrier_release' });
    }
  };
  const streamFn = (model, context, options) => {
    const requestIndex = requestCount++;
    assert.ok(requestIndex < scenario.providerTurns.length, 'Unexpected provider request exceeds authored fake responses');
    assert.equal(options.apiKey, undefined, 'No credential may enter the fake stream');
    assert.equal(options.signal.aborted, false);
    record(requests, 'request', {
      model,
      context,
      optionMetadata: {
        ownKeys: Object.keys(options).sort(),
        apiKeyOwnProperty: Object.hasOwn(options, 'apiKey'),
        apiKeyUndefined: options.apiKey === undefined,
        reasoningOwnProperty: Object.hasOwn(options, 'reasoning'),
        signalAborted: options.signal.aborted,
      },
    });
    const finalMessage = clone(scenario.providerTurns[requestIndex]);
    const partial = clone(finalMessage);
    partial.content = [];
    partial.stopReason = 'pending';
    const stream = createAssistantMessageEventStream();
    const push = event => {
      record(providerEmissionSnapshots, 'provider_emission', { requestIndex, event });
      stream.push(event);
    };
    push({ type: 'start', partial: clone(partial) });
    for (let contentIndex = 0; contentIndex < finalMessage.content.length; contentIndex++) {
      const block = finalMessage.content[contentIndex];
      if (block.type === 'toolCall') {
        partial.content.push({ type: 'toolCall', id: block.id, name: block.name, arguments: {} });
        push({ type: 'toolcall_start', contentIndex, partial: clone(partial) });
        push({ type: 'toolcall_delta', contentIndex, delta: JSON.stringify(block.arguments), partial: clone(partial) });
        partial.content[contentIndex] = clone(block);
        push({ type: 'toolcall_end', contentIndex, toolCall: clone(block), partial: clone(partial) });
      } else {
        assert.equal(block.type, 'text', 'This capture only authors text and tool-call fake emissions');
        partial.content.push({ type: 'text', text: '' });
        push({ type: 'text_start', contentIndex, partial: clone(partial) });
        partial.content[contentIndex] = clone(block);
        push({ type: 'text_delta', contentIndex, delta: block.text, partial: clone(partial) });
        push({ type: 'text_end', contentIndex, content: block.text, partial: clone(partial) });
      }
    }
    push({ type: 'done', reason: finalMessage.stopReason, message: finalMessage });
    return stream;
  };
  const turnSnapshot = turn => ({
    message: turn.message,
    toolResults: turn.toolResults,
    contextMessages: turn.context.messages,
    newMessages: turn.newMessages,
  });
  const config = {
    model: clone(fixture.model),
    convertToLlm(messages) {
      record(hookTrace, 'hook', { kind: 'convert_to_llm', messages });
      return messages;
    },
    async prepareRequest(request) {
      record(hookTrace, 'hook', { kind: 'prepare_request', contextMessages: request.context.messages, model: request.model, thinkingLevel: request.thinkingLevel });
    },
    async prepareNextTurn(turn) {
      record(hookTrace, 'hook', { kind: 'prepare_next_turn', ...turnSnapshot(turn) });
    },
    async finishTurn(turn) {
      lastContextMessages = clone(turn.context.messages);
      record(hookTrace, 'hook', { kind: 'finish_turn', ...turnSnapshot(turn) });
    },
    async beforeToolCall(context) {
      record(toolTrace, 'tool', { kind: 'preflight', id: context.toolCall.id, args: context.args, contextMessages: context.context.messages });
    },
    async afterToolCall(context) {
      record(toolTrace, 'tool', { kind: 'after', id: context.toolCall.id, result: context.result, isError: context.isError, contextMessages: context.context.messages });
    },
    async getSteeringMessages() {
      const messages = steering.splice(0);
      record(queueTrace, 'queue', { kind: 'steering_poll', messages });
      return messages;
    },
    async getFollowUpMessages() {
      const messages = followUp.splice(0);
      record(queueTrace, 'queue', { kind: 'follow_up_poll', messages });
      return messages;
    },
  };
  const running = runAgentLoop(clone(scenario.prompts), { messages: [], tools: [tool] }, config, emit, new AbortController().signal, streamFn);
  await assistantBarrierEntered.promise;
  assert.equal(toolTrace.length, 0, 'Awaited assistant message_end must block preflight and execution');
  assert.equal(requestCount, 1, 'Awaited assistant message_end must block the next request');
  barrierBlockedTools = true;
  record(controls, 'control', { kind: 'barrier_probe', providerRequestCount: requestCount, preflightCount: 0, executionCount: toolExecutionCount });
  for (const message of scenario.steeringMessages) {
    steering.push(clone(message));
    record(queueTrace, 'queue', { kind: 'enqueue_steering', message });
  }
  for (const message of scenario.followUpMessages) {
    followUp.push(clone(message));
    record(queueTrace, 'queue', { kind: 'enqueue_follow_up', message });
  }
  assistantBarrierRelease.release();
  await toolEntered.promise;
  record(controls, 'control', {
    kind: 'tool_gate_probe',
    providerRequestCount: requestCount,
    steeringPollCount: queueTrace.filter(entry => entry.kind === 'steering_poll').length,
    followUpPollCount: queueTrace.filter(entry => entry.kind === 'follow_up_poll').length,
  });
  record(controls, 'control', { kind: 'release_tool' });
  toolRelease.release();
  const finalResult = await running;
  return {
    scenarioId: scenario.scenarioId,
    requests,
    events,
    providerEmissionSnapshots,
    hookTrace,
    toolTrace,
    queueTrace,
    controls,
    order,
    finalResult: clone(finalResult),
    finalContextMessages: lastContextMessages,
    effects: [],
    checks: {
      barrierBlockedTools,
      providerRequestCount: requestCount,
      toolExecutionCount,
      turnStartCount: events.filter(event => event.type === 'turn_start').length,
      turnEndCount: events.filter(event => event.type === 'turn_end').length,
      terminalAgentEndCount: events.filter(event => event.type === 'agent_end').length,
      resultOrder: finalResult.filter(message => message.role === 'toolResult').map(message => message.toolCallId),
      steeringPollCount: queueTrace.filter(entry => entry.kind === 'steering_poll').length,
      followUpPollCount: queueTrace.filter(entry => entry.kind === 'follow_up_poll').length,
      deliveredSteeringMessages: queueTrace.filter(entry => entry.kind === 'steering_poll').flatMap(entry => entry.messages),
      deliveredFollowUpMessages: queueTrace.filter(entry => entry.kind === 'follow_up_poll').flatMap(entry => entry.messages),
      remainingSteeringCount: steering.length,
      remainingFollowUpCount: followUp.length,
    },
  };
}

const originalNow = Date.now;
Date.now = () => fixture.clock.unixMilliseconds;
try {
  const scenarios = [];
  for (const scenario of fixture.scenarios) scenarios.push(await captureScenario(scenario));
  console.log(JSON.stringify({
    observations: { scenarios },
    loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0),
  }));
} finally {
  Date.now = originalNow;
}
