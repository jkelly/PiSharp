import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const fixture = parseJsonSupported(readFileSync(process.argv[2], 'utf8'));
const { AssistantMessageFrameEncoder, reduceAssistantMessageFrames } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/assistant-message-frame.ts')).href);
const { parseStreamingJson } = await import(pathToFileURL(resolve(oracle, 'upstream/packages/ai/src/utils/json-parse.ts')).href);
const encoder = new AssistantMessageFrameEncoder();
const partial = structuredClone(fixture.initialMessage);
const emissionSnapshots = [];
const frames = [];
const prefixReductions = [];
let toolJson = '';
function emit(event) {
  emissionSnapshots.push(structuredClone(event));
  const frame = encoder.encode(event);
  if (frame !== undefined) frames.push(structuredClone(frame));
  const reduced = reduceAssistantMessageFrames(frames);
  prefixReductions.push({ afterEmissionIndex: emissionSnapshots.length - 1, frameCount: frames.length, ...(reduced === undefined ? {} : { reducedMessage: structuredClone(reduced) }) });
}
emit({ type: 'start', partial });
for (const operation of fixture.operations) {
  const contentIndex = operation.contentIndex;
  let event;
  switch (operation.type) {
    case 'thinking_start': case 'text_start':
      assert.equal(contentIndex, partial.content.length);
      partial.content.push(structuredClone(operation.content));
      event = { type: operation.type, contentIndex, partial };
      break;
    case 'toolcall_start':
      assert.equal(contentIndex, partial.content.length);
      partial.content.push(structuredClone(operation.toolCall));
      event = { type: operation.type, contentIndex, partial };
      break;
    case 'text_delta':
      partial.content[contentIndex].text += operation.delta;
      event = { type: operation.type, contentIndex, delta: operation.delta, partial };
      break;
    case 'thinking_delta':
      partial.content[contentIndex].thinking += operation.delta;
      event = { type: operation.type, contentIndex, delta: operation.delta, partial };
      break;
    case 'toolcall_delta':
      toolJson += operation.delta;
      partial.content[contentIndex].arguments = parseStreamingJson(toolJson);
      event = { type: operation.type, contentIndex, delta: operation.delta, partial };
      break;
    case 'text_end':
      partial.content[contentIndex] = { type: 'text', text: operation.content, textSignature: operation.textSignature };
      event = { type: operation.type, contentIndex, content: operation.content, partial };
      break;
    case 'thinking_end':
      partial.content[contentIndex] = { type: 'thinking', thinking: operation.content, thinkingSignature: operation.thinkingSignature, redacted: operation.redacted };
      event = { type: operation.type, contentIndex, content: operation.content, partial };
      break;
    case 'toolcall_end':
      partial.content[contentIndex] = structuredClone(operation.toolCall);
      event = { type: operation.type, contentIndex, toolCall: partial.content[contentIndex], partial };
      break;
    default: throw new Error(`Unsupported authored operation ${operation.type}`);
  }
  emit(event);
}
const terminalResult = { ...structuredClone(partial), stopReason: 'toolUse' };
emit({ type: 'done', reason: 'toolUse', message: terminalResult });
const reducedMessage = reduceAssistantMessageFrames(frames);
assert.deepEqual(reducedMessage.content, terminalResult.content);
assert.equal(reducedMessage.stopReason, 'pending', 'Compact frame reduction excludes terminal settlement');
assert.equal(frames.length, fixture.operations.length + 1, 'Done produces no compact frame');
assert.equal(reducedMessage.content[0].thinkingSignature, fixture.finalSignatures.thinking);
assert.equal(reducedMessage.content[1].textSignature, fixture.finalSignatures.text);
assert.equal(reducedMessage.content[2].thoughtSignature, fixture.finalSignatures.tool);
assert.equal(reducedMessage.content[2].namespace, fixture.finalSignatures.namespace);
assert.deepEqual(reducedMessage.content[2].arguments, fixture.finalArguments);
assert.equal(emissionSnapshots[0].partial.content.length, 0, 'Initial emission snapshot must remain empty');
assert.equal(emissionSnapshots.at(-1).message.content.length, 3);
console.log(JSON.stringify({ observations: { emissionSnapshots, frames, prefixReductions, reducedMessage, terminalResult, effects: [], checks: { indexedContentCount: reducedMessage.content.length, signaturesPreserved: true, authoritativeEndsPreserved: true, finalArgumentsPreserved: true, terminalExcludedFromFrames: true, emissionSnapshotsImmutable: true } }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }));
