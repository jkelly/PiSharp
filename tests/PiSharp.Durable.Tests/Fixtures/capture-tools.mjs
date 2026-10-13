// Prints Fixtures/tools.upstream.json: what the pinned durable coding tools (Pi abe508e1b89912adde45528136c3221eb69acdd7,
// packages/durable/src/tools) produce, for ToolsDifferentialCases.cs and ToolsReadDifferentialCases.cs to replay:
// - declarations: name, description and JSON.stringify(parameters) of createReadTool, createWriteTool, createEditTool, createBashTool and
//   createPowerShellTool, plus their outputLimits and replay;
// - read: per seed of test/tools-read-differential.test.ts (same generator, offsets and limits), a digest of the file and each trial's
//   outcome (see `readOutcome`), the first 16 hex digits of a SHA-256;
// - diffs: per seed of a seeded line-edit generator, a digest of generateDiffString and generateUnifiedPatch (jsdiff 8.0.4 underneath);
// - edits: the edit tool over NodeExecutionEnv for authored cases: result text, details, the file afterwards (hex), or the error message;
// - bash: the bash tool over NodeExecutionEnv for authored commands: streamed output, error message and diagnostics (spill paths masked).
// Usage, from a Pi checkout at that commit (with its node_modules installed; bash needs a POSIX shell or Git Bash):
//   node --no-warnings --conditions=source --experimental-strip-types capture-tools.mjs <pi checkout> > tools.upstream.json
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const pi = process.argv[2];
if (!pi) throw new Error('usage: capture-tools.mjs <pi checkout>');
const at = (path) => pathToFileURL(join(pi, 'packages/durable/src', path)).href;
const tools = await import(at('tools/index.ts'));
const { generateDiffString, generateUnifiedPatch } = await import(at('tools/edit-diff.ts'));
const { NodeExecutionEnv } = await import(at('env/node.ts'));
const { BACKGROUND_CONTEXT } = await import(pathToFileURL(join(pi, 'node_modules/@earendil-works/chord/src/context/index.ts')).href);
const { DEFAULT_MAX_BYTES } = await import(at('truncate.ts'));

const digest = (text) => createHash('sha256').update(text).digest('hex').slice(0, 16);
const hex = (bytes) => Buffer.from(bytes).toString('hex');

// ─── declarations ─────────────────────────────────────────────────────────
const declarations = [tools.createReadTool(), tools.createWriteTool(), tools.createEditTool(), tools.createBashTool(),
  tools.createPowerShellTool()].map((tool) => ({
  name: tool.name,
  description: tool.description,
  parameters: JSON.stringify(tool.parameters),
  outputLimits: tool.outputLimits === undefined ? null : JSON.stringify(tool.outputLimits),
  replay: tool.replay ?? null,
  prepareArguments: typeof tool.prepareArguments === 'function',
}));

