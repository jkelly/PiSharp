// Read-only historical npm evidence: download opaque bytes; never extract or execute them.
import { createHash } from 'node:crypto';
import { lstat, mkdir, mkdtemp, open, readFile, rename, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const historicalArchive = Object.freeze({
  url: 'https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-0.99.1.tgz',
  bytes: 760966,
  sha256: 'f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3',
  name: 'pi-ai-0.99.1.tgz',
});

async function regular(path) {
  try {
    const stat = await lstat(path);
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('Archive cache is not a regular file');
    return stat;
  } catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}

async function verify(path, pin) {
  const stat = await regular(path);
  if (!stat) return false;
  if (stat.size !== pin.bytes) throw new Error('Historical archive size differs from pin');
  const bytes = await readFile(path);
  if (createHash('sha256').update(bytes).digest('hex') !== pin.sha256) throw new Error('Historical archive SHA256 differs from pin');
  return true;
}

export async function ensureArchive(destination, { request = fetch, pin = historicalArchive } = {}) {
  destination = resolve(destination);
  const parent = dirname(destination);
  // Keep every staging file beneath a private, uniquely owned cache directory.
  await mkdir(parent, { recursive: true });
  const parentStat = await lstat(parent);
  if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) throw new Error('Archive cache directory is not a regular directory');
  if (await verify(destination, pin)) return destination;
  const staging = await mkdtemp(join(parent, '.historical-ai-'));
  const temporary = join(staging, 'download');
  let handle;
  try {
    const response = await request(pin.url, { redirect: 'error', signal: AbortSignal.timeout(60000) });
    if (response.status !== 200 || !response.body) throw new Error('Historical archive download did not return HTTP 200 with bytes');
    const declared = response.headers.get('content-length');
    if (declared !== null && Number(declared) !== pin.bytes) throw new Error('Historical archive HTTP size differs from pin');
    handle = await open(temporary, 'wx', 0o600);
    const hash = createHash('sha256');
    let count = 0;
    for await (const chunk of response.body) {
      count += chunk.length;
      if (count > pin.bytes) throw new Error('Historical archive download exceeds pinned size');
      hash.update(chunk);
      await handle.writeFile(chunk);
    }
    if (count !== pin.bytes || hash.digest('hex') !== pin.sha256) throw new Error('Historical archive downloaded bytes differ from pin');
    await handle.sync();
    await handle.close(); handle = null;
    // Concurrent successful writers must still agree on the immutable bytes.
    if (!await verify(destination, pin)) await rename(temporary, destination);
    await verify(destination, pin);
    return destination;
  } finally {
    if (handle) await handle.close();
    await rm(staging, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  const destination = join(repo, 'artifacts/released-npm-ai', historicalArchive.name);
  await ensureArchive(destination);
  console.log(`Verified historical archive: ${historicalArchive.name}; bytes=${historicalArchive.bytes}; sha256=${historicalArchive.sha256}`);
}
