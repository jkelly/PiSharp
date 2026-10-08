import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';
const testRoot=import.meta.dirname,repo=path.resolve(testRoot,'../..'),pin=p=>({path:p,bytes:fs.statSync(p).size,sha256:crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')});
if(pin(process.execPath).sha256!=='3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237'||process.versions.unicode!=='17.0'||process.versions.icu!=='78.3')throw Error('Exact qualified public Source runtime required');
const outputRoot=process.argv[2]?path.resolve(process.argv[2]):repo;
const output=path.join(outputRoot,'src/PiSharp.Tui/Input/TerminalSourceIndicConjunctData.g.cs');
const fixture=path.join(outputRoot,'tests/PiSharp.Tui.IndicConjunct.Tests/fixtures/indic-source-grapheme-probes.json');
if(fs.existsSync(output)||fs.existsSync(fixture))throw Error('Fresh reproducibility destination required');
const segmenter=new Intl.Segmenter(undefined,{granularity:'grapheme'}),ka='\u0915',virama='\u094d',ssa='\u0937';
const offsets=s=>[...segmenter.segment(s)].map(g=>g.index);
const one=s=>{const it=segmenter.segment(s)[Symbol.iterator]();it.next();return it.next().done;};
// UAX29 GB9c isolates each InCB class with a fixed valid consonant/linker/consonant anchor.
// These are unchanged public Intl.Segmenter calls, not an implementation of the editor or boundary algorithm.
const classify=c=>one(c+virama+ssa)?1:one(ka+c+ssa)?3:one(ka+virama+c+ssa)?2:0;
for(const [c,v] of [[ka,1],[ssa,1],[virama,3],['\u200d',2],['\u200c',0],['A',0]])if(classify(c)!==v)throw Error('GB9c anchor discrimination invalid');
const ranges=[],counts=[0,0,0,0],probes=[];let lastClass=0,start=0,scalars=0;
for(let cp=0;cp<=0x10ffff;cp++){
  const scalar=cp<0xd800||cp>0xdfff,c=String.fromCodePoint(cp),kind=scalar?classify(c):0;if(scalar){scalars++;counts[kind]++;}
  if(kind!==lastClass){if(lastClass)ranges.push(start,cp-1,lastClass);start=cp;lastClass=kind;}
  if(kind){const text=kind===1?c+virama+ssa:kind===3?ka+c+ssa:ka+virama+c+ssa;probes.push({id:'incb-'+kind+'-'+cp.toString(16),text,sourceOffsets:offsets(text)});}
  if(cp%0x20000===0)console.log(JSON.stringify({through:cp,scalars,counts}));
}
if(lastClass)ranges.push(start,0x10ffff,lastClass);
const controls=[['latin-linker', 'A'+virama+ssa],['without-linker',ka+'\u0301'+ssa],['zwnj-interrupts',ka+virama+'\u200c'+ssa],['standalone-linker',virama+ssa],['control-interrupts',ka+virama+'\n'+ssa],['zwj-keeps-conjunct',ka+virama+'\u200d'+ssa],['extend-before-linker',ka+'\u0301'+virama+ssa],['extend-after-linker',ka+virama+'\u0301'+ssa],['repeat-linker',ka+virama+virama+ssa],['three-consonants',ka+virama+ssa+virama+ka],['latin-after-linker',ka+virama+'Z'],['cross-script-conjunct',ka+virama+'\u0995'],['combining-latin','e\u0301'],['hangul','\u1100\u1161\u11a8'],['crlf','\r\n'],['emoji-zwj','\u{1f469}\u200d\u{1f4bb}'],['flag','\u{1f1fa}\u{1f1f8}'],['emoji-modifier','\u{1f44d}\u{1f3fd}'],['isolated-surrogate','\ud800'],['supplementary-combining','A\u{1d165}'],['source-caret-prefix',ka+virama],['source-caret-suffix',ssa]];
for(const [id,text] of controls)probes.push({id,text,sourceOffsets:offsets(text)});
for(const p of probes)p.textUtf16Units=Array.from({length:p.text.length},(_,i)=>p.text.charCodeAt(i));
const lines=[];for(let i=0;i<ranges.length;i+=12)lines.push('        '+ranges.slice(i,i+12).map((v,j)=>j%3===2?String(v):'0x'+v.toString(16)).join(', ')+',');
const code='// Generated from exact public Node24.19.0 / ICU78.3 / Unicode17.0 Source profile.\n// Unicode data: copyright Unicode, Inc. and contributors; retained ICU-LICENSE.txt in the generating test fixtures.\n// Reproduce with tests/PiSharp.Tui.IndicConjunct.Tests/generate-indic-source-profile.mjs under its pinned runtime.\nnamespace PiSharp.Tui.Input;\n\ninternal static class TerminalSourceIndicConjunctData\n{\n    internal const string ProfileId = "node24.19.0-icu78.3-unicode17-incb-v1";\n    // Sorted inclusive scalar ranges and class:1Consonant,2Extend,3Linker. Missing values are None.\n    private static readonly int[] Ranges =\n    [\n'+lines.join('\n')+'\n    ];\n\n    internal static int Classify(int scalar)\n    {\n        var low = 0; var high = Ranges.Length / 3 - 1;\n        while (low <= high)\n        {\n            var mid = (low + high) / 2; var at = mid * 3;\n            if (scalar < Ranges[at]) high = mid - 1;\n            else if (scalar > Ranges[at + 1]) low = mid + 1;\n            else return Ranges[at + 2];\n        }\n        return 0;\n    }\n}\n';
fs.mkdirSync(path.dirname(output),{recursive:true});fs.mkdirSync(path.dirname(fixture),{recursive:true});fs.writeFileSync(output,code,{flag:'wx'});
const data={schemaVersion:1,profile:'node24.19.0-icu78.3-unicode17-incb-v1',node:pin(process.execPath),versions:process.versions,scalarCoverage:scalars,counts:{None:counts[0],Consonant:counts[1],Extend:counts[2],Linker:counts[3]},rangeCount:ranges.length/3,method:'UAX29 GB9c property discrimination through unchanged public Intl.Segmenter: c+virama+ssa ->Consonant; ka+c+ssa ->Linker; ka+virama+c+ssa ->Extend after excluding prior classes. Covers every Unicode scalar, excluding surrogate code points. Does not reproduce editor or native segmentation.',probes};
fs.writeFileSync(fixture,JSON.stringify(data,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({data:pin(output),sourceProbes:pin(fixture),scalarCoverage:scalars,counts:data.counts,rangeCount:data.rangeCount,probes:probes.length}));
