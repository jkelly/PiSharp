// Prints Fixtures/tools.upstream.json: what the pinned durable coding tools (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs;
// packages/durable/src/tools) produce, for ToolsDifferentialCases.cs and ToolsReadDifferentialCases.cs to replay:
// - declarations: name, description and JSON.stringify(parameters) of createReadTool, createWriteTool, createEditTool, createBashTool and
//   createPowerShellTool, plus their outputLimits and replay;
// - read: per seed of test/tools-read-differential.test.ts (same generator, offsets and limits), a digest of the file and each trial's
//   outcome (see `readOutcome`), the first 16 hex digits of a SHA-256;
// - diffs: per seed of a seeded line-edit generator, a digest of generateDiffString and generateUnifiedPatch (jsdiff 8.0.4 underneath);
// - edits: the edit tool over NodeExecutionEnv for authored cases: result text, details, the file afterwards (hex), or the error message;
// - bash: the bash tool over NodeExecutionEnv for authored commands: streamed output, error message and diagnostics (spill paths masked).
// From v1.2.0 tool results carry `output` where they carried `content`, and the file adds what is new there: each declaration's
// structuredOutputSchema, callers and deferred; each bash case's isError and structuredOutput (its `output` digested past 400 characters,
// a spill path masked) and the result's own diagnostics after the streamed ones; and `images`: the read tool, without an image processor,
// over small hand-made images and their limits. The tools run with a stand-in ToolExecutionApi, not a Harness: retainedOutput() is
// everything written so far (truncated false), agent() names no model and models resolves none, so read takes images within the default
// limits.
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings --conditions=source --experimental-strip-types capture-tools.mjs <pi checkout> --commit <sha> > tools.upstream.json
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import { pinnedArguments } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-tools.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
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
  ...(pin.output ? {
    structuredOutputSchema: tool.structuredOutputSchema === undefined ? null : JSON.stringify(tool.structuredOutputSchema),
    callers: tool.callers ?? null,
    deferred: tool.deferred ?? null,
  } : {}),
}));
/** The result's content list: `content` at v1.1.0, `output` from v1.2.0. */
const body = (outcome) => (pin.output ? outcome.output : outcome.content);

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
  const parts = ['result', String(outcome.isError), String(body(outcome).length)];
  for (const part of body(outcome)) parts.push(part.type, part.text);
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
const api = (env) => ({
  env, output: () => {}, outputWindow: undefined, diagnostic: () => {}, details: async () => {},
  // v1.2.0 tools also use these; v1.1.0 tools never call them.
  retainedOutput: () => ({ text: '', truncated: false }), agent: async () => ({}), models: { getModel: () => undefined },
});

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
        : { text: body(outcome)[0].text, details: outcome.details }),
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
    const callApi = {
      ...api(env),
      output: (chunk) => output.push(String(chunk)),
      diagnostic: (diagnostic) => diagnostics.push(diagnostic),
      retainedOutput: () => ({ text: output.join(''), truncated: false }),
    };
    const outcome = await attempt(() => tool.execute(args, callApi, BACKGROUND_CONTEXT));
    const text = output.join('');
    bash.push({
      name,
      args,
      output: text.length > 400 ? { length: text.length, digest: digest(text) } : text,
      error: 'error' in outcome ? outcome.error : null,
      content: 'error' in outcome ? null : (body(outcome) ?? null),
      ...(pin.output && !('error' in outcome) ? { isError: outcome.isError ?? null, structuredOutput: shellOutput(outcome.structuredOutput) } : {}),
      diagnostics: [...diagnostics, ...(pin.output && !('error' in outcome) ? outcome.diagnostics ?? [] : [])].map((diagnostic) => ({
        severity: diagnostic.severity,
        code: diagnostic.code ?? null,
        message: diagnostic.message.replace(/^Full output: .+$/, 'Full output: <spill>'),
      })),
    });
  }
}
rmSync(bashDir, { recursive: true, force: true });

