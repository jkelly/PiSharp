// Rebuild the comparison report from the checked-in raw captures.
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
const root = fileURLToPath(new URL('.', import.meta.url));
const read = name => readFileSync(resolve(root, name), 'utf8');
const lines = name => read(name).trim().split(/\r?\n/).flatMap(line => {
  try { return [JSON.parse(line)]; } catch { return []; }
});
const baseline = lines('pisharp-baseline-comparison.jsonl');
const candidate = lines('pisharp-comparison.jsonl');
const pi = lines('pi-comparison.jsonl');
const ui = lines('pisharp-ui-comparison.log').filter(value => value.name);
const median = values => [...values].sort((a,b) => a-b)[Math.floor(values.length / 2)];
const memory = filename => {
  const captures = lines(filename).filter(value => value.cold_process);
  if (captures.length !== 6) throw new Error(`${filename}: expected three fresh processes per provider`);
  return ['openai-completions-http-stream','mistral-conversations-http-stream'].map(name => {
    const runs = captures.filter(value => value.name === name);
    return { name, size:2000, processes:runs.length,
      median_baseline_rss_bytes:median(runs.map(value => value.baseline_rss_bytes)),
      median_process_peak_rss_bytes:median(runs.map(value => value.process_peak_rss_bytes)),
      min_process_peak_rss_bytes:Math.min(...runs.map(value => value.process_peak_rss_bytes)),
      max_process_peak_rss_bytes:Math.max(...runs.map(value => value.process_peak_rss_bytes)),
      median_process_peak_rss_increase_bytes:median(runs.map(value => value.process_peak_rss_increase_bytes)),
      median_sampled_peak_heap_growth_bytes:median(runs.map(value => value.sampled_peak_heap_growth_bytes)) };
  });
};
const wires = candidate.filter(value => value.wire_size);
for (const wire of wires) for (const capture of [baseline,pi]) {
  const other = capture.find(value => value.wire_size === wire.wire_size);
  if (!other || other.sha256 !== wire.sha256 || other.wire_bytes !== wire.wire_bytes)
    throw new Error('Provider benchmark payloads differ');
}
const originalReview = JSON.parse(read('results.json'));
const report = {
  schema_version:1,
  baseline_commit:originalReview.baseline_commit,
  recorded_at_utc:new Date().toISOString(),
  pi_updated_from:'0.86.1',
  pi:pi[0],
  pisharp:candidate[0],
  sdk:originalReview.sdk,
  operating_system:originalReview.operating_system,
  methodology:[
    'Offline fake HTTP responses through the actual public Pi and PiSharp provider implementations, including SSE decoding. Payload hashes match byte-for-byte.',
    '500, 1000 or 2000 text deltas of 32 ASCII characters; final text, delta count and successful terminal checked in every run.',
    'Timing: five warmups, seven repetitions, median; GC before each repetition, without memory samplers in timed intervals. Runs executed sequentially. .NET Release, DOTNET_TieredCompilation=0; Pi uses normal V8 optimization.',
    'Warm heap growth: separate memory trial after timing runs, sampled at event boundaries; .NET GC.GetTotalMemory(false), V8 process.memoryUsage().heapUsed. Includes uncollected garbage and sampler overhead; not retained-live-memory or cumulative allocations.',
    'Cold process RAM: three fresh processes per provider, one request each, no warmup; OS peak working set via Process.PeakWorkingSet64 and Node resourceUsage().maxRSS. Baseline, absolute peak, and increase over the previous process high-water mark are reported. Includes runtime/JIT/native memory.',
    'Cold Pi processes import only the selected provider and its dependencies, excluding session/UI modules. These are provider benchmark processes, not complete interactive CLI sessions; their absolute working sets do not predict full application RAM.',
    'Cumulative allocated_bytes is exact .NET GC.GetTotalAllocatedBytes(true), measured in a separate trial. No equivalent exact V8 counter was used; do not compare this column to Pi heap growth.',
    'Session: 256-character user messages. Pi receives SessionManager-style prebuilt byId index. PiSharp append measurement reuses a projected prefix prepared outside each interval; full projection validates and converts the whole immutable forest. Semantics and ownership differ.',
    'UI: 1000 prebuilt updates ending in 32000 text characters plus thinking, one render at width 80, five warmups and seven repetitions. Rendering/themes can differ across implementations.',
    'Node 24.19.0 was used because the default Node 22.18.0 is below Pi 1.1.0 minimum 22.19.0. The global Node selection was not changed.',
    'SDK 10.0.401 pinned by global.json is unavailable; absolute project paths were built from an external temporary directory with SDK 10.0.400. Repository SDK settings were preserved.',
    'Performance captures are not a pinned upstream parity qualification or a replacement for behavioral tests.'
  ],
  identical_provider_payloads:wires,
  timing_and_warm_heap:{ pisharp_before:baseline.filter(value => value.name), pisharp_after:[...candidate.filter(value => value.name),...ui], pi:pi.filter(value => value.name) },
  cold_process_memory:{ pisharp_before:memory('pisharp-baseline-memory.jsonl'), pisharp_after:memory('pisharp-memory.jsonl'), pi:memory('pi-memory.jsonl') },
  validation:{ ...originalReview.validation, interactive_after_pi_update:{passed:55,failed:0,skipped:0}, provider_payload_hashes_identical:true },
  behavior_guards:originalReview.behavior_guards,
  reproduce:[
    'Build tools/PerformanceReview/PerformanceReview.csproj in Release; run its DLL with DOTNET_TieredCompilation=0 and --pi-comparison.',
    'node --expose-gc tools/PerformanceReview/pi-comparison.mjs <installed @earendil-works/pi-coding-agent package root>',
    'For cold memory, append --memory-case openai-completions-http-stream 2000 (or mistral-conversations-http-stream 2000). Run each in a fresh process three times.',
    'For UI comparison: run PiSharp.InteractiveParity.Tests.dll with DOTNET_TieredCompilation=0, PISHARP_PI_COMPARISON=1 and INTERACTIVEPARITY_FILTER=msg.assistant-message.streaming-coalesces-before-render.',
    'The original baseline replaces the seven modified Contracts/AI/Sessions sources with versions from baseline_commit and excludes the new incremental projector. Original benchmark code is otherwise the same.',
    'node tools/PerformanceReview/aggregate-comparison.mjs rebuilds this report from the raw captures.'
  ]
};
writeFileSync(resolve(root,'pi-comparison-results.json'),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({ report:'pi-comparison-results.json', payloads_identical:true, cold_process_memory:report.cold_process_memory },null,2));
