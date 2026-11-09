// Execute the pinned legacy implementation unchanged. This is an offline fixture generator, not a replacement codec.
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
const [root, destination] = process.argv.slice(2);
if (!root || !destination) throw new Error('usage: node capture-codec.mjs <upstream-v150> <new-output>');
const modulePath = path.join(root, 'packages/chord/src/link.ts');
const { webSocketLink, IpcLink, inflateMessage } = await import(pathToFileURL(modulePath));
const samples = [
  ['hello', { t:'hello', protocol:4, name:'server', kind:'host', codeVersion:1, acks:true }],
  ['void-call', { t:'call', id:1, hold:2, member:'ping' }],
  ['void-result', { t:'result', id:1 }],
  ['unicode-result', { t:'result', id:4, value:{ '10':'ten','2':'two', text:'a\ud800\n雪😀' } }],
  ['threshold', {t:'result',id:5,value:'x'.repeat(65534)}],
  ['over-threshold', {t:'result',id:6,value:'x'.repeat(65535)}],
  ['packed-state', {t:'state',hold:9,m:'notes',as:'snap',value:Array.from({length:40},(_,i)=>`${i}:`+'x'.repeat(4000))}],
  ['packed-call', {t:'call',id:8,hold:9,member:'write',args:{text:'雪😀'.repeat(22000)}}],
];
const fixtures=[];
for (const [id,frame] of samples) {
  let transmitted, ipc;
  const socket={readyState:1,send(data){transmitted=data;},close(){},addEventListener(){}};
  const link=webSocketLink(socket,{compress:true}); const size=link.send(frame);
  const channel={connected:true,send(message,callback){ipc=message;callback?.(null);return true;},on(){},off(){}};
  new IpcLink(channel).send(frame);
  fixtures.push({id,frame,websocket:typeof transmitted==='string'?{text:transmitted}:{base64:Buffer.from(transmitted).toString('base64')},size,ipc,
    inflated:typeof transmitted==='string'?transmitted:inflateMessage(transmitted)});
}
const output={sourceCommit:'f9a26365568127461c47a07a37dcb42c140a3ac5',sourcePath:'packages/chord/src/link.ts',
 sourceSha256:createHash('sha256').update(await fs.readFile(modulePath)).digest('hex'),node:process.version,fixtures};
await fs.writeFile(destination,JSON.stringify(output,null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({fixtures:fixtures.length,destination}));
