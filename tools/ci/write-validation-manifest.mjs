// Wrap actual console reports in current-checkout provenance. sourceSha inside old reports
// describes their upstream oracle, not the PiSharp revision executing the suite.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { platform, arch, release } from 'node:os';
import { pathToFileURL } from 'node:url';

export function summarize(suite, report, prerequisites) {
  const input = Array.isArray(report.tests) ? report.tests : report.results;
  if (!Array.isArray(input) || input.length === 0) throw new Error(`${suite}: no case results`);
  const cases = input.map((row, index) => {
    const id = row.testId ?? row.Id ?? row.id ?? row.name;
    if (!id || typeof row.status !== 'string') throw new Error(`${suite}: invalid case ${index}`);
    const rawStatus = row.status;
    const result = /^(passed|PASS|PASS_AUTHORED_NATIVE_ONLY|PASS_UPSTREAM_CAPTURE)$/.test(rawStatus) ? 'passed'
      : /^(SKIP|skipped|SKIPPED|SKIP_|SKIPPED_)/.test(rawStatus) ? 'skipped' : 'failed';
    return { id, name: row.name ?? id, result, rawStatus, reason: row.reason ?? row.error ?? row.failure ?? null };
  });
  const count = typeof report.availableTests === 'number' ? report.availableTests : typeof report.cases === 'number' ? report.cases : input.length;
  const incomplete = report.completeSuite === false || count !== input.length || (report.failed ?? report.failures ?? 0) > 0 ||
    (report.progressCaptureFailures ?? 0) > 0;
  return { suite, prerequisites, result: incomplete || cases.some(c => c.result === 'failed') ? 'failed'
      : cases.some(c => c.result === 'skipped') ? 'passed-with-skips' : 'passed',
    oracleSourceSha: report.sourceSha ?? null, cases };
}

export function main(args) {
  const reports = [], prerequisites = [], consoles = new Map();
  let output, execution, rejectSkips = false;
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--execution') execution = args[++i];
    else if (args[i] === '--output') output = args[++i];
    else if (args[i] === '--report') reports.push(args[++i]);
    else if (args[i] === '--console') { const spec = args[++i], equal = spec.indexOf('='); if (equal < 1) throw new Error('Invalid console spec'); consoles.set(spec.slice(0, equal), spec.slice(equal + 1)); }
    else if (args[i] === '--prerequisite') prerequisites.push(args[++i]);
    else if (args[i] === '--reject-skips') rejectSkips = true;
    else throw new Error(`Unknown option ${args[i]}`);
  }
  if (!output || !reports.length || !prerequisites.length) throw new Error('--output, --report suite=path and --prerequisite required');
  const git = (...a) => execFileSync('git', a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  const manifest = { schemaVersion: 1, checkoutSha: git('rev-parse', 'HEAD'), checkoutTree: git('rev-parse', 'HEAD^{tree}'),
    trackedChanges: git('diff', 'HEAD', '--name-only').split('\n').filter(Boolean),
    platform: { os: platform(), arch: arch(), release: release(), runner: process.env.RUNNER_OS ?? null },
    runId: process.env.GITHUB_RUN_ID ?? null, runAttempt: process.env.GITHUB_RUN_ATTEMPT ?? null,
    generatedUtc: new Date().toISOString(), suites: [] };
  for (const spec of reports) {
    const equal = spec.indexOf('=');
    if (equal < 1) throw new Error(`Invalid report spec ${spec}`);
    const suite = spec.slice(0, equal), file = resolve(spec.slice(equal + 1));
    try {
      const raw = readFileSync(file);
      const summary = { ...summarize(suite, JSON.parse(raw.toString('utf8')), prerequisites), report: file, reportSha256: createHash('sha256').update(raw).digest('hex') };
      if (consoles.has(suite)) {
        const consolePath = resolve(consoles.get(suite)), consoleRaw = readFileSync(consolePath);
        summary.console = consolePath;
        summary.consoleSha256 = createHash('sha256').update(consoleRaw).digest('hex');
        summary.consoleSkipObservations = consoleRaw.toString('utf8').split(/\r?\n/).filter(line => /^\s*(?:SKIP(?:PED)?(?:_|\b|:)|INFO symlink fixture unavailable:)/i.test(line));
        if (summary.consoleSkipObservations.length && summary.result === 'passed') summary.result = 'passed-with-skips';
      }
      manifest.suites.push(summary);
    }
    catch (error) { manifest.suites.push({ suite, report: file, prerequisites, result: 'missing-or-invalid', error: error.message, cases: [] }); }
  }
  if (execution) {
    try { manifest.execution = JSON.parse(readFileSync(execution, 'utf8')); }
    catch (error) { manifest.execution = { result: 'missing-or-invalid', error: error.message }; }
  }
  manifest.result = (manifest.execution && manifest.execution.result !== 'passed') || manifest.suites.some(s => ['failed', 'missing-or-invalid'].includes(s.result) || rejectSkips && s.result === 'passed-with-skips') ? 'failed'
    : manifest.suites.some(s => s.result === 'passed-with-skips') ? 'passed-with-skips' : 'passed';
  mkdirSync(dirname(resolve(output)), { recursive: true });
  writeFileSync(output, JSON.stringify(manifest, null, 2) + '\n');
  return manifest.result === 'failed' ? 1 : 0;
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) process.exitCode = main(process.argv.slice(2));
