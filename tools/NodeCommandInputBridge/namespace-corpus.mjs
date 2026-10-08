// Narrow original getter capture, not whole AgentSession or original extension callback qualification.
// Authoring only; coordinator must allocate execution and a fresh report path.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { join, isAbsolute } from 'node:path';
import vm from 'node:vm';
import { createToolLoadoutSnapshot, createToolLoadoutCache, invokeLoadoutPreparation } from './tool-loadout.mjs';
assert.equal(process.argv.length, 6);
assert.equal(process.argv[2], '--source-root'); assert.equal(process.argv[4], '--report');
const root = process.argv[3], reportPath = process.argv[5]; assert(isAbsolute(root) && isAbsolute(reportPath));
const path = join(root, 'packages/coding-agent/src/core/agent-session.ts'), bytes = fs.readFileSync(path);
assert.equal(bytes.length, 155372); assert.equal(createHash('sha256').update(bytes).digest('hex'), '26e76e13456757b0419df26b1f4d9bce9faa28c2588ad66af3e4cc3e9b179e0a');
const source = bytes.toString('utf8'), match = source.match(/getNamespace:\s*(\(name\)\s*=>\s*this\._toolDefinitions\.get\(name\)\?\.definition\.namespace),/u);
assert(match, 'Exact pinned getter expression not found');
const namespace = { name: 'mcp__docs', description: 'original metadata' }, empty = { name: '', description: '' };
const definitions = new Map([['direct', { definition: {} }], ['code', { definition: { namespace } }],
  ['hidden', { definition: { namespace } }], ['empty', { definition: { namespace: empty } }]]);
// Execute only the pinned getter expression in a timeout-bounded built-in VM, over authored maps.
const original = vm.runInNewContext('(function () { return ' + match[1] + '; }).call(subject)',
  { subject: { _toolDefinitions: definitions } }, { timeout: 1000 });
const registered = [...definitions].map(([name, entry]) => ({ declaration: { name, description: name, parameters: {} },
  exposure: name === 'hidden' ? 'hidden' : name === 'code' ? 'codemode' : 'direct',
  ...(entry.definition.namespace === undefined ? {} : { namespace: entry.definition.namespace }) }));
const supplied = { snapshotId: 'original-getter-1', declared: ['empty', 'direct'], callable: ['direct', 'code'], registered };
const facade = createToolLoadoutSnapshot(supplied), cases = [];
for (const name of ['direct', 'code', 'hidden', 'empty', 'CODE', 'mcp__docs', 'unknown']) {
  assert.deepEqual(facade.getNamespace(name), original(name));
  cases.push({ id: 'original-getter-' + name, passed: true, presence: original(name) === undefined ? 'undefined' : 'json' });
}
const cache = createToolLoadoutCache(); let calls = 0;
const changes = await invokeLoadoutPreparation(loadout => { calls++; assert.equal(loadout, cache(supplied));
  return { descriptions: { empty: loadout.getNamespace('code').description }, hiddenDeclarations: ['empty'] }; }, cache(supplied));
assert.equal(calls, 1); assert.equal(changes.descriptions.empty, 'original metadata');
assert.deepEqual(facade.declared.map(x => x.name), ['empty', 'direct']);
const report = { sourceCommit: 'd86654abb8862e201933517d6f1fce9f88dd117f', sourcePath: path, sourceSha256: createHash('sha256').update(bytes).digest('hex'),
  scope: 'Pinned original getNamespace expression only; authored maps and preparation callback; no whole AgentSession or admitted original prepareLoadout hook',
  cases, passed: cases.length, authoredPreparationCalls: calls, hostCapabilitiesGranted: false, wholeProviderOrSessionQualified: false };
fs.writeFileSync(reportPath, JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