/** bash's structured output with long output digested and a spill path masked, as for the streamed output. */
function shellOutput(value) {
  if (value === undefined) return null;
  const { output, fullOutputPath, ...rest } = value;
  return {
    output: output.length > 400 ? { length: output.length, digest: digest(output) } : output,
    ...(fullOutputPath === undefined ? {} : { fullOutputPath: '<spill>' }),
    ...rest,
  };
}

// ─── images (v1.2.0): read without an image processor ───────────────────
const u32be = (n) => [(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255];
const u32le = (n) => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255];
const u16le = (n) => [n & 255, (n >>> 8) & 255];
const u16be = (n) => [(n >>> 8) & 255, n & 255];
const ascii = (text) => [...Buffer.from(text, 'latin1')];
/** A PNG declaring `width` by `height` in its IHDR, then an IDAT of `padding` bytes and IEND (CRCs zero: read checks only the header). */
function png(width, height, padding = 0) {
  return Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, ...ascii('IHDR'),
    ...u32be(width), ...u32be(height), 8, 6, 0, 0, 0, 0, 0, 0, 0,
    ...u32be(padding), ...ascii('IDAT'), ...new Array(padding).fill(0), 0, 0, 0, 0, 0, 0, 0, 0, ...ascii('IEND'), 0, 0, 0, 0]);
}
const IMAGE_CASES = [
  ['png', png(2, 3)],
  ['png-too-wide', png(2001, 10)],
  ['png-too-tall', png(10, 2001)],
  ['png-too-large', png(10, 10, 3_500_000)],
  ['gif', Uint8Array.from([...ascii('GIF89a'), ...u16le(4), ...u16le(5), 0, 0, 0, 0x3b])],
  ['jpeg', Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc0, 0, 11, 8, ...u16be(6), ...u16be(7), 1, 1, 0x11, 0, 0xff, 0xd9])],
  ['jpeg-no-size', Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xd9, 0, 0, 0, 0, 0, 0, 0, 0])],
  ['webp-vp8l', Uint8Array.from([...ascii('RIFF'), 26, 0, 0, 0, ...ascii('WEBPVP8L'), 14, 0, 0, 0, 0x2f,
    ...u32le(((9 - 1) & 0x3fff) | (((7 - 1) & 0x3fff) << 14)), 0, 0, 0, 0, 0])],
  ['bmp', Uint8Array.from([...ascii('BM'), 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0, 40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0,
    ...new Array(40).fill(0)])],
  ['apng', Uint8Array.from([...png(2, 2).slice(0, 33), 0, 0, 0, 8, ...ascii('acTL'), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0])],
];
const images = [];
if (pin.output) {
  const imageDir = mkdtempSync(join(tmpdir(), 'pi-durable-image-capture-'));
  const env = new NodeExecutionEnv({ cwd: imageDir });
  const tool = tools.createReadTool();
  const long = (text) => (text.length > 400 ? { length: text.length, digest: digest(text) } : text);
  for (const [name, bytes] of IMAGE_CASES) {
    writeFileSync(join(imageDir, `${name}.bin`), bytes);
    const outcome = await attempt(() => tool.execute({ path: `${name}.bin` }, api(env), BACKGROUND_CONTEXT));
    images.push({
      name,
      file: long(hex(bytes)),
      ...('error' in outcome
        ? { error: outcome.error }
        : {
          isError: outcome.isError ?? false,
          output: body(outcome).map((part) => (part.type === 'image'
            ? { type: 'image', mimeType: part.mimeType, data: long(part.data) }
            : { type: part.type, text: long(part.text) })),
          diagnostics: outcome.diagnostics ?? [],
          details: outcome.details ?? null,
        }),
    });
  }
  rmSync(imageDir, { recursive: true, force: true });
}

console.log(JSON.stringify({ source: pin.commit, declarations, read, diffs, edits, bash, ...(pin.output ? { images } : {}) }, null, 1));
