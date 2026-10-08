import fs from'node:fs';import path from'node:path';import crypto from'node:crypto';
const [output]=process.argv.slice(2);if(!output||fs.existsSync(output))throw Error('Fresh output required');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),receipt=p=>({path:p,bytes:fs.statSync(p).size,sha256:hash(fs.readFileSync(p))});
const parent=path.join(import.meta.dirname,'../../artifacts/word-controls-source-observations.json');
// This is additive runtime evidence, not a replacement for public Editor captures.
const previous=JSON.parse(fs.readFileSync(parent));if(process.version!==previous.node.version||receipt(process.execPath).sha256!==previous.node.sha256)throw Error('Pinned Node identity required');
const defaultWord=new Intl.Segmenter(undefined,{granularity:'word'}),defaultGrapheme=new Intl.Segmenter(undefined,{granularity:'grapheme'});
const texts=[...new Set(previous.cases.filter(c=>/dictionary-source-witness/.test(c.id)).map(c=>c.input.steps[0].data)),
 '\u4e2d\u534e\u4eba\u6c11\u5171\u548c\u56fd','\u5357\u4eac\u5e02\u957f\u6c5f\u5927\u6865','\u4eca\u5929\u5929\u6c14\u5f88\u597d','\u4f60\u597d\u4e16\u754c','\u4e2d\u6587\u6d4b\u8bd5',
 '\u65e5\u672c\u8a9e\u306e\u6587\u7ae0\u3067\u3059','\u30ab\u30bf\u30ab\u30ca\u30c6\u30b9\u30c8','\u3042\u3044\u3046\u3048\u304a','\uff76\uff80\uff76\uff85\uff83\uff7d\uff84','\u30ac\u30c3\u30b3\u30a6','\u30ab\u3099\u30c3\u30b3\u30a6',
 '\u0e20\u0e32\u0e29\u0e32\u0e44\u0e17\u0e22','\u0e17\u0e14\u0e2a\u0e2d\u0e1a','\u0e01\u0e23\u0e38\u0e07\u0e40\u0e17\u0e1e\u0e21\u0e2b\u0e32\u0e19\u0e04\u0e23',
 '\u0e20\u0e32\u0e29\u0e32\u0e44\u0e17\u0e22\u0e17\u0e14\u0e2a\u0e2d\u0e1a','\u0e20\u0e32\u0e29\u0e32\u0e44\u0e17\u0e22abc\u4e2d\u6587\u30ab\u30bf\u30ab\u30ca',
 '\u4f60\u597d\u{20000}\u4e16\u754c','\u4f60\u597d\u0301\u4e16\u754c','\u0e2f\u0e46','\u0e81\u0e82\u0e84\u0e87','\u1780\u1781\u1782\u1783','\u1000\u1001\u1002\u1003','\uac00\ub098\ub2e4','\u30ab\u30bf\u30ab\u30ca_123.foo'];
const observations=[];
for(const text of texts){const seams=[0];let at=0;for(const rune of text){at+=rune.length;seams.push(at)}
 for(const seam of seams)for(const direction of['prefix','suffix']){const fragment=direction==='prefix'?text.slice(0,seam):text.slice(seam);
  observations.push({text,seam,direction,fragment,segments:[...defaultWord.segment(fragment)].map(s=>({segment:s.segment,index:s.index,isWordLike:s.isWordLike}))});}
}
const localeWitnesses=['en-US','zh','ja','th','lo','km','my'].map(locale=>{const s=new Intl.Segmenter(locale,{granularity:'word'});return{requested:locale,resolved:s.resolvedOptions(),cases:texts.map(text=>({text,segments:[...s.segment(text)].map(v=>({segment:v.segment,index:v.index,isWordLike:v.isWordLike}))}))};});
const evidence={schemaVersion:1,scope:'Exact runtime word segments for all scalar-seam prefixes/suffixes of original dictionary witnesses and additive ambiguity/normalization/mixed-script texts. No fixture expected rewritten; no native or optional profile acceptance.',runtime:{host:receipt(process.execPath),version:process.version,versions:process.versions,wordResolved:defaultWord.resolvedOptions(),graphemeResolved:defaultGrapheme.resolvedOptions(),intlDefaultLocale:new Intl.DateTimeFormat().resolvedOptions().locale},sourceEditorEvidence:receipt(parent),driver:receipt(import.meta.filename),sourceWords:previous.cases.filter(c=>/dictionary-source-witness/.test(c.id)),observations,localeWitnesses};
fs.writeFileSync(output,JSON.stringify(evidence,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({output:receipt(output),texts:texts.length,fragmentObservations:observations.length,locales:localeWitnesses.length,runtime:evidence.runtime}));
