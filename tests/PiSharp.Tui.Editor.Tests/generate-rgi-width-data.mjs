import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';

const [outputArg]=process.argv.slice(2);
if(!outputArg)throw Error('A fresh generated fragment output is required.');
const output=path.resolve(outputArg),data=path.join(import.meta.dirname,'width-data/emoji17');
const receipt=p=>({path:p,bytes:fs.statSync(p).size,sha256:crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')});
const nodePin='3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237';
if(receipt(process.execPath).sha256!==nodePin||process.version!=='v24.19.0'||fs.existsSync(output)||fs.existsSync(output+'.manifest.json'))throw Error('Pinned runtime and fresh output required.');
const pins=[['emoji-sequences.txt','12cc8267dc33cbd11ed32bcf6fc5dc2ad9c7a77bae1bdfba2f41b1b9b3ead8dd'],['emoji-zwj-sequences.txt','5b25441daed2322b068c5e70cda522946a4f0274df864445a1965a92e5fc5cad']];
const entries=[];
for(const [file,sha256]of pins){
 const filename=path.join(data,file);if(receipt(filename).sha256!==sha256)throw Error('Official data changed.');
 for(const [line,raw]of fs.readFileSync(filename,'utf8').split(/\r?\n/).entries()){
  const clean=raw.split('#')[0].trim();if(!clean)continue;
  const [hex,kind]=clean.split(';').map(x=>x.trim());
  if(!['Basic_Emoji','Emoji_Keycap_Sequence','RGI_Emoji_Flag_Sequence','RGI_Emoji_Tag_Sequence','RGI_Emoji_Modifier_Sequence','RGI_Emoji_ZWJ_Sequence'].includes(kind))throw Error('Unknown official union member.');
  if(hex.includes('..')){const [first,last]=hex.split('..').map(x=>parseInt(x,16));for(let cp=first;cp<=last;cp++)entries.push({file,line:line+1,kind,text:String.fromCodePoint(cp)});}
  else entries.push({file,line:line+1,kind,text:String.fromCodePoint(...hex.split(/\s+/).map(x=>parseInt(x,16)))});
 }
}
const input=JSON.parse(fs.readFileSync(path.join(data,'rgi-width-inputs.json'),'utf8'));
if(JSON.stringify(entries)!==JSON.stringify(input.entries))throw Error('Official entries differ from pre-implementation capture input.');
const rgi=/^\p{RGI_Emoji}$/v,official=new Set(entries.map(e=>e.text));
if(entries.length!==3953||official.size!==3953||entries.some(e=>!rgi.test(e.text)))throw Error('Complete official union/property verification failed.');
// Exhaustive scalar and scalar+VS16 checks establish coverage of both existing fast paths.
let scalarChecks=0,scalarPositives=0,vs16Positives=0;
for(let cp=0;cp<=0x10ffff;cp++){
 if(cp>=0xd800&&cp<=0xdfff)continue;
 const scalar=String.fromCodePoint(cp),one=rgi.test(scalar),two=rgi.test(scalar+'\ufe0f');
 if(one!==official.has(scalar)||two!==official.has(scalar+'\ufe0f'))throw Error('Official scalar/VS16 coverage differs at '+cp.toString(16));
 scalarChecks+=2;scalarPositives+=Number(one);vs16Positives+=Number(two);
}
const sequences=[...official].filter(s=>[...s].length>1&&!(s.endsWith('\ufe0f')&&[...s].length===2)).sort();
const offsets=[0];let flat='';for(const sequence of sequences){flat+=sequence;offsets.push(flat.length);}
const escapes=s=>Array.from({length:s.length},(_,i)=>'\\u'+s.charCodeAt(i).toString(16).padStart(4,'0')).join('');
const literal=[];for(let at=0;at<flat.length;at+=64)literal.push('        "'+escapes(flat.slice(at,at+64))+'"');
const rows=[];for(let at=0;at<offsets.length;at+=16)rows.push('        '+offsets.slice(at,at+16).join(', ')+',');
const fragment=`
// Generated from the complete official Unicode 17 RGI sequence union; see tests width-data/emoji17.
// Unicode data license is retained in full. No input-dependent cache or alternate width profile.
internal static partial class TerminalEditorSourceWidth
{
    private static bool IsRgiEmoji(string value)
    {
        var runes = value.EnumerateRunes();
        if (!runes.MoveNext()) return false;
        var first = runes.Current.Value;
        if (!runes.MoveNext()) return In(first, RgiSingleRanges);
        var second = runes.Current.Value;
        if (second == 0xfe0f && !runes.MoveNext()) return RgiVs16.Contains(first);
        var offsets = RgiMultiOffsets; var data = RgiMultiData.AsSpan();
        var low = 0; var high = offsets.Length - 2;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var comparison = value.AsSpan().SequenceCompareTo(data[offsets[mid]..offsets[mid + 1]]);
            if (comparison < 0) high = mid - 1;
            else if (comparison > 0) low = mid + 1;
            else return true;
        }
        return false;
    }

    private static ReadOnlySpan<int> RgiMultiOffsets => [
${rows.join('\n')}
    ];
    private static string RgiMultiData =>
${literal.join(' +\n')};
}
`;
fs.writeFileSync(output,fragment,{flag:'wx'});
fs.writeFileSync(output+'.manifest.json',JSON.stringify({schemaVersion:1,generator:receipt(fileURLToPath(import.meta.url)),node:receipt(process.execPath),nodeVersion:process.version,unicode:process.versions.unicode,icu:process.versions.icu,officialInputs:pins.map(([f])=>receipt(path.join(data,f))),license:receipt(path.join(data,'icu78-Unicode-full-LICENSE')),preImplementationInput:receipt(path.join(data,'rgi-width-inputs.json')),officialEntries:entries.length,completeOfficialUnionVerified:true,scalarAndVs16Checks:scalarChecks,scalarPositives,vs16Positives,multiSequences:sequences.length,multiUtf16Units:flat.length,sequenceOffsets:offsets.length,fragment:receipt(output),networkUsed:false,publicApiChanges:0},null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({officialEntries:entries.length,scalarChecks,scalarPositives,vs16Positives,multiSequences:sequences.length,multiUtf16Units:flat.length,fragment:receipt(output)}));
