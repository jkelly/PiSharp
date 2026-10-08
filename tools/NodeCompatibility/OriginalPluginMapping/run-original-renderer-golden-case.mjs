// Authored entry only. The coordinator owns the finite grant, pinned inputs,
// actual original process/job/capture settlement and fresh-process scheduling.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
assert.deepEqual(process.argv.slice(2).filter((_, index) => index % 2 === 0), ['--config', '--config-sha256']);
assert.equal(process.argv.length, 6);
const configBytes = fs.readFileSync(process.argv[3]); assert.equal(hash(configBytes), process.argv[5]);
const config = JSON.parse(configBytes);
assert.equal(config.schemaVersion, 1); assert.equal(config.profile, 'source-only-original-renderers-r874');
assert(/^[0-9a-f]{40}$/u.test(config.candidate) && /^[0-9a-f]{40}$/u.test(config.tree));
assert.equal(config.fixture.path, resolve(config.roots.repo, 'tools/NodeCompatibility/OriginalPluginMapping/original-renderer-golden-cases.mjs'));
const fixtureBytes = fs.readFileSync(config.fixture.path);
assert.equal(fixtureBytes.length, config.fixture.bytes); assert.equal(hash(fixtureBytes), config.fixture.sha256);
assert.equal(process.version, 'v24.19.0'); assert.equal(process.platform, 'win32'); assert.equal(process.arch, 'x64');
assert.equal(process.env.PISHARP_REAL_EXTENSION_ORACLE, config.roots.oracle); assert.equal(process.env.TZ, 'UTC');
assert.equal(config.deadlineMilliseconds, 240000);
const { originalRendererGoldenCases, runOriginalRendererGoldenCase } = await import(pathToFileURL(config.fixture.path).href);
assert.deepEqual(originalRendererGoldenCases, ['rg-call', 'rg-result-partial', 'rg-result-no-matches', 'rg-result-expanded']);
assert(originalRendererGoldenCases.includes(config.caseId));
const stop = new AbortController(), timeout = setTimeout(() => stop.abort(new Error('Original renderer source deadline reached')), config.deadlineMilliseconds);
timeout.unref();
try {
  const sourceCase = await runOriginalRendererGoldenCase(config.caseId, { roots: config.roots, cwd: config.cwd, signal: stop.signal });
  stop.signal.throwIfAborted();
  console.log(JSON.stringify({ schemaVersion: 1, complete: true, status: 'passed', caseId: config.caseId,
    candidate: config.candidate, tree: config.tree, processId: process.pid, configSha256: process.argv[5], sourceCase,
    nativeHostQualified: false, rendererTransportQualified: false }));
} catch (error) {
  console.log(JSON.stringify({ schemaVersion: 1, complete: false, status: 'failed', caseId: config.caseId,
    candidate: config.candidate, tree: config.tree, processId: process.pid,
    sourceFailureProjection: { name: error?.name, message: error?.message, stack: error?.stack },
    nativeHostQualified: false, rendererTransportQualified: false }));
  console.error(error?.stack ?? String(error)); process.exitCode = 1;
} finally { clearTimeout(timeout); }
