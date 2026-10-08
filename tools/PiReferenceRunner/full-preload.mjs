import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { isAbsolute, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const oracleRoot = resolve(process.env.PISHARP_REFERENCE_ORACLE);
const loaded = new Map();
globalThis.pisharpLoadedReferenceModules = loaded;
// This observer forwards the original loader result without transforming source.
registerHooks({ load(url, context, nextLoad) {
  const result = nextLoad(url, context);
  if (url.startsWith('file:')) {
    const path = fileURLToPath(url);
    const within = relative(oracleRoot, path);
    if (within && !isAbsolute(within) && !within.startsWith('..')) {
      const bytes = readFileSync(path);
      loaded.set(within.replaceAll('\\', '/'), { path: within.replaceAll('\\', '/'), sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length });
    }
  }
  return result;
} });
await import('./offline-guard.mjs');
await import(pathToFileURL(resolve(oracleRoot, 'upstream/packages/coding-agent/src/experimental/source-resolver.ts')).href);
