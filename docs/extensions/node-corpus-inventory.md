# Real TypeScript extension corpus inventory

This is the P7-01 feasibility inventory against public Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. It selects **11 unchanged extension entrypoints and one required relative helper**, comprising 12 TypeScript files. [corpus.plan.json](../../compatibility/node/corpus.plan.json) records source SHA256/Git-blob identities, complete import declarations, API anchors, dependency-lock instances and 42 authored scenario groups.

No selected extension or imported runtime helper was executed. None of the 42 groups has a captured outcome. P7-01, P7-02 and P7-G remain HOLD; mandatory requirements remain Deferred and bridge compatibility remains Unverified. This inventory changes no parity row, support matrix, implementation status or phase acceptance.

## Canonical provenance and notices

All 12 selected source files and 20 contextual source/manifest/lock files were read as inert bytes and compared directly with the acquired publisher archive `artifacts/released-baseline/pi-0.99.1-source.tar.gz`. Its SHA256 is `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`. All **32 selected payload comparisons matched**. No archive extraction, Git invocation, source execution, install or network request was performed.

The pinned inspection establishes the larger identity: 2,093 canonical tracked comparisons, 2,091 raw Git-blob matches and two declared LF-to-CRLF conversions (`pi-test.bat` and `pi-test.ps1`). No selected corpus file is converted. This lane compared selected payloads, without recomputing the whole source fingerprint. The pinned Responses setup receipt supplies canonical/acquired fingerprint records; its historical receipt/helper ownership is unchanged.

`gitBlob` means SHA1 of the raw Git blob header plus exact file bytes, computed without Git execution. The whole MIT notice is preserved in the plan and below. No upstream source copies are added here.

## Real extensions and authored probes

Paths are relative to `packages/coding-agent/examples/extensions/`. Tier letters in the plan describe probes, not a published support classification.

| Source | Required coverage |
| --- | --- |
| hello.ts | Real defineTool/TypeBox tool; complete greeting/details, Unicode/newline and native final-policy denial. |
| commands.ts | Command/completion callbacks, synchronous getCommands, ordered source groups, null completions, selector cancellation/header selection and confirm/path notification. |
| input-transform.ts | Continue/Transform/Handled, extension-source guard, image preservation, ping/empty query, recorded clock/locale output and unavailable notification. |
| protected-paths.ts | Tool blocker with UI/no-UI and own-undefined continuation; actual substring/path-separator behavior. Native final authorization remains mandatory. |
| plan-mode/index.ts + utils.ts | Actual context-array replacement, command/flag/shortcut, synchronous tool state, allowlisted hook, persisted resume, all execute/stay/refine dialog branches and complete follow-up ordering. |
| todo.ts | Full tool-result state/nextId/errors, branch restore and shallow references. Retain renderCall/renderResult and custom /todos component probes. |
| provider-payload.ts | Complete provider hooks, exact .pi/provider-payload.log bytes, missing-directory fault and owned-workspace isolation through real Node fs calls. |
| custom-provider-anthropic/index.ts | Two-model/provider/OAuth registration and real SDK **0.52.0** request/stream/usage/error/abort/auth probes, including thinking/tool signatures and complete push/drain/final records. |
| rpc-demo.ts | Dialog/status/title/widget/editor calls, dangerous-tool select, no-UI block, session-before-switch confirmation and pending-dialog cancellation/stale replies. |
| custom-footer.ts | Whole factory/render/invalidate/branch subscription/dispose and width/session observations; initial Tier C explicit unsupported diagnostic. |
| with-deps/index.ts | Real ms2.1.3, TypeBox and CJS/default interop; complete duration result/error and local/hoisted/missing/wrong dependency probes. |

The 42 groups have concrete inputs and required observations. They are authored scenarios, not assertions, goldens or source-derived outcomes. Provider event outlines require complete raw HTTP/SSE fixtures before genuine execution; they are explicitly not frozen wire fixtures.

Both todo and plan-mode register /todos. Retain both load orders and measure actual upstream collision behavior before choosing bridge policy. Do not rename source commands or discard registrations to manufacture compatibility.

