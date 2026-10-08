// Public export adapter: location mapping is not execution or source admission.
// Each caller retains its source/commit/dependency/runtime checks.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';

export const canonicalReferenceRoot = 'P:/PiSharp/root';

function noLinks(path) {
  let current = resolve(path);
  for (;;) {
    assert(!lstatSync(current).isSymbolicLink(), 'Reference layout link/junction rejected');
    const parent = dirname(current);
    if (parent === current) return;
    current = parent;
  }
}

export function validatedReferenceRoot() {
  const selected = process.env.PISHARP_PUBLIC_REFERENCE_ROOT;
  assert(selected && isAbsolute(selected), 'Set PISHARP_PUBLIC_REFERENCE_ROOT to an explicit qualified local reference layout');
  const root = resolve(selected);
  noLinks(root);
  assert(lstatSync(root).isDirectory(), 'Reference layout root must be an existing directory');
  assert.equal(realpathSync(root).toLowerCase(), root.toLowerCase(), 'Reference layout physical root differs');
  return root;
}

export function physicalReferencePath(canonicalIdentity) {
  assert.equal(typeof canonicalIdentity, 'string');
  const canonical = canonicalIdentity.replaceAll('\\', '/');
  assert(canonical.startsWith(canonicalReferenceRoot + '/'), 'Expected a canonical public reference identity');
  const suffix = canonical.slice(canonicalReferenceRoot.length + 1);
  assert(suffix && suffix.split('/').every(part => part && part !== '.' && part !== '..'), 'Reference identity traversal/empty segment rejected');
  const root = validatedReferenceRoot();
  const path = resolve(root, ...suffix.split('/'));
  const tail = relative(root, path);
  assert(tail && !isAbsolute(tail) && tail !== '..' && !tail.startsWith('..' + sep), 'Reference path escaped explicit root');
  return path;
}

export function qualifiedReferenceFile(canonicalIdentity, expectedSha256) {
  assert(/^[a-f0-9]{64}$/.test(expectedSha256), 'Exact expected SHA-256 required');
  const path = physicalReferencePath(canonicalIdentity);
  noLinks(path);
  const stat = lstatSync(path);
  assert(stat.isFile() && stat.size <= 134217728, 'Bounded regular reference file required');
  assert.equal(createHash('sha256').update(readFileSync(path)).digest('hex'), expectedSha256, 'Qualified reference file identity differs');
  return path;
}
