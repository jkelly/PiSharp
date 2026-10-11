// Run: node --expose-gc pi-comparison.mjs <installed pi-coding-agent package directory>
// No network requests or credentials: both real Pi provider implementations receive fake fetch SSE.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { performance } from 'node:perf_hooks';
import { createHash } from 'node:crypto';

const root = resolve(process.argv[2]);
const memoryOnly = process.argv.includes('--memory-case');
const memoryArgument = process.argv.indexOf('--memory-case');
const selectedName = memoryOnly ? process.argv[memoryArgument + 1] : null;
const selectedSize = memoryOnly ? Number(process.argv[memoryArgument + 2]) : 0;
const retentionArgument = process.argv.indexOf('--retention-repetitions');
const retentionRepetitions = retentionArgument < 0 ? 0 : Number(process.argv[retentionArgument + 1]);
const load = relative => import(pathToFileURL(resolve(root, relative)).href);
const pkg = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'));
const ai = 'node_modules/@earendil-works/pi-ai';
const completions = !memoryOnly || selectedName === 'openai-completions-http-stream'
  ? await load(`${ai}/dist/api/openai-completions.js`) : null;
const mistral = !memoryOnly || selectedName === 'mistral-conversations-http-stream'
  ? await load(`${ai}/dist/api/mistral-conversations.js`) : null;
let buildSessionProjection, AssistantMessageComponent;
if (!memoryOnly) {
  ({ buildSessionProjection } = await load('dist/core/session-manager.js'));
  ({ AssistantMessageComponent } = await load('dist/modes/interactive/components/assistant-message.js'));
  const { initTheme } = await load('dist/modes/interactive/theme/theme.js');
  initTheme('dark', false);
}
if (!global.gc) throw new Error('Run with --expose-gc');
console.log(JSON.stringify({ implementation: pkg.name, version: pkg.version, runtime: process.version, warmups: !memoryOnly || retentionRepetitions > 0 ? 5 : 0, repetitions: retentionRepetitions > 0 ? retentionRepetitions : memoryOnly ? 1 : 7, package_root: root }));

async function measure(name, size, action) {
  if (retentionRepetitions > 0) {
    for (let i = 0; i < 5; i++) await action();
    global.gc();
    const baseline = process.memoryUsage();
    const checkpoints = [];
    for (let i = 1; i <= retentionRepetitions; i++) {
      await action();
      if (i % 10 === 0 || i === retentionRepetitions) {
        global.gc();
        const usage = process.memoryUsage();
        checkpoints.push({ completed_streams:i, post_gc_heap_bytes:usage.heapUsed, post_gc_rss_bytes:usage.rss,
          external_bytes:usage.external, array_buffer_bytes:usage.arrayBuffers });
      }
    }
    console.log(JSON.stringify({ name, size, retention_repetitions:retentionRepetitions, warmups:5,
      baseline_post_gc_heap_bytes:baseline.heapUsed, baseline_post_gc_rss_bytes:baseline.rss,
      baseline_external_bytes:baseline.external, checkpoints }));
    return;
  }
  const timings = [];
  if (!memoryOnly) {
    for (let i = 0; i < 5; i++) await action();
    for (let i = 0; i < 7; i++) {
      global.gc();
      const start = performance.now(); await action();
      timings.push(performance.now() - start);
    }
  }
  global.gc();
  const before = process.memoryUsage();
  const baselinePeakRss = process.resourceUsage().maxRSS * 1024;
  const baseline = before.heapUsed;
  let peak = baseline;
  await action(() => { peak = Math.max(peak, process.memoryUsage().heapUsed); });
  if (memoryOnly)
    console.log(JSON.stringify({ name, size, cold_process:true, baseline_rss_bytes:before.rss,
      baseline_process_peak_rss_bytes:baselinePeakRss, process_peak_rss_bytes:process.resourceUsage().maxRSS*1024,
      process_peak_rss_increase_bytes:process.resourceUsage().maxRSS*1024-baselinePeakRss,
      sampled_peak_heap_growth_bytes:peak-baseline }));
  else
    console.log(JSON.stringify({ name, size, median_ms: timings.sort((a,b) => a-b)[3], min_ms: timings[0], max_ms: timings[6], sampled_peak_heap_growth_bytes: peak - baseline }));
}

for (const count of [500, 1000, 2000]) {
  if (memoryOnly && count !== selectedSize) continue;
  const chunk = `data: ${JSON.stringify({id:'performance',choices:[{index:0,delta:{content:'x'.repeat(32)}}]})}\n\n`;
  const wire = new TextEncoder().encode(chunk.repeat(count) + 'data: {"id":"performance","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n');
  console.log(JSON.stringify({ wire_size: count, wire_bytes: wire.length, sha256: createHash('sha256').update(wire).digest('hex') }));
  for (const [api, implementation] of [['openai-completions',completions], ['mistral-conversations',mistral]]) {
    if (memoryOnly && selectedName !== `${api}-http-stream`) continue;
    const model = { id:'offline',name:'offline',api,provider:api === 'openai-completions' ? 'offline' : 'mistral',baseUrl:'https://offline.invalid',reasoning:false,input:['text'],cost:{input:0,output:0,cacheRead:0,cacheWrite:0},contextWindow:100000,maxTokens:100000 };
    const options = { apiKey:'offline',maxRetries:0,fetch:async () => new Response(wire,{status:200,headers:{'content-type':'text/event-stream'}}) };
    await measure(`${api}-http-stream`,count,async sample => {
      let terminal; let deltas = 0;
      for await (const event of implementation.stream(model,{messages:[]},options)) {
        if (event.type === 'text_delta') deltas++;
        if (event.type === 'done' || event.type === 'error') terminal = event;
        sample?.();
      }
      if (terminal?.type !== 'done' || deltas !== count || terminal.message.content[0].text !== 'x'.repeat(count*32))
        throw new Error(`Pi stream mismatch ${api}: ${JSON.stringify(terminal)}`);
    });
  }
}
for (const count of [100,500,1000,2000]) {
  if (memoryOnly) continue;
  const entries = Array.from({length:count}, (_,i) => ({type:'message',id:`e${i}`,parentId:i===0?null:`e${i-1}`,timestamp:'2024-01-01T00:00:00.000Z',message:{role:'user',content:'x'.repeat(256),timestamp:0}}));
  // SessionManager maintains this index; give Pi its existing index, outside the measured interval.
  const byId = new Map(entries.map(entry => [entry.id,entry]));
  await measure('session-projection-existing-index',count,async sample => {
    const projection = buildSessionProjection(entries,entries.at(-1).id,byId);
    if (projection.messages.length !== count) throw new Error('Pi session projection mismatch');
    sample?.();
  });
}
if (!memoryOnly) {
  const messages = Array.from({length:1000},(_,i) => ({role:'assistant',stopReason:'pending',content:[{type:'thinking',thinking:'reasoning'},{type:'text',text:'x'.repeat((i+1)*32)}]}));
  await measure('ui-1000-updates-one-render',1000,async sample => {
    const component = new AssistantMessageComponent();
    for (const message of messages) { component.updateContent(message,true); sample?.(); }
    const rendered = component.render(80).join('\n');
    if (!rendered.includes('x'.repeat(32))) throw new Error('Pi UI render mismatch');
    sample?.();
  });
}
