// Freeze the 1.7.0 source evidence. Supply complete JSON reports named <suite>.json and the
// GitHub checks API snapshot for the pinned PR head. Runtime products were built from the
// same src tree at both revisions; CI-only test and workflow changes have their own SHA.
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { summarize } from '../ci/write-validation-manifest.mjs';

const [reportDirectory, checksPath] = process.argv.slice(2);
if (!reportDirectory || !checksPath) throw Error('Usage: node tools/CompatibilityReport/collect-sync-1.7.0-validation.mjs <reports-directory> <github-check-runs.json>');
const read = path => JSON.parse(readFileSync(path, 'utf8'));
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
const localRevision = '88b138e2697eea1f2849f3be9a88dfc9d380ad5c';
const prHead = '33d6eff0cbcc42eeb5a26870fb779a456e7ad4ed';
const nativeTree = git('rev-parse', localRevision + ':src');
assert.equal(git('rev-parse', prHead + ':src'), nativeTree);
const { source } = read('compatibility/target.lock.json');
const checks = read(checksPath).check_runs;
const gapSuites = read('tools/ci/gap-suites.json').groups;
const ci = new Map();
function addCheck(job, suites) {
  const matches = checks.filter(row => row.name === job && row.head_sha === prHead && row.status === 'completed' && row.conclusion === 'success');
  assert.equal(matches.length, 1, 'Expected one successful check at the frozen revision: ' + job);
  const { id, html_url, completed_at } = matches[0];
  assert.match(html_url, /^https:\/\/github\.com\/jkelly\/PiSharp\/actions\/runs\/\d+\/job\/\d+$/);
  assert.ok(html_url.endsWith('/' + id));
  ci.set(job, { job, result: 'success', completedAt: completed_at, url: html_url, suites });
}
addCheck('Required production Pi runtime installer', ['NodeRuntime']);
addCheck('Offline and frozen-source applicability', ['SourceLock']);
for (const suite of ['Tui', 'Compatibility']) addCheck('Core console (' + suite + ', Windows)', [suite]);
for (const group of ['chord', 'env']) {
  const names = gapSuites[group].map(name => name.replace(/^PiSharp\./, '').replace(/\.Tests$/, ''));
  addCheck(group + ' (ubuntu-latest)', names);
}
const localSuites = ['AnthropicAuthSync','AzureResponses','Classifiers','CliParity','CliSync','Codemode','Durable','Chord','ExtensionParity','InteractiveParity','McpParity','McpSync','MistralConversations','Models','ProviderApis','ProviderSync','Schema','SessionParity','Telemetry','ToolSearch','WireSync'];
const reports = localSuites.map(suite => {
  const bytes = readFileSync(resolve(reportDirectory, suite + '.json'));
  const report = JSON.parse(bytes);
  const summary = summarize(suite, report, []);
  assert.notEqual(summary.result, 'failed', 'Incomplete or failed source-validation report: ' + suite);
  const ids = summary.cases.map(c => c.id);
  assert.equal(new Set(ids).size, ids.length, 'Duplicate case IDs: ' + suite);
  const counts = Object.fromEntries([...new Set(summary.cases.map(c => c.rawStatus))].sort().map(status => [status, summary.cases.filter(c => c.rawStatus === status).length]));
  return { suite, reportSha256: sha256(bytes), cases: ids.length, counts, skipped: summary.cases.filter(c => c.result === 'skipped').map(c => c.id),
    passedIds: summary.cases.filter(c => c.result === 'passed').map(c => c.id),
    caseResultsSha256: sha256(JSON.stringify(summary.cases.map(c => [c.id, c.rawStatus]))),
    // The sourceSha in these historical runners identifies an upstream fixture, not the PiSharp product build.
    oracleSourceSha: summary.oracleSourceSha, result: summary.result };
});
// These suites emit named PASS lines or explicit boolean observations. Retain the successful
// named cases only, exclude any platform SKIP also printed as PASS by an outer case, and keep
// the source log hash. This counts observations rather than assertions inside each case.
for (const suite of ['Compatibility','Tui','Chord.Durable.Integration','Env.Terminal','Env.Process','Env.Adapter','SourceLock','NodeRuntime']) {
  const file = suite === 'SourceLock' ? 'SourceLock.tap' : suite === 'NodeRuntime' ? 'NodeRuntime.json' : suite + '.log';
  const bytes = readFileSync(resolve(reportDirectory, file));
  const log = bytes.toString();
  let passedIds, skipped = [];
  if (suite === 'SourceLock') {
    assert.match(log, /^# fail 0$/m);
    assert.match(log, /^# skipped 0$/m);
    passedIds = [...log.matchAll(/^ok \d+ - (.+)$/gm)].map(m => m[1]);
    assert.equal(passedIds.length, 3);
  } else if (suite === 'NodeRuntime') {
    const abi = JSON.parse(log);
    assert.equal(abi.version, '1.7.0');
    assert.equal(abi.codingAgentVersion, '1.7.0');
    passedIds = ['sameTui','sameCompile','sameAgent','sameDefault','sameType','sameValue','sameModels','reloaded'];
    for (const id of passedIds) assert.equal(abi[id], true, id);
  } else {
    assert.doesNotMatch(log, /^FAIL\b/m, suite);
    skipped = [...log.matchAll(/^SKIP (.+?)[:].+$/gm)].map(m => m[1]);
    passedIds = [...log.matchAll(/^PASS (.+)$/gm)].map(m => m[1])
      .filter(id => !/^\d+ native .* cases$/.test(id) && !skipped.includes(id));
  }
  assert.ok(passedIds.length, suite);
  assert.equal(new Set(passedIds).size, passedIds.length, 'Duplicate case names: ' + suite);
  reports.push({suite, reportSha256: sha256(bytes), cases: passedIds.length + skipped.length,
    passedIds, skipped, result: skipped.length ? 'passed-with-skips' : 'passed'});
}
const requiredSuites = reports.map(row => row.suite).sort();
const witnessed = new Set([...reports.map(r => r.suite), ...[...ci.values()].flatMap(row => row.suites)]);
const pendingSuites = requiredSuites.filter(s => !witnessed.has(s));
assert.deepEqual(pendingSuites, []);
const limitations = [
  'This scope validates the native behavior groups in the 1.7.0 coverage record. The source freeze and native tests do not establish complete upstream behavioral equivalence.',
  'The original full clean-checkout ExtensionParity run lacked two sample manifests. Both sample cases passed after building the samples; the selected complete ExtensionParity report covers 181 cases including production installation.',
  'Historical v0.99.1 Pi Messages qualification reported nine failures and one open outcome, versus six failures and one open outcome on the previous release. Three additional historical comparisons concern first-data-only and split-CRLF framing superseded by Pi 1.7. Current SSE regression cases passed.',
  'The historical macOS MCP transport suite retained two failures also reproduced on the previous release. The Google preterminal resource-limit fixture exceeded the system JSON serializer limit on both revisions.',
  'Suite-specific skips are recorded by case ID. Live provider accounts, physical Windows console delivery, upstream Chord v2 peer interoperability and full legacy qualification are outside this source acceptance.',
  'Package qualification and publication have separate gates. These source receipts do not record a published or fully qualified package.'
];
const ledger = {
  schemaVersion: 1, sourceValidationOnly: true, target: source.tag, upstreamCommit: source.commit,
  localBuild: { platform: 'macOS arm64', sdk: '10.0.401', referenceRevision: localRevision, nativeSourceTree: nativeTree,
    context: 'The clean-checkout release-list reports were run on this commit. Additional full Chord, Durable and ExtensionParity reports are from the completed source-sync worktree on the same machine.' },
  ci: { repository: 'jkelly/PiSharp', pullRequest: 77, head: prHead, nativeSourceTree: nativeTree, checks: [...ci.values()] },
  requiredSuites, pendingSuites, additionalPending: [], reports, limitations,
  packageValidation: { status: 'pending', scope: 'This ledger records source validation only.' }
};
writeFileSync('AI_DOCS/sync-1.7.0-source-validation.json', JSON.stringify(ledger, null, 2) + '\n');
