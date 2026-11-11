// Offline verification against frozen Pi v1.5.2, run from the implementation worktree root.
import fs from 'node:fs';
import vm from 'node:vm';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

const native = fs.readFileSync('src/PiSharp.Codemode/Runtime/codemode-prelude.js', 'utf8').replaceAll('\r\n', '\n');
const body = native.slice(native.indexOf('\n') + 1);
const source = fs.readFileSync('../upstream-v152/packages/codemode/src/runtime/prelude-source.ts', 'utf8').replaceAll('\r\n', '\n');
const evaluated = vm.runInNewContext(source.replaceAll('export const ', 'const ').replace('PRELUDE_SOURCE: string', 'PRELUDE_SOURCE') + '\nPRELUDE_SOURCE');
assert(body === evaluated, 'native body must equal evaluated v1.5.2 PRELUDE_SOURCE');
// The fingerprint hashes UTF-16 code units, as in the upstream deterministic engine.
let preludeHash = 2166136261;
for (let index = 0; index < evaluated.length; index++)
  preludeHash = Math.imul(preludeHash ^ evaluated.charCodeAt(index), 16777619) >>> 0;
preludeHash = preludeHash.toString(16).padStart(8, '0');
assert.equal(preludeHash, '4dce1bde', 'current Pi 1.5.2 FNV-1a pin');

async function run(code) {
  const events = [];
  // A plain global is required: Node's contextified proxy changes descriptor behavior during lockdown.
  const context = vm.createContext(vm.constants.DONT_CONTEXTIFY);
  vm.runInContext('delete globalThis.console', context);
  const init = vm.runInContext(body, context);
  const runtime = init((...event) => events.push(event), '[]', '[]', '{}');
  runtime.run(vm.runInContext('(async () => {' + code + '\n})', context));
  await new Promise(setImmediate);
  return events;
}

const events = await run('for(const level of ["log","info","warn","error","debug"]) console[level]("avg",NaN,Infinity,-Infinity,{a:1}); text(NaN);text(Infinity);text(-Infinity);console.log({a:NaN,b:Infinity,c:-Infinity});');
const outputs = events.filter(x => x[0] === 'output');
assert.equal(outputs.length, 9);
for (const event of outputs.slice(0,5)) assert.deepEqual(event.slice(0,3), ['output','console','avg NaN Infinity -Infinity {"a":1}']);
assert.deepEqual(outputs.slice(5,8).map(x => x.slice(0,3)), [['output','text','NaN'],['output','text','Infinity'],['output','text','-Infinity']]);
assert.deepEqual(outputs[8].slice(0,3), ['output','console','{"a":null,"b":null,"c":null}']);
assert.equal(events.at(-1)[0], 'done');
assert.equal(events.at(-1)[1], true);
for (const value of ['NaN', 'Infinity', '-Infinity']) {
  const result = await run('throw ' + value);
  assert.equal(result[0][0], 'done');
  assert.equal(result[0][1], false);
  assert.equal(JSON.parse(result[0][2]).message, value);
}
console.log(JSON.stringify({status:'PASS', upstreamBodyIdentical:true, sha256:crypto.createHash('sha256').update(body).digest('hex'), preludeHash, consoleLevels:5, textPrimitives:3, thrownPrimitives:3}));
