// Rebuild the second memory pass report from saved captures; Node only, no dependencies.
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
const base = fileURLToPath(new URL('memory-pass-2/', import.meta.url));
const read = name => readFileSync(resolve(base,name),'utf8').replace(/^\uFEFF/,'');
const json = name => JSON.parse(read(name));
const lines = name => read(name).trim().split(/\r?\n/).map(line=>JSON.parse(line));
const names = ['openai-completions-http-stream','mistral-conversations-http-stream'];
const median = xs => [...xs].sort((a,b)=>a-b)[Math.floor(xs.length/2)];
const timing = rows => {
  const groups = new Map();
  for (const row of rows.filter(r=>names.includes(r.name))) {
    const key = row.name+':'+row.size;
    if (!groups.has(key)) groups.set(key,[]);
    groups.get(key).push(row);
  }
  return [rows[0],...rows.filter(r=>r.wire_size),...[...groups.values()].map(runs=>{
    if (runs.length!==3) throw Error('Expected three interleaved timing processes per case');
    const result = {...runs[0],processes:3,median_ms:median(runs.map(r=>r.median_ms)),
      min_ms:Math.min(...runs.map(r=>r.min_ms)),max_ms:Math.max(...runs.map(r=>r.max_ms))};
    for (const field of ['allocated_bytes','sampled_peak_heap_growth_bytes'])
      if (runs.every(r=>Number.isFinite(r[field]))) result[field]=median(runs.map(r=>r[field]));
    return result;
  })];
};
const captures = Object.fromEntries(['before','after','pi'].map(k=>[k,{
  cold:lines(k+'-memory.jsonl'), timing:timing(lines(k+'-timing-series.jsonl')), retained:lines(k+'-retention-detail.jsonl')
}]));
const wires = new Map();
for (const implementation of Object.values(captures)) for (const group of Object.values(implementation)) for (const row of group) {
  if (!row.wire_size) continue;
  const signature=row.wire_bytes+':'+row.sha256;
  if (wires.has(row.wire_size)&&wires.get(row.wire_size)!==signature) throw Error('Payload mismatch');
  wires.set(row.wire_size,signature);
}
const cold = rows => names.map(name=>{
  const runs=rows.filter(r=>r.cold_process&&r.name===name);
  if(runs.length!==5)throw Error('Expected five cold processes per provider');
  return {name,size:2000,processes:5,
    median_baseline_rss_bytes:median(runs.map(r=>r.baseline_rss_bytes)),
    median_peak_rss_bytes:median(runs.map(r=>r.process_peak_rss_bytes)),
    min_peak_rss_bytes:Math.min(...runs.map(r=>r.process_peak_rss_bytes)),
    max_peak_rss_bytes:Math.max(...runs.map(r=>r.process_peak_rss_bytes)),
    median_peak_rss_increase_bytes:median(runs.map(r=>r.process_peak_rss_increase_bytes)),
    median_sampled_peak_heap_growth_bytes:median(runs.map(r=>r.sampled_peak_heap_growth_bytes))};
});
const retained = rows => names.map(name=>{
  const runs=rows.filter(r=>r.retention_repetitions&&r.name===name);
  if(runs.length!==3)throw Error('Expected three retention processes per provider');
  const last=runs.map(r=>r.checkpoints.at(-1));
  const result={name,size:2000,processes:3,warmups:5,measured_streams:30,
    median_baseline_post_gc_heap_bytes:median(runs.map(r=>r.baseline_post_gc_heap_bytes)),
    median_post_gc_heap_bytes:median(last.map(r=>r.post_gc_heap_bytes)),
    median_post_gc_heap_growth_bytes:median(runs.map(r=>r.checkpoints.at(-1).post_gc_heap_bytes-r.baseline_post_gc_heap_bytes)),
    median_post_gc_rss_bytes:median(last.map(r=>r.post_gc_rss_bytes)),
    min_post_gc_rss_bytes:Math.min(...last.map(r=>r.post_gc_rss_bytes)),
    max_post_gc_rss_bytes:Math.max(...last.map(r=>r.post_gc_rss_bytes)),
    checkpoints:[10,20,30].map(n=>({completed_streams:n,
      median_post_gc_heap_bytes:median(runs.map(r=>r.checkpoints.find(c=>c.completed_streams===n).post_gc_heap_bytes)),
      median_post_gc_rss_bytes:median(runs.map(r=>r.checkpoints.find(c=>c.completed_streams===n).post_gc_rss_bytes))}))};
  for(const f of ['gc_committed_bytes','gc_heap_size_bytes','gc_fragmented_bytes','external_bytes','array_buffer_bytes'])
    if(last.every(r=>Number.isFinite(r[f])))result['median_'+f]=median(last.map(r=>r[f]));
  return result;
});
const prior=JSON.parse(readFileSync(new URL('results.json',import.meta.url),'utf8'));
const transport=json('transport.json'),compatibility=json('compatibility.json'),compatBefore=json('compatibility-before.json');
const failures=doc=>doc.tests.filter(t=>t.status==='failed').map(t=>t.testId).sort();
const same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const transportUnchanged=same(failures(transport),[...prior.validation.unchanged_transport_failures].sort());
const compatUnchanged=same(failures(compatibility),failures(compatBefore));
if(!transportUnchanged||!compatUnchanged)throw Error('New behavior failures');
const summary=doc=>({passed:doc.passed,failed:doc.failed});
const parity=doc=>({passed:doc.cases-doc.failures,failed:doc.failures,...(doc.skipped===undefined?{}:{skipped:doc.skipped})});
const report={
  schema_version:1,recorded_at_utc:new Date().toISOString(),
  baseline_description:'Previous optimization pass, preserved before this pass; not pristine HEAD.',
  base_commit:prior.baseline_commit,sdk:prior.sdk,operating_system:prior.operating_system,
  pi:captures.pi.timing[0],pisharp:captures.after.timing[0],harness_hashes:json('harness-hashes.json'),
  methodology:[
    'Actual installed Pi 1.1.0 and PiSharp HTTP/SSE providers, with identical fake offline responses. No credentials or model network latency.',
    '2000 deltas of 32 ASCII characters, 214100 wire bytes; every stream verifies delta count, successful terminal and final text. Timing also covers 500/1000 deltas.',
    'Cold RSS: five fresh processes per provider/implementation, one stream, no warmup; implementations interleaved, sequential. Windows OS peak working set includes runtime/JIT/native memory.',
    'Sustained RSS/heap: three fresh processes per implementation/provider, five warmups then 30 streams, full GC after warmup and at streams 10/20/30. RSS measured after GC. This is a diagnostic, not normal application GC scheduling.',
    'Post-GC heap is managed live data (.NET GC.GetTotalMemory, V8 heapUsed), not RAM. GC committed includes reusable memory without live objects. V8 external includes arrayBuffers; do not add them.',
    'Timing: three fresh processes per implementation, order interleaved/rotated. Each uses five warmups and median of seven iterations; report median of three process medians, GC before each, no sampler in timed intervals. Separate .NET cumulative allocation/event-boundary heap trial, median across three processes. Release, TieredCompilation=0, default workstation GC.',
    'Allocation profile: dotnet-trace 10.0.750501 gc-verbose, 30 streams; TraceEvent 3.2.8 groups GC allocation ticks by type/nearest PiSharp frame. Sample totals/sites are estimates including startup, not exact accounting or retained memory.',
    'Cumulative allocated_bytes is exact .NET allocation churn. No equivalent V8 cumulative counter used; do not compare it to V8 heapUsed growth.',
    'Provider benchmark processes only. Cold Pi imports the selected provider. Full interactive CLI sessions/extensions/tools/long histories have not been RAM-qualified here.',
    'Node 24.19.0 used: default Node 22.18.0 is below Pi minimum 22.19.0. Global Node selection unchanged.',
    'Unavailable pinned SDK 10.0.401 bypassed by building absolute project paths from temporary cwd using installed 10.0.400. Repository SDK settings unchanged.',
    'Working set varies with GC/native allocator/OS. Repeated-stream ranges matter; cold peaks alone do not establish lower sustained RAM than Pi.'
  ],
  identical_payloads:[...wires].map(([size,signature])=>({size,signature})),
  cold_process_memory:Object.fromEntries(Object.entries(captures).map(([k,v])=>[k,cold(v.cold)])),
  sustained_post_gc_memory:Object.fromEntries(Object.entries(captures).map(([k,v])=>[k,retained(v.retained)])),
  timing_and_allocation:Object.fromEntries(Object.entries(captures).map(([k,v])=>[k,v.timing.filter(r=>names.includes(r.name))])),
  allocation_profile:Object.fromEntries(['before','after'].map(k=>[k,Object.fromEntries(names.map(n=>[n,json(k+'-profile-'+n+'.json')]))])),
  validation:{
    transport:summary(transport),transport_failures_unchanged:transportUnchanged,unchanged_transport_failures:failures(transport),
    compatibility:summary(compatibility),compatibility_before:summary(compatBefore),compatibility_failures_unchanged:compatUnchanged,
    unchanged_compatibility_failures:failures(compatibility),mistral:summary(json('mistral.json')),sessions:summary(json('sessions.json')),
    cli_json:parity(json('cli-json.json')),interactive:parity(json('interactive.json')),
    frozen_source_fixtures:json('source-fixture-validation.json'),
    new_guards:['Completions 1000-delta allocation <12 MB; Mistral <7 MB',
      'Mistral BOM/multiline/fragmented UTF-8 reads 1/2/3/7/31/127/4096 bytes and per-frame limits',
      'Strict JSON escaped/lone-surrogate duplicate names; objects 0/1/7/8/9/16/300 properties; depths 1000/1001',
      'Generic SSE 9000/12000-character fields across single/multiple StringBuilder chunks, subsequent buffer reuse, 1/4096-byte reads, Standard/SDK profiles'],
    builds:'Affected projects/benchmarks built Release, zero warnings/errors.',
    patch_checks:'git diff --check and git apply --check restore-previous-pass.patch passed.'
  },
  reproduce:[
    'Build tools/PerformanceReview/PerformanceReview.csproj and TraceAnalysis/TraceAnalysis.csproj in Release.',
    'Previous-pass DLLs preserved locally under ignored tools/PerformanceReview/bin/memory-pass-2-before and memory-pass-2-before-retention. Rebuild elsewhere by applying restore-previous-pass.patch to an isolated candidate copy, then build the benchmark. Do not apply to active checkout.',
    'measure-memory-pass-2.ps1 -PiRoot <installed package root> -Node <Node24 executable> -BeforeHarness <before DLL>; requires fresh output files.',
    'measure-retention-pass-2.ps1 with same arguments using previous production sources plus current benchmark helper for BeforeHarness; requires fresh detailed output files.',
    'measure-timing-pass-2.ps1 with same arguments, using preserved before DLL; requires fresh timing-series output files.',
    'dotnet-trace collect --profile gc-verbose --output <fresh trace> -- dotnet <harness DLL> --pi-comparison --memory-case <provider-http-stream> 2000 --profile-repetitions 30',
    'dotnet TraceAnalysis.dll <trace.nettrace> prints summaries. Full traces remain in temp profile directory; summaries saved here.',
    'node tools/PerformanceReview/aggregate-memory-pass-2.mjs rebuilds results.json and RESULTS.md from saved captures.'
  ]
};
const mib=n=>(n/1048576).toFixed(1);
const at=(section,k,n)=>report[section][k].find(r=>r.name===n);
const reduction=(a,b)=>((1-b/a)*100).toFixed(1)+'%';
let md='# Memory optimization pass 2\n\nPrevious PiSharp means the previous optimization pass. Pi is the installed **1.1.0**, using Node **24.19.0**; PiSharp is .NET **10.0.11**, Release. The offline HTTP/SSE benchmarks verify every final message and use identical 2,000 × 32-character delta streams.\n\n';
md+='| Process RAM (MiB) | Previous PiSharp | Current PiSharp | Pi 1.1.0 |\n|---|---:|---:|---:|\n';
for(const n of names){
  const label=n.startsWith('openai')?'Completions':'Mistral';
  md+='| '+label+' cold peak, median of 5 | '+['before','after','pi'].map(k=>mib(at('cold_process_memory',k,n).median_peak_rss_bytes)).join(' | ')+' |\n';
  md+='| '+label+' after 30 streams + GC, median of 3 | '+['before','after','pi'].map(k=>mib(at('sustained_post_gc_memory',k,n).median_post_gc_rss_bytes)).join(' | ')+' |\n';
}
md+='\nBoth cold and sustained RSS fell. Completions cold peak is now close to Pi; **sustained RSS remains higher than Pi for both providers**. These provider benchmarks do not measure the full interactive CLI.\n\n';
md+='| .NET allocation per 2,000-delta stream | Previous | Current | Reduction |\n|---|---:|---:|---:|\n';
for(const n of names){
  const a=report.timing_and_allocation.before.find(r=>r.name===n&&r.size===2000),b=report.timing_and_allocation.after.find(r=>r.name===n&&r.size===2000);
  md+='| '+n+' | '+mib(a.allocated_bytes)+' MiB | '+mib(b.allocated_bytes)+' MiB | '+reduction(a.allocated_bytes,b.allocated_bytes)+' |\n';
}
md+='\nAllocation totals measure churn, not retained RAM. Timing in this campaign (before → after → Pi):\n\n';
for(const n of names)md+='- '+n+': '+['before','after','pi'].map(k=>report.timing_and_allocation[k].find(r=>r.name===n&&r.size===2000).median_ms.toFixed(2)+' ms').join(' → ')+'.\n';
md+='\n## Profile and fixes\n\n- Mistral copied the entire remaining 4 KiB read buffer after each small frame. A consumed offset now advances through the buffer, compacting once per read. Single-line parsing avoids regex/LINQ arrays. Valid Unicode text avoids a redundant sanitization copy.\n- Completions repeatedly built/serialized/parsed identical snapshots. It now reuses one frame capture and its immutable owned JSON, omits the message before cloning descriptors, and uses existing message/content builders before final strict admission.\n- JSON writes use one growable byte buffer instead of the writer/MemoryStream copying path. Duplicate-key validation holds the first eight names inline, using a hash set for larger objects.\n- Generic SSE decoding uses spans for single-chunk lines, allocating only retained event names/IDs. Existing multiline/fragmented behavior remains covered.\n\n';
for(const n of names)md+='30-stream allocation sampling, '+n+': '+mib(report.allocation_profile.before[n].sampled_allocation_bytes)+' → '+mib(report.allocation_profile.after[n].sampled_allocation_bytes)+' MiB. Sampling includes startup and is approximate; exact per-stream allocation appears above.\n\n';
md+='Remaining Completions allocation leaders are JSON writing/decoding and owned source snapshots. Mistral now spends its largest shares on framing strings, parsing and owned JSON validation rather than repeated remainder copies.\n\n## Remaining RAM gap\n\n';
for(const n of names){
  const a=at('sustained_post_gc_memory','after',n),p=at('sustained_post_gc_memory','pi',n);
  md+='- '+n+': live post-GC heap '+mib(a.median_post_gc_heap_bytes)+' MiB (Pi '+mib(p.median_post_gc_heap_bytes)+' MiB), while .NET holds '+mib(a.median_gc_committed_bytes)+' MiB of GC committed space. Sustained RSS ranges '+mib(a.min_post_gc_rss_bytes)+'–'+mib(a.max_post_gc_rss_bytes)+' MiB; Pi '+mib(p.min_post_gc_rss_bytes)+'–'+mib(p.max_post_gc_rss_bytes)+' MiB.\n';
}
md+='\nLive heap is broadly stable at the 10/20/30-stream checkpoints. These runs point to GC space retained for reuse, plus runtime/native memory, as the largest remaining resident-memory cost; they do not establish absence of leaks in other workloads. No forced GC, global GC tuning, pooling of mutable snapshots or reduced limits were added to production.\n\n## Behavior verification\n\n';
md+='- Frozen source captures: 38 anchored-tool + 184 thinking scenarios passed; 409 turns and 4,020 complete publications, matching snapshot shape/order and serialized values.\n- Mistral 60/60; sessions 138/138; CLI JSON 17/17; selected interactive/component/startup cases 55/55 passed.\n- Transport: 219 passed / 24 failed, exactly the existing failure IDs. Compatibility: 224 passed / 10 failed on both previous/current production sources, identical failure IDs. One existing failure requires a missing released npm catalog artifact. These suites are not fully green.\n- Added allocation ceilings, fragmented-read/frame-limit checks, 9/12-KiB SSE line/buffer reuse checks and duplicate-key/depth controls. Release builds and whitespace checks passed.\n\nSee results.json for measurements, ranges, profile summaries, failure IDs and reproduction steps. Raw JSONL captures and behavioral summaries are alongside it. restore-previous-pass.patch reconstructs this pass’s production baseline in an isolated candidate copy.\n';
writeFileSync(resolve(base,'results.json'),JSON.stringify(report,null,2)+'\n');
writeFileSync(resolve(base,'RESULTS.md'),md);
console.log(JSON.stringify({report:'memory-pass-2/results.json',cold:report.cold_process_memory,sustained:report.sustained_post_gc_memory},null,2));
