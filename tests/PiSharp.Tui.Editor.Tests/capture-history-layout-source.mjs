import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import childProcess from 'node:child_process';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { registerHooks, syncBuiltinESMExports } from 'node:module';

const args = process.argv.slice(2);
if (args.length !== 6 || args[0] !== '--manifest' || args[2] !== '--inputs' || args[4] !== '--output')
  throw Error('Usage: capture-source.mjs --manifest <frozen-source-manifest> --inputs <frozen-inputs> --output <fresh-output>');
const manifestPath = path.resolve(args[1]), inputPath = path.resolve(args[3]), output = path.resolve(args[5]);
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const driverPath = fileURLToPath(import.meta.url);
const frozen = { driver: { path: driverPath, sha256: hash(fs.readFileSync(driverPath)) },
  manifest: { path: manifestPath, sha256: hash(fs.readFileSync(manifestPath)) },
  input: { path: inputPath, sha256: hash(fs.readFileSync(inputPath)) } };
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8')), input = JSON.parse(fs.readFileSync(inputPath, 'utf8'));
if (manifest.candidate !== input.upstreamCommit || process.version !== manifest.node.version || hash(fs.readFileSync(process.execPath)) !== manifest.node.sha256 || fs.existsSync(output))
  throw Error('Frozen inputs/runtime/source or fresh output differs.');
const allowed = new Map([...manifest.files, ...manifest.packageFiles].map(row => [path.resolve(row.path).toLowerCase(), row]));
const loaded = new Map(), builtinImports = new Set(), prohibitedEffects = [];
for (const method of ['exec', 'execSync', 'execFile', 'execFileSync', 'spawn', 'spawnSync', 'fork'])
  childProcess[method] = () => { prohibitedEffects.push(method); throw Error('Source editor capture forbids child execution.'); };
globalThis.fetch = () => { prohibitedEffects.push('fetch'); throw Error('Source editor capture forbids network.'); };
globalThis.WebSocket = class { constructor() { prohibitedEffects.push('WebSocket'); throw Error('Source editor capture forbids network.'); } };
syncBuiltinESMExports();
registerHooks({
  resolve(specifier, context, next) {
    const result = next(specifier, context);
    if (result.url.startsWith('node:')) {
      if (/^node:(net|http|https|tls|dgram)$/.test(result.url)) throw Error('Network module outside capture boundary.');
      builtinImports.add(result.url);
    } else if (!result.url.startsWith('file:') || !allowed.has(path.resolve(fileURLToPath(result.url)).toLowerCase())) {
      throw Error(`Import outside frozen source/dependency boundary: ${result.url}`);
    }
    return result;
  },
  load(url, context, next) {
    if (url.startsWith('file:')) {
      const file = fileURLToPath(url), row = allowed.get(path.resolve(file).toLowerCase()), bytes = fs.readFileSync(file);
      if (!row || bytes.length !== row.bytes || hash(bytes) !== row.sha256) throw Error(`Loaded bytes changed: ${file}`);
      loaded.set(file, { path: file, bytes: row.bytes, sha256: row.sha256 });
    }
    return next(url, context);
  }
});
const { Editor } = await import(pathToFileURL(path.join(manifest.upstream, 'packages/tui/src/components/editor.ts')).href);
const cases = [];
for (const test of input.cases) {
  const changes = [], submits = []; let renderRequests = 0, renderCalls = 0, lastRenderColumns = null, lastRenderedRows = null;
  const terminal = { rows: 24 };
  const editor = new Editor({ terminal, requestRender() { renderRequests++; } }, { borderColor: value => value, selectList: { selectedPrefix: value => value, selectedText: value => value, description: value => value, scrollInfo: value => value, noMatch: value => value } });
  editor.focused = true; // Documented Focusable component property; no private state replaced.
  editor.onChange = value => changes.push(value); editor.onSubmit = value => submits.push(value);
  const steps = [];
  for (const step of test.steps) {
    if (step.kind === 'set') editor.setText(step.data);
    else if (step.kind === 'input') editor.handleInput(step.data);
    else if (step.kind === 'insert') editor.insertTextAtCursor(step.data);
    else if (step.kind === 'history') editor.addToHistory(step.data);
    else if (step.kind === 'render') { terminal.rows = step.terminalRows; lastRenderColumns = step.columns; lastRenderedRows = [...editor.render(step.columns)]; renderCalls++; }
    else throw Error('Unknown frozen operation.');
    await Promise.resolve();
    const text = editor.getText(), cursor = editor.getCursor();
    const lines = text.split('\n'); let cursorUtf16Offset = cursor.col;
    for (let index = 0; index < cursor.line; index++) cursorUtf16Offset += lines[index].length + 1;
    steps.push({ text, expandedText: editor.getExpandedText(), cursor, cursorUtf16Offset, changes: [...changes], submits: [...submits], renderRequests, renderCalls, lastRenderColumns, terminalRows: terminal.rows, lastRenderedRows });
  }
  cases.push({ id: test.id, input: { steps: test.steps }, observation: { steps } });
}
if (prohibitedEffects.length) throw Error('Forbidden source side effects occurred.');
for (const row of loaded.values()) if (hash(fs.readFileSync(row.path)) !== row.sha256) throw Error('Loaded source changed after capture.');
for (const row of Object.values(frozen)) if (hash(fs.readFileSync(row.path)) !== row.sha256) throw Error('Capture driver/manifest/input changed during execution.');
fs.writeFileSync(output, JSON.stringify({ schemaVersion: 1, upstreamCommit: manifest.candidate,
  driver: frozen.driver, sourceManifest: frozen.manifest, inputs: frozen.input,
  node: manifest.node, loadedFiles: [...loaded.values()], builtinImports: [...builtinImports].sort(), prohibitedEffects,
  sourceModified: false, injectedBoundary: 'Documented constructor TUI terminal.rows/render scheduler and theme, public Focusable.focused property and callbacks; no private method/state replacement',
  observationBoundary: 'Public addToHistory/setText/insertTextAtCursor/handleInput/render operations, complete text/expanded text/cursor, accumulated callbacks/render requests and exact returned render rows. No native implementation/comparison or phase acceptance is asserted. All four existing corpora remain unchanged.', cases }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ output, cases: cases.length, loadedFiles: loaded.size, sha256: hash(fs.readFileSync(output)) }));
