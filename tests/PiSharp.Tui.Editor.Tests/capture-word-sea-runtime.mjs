import fs from'node:fs';import path from'node:path';import crypto from'node:crypto';
const [output]=process.argv.slice(2);if(!output||fs.existsSync(output))throw Error('Fresh output required');
const receipt=p=>({path:p,bytes:fs.statSync(p).size,sha256:crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')});
if(process.versions.node!=='24.19.0'||process.versions.icu!=='78.3'||process.versions.unicode!=='17.0'||receipt(process.execPath).sha256!=='3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237')throw Error('Pinned Node required');
const refs=path.join(import.meta.dirname,'../../artifacts/reference-icu-sea'),texts=[],inputs=[];
for(const[name,mark,alphabet]of[['laodict','\u0eb1','\u0e81\u0e82\u0e84\u0e87\u0e88\u0e8a'],['khmerdict','\u17b6','\u1780\u1781\u1782\u1783\u1784\u1785'],['burmesedict','\u102d','\u1000\u1001\u1002\u1003\u1004\u1005']]){
 const p=path.join(refs,'icu78-'+name+'.txt'),words=fs.readFileSync(p,'utf8').split(/\r?\n/).map(s=>s.trim()).filter(s=>s&&!s.startsWith('#'));inputs.push(receipt(p));
 for(const i of[0,Math.floor(words.length/4),Math.floor(words.length/2),Math.floor(words.length*3/4),words.length-1]){texts.push(words[i],words[i]+words[(i+1)%words.length]);}
 const pair=words[Math.floor(words.length/2)]+words[Math.floor(words.length/2)+1];texts.push(mark+pair,pair+'.'+pair,'abc'+pair+'123',pair+'\u4e2d\u6587'+'\u0e20\u0e32\u0e29\u0e32\u0e44\u0e17\u0e22');
 for(let n=1;n<=6;n++)texts.push(alphabet.slice(0,n));
}
const unique=[...new Set(texts)],segmenter=new Intl.Segmenter(undefined,{granularity:'word'}),observations=[];
const segments=(s,text)=>[...s.segment(text)].map(v=>({segment:v.segment,index:v.index,isWordLike:v.isWordLike}));
for(const text of unique){const seams=[0];let at=0;for(const r of text){at+=r.length;seams.push(at)}for(const seam of seams)for(const direction of['prefix','suffix']){const fragment=direction==='prefix'?text.slice(0,seam):text.slice(seam);observations.push({text,seam,direction,fragment,segments:segments(segmenter,fragment)});}}
const locales=['en-US','lo','km','my'].map(locale=>{const s=new Intl.Segmenter(locale,{granularity:'word'});return{locale,resolved:s.resolvedOptions(),cases:unique.map(text=>({text,segments:segments(s,text)}))};});
fs.writeFileSync(output,JSON.stringify({schemaVersion:1,scope:'Additive exact SEA dictionary prefix/suffix observations; official deterministic wordlist samples, ambiguity/marks/minspan/mixed scripts. All prior complete corpora remain unchanged.',host:receipt(process.execPath),versions:process.versions,defaultResolved:segmenter.resolvedOptions(),driver:receipt(import.meta.filename),officialInputs:inputs,texts:unique,observations,locales},null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({output:receipt(output),texts:unique.length,observations:observations.length}));
