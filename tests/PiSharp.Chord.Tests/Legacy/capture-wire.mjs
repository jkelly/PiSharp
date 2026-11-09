import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
const [root,destination]=process.argv.slice(2);
if(!root||!destination)throw new Error('usage: node --conditions=source capture-wire.mjs <v150> <new-output>');
const source=path.join(root,'packages/chord/src/wire.ts');
const {isFrame,PROTOCOL_VERSION}=await import(pathToFileURL(source));
const token={name:'test',scope:'remote',version:1,input:{type:'object'},host:{name:'server'},variants:{main:{x:{kind:'state',schema:{type:'number'}},ping:{kind:'method'}}}};
const frames=[
 {t:'hello',protocol:4,name:'server',kind:'host',codeVersion:1,acks:true},
 {t:'hello',name:'client',kind:'client',codeVersion:0},
 {t:'ack',n:1},{t:'hold',id:1,at:'server',token,input:{},state:false,again:true,origin:'o'},
 {t:'release',id:1},{t:'shape',id:1,variant:'main',members:['x','ping'],contract:'x',state:['x']},
 {t:'gone',id:1,error:{code:'gone',message:'gone: removed'}},
 {t:'call',id:2,hold:1,member:'ping'},{t:'call',id:3,hold:1,member:'ping',args:null,root:{rootId:'r',codeVersion:1}},
 {t:'result',id:2},{t:'result',id:3,value:null},{t:'cancel',id:2},{t:'sub',hold:1},{t:'unsub',hold:1},{t:'resync',hold:1,m:'x'},
 {t:'state',hold:1,m:'x',as:'snap',value:1},{t:'state',hold:1,m:'x',as:'reset'},{t:'state',hold:1,m:'x',as:'ops',ops:[['r',null]]},
 {t:'state',hold:1,m:'x',as:'error',error:{code:'unavailable',message:'failed',reason:'offline'}},
 {t:'ping',n:-1},{t:'pong',n:0},{t:'bye'},{t:'root_end',rootId:'r'},{t:'invalidate',pattern:{kind:'user'}},
 {t:'prepare',reload:1,codeVersion:2,plugins:['p'],options:{remove:[],gateDeadline:0,retireDeadline:2}},
 {t:'vote',reload:1,report:{codeVersion:2,previous:1,changed:['p'],removed:[],prepared:['x'],gated:[]}},
 {t:'commit',reload:1},{t:'retire',codeVersion:1},{t:'abort',reload:1},
 ...[0,-1,1.5,'1',null].map(n=>({t:'ack',n})),
 {t:'hello',name:'',kind:'host',codeVersion:1},{t:'hold',id:1,at:'server',token,input:{},state:true},
 {t:'hold',id:1,at:'server',token,input:{},again:false},{t:'state',hold:1,m:'x',as:'invalid'},
 {t:'state',hold:1,m:'x',as:'ops',ops:[1]},{t:'gone',id:1,error:{code:'fake',message:'bad'}},{t:'bound',entries:[]},
 {t:'shape',id:1,members:[],contract:'x',state:[],future:{a:true}},
];
const output={sourceCommit:'f9a26365568127461c47a07a37dcb42c140a3ac5',sourcePath:'packages/chord/src/wire.ts',sourceSha256:createHash('sha256').update(await fs.readFile(source)).digest('hex'),node:process.version,protocol:PROTOCOL_VERSION,fixtures:frames.map((frame,i)=>({id:`wire-${i}`,frame,valid:isFrame(frame)}))};
await fs.writeFile(destination,JSON.stringify(output,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({cases:frames.length,destination}));
