import assert from "node:assert/strict";
import test from "node:test";
import { compareJson } from "./canonical-json.mjs";
import { canonicalRawJson, compareRawJson, parseJsonSupported } from "./raw-json.mjs";

test("object keys may differ in order, including nested keys", () => {
  assert.equal(compareJson({ b: [1, { z: 2, a: 3 }], a: null }, { a: null, b: [1, { a: 3, z: 2 }] }), true);
});

test("raw comparison retains integers and decimal lexemes beyond JS precision", () => {
  assert.equal(compareRawJson('{"n":9007199254740992}', '{"n":9007199254740993}'), false);
  assert.equal(compareRawJson('{"n":0.123456789012345678901}', '{"n":0.123456789012345678902}'), false);
  assert.equal(compareRawJson('{"b":1,"a":[null,"π"]}', '{"a":[null,"π"],"b":1}'), true);
  assert.throws(() => canonicalRawJson('{"x":1,"x":1}'), /Duplicate/);
  assert.throws(() => canonicalRawJson('{"x":1,"\\u0078":2}'), /Duplicate/);
  assert.throws(() => parseJsonSupported('{"n":9007199254740993}'), /precision/);
  assert.throws(() => parseJsonSupported('{"n":0.123456789012345678901}'), /precision/);
  assert.deepEqual(parseJsonSupported('{"n":0.000001}'), { n: 0.000001 });
});

test("invalid raw evidence fails explicitly", () => {
  for (const raw of ['{"x":1,}', '[1,]', '[01]', 'NaN', 'true false', '"raw\nnewline"', '"\\x"']) assert.throws(() => canonicalRawJson(raw));
});

test("deliberate semantic mutations remain visible", () => {
  const original = { events: ["start", "done"], text: "é\r\nC:\\temp\\π", signature: "opaque==", id: "call-1", usage: { output: 0 } };
  const mutations = [
    { ...original, events: ["done", "start"] },
    { ...original, events: ["start"] },
    { ...original, text: "é\nC:\\temp\\π" },
    { ...original, text: "e\u0301\r\nC:\\temp\\π" },
    { ...original, signature: "different==" },
    { ...original, signature: null },
    { ...original, id: "call-2" },
    { ...original, usage: {} },
    { ...original, usage: { output: null } },
    { ...original, usage: { output: 0.000000001 } },
    { ...original, unknownOpaque: { bytes: [2, 1] } },
  ];
  for (const mutation of mutations) assert.equal(compareJson(original, mutation), false);
  assert.equal(compareJson({}, { optional: null }), false);
  assert.equal(compareJson(["call-1", "call-1"], ["call-1", "call-2"]), false);
});
