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
const { Editor } = await import(pathToFileURL(path.join(manifest.upstream,'packages/tui/src/components/editor.ts')).href);
const { TuiAltScreen } = await import(pathToFileURL(path.join(manifest.upstream,'packages/tui/src/tui-alt-screen.ts')).href);
const { visibleWidth, normalizeTerminalOutput } = await import(pathToFileURL(path.join(manifest.upstream,'packages/tui/src/utils.ts')).href);
const observations=[];
for(const fixture of input.cases){
 const writes=[],operations=[],changes=[];let terminalStopped=false;
 const terminal={columns:fixture.columns,rows:fixture.terminalRows,kittyProtocolActive:false,start(){operations.push('start')},stop(){terminalStopped=true;operations.push('stop')},write(data){writes.push(data)},hideCursor(){operations.push('hideCursor')},showCursor(){operations.push('showCursor')},moveBy(lines){operations.push({moveBy:lines})},clearLine(){operations.push('clearLine')},clearFromCursor(){operations.push('clearFromCursor')},clearScreen(){operations.push('clearScreen')},setTitle(title){operations.push({setTitle:title})},async drainInput(){operations.push('drainInput')}};
 const tui=new TuiAltScreen(terminal,false,process.env.TEMP,{mouse:false});
 const editor=new Editor(tui,{borderColor:value=>value,selectList:{selectedPrefix:value=>value,selectedText:value=>value,description:value=>value,scrollInfo:value=>value,noMatch:value=>value}});
 editor.onChange=value=>changes.push(value);tui.setLayoutRoot(editor);tui.setFocus(fixture.focused?editor:null);
 let error=null,startWrites=[],renderWrites=[],stopWrites=[],componentRows=null;
 try{
  if(fixture.admission==='set')editor.setText(fixture.text);
  else if(fixture.admission==='insert')editor.insertTextAtCursor(fixture.text);
  else editor.handleInput('\x1b[200~'+fixture.text+'\x1b[201~');
  componentRows=[...editor.render(fixture.columns)];
  tui.start();startWrites=[...writes];const start=writes.length;tui.renderNow(true);renderWrites=writes.slice(start);
 }catch(caught){error={name:caught.name,message:caught.message,stack:caught.stack};}
 finally{const start=writes.length;tui.stop({preserveScreen:true});stopWrites=writes.slice(start);await new Promise(resolve=>setImmediate(resolve));}
 observations.push({input:fixture,observation:{text:editor.getText(),expandedText:editor.getExpandedText(),cursor:editor.getCursor(),focused:editor.focused,changes,componentRows,componentRowsWidth:componentRows?.map(visibleWidth)??null,normalizedComponentRows:componentRows?.map(normalizeTerminalOutput)??null,startWrites,renderWrites,stopWrites,terminalOperations:operations,terminalStopped,error}});
}
if(prohibitedEffects.length)throw Error('Forbidden source side effects occurred');
for(const row of loaded.values())if(hash(fs.readFileSync(row.path))!==row.sha256)throw Error('Loaded source changed');
for(const row of Object.values(frozen))if(hash(fs.readFileSync(row.path))!==row.sha256)throw Error('Capture inputs changed');
fs.writeFileSync(output,JSON.stringify({schemaVersion:1,upstreamCommit:manifest.candidate,driver:frozen.driver,sourceManifest:frozen.manifest,inputs:frozen.input,node:manifest.node,loadedFiles:[...loaded.values()],builtinImports:[...builtinImports].sort(),prohibitedEffects,sourceModified:false,boundary:'Public Editor set/insert/paste/render and actual public TuiAltScreen start/renderNow/stop with inert borrowed terminal; no private method replacement, no physical writes, no UI/network/child actions',observations},null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({output,cases:observations.length,sourceErrors:observations.filter(o=>o.observation.error).length,loadedFiles:loaded.size,sha256:hash(fs.readFileSync(output))}));