Todo's shallow array copies retain mutable todo objects; toggling can change earlier retained results. Capture immediate snapshots and retained-reference facts separately. Stripping renderers or cloning every observation into JSON cannot establish whole-extension identity semantics. The mixed tool/custom-renderer registration policy remains a concrete spike decision.

## Exact dependency and module identities

The plan records **18 lock instances**, including workspace links, runtime packages, two extension-local locks and an extraneous Anthropic neighbor. Acquired archive/installed/license closure remains Unverified.

The root lock records jiti 2.7.0, typebox 1.3.27, ms 2.1.3 and @types/ms 2.1.0. It has Anthropic SDK 0.124.0 at the root, **0.52.0 beneath coding-agent**, and an explicitly extraneous 0.91.1 beneath nested pi-ai. Pi coding-agent/AI/TUI/Agent are workspace links. Contextual TUI dependencies include get-east-asian-width 1.6.0 and marked 18.0.11. Runtime imports and type-only declarations are recorded separately; type-only imports are not loaded runtime dependencies.

The Anthropic example manifest names pi-extension-custom-provider-anthropic and requires exact 0.52.0. Its local lock retains root name pi-extension-custom-provider and range ^0.52.0. With-deps has exact manifest versions and caret ranges in its local lock root. Preserve both canonical discrepancies and qualify exact acquired/imported instances under a reviewed plan. Neither assume npm ci works nor substitute the core SDK.

The public loader routes the pi-ai root to **compat**, also in source-mode virtual modules. Required exports include Type, StringEnum, cost/transcript helpers and event streams; coding-agent supplies defineTool/CONFIG_DIR_NAME; TUI supplies Key/Text/key and width helpers. A core-AI-only alias changes contracts. Explicit facades must bind to native-owned state rather than load an unrelated registry or substitute the whole upstream engine.

No direct import()/require() expression occurs in these 12 reviewed files. This direct-source observation does not close the loader's dynamic jiti/static-loader/virtual-module branches, SDK imports, transitive helpers, assets or native addons. Lock license labels do not establish complete notices.

## Concrete native execution blockers

The plan pins current registration, reducer, registry-snapshot and Agent-binding files. Experimental native owner-scoped registration, input/tool-call/tool-result reducers and prepared Agent adapters are useful prerequisites. This snapshot has no ExtensionHost/Compatibility.Node project or qualified JS worker/proxy.

Current IExtensionContext exposes ownership/cancellation. Synchronous command/tool/session getters, cwd/model/mode/hasUI snapshots and freshness rules remain required; turning getters async breaks unchanged source. Selected context/provider/session callbacks, UI services and custom provider/OAuth registration are absent from current IExtensionRegistry. Native implementation elsewhere is a dependency, not Node qualification.

The first runnable spike requires P7-03/04/05/06/07: bounded full-duplex framing, opaque callbacks, transactional registration, optional supervised Node, exact loader/dependency identity, real native schema/final-policy binding, typed decisions and synchronous snapshots. A reader must service nested host requests while the original hook awaits a dialog. Generation fences, cancellation, owner cleanup, queue caps and unknown-effect handling must be real.

P7-08 UI/provider adapters remain separately gated. UI absence cannot approve dangerous commands. Direct Node fs/process APIs retain trusted same-user OS access. Custom terminal components have no initial promise; upstream positive rendering/cleanup and explicit bridge negative probes remain required. Mixed todo renderer policy must be visible, without silent field removal or a permanent phase exclusion.

## Review, spike and qualification

Root review is followed by a small optional protocol/loader spike and exact dependency admission. Begin hello/input/protected/with-deps against native registry/reducers/tool policy, then state/context/dialog/provider probes. Runtime/compiler/loader/SDK acquisition requires public provenance, full notices and an owned restore plan. Ordinary discovery installs nothing.

Genuine captures must run whole unchanged factories and callbacks through the pinned public loader/Runner and minimal bridge with identical synthetic inputs. Existing qualified whole ExtensionRunner reducer fixtures inform semantics but use authored callbacks; they are not this real-extension corpus.

