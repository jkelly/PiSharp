// The inherited grapheme profile stays pinned; verify the new library's width source has identical data.
import {readFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
import {join} from 'node:path';
const [root,nativeFile]=process.argv.slice(2);
const {WIDTHS}=await import(pathToFileURL(join(root,'dist/width-data.js')).href);
const native=readFileSync(nativeFile,'utf8').match(/int\[\] Widths\s*=\s*\[([\s\S]+?)\];/)[1].match(/0x[\da-f]+|\d+/gi).map(Number);
if(JSON.stringify(native)!==JSON.stringify(WIDTHS.flat())) throw new Error('Width data differs');
console.log(JSON.stringify({widthRuns:WIDTHS.length,identical:true,runtime:process.version,icu:process.versions.icu}));
