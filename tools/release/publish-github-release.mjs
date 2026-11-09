// Called only by the gated tag-push release job, after validated packages reach NuGet.
// This helper never creates a Git tag; a prepared draft is published only after asset upload succeeds.
import { spawn } from 'node:child_process';
import { lstat, readdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export function runGh(args, { cwd } = {}) {
  return new Promise((resolveResult, reject) => {
    const child = spawn('gh', args, { cwd, shell: false, windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'], timeout: 600000 });
    let stdout = '', stderr = '', bytes = 0, overflow = false;
    const collect = key => data => {
      bytes += data.length;
      if (bytes > 4 * 1024 * 1024) { overflow = true; child.kill(); return; }
      if (key === 'stdout') stdout += data.toString('utf8'); else stderr += data.toString('utf8');
    };
    child.stdout.on('data', collect('stdout')); child.stderr.on('data', collect('stderr'));
    child.on('error', reject);
    child.on('close', code => overflow ? reject(new Error('gh output exceeded the bounded response size'))
      : resolveResult({ code, stdout, stderr }));
  });
}

async function regularFile(path) {
  try {
    const stat = await lstat(path);
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`Expected a regular release input file: ${path}`);
    return stat;
  } catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}

export async function publishGitHubRelease(options, { gh = runGh } = {}) {
  const { repo, version, tag, expectedSourceSha, eventName, refType } = options;
  if (eventName !== 'push' || refType !== 'tag') throw new Error('Release publication requires a tag push');
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo ?? '')) throw new Error('An explicit GitHub repository is required');
  if (!/^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/.test(version ?? '') || tag !== `v${version}`)
    throw new Error('The pushed tag must exactly match the release version');
  if (!/^[0-9a-f]{40}$/.test(expectedSourceSha ?? '')) throw new Error('An exact source commit SHA is required');
  const cwd = resolve(options.cwd ?? process.cwd());
  const packages = resolve(cwd, options.packageDirectory ?? 'artifacts/packages');
  const packageStat = await lstat(packages);
  if (!packageStat.isDirectory() || packageStat.isSymbolicLink()) throw new Error('Release packages must be in a regular directory');
  const assets = [];
  for (const name of (await readdir(packages)).sort()) {
    if (!name.endsWith('.nupkg')) continue;
    if (!/^[A-Za-z0-9_.-]+\.nupkg$/.test(name) || !name.endsWith(`.${version}.nupkg`))
      throw new Error('Every release package must match the exact version');
    const path = join(packages, name);
    if (!(await regularFile(path))?.size) throw new Error('Release packages must be nonempty regular files');
    assets.push(path);
  }
  if (!assets.length) throw new Error('No validated release packages were supplied');
  const notes = resolve(cwd, options.notesDirectory ?? 'docs/release-notes', `${version}.md`);
  const hasNotes = Boolean(await regularFile(notes));
  const prerelease = version.includes('-');
  const base = `repos/${repo}`;

  async function api(path, { missing = false } = {}) {
    const result = await gh(['api', '--include', '--method', 'GET', path], { cwd });
    const output = result.stdout.replace(/\r\n/g, '\n');
    const header = /^HTTP\/[0-9.]+\s+(\d{3})[^\n]*\n/.exec(output);
    const boundary = output.indexOf('\n\n');
    if (!header || boundary < 0) throw new Error('GitHub API did not return an HTTP response; publication refused');
    const status = Number(header[1]);
    let body;
    try { body = JSON.parse(output.slice(boundary + 2)); }
    catch { throw new Error(`GitHub API returned invalid JSON (HTTP ${status}); publication refused`); }
    // Only an actual API HTTP 404 is absence. Auth, network, rate-limit and gh failures stay failures.
    if (missing && status === 404 && result.code === 1 && body?.message === 'Not Found') return null;
    if (result.code !== 0 || status !== 200) throw new Error(`GitHub API request failed (HTTP ${status}, gh exit ${result.code}); publication refused`);
    return body;
  }

  async function verifyTag() {
    const reference = await api(`${base}/git/ref/tags/${encodeURIComponent(tag)}`);
    if (reference.ref !== `refs/tags/${tag}`) throw new Error('GitHub returned a different tag reference');
    let object = reference.object;
    const seen = new Set();
    for (let depth = 0; object?.type === 'tag' && depth < 8; depth++) {
      if (!/^[0-9a-f]{40}$/.test(object.sha ?? '') || seen.has(object.sha)) throw new Error('Invalid or cyclic annotated tag');
      seen.add(object.sha);
      object = (await api(`${base}/git/tags/${object.sha}`)).object;
    }
    if (object?.type !== 'commit' || object.sha !== expectedSourceSha)
      throw new Error('The existing release tag does not resolve to the expected source commit');
  }

  async function release() {
    const value = await api(`${base}/releases/tags/${encodeURIComponent(tag)}`, { missing: true });
    if (value !== null && (!Number.isSafeInteger(value.id) || value.id <= 0 || value.tag_name !== tag || typeof value.draft !== 'boolean'))
      throw new Error('GitHub returned an invalid or mismatched release');
    return value;
  }

  async function mutate(args) {
    const result = await gh(args, { cwd });
    if (result.code !== 0) throw new Error(`gh release ${args[1]} failed (exit ${result.code}); publication stopped`);
  }

  await verifyTag();
  const existing = await release();
  if (existing && !existing.draft) throw new Error('The release is already published; refusing to mutate it');
  if (!existing) {
    await verifyTag();
    await mutate(['release', 'create', tag, '--repo', repo, '--verify-tag', '--title', `PiSharp ${version}`,
      ...(hasNotes ? ['--notes-file', notes] : ['--generate-notes']), ...(prerelease ? ['--prerelease'] : []), ...assets]);
    return { action: 'created', tag, sourceSha: expectedSourceSha, assets: assets.length };
  }

  await mutate(['release', 'upload', tag, '--repo', repo, '--clobber', ...assets]);
  // An interrupted upload leaves the draft unpublished. Recheck both identities before promotion.
  await verifyTag();
  const current = await release();
  if (!current?.draft || current.id !== existing.id) throw new Error('The draft changed during upload; refusing promotion');
  await mutate(['release', 'edit', tag, '--repo', repo, '--title', `PiSharp ${version}`, '--draft=false',
    `--prerelease=${prerelease}`, ...(hasNotes ? ['--notes-file', notes] : [])]);
  return { action: 'promoted', tag, sourceSha: expectedSourceSha, assets: assets.length };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const result = await publishGitHubRelease({ repo: process.env.GITHUB_REPOSITORY, version: process.env.VERSION,
    tag: process.env.GITHUB_REF_NAME, expectedSourceSha: process.env.GITHUB_SHA,
    eventName: process.env.GITHUB_EVENT_NAME, refType: process.env.GITHUB_REF_TYPE });
  console.log(JSON.stringify(result));
}