All mutable home/cache/temp/log/evidence files belong under explicit caller-owned --run-root outside the read-only repo/source/oracle. A non-overlapping task-2 sibling is valid. Capture complete registrations, callback/host-call order, results, state, errors, cancellation, provider requests/events/finals/cleanup, undefined and numeric facts. Keep source mutable alias observations separate from native immutable JSON. Only reviewed clock/locale/crypto and physical-root identities may be normalized; never filter fields, arrays, errors or native differences.

Only measured differentials/fault probes can create compatibility/node/corpus.json, a proceed/narrow/defer decision and P7-02 per-export/method support matrix. Static metadata/module load provide no product parity credit. Remaining public extensions, dynamic imports, workers/assets, optional dependencies and public experimental neighbors remain unresolved; this bounded spike does not permanently exclude them or close Phase7.

## Inert verification

The authoring lane used built-in Node hashing/JSON/archive metadata only. It imported no public source/SDK and wrote no fixture/oracle. Final checks verify structure/derived counts, exact pins/import/API anchors, lock entries and whole MIT bytes. Root can repeat those read-only checks with the pinned baseline Node executable and sibling source. All genuine upstream/bridge scenarios remain pending implementation and separate execution authorization.

## Whole upstream MIT notice

~~~text
MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
~~~

## Repeat the inert audit

From the frozen worktree, run this PowerShell command with the baseline Node v24.19.0 on PATH. It performs the same nine metadata audit groups and six in-memory tamper assertions, prints the two deliverable hashes, and writes no files. Adjust the explicit read-only sibling source path for a detached repository elsewhere.

