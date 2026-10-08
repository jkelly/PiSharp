import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const args = process.argv.slice(2);
if (args.length !== 6 || args[0] !== '--base' || args[2] !== '--package' || args[4] !== '--output')
  throw Error('Usage: prepare-source.mjs --base <pinned-local-Pi-repo> --package <approved-package-root> --output <fresh-private-run-root>');
const base = path.resolve(args[1]), approved = path.resolve(args[3]), root = path.resolve(args[5]);
const candidate = 'd86654abb8862e201933517d6f1fce9f88dd117f';
const gitExecutable = 'C:/Program Files/Git/cmd/git.exe';
const git = (repo, ...params) => execFileSync(gitExecutable, ['-c', `safe.directory=${repo}`, '-c', 'core.autocrlf=false', '-C', repo, ...params]);
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
if (fs.existsSync(root) || git(base, 'rev-parse', 'HEAD').toString().trim() !== candidate || git(base, 'status', '--porcelain').length)
  throw Error('Fresh private root and clean exact upstream required.');
const archive = fs.readFileSync(path.join(approved, 'get-east-asian-width-1.6.0.tgz'));
if (hash(archive) !== '4451f58e2f5a4ef27ae8b4ba25b64be97d3a8413dc8614a4321a10d418eee6d7' ||
    'sha512-' + crypto.createHash('sha512').update(archive).digest('base64') !== 'sha512-QRbvDIbx6YklUe6RxeTeleMR0yv3cYH6PsPZHcnVn7xv7zO1BHN8r0XETu8n6Ye3Q+ahtSarc3WgtNWmehIBfA==')
  throw Error('Upstream-locked width dependency archive differs.');
const inspection = JSON.parse(fs.readFileSync(path.join(approved, 'inspection.json'), 'utf8'));
if (inspection.files.length !== 8 || inspection.version !== '1.6.0') throw Error('Approved package census differs.');
fs.mkdirSync(root, { recursive: true });
const upstream = path.join(root, 'upstream');
execFileSync(gitExecutable, ['-c', `safe.directory=${base}`, '-c', 'core.autocrlf=false', 'clone', '--no-hardlinks', '--no-checkout', base, upstream]);
git(upstream, 'checkout', '--detach', candidate);
const files = [], physicalRepairs = [];
for (const entry of git(upstream, 'ls-files', '-s').toString('utf8').trim().split(/\r?\n/)) {
  const match = entry.match(/^(\d+) ([a-f0-9]{40}) 0\t(.+)$/);
  if (!match) throw Error('Unexpected immutable Git index entry.');
  const [, , gitBlob, file] = match;
  const target = path.join(upstream, file);
  let exact = fs.readFileSync(target);
  const observedBlob = crypto.createHash('sha1').update(`blob ${exact.length}\0`).update(exact).digest('hex');
  if (observedBlob !== gitBlob) {
    // Git's eol attributes can materialize CRLF even with autocrlf disabled. Restore the immutable blob bytes.
    exact = git(upstream, 'show', `${candidate}:${file}`);
    if (crypto.createHash('sha1').update(`blob ${exact.length}\0`).update(exact).digest('hex') !== gitBlob) throw Error('Canonical blob identity differs.');
    fs.writeFileSync(target, exact); physicalRepairs.push(file);
  }
  files.push({ path: target, relativePath: file, bytes: exact.length, sha256: hash(exact), gitBlob });
}
const packageRoot = path.join(root, 'node_modules', 'get-east-asian-width');
fs.mkdirSync(packageRoot, { recursive: true });
const packageFiles = [];
for (const row of inspection.files) {
  const source = path.join(approved, 'package', row.path), target = path.join(packageRoot, row.path);
  const data = fs.readFileSync(source);
  if (data.length !== row.bytes || hash(data) !== row.sha256) throw Error('Approved package content changed.');
  fs.copyFileSync(source, target); packageFiles.push({ path: target, bytes: row.bytes, sha256: row.sha256 });
}
const manifest = { schemaVersion: 1, candidate, tree: git(upstream, 'rev-parse', 'HEAD^{tree}').toString().trim(), root, upstream,
  node: { path: process.execPath, version: process.version, versions: process.versions, sha256: hash(fs.readFileSync(process.execPath)) },
  dependencyArchive: { bytes: archive.length, sha256: hash(archive), integrity: inspection.archive.integrity },
  files, packageFiles, physicalRepairs, privateNonHardlinkedCheckout: true, sourceChanged: false, scriptsOrInstallRun: false };
const output = path.join(root, 'source-manifest.json');
fs.writeFileSync(output, JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ output, sha256: hash(fs.readFileSync(output)), sourceFiles: files.length, packageFiles: packageFiles.length }));
