// Whole unchanged Agent lifecycle; authored streams/tools/gates are explicit seams.
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const inside = (root, path) => { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); };
export function createGate() {
  let release; const promise = new Promise(resolvePromise => { release = resolvePromise; });
  return { promise, release };
}
// JSON.stringify observation is retained verbatim as a parsed JSON value. Values
// JSON cannot carry are inventoried separately, without replacing source fields.
export function inspectJsonObservation(value) {
  const ownUndefinedPaths = [], callablePaths = [], setValues = [];
  function inspect(current, path = '') {
    if (current === undefined) { ownUndefinedPaths.push(path); return; }
    if (typeof current === 'function') { callablePaths.push(path); return; }
    if (current instanceof Set) { setValues.push({ path, values: [...current] }); return; }
    if (current && typeof current === 'object') for (const key of Object.keys(current)) inspect(current[key], path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1'));
  }
  inspect(value); const raw = JSON.stringify(value);
  return { ...(raw === undefined ? {} : { json: JSON.parse(raw) }), ownUndefinedPaths, callablePaths, setValues };
}
export async function captureAgentProgress(oracle, fixture, admittedPackages) {
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Locked observer/offline preload required');
  registerHooks({ resolve(specifier, context, nextResolve) {
    const result = nextResolve(specifier, context);
    const admitted = result.url.startsWith('node:') || (result.url.startsWith('file:') && (inside(resolve(oracle, 'upstream'), fileURLToPath(result.url)) || admittedPackages.some(name => inside(resolve(oracle, 'node_modules', name), fileURLToPath(result.url)))));
    assert(admitted, 'Ancestor/global/unreviewed module resolution rejected: ' + result.url); return result;
  } });
  const originalNow = Date.now;
  Date.now = () => fixture.clock.unixMilliseconds;
  try {
    const { Agent } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/agent/src/agent.ts')).href);
    const { createAssistantMessageEventStream } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/event-stream.ts')).href);
    assert.equal(typeof Agent, 'function');
    const cases = [];
    for (const test of fixture.cases) {
      const events = [], listenerTrace = [], toolTrace = [], hooks = [], providerFrames = [], requests = [], controls = [], observations = [];
      const started = new Map(test.tools.map(tool => [tool.call.id, createGate()]));
      const emitGates = new Map(test.tools.map(tool => [tool.call.id, createGate()]));
      const ends = new Map(test.tools.map(tool => [tool.call.id, createGate()]));
      const secondUpdates = new Map(test.tools.map(tool => [tool.call.id, createGate()]));
      const callbacks = new Map(), updateCounts = new Map(), eventMeta = new WeakMap();
      const blockedEntered = createGate(), blockedRelease = createGate(), abortObserved = createGate(), executeLeaving = createGate();
      const agentEndEntered = createGate(), agentEndRelease = createGate();
      let requestCount = 0, idleResolved = false, promptResolved = false, activeListeners = 0, maxActiveListeners = 0;
      let agent;
      const tools = test.tools.map(spec => ({
        name: spec.call.name, label: spec.call.name, description: spec.description,
        parameters: structuredClone(fixture.toolParameters),
        ...(spec.outputSchema === undefined ? {} : { outputSchema: structuredClone(spec.outputSchema) }),
        async execute(id, args, signal, onUpdate) {
          assert.equal(id, spec.call.id); assert(signal instanceof AbortSignal);
          callbacks.set(id, onUpdate); toolTrace.push({ kind: 'execute_enter', id, args: inspectJsonObservation(args), signalAborted: signal.aborted });
          if (test.mode === 'abort-blocked-update') signal.addEventListener('abort', () => { toolTrace.push({ kind: 'tool_signal_abort', id, signalAborted: signal.aborted }); abortObserved.release(); }, { once: true });
          started.get(id).release(); await emitGates.get(id).promise;
          const deliver = partial => {
            toolTrace.push({ kind: 'invoke_update', id, partial: inspectJsonObservation(partial), signalAborted: signal.aborted });
            const returned = onUpdate(structuredClone(partial));
            toolTrace.push({ kind: 'update_callback_return', id, returned: inspectJsonObservation(returned), signalAborted: signal.aborted });
          };
          for (const partial of spec.updates) deliver(partial);
          if (test.mode === 'abort-blocked-update') {
            await abortObserved.promise;
            deliver(spec.updateAfterAbort);
            toolTrace.push({ kind: 'execute_throw', id, errorMessage: spec.abortError, signalAborted: signal.aborted });
            executeLeaving.release(); throw new Error(spec.abortError);
          }
          toolTrace.push({ kind: 'execute_return', id, result: inspectJsonObservation(spec.result), signalAborted: signal.aborted });
          if (id === test.blockedToolId) executeLeaving.release();
          return structuredClone(spec.result);
        }
      }));
      const finalAssistant = { ...structuredClone(fixture.assistantMessage), content: test.tools.map(spec => ({ type: 'toolCall', ...structuredClone(spec.call) })) };
      const streamFn = (model, context, options) => {
        assert.equal(++requestCount, 1, 'Authored finishTurn end must limit capture to one fake provider request');
        assert.equal(options.apiKey, undefined, 'No credential enters the fake provider');
        requests.push({ model: inspectJsonObservation(model), context: inspectJsonObservation(context), options: inspectJsonObservation(options), signalAborted: options.signal?.aborted ?? null });
        const stream = createAssistantMessageEventStream();
        const push = event => { providerFrames.push(inspectJsonObservation(event)); stream.push(event); };
        const partial = { ...structuredClone(finalAssistant), content: [], stopReason: 'pending' };
        push({ type: 'start', partial: structuredClone(partial) });
        for (let contentIndex = 0; contentIndex < finalAssistant.content.length; contentIndex++) {
          const toolCall = finalAssistant.content[contentIndex];
          partial.content.push({ type: 'toolCall', id: toolCall.id, name: toolCall.name, arguments: {} });
          push({ type: 'toolcall_start', contentIndex, partial: structuredClone(partial) });
          push({ type: 'toolcall_delta', contentIndex, delta: JSON.stringify(toolCall.arguments), partial: structuredClone(partial) });
          partial.content[contentIndex] = structuredClone(toolCall);
          push({ type: 'toolcall_end', contentIndex, toolCall: structuredClone(toolCall), partial: structuredClone(partial) });
        }
        push({ type: 'done', reason: 'toolUse', message: structuredClone(finalAssistant) }); return stream;
      };
      agent = new Agent({ initialState: { model: structuredClone(fixture.model), systemPrompt: fixture.systemPrompt, tools }, streamFn,
        toolExecution: test.toolExecution,
        async beforeToolCall(context, signal) {
          const spec = test.tools.find(tool => tool.call.id === context.toolCall.id), returned = spec.beforeHook;
          hooks.push({ kind: 'beforeToolCall', context: inspectJsonObservation(context), signalAborted: signal.aborted, returned: inspectJsonObservation(returned) });
          return structuredClone(returned);
        },
        async afterToolCall(context, signal) {
          const spec = test.tools.find(tool => tool.call.id === context.toolCall.id), returned = spec.afterHook;
          hooks.push({ kind: 'afterToolCall', context: inspectJsonObservation(context), signalAborted: signal.aborted, returned: inspectJsonObservation(returned) });
          return structuredClone(returned);
        },
        async finishTurn(context, signal) {
          const returned = { action: 'end' };
          hooks.push({ kind: 'finishTurn', context: inspectJsonObservation(context), signalAborted: signal.aborted, returned: inspectJsonObservation(returned) }); return returned;
        }
      });
      const initialState = inspectJsonObservation(agent.state);
      agent.subscribe(async (event, signal) => {
        const index = events.length, ordinal = event.type === 'tool_execution_update' ? (updateCounts.get(event.toolCallId) ?? 0) + 1 : undefined;
        if (ordinal !== undefined) updateCounts.set(event.toolCallId, ordinal);
        eventMeta.set(event, { index, ordinal });
        events.push({ index, event: inspectJsonObservation(event), stateBeforeListener: inspectJsonObservation(agent.state), signalAborted: signal.aborted });
        activeListeners++; maxActiveListeners = Math.max(maxActiveListeners, activeListeners);
        listenerTrace.push({ kind: 'first_enter', index, type: event.type, activeListeners, signalAborted: signal.aborted });
        if (event.type === 'tool_execution_update' && event.toolCallId === test.blockedToolId && ordinal === 1) {
          controls.push({ kind: 'first_update_barrier_enter', id: event.toolCallId }); blockedEntered.release(); await blockedRelease.promise;
          controls.push({ kind: 'first_update_barrier_release', id: event.toolCallId, signalAborted: signal.aborted });
        }
        if (event.type === 'agent_end') { controls.push({ kind: 'agent_end_barrier_enter' }); agentEndEntered.release(); await agentEndRelease.promise; controls.push({ kind: 'agent_end_barrier_release' }); }
        listenerTrace.push({ kind: 'first_exit', index, type: event.type, activeListeners, signalAborted: signal.aborted }); activeListeners--;
      });
      agent.subscribe(async (event, signal) => {
        const { index, ordinal } = eventMeta.get(event);
        listenerTrace.push({ kind: 'second_enter', index, type: event.type, signalAborted: signal.aborted });
        if (event.type === 'tool_execution_update' && ordinal === 2) secondUpdates.get(event.toolCallId).release();
        if (event.type === 'tool_execution_end') ends.get(event.toolCallId).release();
        listenerTrace.push({ kind: 'second_exit', index, type: event.type, signalAborted: signal.aborted });
      });
      const running = agent.prompt(structuredClone(fixture.prompt)).then(value => { promptResolved = true; return value; });
      const idle = agent.waitForIdle().then(value => { idleResolved = true; return value; });
      const probe = kind => {
        observations.push({ kind, eventCount: events.length, callbackCount: toolTrace.filter(item => item.kind === 'invoke_update').length, acceptedUpdateCount: events.filter(item => item.event.json.type === 'tool_execution_update').length, afterHookIds: hooks.filter(item => item.kind === 'afterToolCall').map(item => item.context.json.toolCall.id), toolEndIds: events.filter(item => item.event.json.type === 'tool_execution_end').map(item => item.event.json.toolCallId), toolMessageIds: events.filter(item => item.event.json.type === 'message_end' && item.event.json.message.role === 'toolResult').map(item => item.event.json.message.toolCallId), activeListeners, idleResolved, promptResolved, state: inspectJsonObservation(agent.state), activeSignal: inspectJsonObservation(agent.signal), signalAborted: agent.signal?.aborted ?? null });
      };
      if (test.mode === 'parallel-blocked-update') {
        await Promise.all(test.tools.map(spec => started.get(spec.call.id).promise));
        controls.push({ kind: 'release_tool_updates', id: test.blockedToolId }); emitGates.get(test.blockedToolId).release();
        await blockedEntered.promise; await secondUpdates.get(test.blockedToolId).promise; await executeLeaving.promise;
        probe('first_update_blocked_after_second_listener_and_execute_return');
        const other = test.tools.find(spec => spec.call.id !== test.blockedToolId);
        controls.push({ kind: 'release_tool_updates', id: other.call.id }); emitGates.get(other.call.id).release(); await ends.get(other.call.id).promise;
        probe('unrelated_tool_finalized_while_first_update_blocked');
        controls.push({ kind: 'release_first_update' }); blockedRelease.release(); await ends.get(test.blockedToolId).promise;
      } else if (test.mode === 'abort-blocked-update') {
        const id = test.tools[0].call.id; await started.get(id).promise; controls.push({ kind: 'release_tool_updates', id }); emitGates.get(id).release();
        await blockedEntered.promise; await secondUpdates.get(id).promise; probe('before_abort_with_first_update_blocked');
        controls.push({ kind: 'call_public_abort' }); agent.abort(); await abortObserved.promise; await executeLeaving.promise;
        probe('actual_tool_signal_aborted_after_third_update_before_barrier_release');
        controls.push({ kind: 'release_first_update' }); blockedRelease.release(); await ends.get(id).promise;
      } else {
        assert.equal(test.mode, 'sequential-field-shapes');
        for (const spec of test.tools) { await started.get(spec.call.id).promise; controls.push({ kind: 'release_tool_updates', id: spec.call.id }); emitGates.get(spec.call.id).release(); await ends.get(spec.call.id).promise; }
      }
      await agentEndEntered.promise; probe('agent_end_listener_blocked');
      for (const spec of test.tools) {
        const before = events.length, returned = callbacks.get(spec.call.id)(structuredClone(spec.lateUpdate));
        controls.push({ kind: 'late_callback_after_tool_end', id: spec.call.id, eventCountBefore: before, eventCountAfter: events.length, returned: inspectJsonObservation(returned) });
      }
      probe('late_callbacks_returned_while_agent_end_blocked');
      controls.push({ kind: 'release_agent_end' }); agentEndRelease.release();
      const promptReturn = await running, idleReturn = await idle;
      probe('public_prompt_and_idle_settled');
      const finalState = inspectJsonObservation(agent.state);
      const beforeIdleLate = events.length, idleLateReturn = callbacks.get(test.tools[0].call.id)(structuredClone(test.tools[0].lateUpdate));
      controls.push({ kind: 'late_callback_after_idle', id: test.tools[0].call.id, eventCountBefore: beforeIdleLate, eventCountAfter: events.length, returned: inspectJsonObservation(idleLateReturn) });
      assert.equal(requestCount, 1); assert.equal(events.at(-1).event.json.type, 'agent_end'); assert.equal(agent.state.isStreaming, false); assert.equal(agent.signal, undefined);
      cases.push({ caseId: test.caseId, mode: test.mode, initialState, requests, providerFrames, events, listenerTrace, toolTrace, hooks, controls, observations, finalState, promptReturn: inspectJsonObservation(promptReturn), idleReturn: inspectJsonObservation(idleReturn), metrics: { callbackCount: toolTrace.filter(item => item.kind === 'invoke_update').length, lateCallbackCount: test.tools.length + 1, acceptedUpdateCount: events.filter(item => item.event.json.type === 'tool_execution_update').length, maxActiveFirstListeners: maxActiveListeners } });
    }
    return { observations: { cases, seams: { source: 'Whole unchanged Agent public constructor/prompt/subscribe/abort/waitForIdle; unchanged agent-loop execution/finalization', stream: 'Authored AssistantMessageEventStream frames, one fake request per case', tools: 'Authored execute callbacks/results/errors, beforeToolCall/afterToolCall hook returns, finishTurn action end', barriers: 'Explicit promise gates; no sleeps or source scheduler replacement', childDateNowOverride: fixture.clock, jsonObservation: 'JSON.stringify value plus own-undefined/function/Set inventories; no field filtering', networkAndProcessesBlocked: true } }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) };
  } finally { Date.now = originalNow; }
}
