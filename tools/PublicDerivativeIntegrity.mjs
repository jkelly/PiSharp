// The literal table digest binds reviewed current public derivative bytes only.
// Historical capture records stay unchanged. This verifies public derivative bytes only.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const tablePath = 'PUBLIC-DERIVATIVE-INTEGRITY.json';
const tableSha256 = '92947cdbac818623cda5caea8c8e272c5e8c7cff655d9536c6565078bb98a1f8';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const digest = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const size = value => Number.isSafeInteger(value) && value >= 0;
const safePath = value => typeof value === 'string' && value.length > 0 && !isAbsolute(value) && !value.includes('\\') && value.split('/').every(part => part && part !== '.' && part !== '..' && !part.includes(':'));
function regularBytes(path) {
  assert(safePath(path), 'Derivative integrity requires a canonical repository-relative path');
  let full = repo;
  assert(!lstatSync(full).isSymbolicLink(), 'Derivative repository link rejected');
  for (const part of path.split('/')) { full = join(full, part); assert(!lstatSync(full).isSymbolicLink(), 'Derivative path link rejected'); }
  assert(lstatSync(full).isFile(), 'Derivative pin must name a regular file');
  const suffix = relative(realpathSync(repo), realpathSync(full));
  assert(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Derivative physical path escapes repository');
  return readFileSync(full);
}
let frozen;
function table() {
  if (frozen) return frozen;
  const bytes = regularBytes(tablePath);
  assert.equal(sha256(bytes), tableSha256, 'Unreviewed derivative integrity table rejected');
  const value = JSON.parse(bytes.toString('utf8'));
  assert.equal(value.schema, 'public-path-derivative-integrity-v1');
  assert.equal(value.verificationScope, 'current-public-derivative-bytes-only');
  assert.equal(value.originalRawCaptureClaim, false);
  assert.equal(value.runtimeQualificationTransferred, false);
  assert(Array.isArray(value.files) && value.files.length > 0);
  const records = new Map();
  for (const row of value.files) {
    assert.deepEqual(Object.keys(row).sort(), ['path', 'originalSha256', 'originalBytes', 'currentSha256', 'currentBytes'].sort());
    assert(safePath(row.path) && digest(row.originalSha256) && digest(row.currentSha256) && size(row.originalBytes) && size(row.currentBytes));
    assert(!records.has(row.path), 'Duplicate derivative file identity rejected');
    records.set(row.path, Object.freeze({ ...row }));
  }
  assert(Array.isArray(value.currentDependencies) && value.currentDependencies.length > 0);
  const dependencies = new Set();
  for (const pin of value.currentDependencies) {
    assert.deepEqual(Object.keys(pin).sort(), ['path', 'bytes', 'sha256'].sort());
    assert(safePath(pin.path) && size(pin.bytes) && digest(pin.sha256) && !dependencies.has(pin.path));
    dependencies.add(pin.path);
    const actual = regularBytes(pin.path);
    assert.equal(actual.length, pin.bytes); assert.equal(sha256(actual), pin.sha256);
  }
  frozen = records;
  return frozen;
}
export function currentPublicHarnessPin(reference) {
  assert(reference && safePath(reference.path) && digest(reference.sha256) && size(reference.bytes), 'Complete historical path/hash/length tuple required');
  const row = table().get(reference.path);
  assert(row && row.originalSha256 === reference.sha256 && row.originalBytes === reference.bytes, 'Unknown historical tuple rejected; no fallback admission');
  const bytes = regularBytes(row.path);
  assert.equal(bytes.length, row.currentBytes, 'Public derivative length mismatch');
  assert.equal(sha256(bytes), row.currentSha256, 'Public derivative digest mismatch');
  return { ...reference, bytes: row.currentBytes, sha256: row.currentSha256 };
}
export function currentPublicHarnessPins(references) {
  assert(Array.isArray(references) && references.length > 0);
  assert.equal(new Set(references.map(pin => pin.path)).size, references.length, 'Duplicate historical harness path rejected');
  return references.map(currentPublicHarnessPin);
}
export const publicDerivativeVerificationScope = Object.freeze({
  verificationScope: 'current-public-derivative-bytes-only',
  originalRawCaptureClaim: false,
  runtimeQualificationTransferred: false,
  historicalCapturePinsRewritten: false
});
