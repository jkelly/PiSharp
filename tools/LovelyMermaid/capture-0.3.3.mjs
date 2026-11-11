// Capture actual output from the exact npm library; never calls the native implementation.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const [root, corpusFile, output] = process.argv.slice(2);
if (!root || !corpusFile || !output) throw new Error('usage: node capture-0.3.3.mjs <exact package root> <corpus.json> <output.json>');
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
if (pkg.name !== 'lovely-mermaid' || pkg.version !== '0.3.3') throw new Error('Expected lovely-mermaid@0.3.3');
const receipt = JSON.parse(readFileSync(new URL('./source-0.3.3.json', import.meta.url), 'utf8'));
for (const {path, sha256} of receipt.files) {
  const hash = createHash('sha256').update(readFileSync(join(root, path))).digest('hex');
  if (hash !== sha256) throw new Error(`Reference source changed: ${path}`);
}
const lib = await import(pathToFileURL(join(root, 'dist/index.js')).href);
const corpus = JSON.parse(readFileSync(corpusFile, 'utf8'));
const customTheme = {border:'90',text:'37',edge:'36',edgeLabel:'37',title:'1;36'};
const cases = {};
for (const {id, src} of corpus) {
  if (Object.hasOwn(cases, id)) throw new Error(`Duplicate case: ${id}`);
  const render = lib.render(src);
  const detail = !/^(fuzz|large|cap|canvas|prefix)-/.test(id);
  cases[id] = {
    kind: lib.diagramKind(src), render,
    ...(detail ? {
      ansi: render === null ? null : lib.toAnsi(render),
      themed: render === null ? null : lib.toAnsi(render, customTheme),
      box: lib.sourceBox(src), box30: lib.sourceBox(src, 30),
    } : {}),
  };
}
// Direct exported-style API probes exercise all named colors and interactions that a diagram may not naturally combine.
const {NAMED_COLORS} = await import(pathToFileURL(join(root, 'dist/css-colors.js')).href);
const styleInputs = Object.keys(NAMED_COLORS).map(color => ({id:`named-${color}`,classes:['a'],defs:{a:{fill:color}}}));
for (const color of ['#f96','#aAbBcC','RGB(300, 0, 5)','rgba(1,2,3,0.5)','rgb(4,5,6','rgb(1\uFEFF, 2,3)','rgb(١,2,3)','transparent','nonsense']) {
  styleInputs.push({id:`color-${color}`,classes:['a'],defs:{a:{fill:color,stroke:'#123',color:'navy'}}});
}
for (const [id,classes,defs] of [
  ['bold-only',['a'],{a:{'font-weight':'bold'}}],
  ['normal-overrides-bold',['a','b'],{a:{'font-weight':'bold',fill:'#000'},b:{'font-weight':'normal',fill:'#fff'}}],
  ['unknown-class',['missing'],{a:{fill:'red'}}],
  ['unsupported',['a'],{a:{'stroke-width':'3px','font-style':'italic'}}],
  ['null-classes',undefined,{a:{fill:'red'}}],
  ['empty-classes',[],{a:{fill:'red'}}],
]) styleInputs.push({id,classes,defs});
const styles = styleInputs.map(({id,classes,defs}) => {
  const art={width:1,plain:[],warnings:[],classDefs:defs,styled:[['border','text','edge','edgeLabel','title','none'].map(role=>({text:role,role,...(classes===undefined?{}:{classes}),href:'https://style.example.test/'}))]};
  return {id,classes,defs,resolved:lib.resolveClassStyle(classes,defs),ansi:lib.toAnsi(art,customTheme)};
});
const metadata = {library:'lovely-mermaid@0.3.3', capture:'actual npm dist/index.js execution; authored inputs',
  integrity:receipt.integrity, runtime:`Node ${process.version}; ICU ${process.versions.icu}; Unicode ${process.versions.unicode}`,
  cases, styles};
const capturedBytes=JSON.stringify(metadata, null, 0) + '\n';
writeFileSync(output, capturedBytes);
writeFileSync(new URL('./capture-0.3.3.json', import.meta.url),JSON.stringify({
  library:metadata.library, capture:metadata.capture, runtime:metadata.runtime, integrity:metadata.integrity,
  corpusFile, corpusSha256:createHash('sha256').update(readFileSync(corpusFile)).digest('hex'),
  output, outputSha256:createHash('sha256').update(capturedBytes).digest('hex'),
  diagramCases:corpus.length, styleCases:styles.length,
  rendered:Object.values(cases).filter(c=>c.render!==null).length,
  referenceFilesHashVerified:true,
},null,2)+'\n');
console.log(JSON.stringify({library:metadata.library, cases:corpus.length, rendered:Object.values(cases).filter(c=>c.render!==null).length, output}));
