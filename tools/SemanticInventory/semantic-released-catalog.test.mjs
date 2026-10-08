// Root-run pure checks: builtin fixture construction + read-only acquired artifacts; no writes/process/native.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import { canonicalRawJson } from '../CompatibilityReport/raw-json.mjs';
import { repo, readPlan, inspectReleaseArchive, inspectCatalog, parseArgs, assertPlan, admissionRows, hash, confined } from './setup-semantic-released-catalog.mjs';

const plan = readPlan(), cases = [], clone = value => JSON.parse(JSON.stringify(value));
function test(name, action) { try { action(); cases.push({ name, passed: true }); } catch (error) { cases.push({ name, passed: false, error: { name: error.name, message: error.message } }); } }
function reject(name, action) { test(name, () => assert.throws(action)); }
function archive(rows, mutate) { const blocks = []; for (const row of rows) { const data = Buffer.from(row.data ?? 'x'), h = Buffer.alloc(512); h.write(row.name); h.write('0000644\0', 100); h.write('0000000\0', 108); h.write('0000000\0', 116); h.write(data.length.toString(8).padStart(11, '0') + '\0', 124); h.write('00000000000\0', 136); h.fill(32, 148, 156); h.write(row.type ?? '0', 156); if (row.link) h.write(row.link, 157); h.write('ustar\0' + '00', 257); const sum = h.reduce((a, b) => a + b, 0); h.write(sum.toString(8).padStart(6, '0') + '\0 ', 148); blocks.push(h, data, Buffer.alloc((512 - data.length % 512) % 512)); } const tar = Buffer.concat([...blocks, Buffer.alloc(1024)]); mutate?.(tar); return gzipSync(tar); }
const inspect = (rows, caps = plan.limits, mutate) => inspectReleaseArchive(archive(rows, mutate), caps);
test('exact release root regular member retained byte-for-byte', () => { const x = inspect([{ name: 'pi-0.99.1/a.json', data: '{"n":0.123456789012345678901}' }]); assert.equal(x.files.get('a.json').toString(), '{"n":0.123456789012345678901}'); assert.equal(x.memberCount, 1); });
test('exact release root directory plus member', () => { const x = inspect([{ name: 'pi-0.99.1/', type: '5', data: '' }, { name: 'pi-0.99.1/a', data: 'x' }]); assert.equal(x.directories, 1); });
for (const [name, member] of [['absolute', '/pi-0.99.1/a'], ['traversal', 'pi-0.99.1/../a'], ['dot component', 'pi-0.99.1/./a'], ['outside prefix', 'pi-0.99.10/a'], ['drive', 'C:/pi-0.99.1/a'], ['ADS', 'pi-0.99.1/a:stream'], ['backslash', 'pi-0.99.1/a\\b'], ['Windows device', 'pi-0.99.1/con.txt'], ['superscript device', 'pi-0.99.1/com\u00b9.txt'], ['trailing dot', 'pi-0.99.1/a.'], ['trailing space', 'pi-0.99.1/a '], ['empty component', 'pi-0.99.1//a'], ['control', 'pi-0.99.1/a\u0001']]) reject(name + ' path rejected', () => inspect([{ name: member }]));
reject('unapproved publisher root rejected', () => inspectReleaseArchive(archive([{ name: 'package/a' }]), plan.limits, 'package'));
reject('duplicate file rejected', () => inspect([{ name: 'pi-0.99.1/a' }, { name: 'pi-0.99.1/a' }]));
reject('case-colliding file rejected', () => inspect([{ name: 'pi-0.99.1/a' }, { name: 'pi-0.99.1/A' }]));
reject('file-as-parent rejected', () => inspect([{ name: 'pi-0.99.1/a' }, { name: 'pi-0.99.1/a/b' }]));
reject('directory data rejected', () => inspect([{ name: 'pi-0.99.1/a/', type: '5', data: 'x' }]));
for (const type of ['1','2','3','4','6','g','L','K']) reject('link/device/global/extension ' + type + ' rejected', () => inspect([{ name: 'pi-0.99.1/a', type, data: '' }]));
reject('regular link target rejected', () => inspect([{ name: 'pi-0.99.1/a', link: 'b' }]));
reject('header checksum rejected', () => inspect([{ name: 'pi-0.99.1/a' }], plan.limits, tar => { tar[100] ^= 1; }));
reject('nonzero padding rejected', () => inspect([{ name: 'pi-0.99.1/a' }], plan.limits, tar => { tar[513] = 1; }));
reject('terminal bytes rejected', () => inspect([{ name: 'pi-0.99.1/a' }], plan.limits, tar => { tar[tar.length - 1] = 1; }));
reject('compressed cap rejected', () => inspect([{ name: 'pi-0.99.1/a' }], { ...plan.limits, compressedBytes: 1 }));
reject('expanded cap rejected', () => inspect([{ name: 'pi-0.99.1/a' }], { ...plan.limits, expandedBytes: 512 }));
reject('member count cap rejected', () => inspect([{ name: 'pi-0.99.1/a' }, { name: 'pi-0.99.1/b' }], { ...plan.limits, members: 1 }));
reject('member byte cap rejected', () => inspect([{ name: 'pi-0.99.1/a', data: 'ab' }], { ...plan.limits, memberBytes: 1 }));
reject('component byte cap rejected', () => inspect([{ name: 'pi-0.99.1/long' }], { ...plan.limits, componentUtf8Bytes: 3 }));
reject('depth cap rejected', () => inspect([{ name: 'pi-0.99.1/a/b' }], { ...plan.limits, depth: 2 }));
test('default mode is read-only exact root', () => assert.equal(parseArgs([], plan).mode, '--check'));
test('explicit oracle admitted for read-only check', () => assert.equal(parseArgs(['--check', '--oracle', plan.oracle], plan).mode, '--check'));
for (const args of [['--unknown'], ['--check', '--check'], ['--prepare', '--admit'], ['--oracle'], ['--oracle', plan.origin.root], ['--oracle', plan.oracle, '--oracle', plan.oracle]]) reject('strict args ' + JSON.stringify(args), () => parseArgs(args, plan));
reject('confinement rejects parent', () => confined(plan.oracle, join(plan.oracle, '..', 'foreign')));
for (const [label, mutate] of [['omitted catalog member', x => x.catalog.files.pop()], ['catalog precision/hash changed', x => { x.catalog.files[0].sha256 = '0'.repeat(64); x.catalog.files[0].bytes++; }], ['catalog wrong namespace', x => x.catalog.files[0].path = 'packages/ai/other.json'], ['network allowed', x => x.policy.network = true], ['phase completion', x => x.phaseGatesPassed.push('P1')], ['wrong publisher root', x => x.release.root = 'package']]) reject(label, () => { const x = clone(plan); mutate(x); assertPlan(x); });
const originReceipt = JSON.parse(readFileSync(join(plan.origin.root, plan.origin.receipt), 'utf8'));
test('complete4538 plus43 identity set is4581', () => { const rows = admissionRows(originReceipt, plan.catalog.files); assert.equal(rows.length, 4581); assert.equal(originReceipt.files.length, 4538); });
reject('catalog collision rejected', () => admissionRows(originReceipt, [...plan.catalog.files.slice(1), { ...plan.catalog.files[0], path: originReceipt.files.find(row => row.path.startsWith('upstream/')).path.slice('upstream/'.length) }]));
const sourceBytes = readFileSync(join(repo, 'artifacts/released-baseline/pi-0.99.1-source.tar.gz'));
test('actual official source archive passes strict inert inspection and complete source/data identity', () => { assert.equal(hash(sourceBytes), plan.release.archiveSha256); const origin = { originalSourceRows: originReceipt.files.filter(row => row.path.startsWith('upstream/') && !row.path.startsWith('upstream/node_modules/')) }; const result = inspectCatalog(sourceBytes, plan, origin, JSON.parse, canonicalRawJson); assert.equal(result.selected.size, 43); assert.equal(result.evidence.selectedPayloadBytes, 902551); assert.equal(result.evidence.originalSourceComparisons.length, 2093); const conversions = result.evidence.originalSourceComparisons.filter(row => row.recordedGitToCheckoutConversion); assert.equal(conversions.length, 2); assert(conversions.every(row => row.rawPublisherEqualsPriorCheckout && row.oldPayloadRewritten === false)); assert.deepEqual(conversions.map(row => row.canonicalGit.sha256), plan.release.recordedSourceIdentities.map(row => row.sha256)); });
reject('changed acquired source bytes rejected before parse', () => { const b = Buffer.from(sourceBytes); b[0] ^= 1; inspectCatalog(b, plan, {}, JSON.parse, canonicalRawJson); });
console.log(JSON.stringify({ scope: 'root-run pure/read-only inert artifact checks; no capture/native/process/network', cases, passed: cases.filter(x => x.passed).length, failed: cases.filter(x => !x.passed).length }));
if (cases.some(x => !x.passed)) process.exitCode = 1;