~~~powershell
@'
import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';import zlib from 'node:zlib';import assert from 'node:assert/strict';
const repo=process.cwd(),up=path.resolve('../Pi-upstream-v0.99.1');
const p=JSON.parse(fs.readFileSync(path.join(repo,'compatibility/node/corpus.plan.json'),'utf8')),doc=fs.readFileSync(path.join(repo,'docs/extensions/node-corpus-inventory.md'),'utf8'),sha=b=>crypto.createHash('sha256').update(b).digest('hex');
let groups=0;const group=(label,fn)=>{fn();groups++;console.log('PASS '+label);};
const confined=(root,relative)=>{assert.equal(typeof relative,'string');assert(!relative.includes('\\'));assert(!path.isAbsolute(relative));assert(!relative.split('/').some(v=>v==='..'||v===''||v==='.'||v.includes(':')));const full=path.resolve(root,relative);assert(full.startsWith(path.resolve(root)+path.sep));return full};
const verifyPin=(root,r,blob=false)=>{const b=fs.readFileSync(confined(root,r.path));assert.equal(b.length,r.bytes);assert.equal(sha(b),r.sha256);if(blob)assert.equal(crypto.createHash('sha1').update(Buffer.from('blob '+b.length+'\0')).update(b).digest('hex'),r.gitBlob);return b};
const profile=q=>{assert.equal(q.kind,'P7-01-real-unmodified-public-extension-corpus-plan');assert.equal(q.source.commit,'d86654abb8862e201933517d6f1fce9f88dd117f');assert.equal(q.status,'Unverified');assert.equal(q.gate.P7_01,'HOLD');assert.equal(q.gate.P7_02,'HOLD');assert.equal(q.gate.P7_G,'HOLD');assert.equal(q.gate.productParityCredit,false);assert.equal(q.gate.upstreamExtensionsExecuted,false);assert.equal(q.gate.bridgeExecuted,false);assert.equal(q.gate.supportMatrixFrozen,false);assert.equal(q.gate.proceedNarrowDeferDecision,null);assert.equal(q.gate.requirementStatus,'Deferred');assert.equal(q.counts.executedScenarioGroups,0);assert.equal(q.counts.passingScenarioGroups,0);};
group('explicit held profile and source identity',()=>profile(p));
group('derived corpus counts and confinement',()=>{assert.equal(p.extensions.length,11);assert.equal(p.sourceFiles.length,12);assert.equal(p.contextSourceFiles.length,20);assert.equal(p.dependencyLockInstances.length,18);assert.equal(new Set(p.sourceFiles.map(r=>r.path)).size,12);assert.equal(new Set(p.extensions.map(r=>r.id)).size,11);assert.equal(new Set(p.extensions.map(r=>r.entrypoint)).size,11);assert.equal(p.sourceFiles.filter(r=>r.role==='unchanged-relative-helper').length,1);assert.equal(p.counts.realExtensionEntrypoints,p.extensions.length);assert.equal(p.counts.realExtensionTypeScriptFiles,p.sourceFiles.length);assert.equal(p.counts.lockInstancesRecorded,p.dependencyLockInstances.length);const ids=new Set(p.blockers.map(r=>r.id));let total=0;for(const e of p.extensions){confined(up,e.entrypoint);assert(p.sourceFiles.some(r=>r.path===e.entrypoint));assert.equal(e.bridgeCompatibility,'Unverified');for(const dep of e.requiredDependencies)assert(ids.has(dep));for(const s of e.scenarios){total++;assert.equal(s.status,'Unverified');assert.equal(s.upstreamEvidence,null);assert.equal(s.bridgeEvidence,null);assert(s.inputs&&s.observations.length>0)}}assert.equal(total,42);assert.equal(p.counts.authoredScenarioGroups,total);assert.equal(new Set(p.extensions.flatMap(e=>e.scenarios.map(s=>s.id))).size,total);});
group('all exact source and contextual file pins',()=>{for(const r of [...p.sourceFiles,...p.contextSourceFiles])verifyPin(up,r,true);});
group('all authority receipt native and runtime pins',()=>{for(const r of [...p.evidence.authorityPins,p.evidence.canonicalSourceFingerprintReceipt,...p.nativeDependencyPins,p.evidence.publisherArchive])verifyPin(repo,r);assert.equal(process.version,p.evidence.metadataRuntime.version);const b=fs.readFileSync(process.execPath);assert.equal(b.length,p.evidence.metadataRuntime.bytes);assert.equal(sha(b),p.evidence.metadataRuntime.sha256);});
group('canonical fingerprint receipt and whole notice',()=>{const receipt=JSON.parse(verifyPin(repo,p.evidence.canonicalSourceFingerprintReceipt));assert.equal(receipt.sourceSha,p.source.commit);assert.deepEqual(receipt.preparation.sourceFingerprint.canonicalGit,{files:p.source.wholeCanonicalFingerprint.files,sha256:p.source.wholeCanonicalFingerprint.sha256});assert.deepEqual(receipt.preparation.sourceFingerprint.acquiredCheckout,{files:p.source.acquiredCheckoutFingerprint.files,sha256:p.source.acquiredCheckoutFingerprint.sha256});assert.deepEqual(receipt.preparation.sourceFingerprint.declaredCheckoutConversions.map(r=>r.path),p.source.acquiredCheckoutFingerprint.attributeConversions);const notice=verifyPin(up,p.license.source,true).toString('utf8');assert.equal(p.license.wholeNotice,notice);assert(doc.includes(notice));});
group('direct imports event registrations and every reviewed API anchor',()=>{for(const r of p.sourceFiles){const text=verifyPin(up,r,true).toString('utf8');const imports=[...text.matchAll(/import\s+([\s\S]*?)\s+from\s+["']([^"']+)["'];/g)].map(m=>{const clause=m[1],typeOnly=/^type\s/.test(clause),members=clause.replace(/^type\s+/,'').replace(/^\{|\}$/g,'').split(',').map(s=>s.trim()).filter(Boolean);return{specifier:m[2],rawClause:clause,kind:typeOnly?'type-only':members.some(s=>s.startsWith('type '))?'mixed':'runtime',runtimeBindings:typeOnly?[]:members.filter(s=>!s.startsWith('type ')),typeBindings:typeOnly?members:members.filter(s=>s.startsWith('type ')).map(s=>s.slice(5)),line:text.slice(0,m.index).split('\n').length}});assert.deepEqual(r.imports,imports);assert.equal(r.directDynamicImportOrRequireExpressions,[...text.matchAll(/\b(?:import|require)\s*\(/g)].length);const e=p.extensions.find(e=>e.entrypoint===r.path);if(e){assert.deepEqual(e.registeredEvents,[...text.matchAll(/pi\.on\("([^"]+)"/g)].map(m=>m[1]));for(const a of e.actualApiUsage){const at=text.indexOf(a.api);assert(at>=0);assert.equal(a.line,text.slice(0,at).split('\n').length);assert.equal(a.status,'Unverified')}}}});
group('every exact canonical dependency-lock instance',()=>{const unique=new Set();for(const r of p.dependencyLockInstances){const k=r.lockPath+'#'+r.instance;assert(!unique.has(k));unique.add(k);const lock=JSON.parse(fs.readFileSync(confined(up,r.lockPath),'utf8'));assert.deepEqual(r.entry,lock.packages[r.instance]);assert.equal(r.acquiredClosure,'Unverified');}const sdk=p.dependencyLockInstances.filter(r=>r.instance.endsWith('node_modules/@anthropic-ai/sdk'));assert.deepEqual([...new Set(sdk.map(r=>r.entry.version))].sort(),['0.124.0','0.52.0','0.91.1'].sort());assert(p.dependencyLockInstances.some(r=>r.entry.extraneous===true&&r.entry.version==='0.91.1'));});
group('selected archive payload and existing canonical inspection',()=>{const archive=verifyPin(repo,p.evidence.publisherArchive),tar=zlib.gunzipSync(archive,{maxOutputLength:40*1024*1024}),wanted=new Map([...p.sourceFiles,...p.contextSourceFiles].map(r=>['pi-0.99.1/'+r.path,r])),seen=new Set();const str=b=>b.subarray(0,b.indexOf(0)<0?b.length:b.indexOf(0)).toString('utf8');for(let at=0;at+512<=tar.length;){const h=tar.subarray(at,at+512);if(h.every(v=>v===0))break;const n=str(h.subarray(0,100)),pre=str(h.subarray(345,500)),name=pre?pre+'/'+n:n,size=parseInt(str(h.subarray(124,136)).trim(),8),stored=parseInt(str(h.subarray(148,156)).trim(),8);let sum=0;for(let i=0;i<512;i++)sum+=i>=148&&i<156?32:h[i];assert.equal(sum,stored);assert(Number.isSafeInteger(size)&&size>=0&&at+512+size<=tar.length);assert(name.startsWith('pi-0.99.1/')&&!name.split('/').includes('..')&&!name.includes('\\'));if(wanted.has(name)){assert(!seen.has(name));assert(h[156]===48||h[156]===0);seen.add(name);assert(tar.subarray(at+512,at+512+size).equals(verifyPin(up,wanted.get(name),true)));}at+=512+Math.ceil(size/512)*512;}assert.equal(seen.size,32);assert.equal(p.evidence.publisherArchive.selectedPayloadComparisons,seen.size);const inspection=JSON.parse(verifyPin(repo,p.evidence.authorityPins.find(r=>r.path.endsWith('/inspection.json'))));assert.equal(inspection.sourceSha,p.source.commit);assert.equal(inspection.sourceComparison.comparedRegularFiles,2093);assert.equal(inspection.sourceComparison.differingTrackedFiles,0);});
group('in-memory tamper controls reject identity pin gate and confinement mutations',()=>{const bad=structuredClone(p);bad.source.commit='0'.repeat(40);assert.throws(()=>profile(bad));const credit=structuredClone(p);credit.gate.productParityCredit=true;assert.throws(()=>profile(credit));assert.throws(()=>verifyPin(up,{...p.sourceFiles[0],sha256:'0'.repeat(64)},true));assert.throws(()=>confined(up,'../outside.ts'));assert.throws(()=>confined(up,'C:/outside.ts'));assert.throws(()=>confined(up,'a\\b'));});
for(const relative of ['compatibility/node/corpus.plan.json','docs/extensions/node-corpus-inventory.md']){const b=fs.readFileSync(path.join(repo,relative));console.log(JSON.stringify({path:relative,bytes:b.length,sha256:sha(b)}))}
console.log(JSON.stringify({inertAuditGroupsPassed:groups,selectedPayloadComparisons:32,extensionEntrypoints:11,sourceFiles:12,authoredScenarioGroups:42,executedScenarioGroups:0,packageLockInstances:18,publicSourceImported:false,sourceOrGoldenMutated:false}));
'@ | node --input-type=module
~~~
