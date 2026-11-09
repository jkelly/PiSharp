// Independent v1.5.0 evidence. Never upgrades/relabels the historical fixture corpus.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const PIN = 'f9a26365568127461c47a07a37dcb42c140a3ac5';
const here = dirname(fileURLToPath(import.meta.url));
const [upstreamArg, outArg] = process.argv.slice(2);
if (!upstreamArg || !outArg) throw new Error('Usage: node tools/DurableFixtures150/capture.mjs <upstream checkout> <new output directory>');
const upstream = resolve(upstreamArg), out = resolve(outArg);
// An allowlist, not a regex blacklist: credentials, NODE_OPTIONS and provider settings never reach child processes.
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => ['SYSTEMROOT','WINDIR','PATH','PATHEXT','TEMP','TMP','COMSPEC'].includes(key.toUpperCase())));
function run(executable, args, cwd = upstream) {
  if (executable === 'git') args = ['-c', `safe.directory=${upstream.replaceAll('\\', '/')}`, ...args];
  const result = spawnSync(executable, args, { cwd, env, encoding: 'utf8', timeout: 120000, maxBuffer: 8 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(`${executable} ${args.join(' ')} failed: ${result.error ?? result.stderr ?? result.stdout}`);
  return result.stdout.trim();
}
if (run('git', ['rev-parse', 'HEAD']) !== PIN) throw new Error('Refusing a checkout outside the exact v1.5.0 pin');
if (run('git', ['status', '--porcelain', '--untracked-files=no'])) throw new Error('Refusing modified tracked upstream sources');
if (JSON.parse(await readFile(join(upstream, 'packages/durable/package.json'))).version !== '1.5.0') throw new Error('Wrong Durable version');
// pi-ai subpath exports require emitted JS. --noCheck emits the exact checked-out sources without requiring optional
// generated live-provider catalogs, which are absent from Git. No catalog/provider generation or network is performed.
for (const pkg of ['telemetry', 'schema', 'ai']) run(process.execPath, ['node_modules/typescript/bin/tsc', '--noCheck', '-p', `packages/${pkg}/tsconfig.build.json`]);
await mkdir(dirname(out), { recursive: true });
await mkdir(out, { recursive: false });
for (const name of ['first', 'second']) run(process.execPath, ['--no-warnings', '--conditions=source', '--experimental-strip-types', join(here, 'worker.mjs'), upstream, join(out, name)]);
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
async function files(root, prefix = '') {
  const result = [];
  for (const item of (await readdir(join(root, prefix), { withFileTypes: true })).sort((a,b) => a.name.localeCompare(b.name))) {
    const path = prefix ? `${prefix}/${item.name}` : item.name;
    if (item.isDirectory()) result.push(...await files(root, path));
    else result.push({ path, bytes: (await readFile(join(root, path))).length, sha256: sha(await readFile(join(root, path))) });
  }
  return result;
}
// Only provider session IDs are random in these scenarios. Preserve all clocks, sequences, states and event ordering.
function normalize(value) {
  if (Array.isArray(value)) return value.map(normalize);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, key === 'sessionId' && typeof item === 'string' ? '<session>' : normalize(item)]));
  return value;
}
const first = await files(join(out, 'first')), second = await files(join(out, 'second'));
if (JSON.stringify(first.map(x => x.path)) !== JSON.stringify(second.map(x => x.path))) throw new Error('Capture file inventories differ');
const compared = [];
for (const file of first.filter(x => x.path.endsWith('.json'))) {
  const a = JSON.stringify(normalize(JSON.parse(await readFile(join(out, 'first', file.path)))));
  const b = JSON.stringify(normalize(JSON.parse(await readFile(join(out, 'second', file.path)))));
  if (a !== b) throw new Error(`Non-reproducible capture: ${file.path}`);
  compared.push({ path: file.path, normalizedSha256: sha(a) });
}
const sourceFiles = run('git', ['ls-files', 'packages/durable/src', 'packages/ai/src', 'packages/schema/src', 'packages/chord/src', 'packages/telemetry/src']).split(/\r?\n/);
const source = [];
for (const path of sourceFiles) source.push([path, sha(await readFile(join(upstream, path)))]);
await writeFile(join(out, '.gitattributes'), '* -text\n');
await writeFile(join(out, 'manifest.json'), JSON.stringify({
  kind: 'PASS_UPSTREAM_CAPTURE', upstream: { release: 'v1.5.0', commit: PIN, sourceTreeSha256: sha(JSON.stringify(source)), sourceFiles: source.length },
  runtime: { node: process.version, executableSha256: sha(await readFile(process.execPath)) },
  dependencyLockSha256: sha(await readFile(join(upstream, 'package-lock.json'))),
  scenarioSha256: sha(await readFile(join(here, 'worker.mjs'))), captureDriverSha256: sha(await readFile(fileURLToPath(import.meta.url))),
  command: 'node tools/DurableFixtures150/capture.mjs <exact upstream checkout> <new output directory>',
  environment: { inherited: Object.keys(env).sort(), providers: ['faux'], network: 'fetch denied; no provider factories or catalogs loaded' },
  normalization: [{ field: 'sessionId string', reason: 'upstream provider identity uses uuidv7 wall clock and random bits' }],
  comparison: { runs: 2, result: 'equal', compared, sqlite: 'Compare every table row including historical revisions, metadata and upstream-decoded current records; physical SQLite pages are not semantic evidence.', jsonl: 'Compare every parsed JSONL line in every file, including commit order, plus upstream-decoded current records.' },
  runs: { first, second },
}, null, 2) + '\n');
console.log(`Captured two reproducible upstream v1.5.0 runs (${compared.length} JSON comparisons) in ${out}`);