// ─── read (test/tools-read-differential.test.ts, verbatim generator) ─────
function random(seed) {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const PIECES = [[0x0a], [0x0a], [0x61], [0x62, 0x63, 0x64, 0x65], [0xef, 0xbb, 0xbf], [0xc3, 0xa9], [0xe2, 0x82, 0xac],
  [0xf0, 0x9f, 0x98, 0x80], [0xe2, 0x82], [0xff], [0x0d, 0x0a]];
function randomFile(next) {
  const kind = next();
  if (kind < 0.1) return Uint8Array.from([0xef, 0xbb, 0xbf, ...new TextEncoder().encode('x\n'.repeat(2100))]);
  if (kind < 0.2) {
    const line = new Uint8Array(DEFAULT_MAX_BYTES + 200 + Math.floor(next() * 400)).fill(0x61);
    line[DEFAULT_MAX_BYTES - 1 + Math.floor(next() * 3)] = 0xc3;
    return Uint8Array.from([...line, 0x0a, 0x62]);
  }
  if (kind < 0.3) return new TextEncoder().encode(`${'0123456789'.repeat(30)}\n`.repeat(200));
  const bytes = [];
  const pieces = Math.floor(next() * 80);
  for (let index = 0; index < pieces; index++) bytes.push(...PIECES[Math.floor(next() * PIECES.length)]);
  return Uint8Array.from(bytes);
}
const OFFSETS = [undefined, 0, 1, 2, 3, 2.5, -4, 50, 2001, 3000, 1e20, Number.NaN];
const LIMITS = [undefined, 0, 1, 2, -3, -1e20, 1.5, 7, 2000, 2500, 1e20, Number.NaN];
const TRUNCATION_KEYS = ['truncated', 'truncatedBy', 'totalLines', 'totalBytes', 'outputLines', 'outputBytes', 'lastLinePartial',
  'firstLineExceedsLimit', 'maxLines', 'maxBytes'];

/** The outcome as one string: fields joined with NUL, numbers as String(n), absent values as "undefined". */
function readOutcome(outcome) {
  if ('error' in outcome) return ['error', outcome.error].join('\0');
  const parts = ['result', String(outcome.isError), String(outcome.content.length)];
  for (const part of outcome.content) parts.push(part.type, part.text);
  if (outcome.details === undefined) parts.push('no-details');
  else {
    parts.push('details', Object.keys(outcome.details).join(','), Object.keys(outcome.details.truncation).join(','));
    for (const key of TRUNCATION_KEYS) parts.push(String(outcome.details.truncation[key]));
  }
  parts.push(String(outcome.diagnostics.length));
  for (const diagnostic of outcome.diagnostics) parts.push(diagnostic.severity, String(diagnostic.code), diagnostic.message);
  return parts.join('\0');
}
async function attempt(run) {
  try {
    return await run();
  } catch (error) {
    return { error: error.message };
  }
}
const api = (env) => ({ env, output: () => {}, outputWindow: undefined, diagnostic: () => {}, details: async () => {} });

const readDir = mkdtempSync(join(tmpdir(), 'pi-durable-read-capture-'));
const read = [];
{
  const env = new NodeExecutionEnv({ cwd: readDir });
  const tool = tools.createReadTool();
  for (let seed = 1; seed <= 400; seed++) {
    const next = random(seed);
    const file = randomFile(next);
    writeFileSync(join(readDir, 'f.txt'), file);
    const trials = [];
    for (let trial = 0; trial < 4; trial++) {
      const offset = OFFSETS[Math.floor(next() * OFFSETS.length)];
      const limit = LIMITS[Math.floor(next() * LIMITS.length)];
      const args = { path: 'f.txt', ...(offset === undefined ? {} : { offset }), ...(limit === undefined ? {} : { limit }) };
      const outcome = await attempt(() => tool.execute(args, api(env), BACKGROUND_CONTEXT));
      trials.push(digest(`${String(offset)}\0${String(limit)}\0${readOutcome(outcome)}`));
    }
    read.push({ seed, file: digest(hex(file)), trials });
  }
}
rmSync(readDir, { recursive: true, force: true });

// ─── diffs: a seeded line-edit generator ─────────────────────────────────
const WORDS = ['alpha', 'beta', 'gamma', '', '  indented', 'x', 'y', 'tail  ', '\u00E9'];
function diffPair(next) {
  const lines = [];
  const count = Math.floor(next() * 14);
  for (let index = 0; index < count; index++) lines.push(WORDS[Math.floor(next() * WORDS.length)]);
  const edited = [];
  for (const line of lines) {
    const roll = next();
    if (roll < 0.55) edited.push(line);
    else if (roll < 0.7) continue;
    else if (roll < 0.85) edited.push(WORDS[Math.floor(next() * WORDS.length)]);
    else edited.push(line, WORDS[Math.floor(next() * WORDS.length)]);
  }
  if (next() < 0.3) edited.unshift(WORDS[Math.floor(next() * WORDS.length)]);
  const oldEnd = next() < 0.7 ? '\n' : '';
  const newEnd = next() < 0.7 ? '\n' : '';
  const oldText = lines.length === 0 ? '' : lines.join('\n') + oldEnd;
  const newText = edited.length === 0 ? '' : edited.join('\n') + newEnd;
  return { oldText, newText };
}
const diffs = [];
for (let seed = 1; seed <= 600; seed++) {
  const { oldText, newText } = diffPair(random(seed));
  const display = generateDiffString(oldText, newText);
  const patch = generateUnifiedPatch('file.txt', oldText, newText);
  diffs.push(digest([oldText, newText, display.diff, String(display.firstChangedLine), patch].join('\0')));
}

// ─── edits: the edit tool over a real file ───────────────────────────────
const EDIT_CASES = [
  ['single', 'hello world\n', [['world', 'durable']]],
  ['disjoint', 'alpha\nbeta\ngamma\ndelta\n', [['alpha\n', 'ALPHA\n'], ['gamma\n', 'GAMMA\n']]],
  ['multiline', 'a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\n', [['c\nd', 'C\nD\nD2'], ['k\n', '']]],
  ['far-apart', Array.from({ length: 40 }, (_, i) => `line ${i + 1}`).join('\n') + '\n', [['line 3\n', 'LINE 3\n'], ['line 30\n', 'LINE 30\n']]],
  ['no-trailing-newline', 'one\ntwo', [['two', 'TWO']]],
  ['append-at-end', 'one\ntwo', [['two', 'two\nthree\n']]],
  ['delete-all', 'only\n', [['only\n', '']]],
  ['crlf', 'one\r\ntwo\r\nthree\r\n', [['two', 'TWO']]],
  ['crlf-old-text', 'one\r\ntwo\r\nthree\r\n', [['one\r\ntwo', '1\r\n2']]],
  ['bom', '\uFEFFone\ntwo\n', [['one', 'ONE']]],
  ['bom-crlf', '\uFEFFone\r\ntwo\r\n', [['two', 'TWO']]],
  ['fuzzy-quotes', 'say \u201Chello\u201D and \u2018bye\u2019\nkeep \u201Cthis\u201D\n', [['say "hello"', 'say "hi"']]],
  ['fuzzy-trailing-space', 'value = 1   \nnext\n', [['value = 1\nnext', 'value = 2\nnext']]],
  ['fuzzy-dash', 'a \u2014 b\nc \u2013 d\n', [['a - b', 'a + b']]],
  ['fuzzy-nbsp', 'x\u00A0y\nz\n', [['x y', 'xy']]],
  ['fuzzy-keeps-other-lines', 'keep\u2019s   \nfix \u201Cme\u201D\nkeep\u2019s   \n', [['fix "me"', 'fixed']]],
  ['fuzzy-nfkc', '\uFB01le name\nnext\n', [['file name', 'renamed']]],
  ['not-found', 'foo foo foo', [['bar', 'baz']]],
  ['duplicate', 'foo foo foo', [['foo', 'bar']]],
  ['duplicate-multi', 'a\nb\na\n', [['b', 'B'], ['a', 'A']]],
  ['not-found-multi', 'a\nb\n', [['a', 'A'], ['zzz', 'Z']]],
  ['overlap', 'one\ntwo\nthree\n', [['one\ntwo\n', 'ONE\nTWO\n'], ['two\nthree\n', 'TWO\nTHREE\n']]],
  ['empty-old', 'abc', [['', 'x']]],
  ['empty-old-multi', 'abc', [['a', 'A'], ['', 'x']]],
  ['no-change', 'same\n', [['same', 'same']]],
  ['no-change-multi', 'a b\n', [['a', 'a'], ['b', 'b']]],
  ['whitespace-old-text', 'a  b\n', [['  ', ' ']]],
  ['emoji', 'smile \u{1F600} here\n', [['\u{1F600}', '\u{1F601}']]],
];
const editDir = mkdtempSync(join(tmpdir(), 'pi-durable-edit-capture-'));
const edits = [];
{
  const env = new NodeExecutionEnv({ cwd: editDir });
  const tool = tools.createEditTool();
  for (const [name, content, pairs] of EDIT_CASES) {
    writeFileSync(join(editDir, 'edit.txt'), content);
    const args = { path: 'edit.txt', edits: pairs.map(([oldText, newText]) => ({ oldText, newText })) };
    const outcome = await attempt(() => tool.execute(args, api(env), BACKGROUND_CONTEXT));
    edits.push({
      name,
      content,
      edits: args.edits,
      ...('error' in outcome
        ? { error: outcome.error }
        : { text: outcome.content[0].text, details: outcome.details }),
      after: hex(readFileSync(join(editDir, 'edit.txt'))),
    });
  }
}
rmSync(editDir, { recursive: true, force: true });

// ─── bash ────────────────────────────────────────────────────────────────
const BASH_CASES = [
  ['echo', { command: 'echo hello' }],
  ['printf-no-newline', { command: 'printf small' }],
  ['exit-code', { command: 'printf failed; exit 7' }],
  ['stderr-only', { command: 'printf oops >&2; exit 2' }],
  ['utf8', { command: "printf 'h\\303\\251llo\\n'" }],
  ['spill-and-fail', { command: 'i=1; while [ $i -le 3000 ]; do echo line-$i; i=$((i + 1)); done; exit 7' }],
  ['timeout', { command: 'sleep 2', timeout: 0.05 }],
  ['invalid-timeout', { command: 'echo never', timeout: -1 }],
  ['too-long-timeout', { command: 'echo never', timeout: 1e10 }],
];
const bashDir = mkdtempSync(join(tmpdir(), 'pi-durable-bash-capture-'));
const bash = [];
{
  const env = new NodeExecutionEnv({ cwd: bashDir });
  const tool = tools.createBashTool();
  for (const [name, args] of BASH_CASES) {
    const output = [];
    const diagnostics = [];
    const callApi = { ...api(env), output: (chunk) => output.push(String(chunk)), diagnostic: (diagnostic) => diagnostics.push(diagnostic) };
    const outcome = await attempt(() => tool.execute(args, callApi, BACKGROUND_CONTEXT));
    const text = output.join('');
    bash.push({
      name,
      args,
      output: text.length > 400 ? { length: text.length, digest: digest(text) } : text,
      error: 'error' in outcome ? outcome.error : null,
      content: 'error' in outcome ? null : (outcome.content ?? null),
      diagnostics: diagnostics.map((diagnostic) => ({
        severity: diagnostic.severity,
        code: diagnostic.code ?? null,
        message: diagnostic.message.replace(/^Full output: .+$/, 'Full output: <spill>'),
      })),
    });
  }
}
rmSync(bashDir, { recursive: true, force: true });

console.log(JSON.stringify({ source: 'abe508e1b89912adde45528136c3221eb69acdd7', declarations, read, diffs, edits, bash }, null, 1));
