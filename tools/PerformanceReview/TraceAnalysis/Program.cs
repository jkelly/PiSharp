using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Text.Json;
var etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var log = new TraceLog(etlx);
var groups = new Dictionary<(string Type, string Site), (long Bytes, int Samples)>();
long total = 0; int samples = 0;
foreach (var item in log.Events)
{
    if (item is not GCAllocationTickTraceData data) continue;
    var frames = new List<string>();
    for (var stack = data.CallStack(); stack is not null; stack = stack.Caller)
        frames.Add(stack.CodeAddress.FullMethodName);
    var site = frames.FirstOrDefault(f => f.StartsWith("PiSharp.")) ?? "(runtime/startup)";
    var key = (data.TypeName, site);
    groups.TryGetValue(key, out var prior);
    var bytes = data.AllocationAmount64;
    groups[key] = (prior.Bytes + bytes, prior.Samples + 1);
    total += bytes; samples++;
}
Console.WriteLine(JsonSerializer.Serialize(new { sampled_allocation_bytes=total, samples, types=groups.GroupBy(g=>g.Key.Type).Select(g=>new{type=g.Key,bytes=g.Sum(x=>x.Value.Bytes)}).OrderByDescending(g=>g.bytes).Take(15), sites=groups.OrderByDescending(g=>g.Value.Bytes).Take(35).Select(g=>new{type=g.Key.Type,site=g.Key.Site,bytes=g.Value.Bytes,samples=g.Value.Samples}) },new JsonSerializerOptions{WriteIndented=true}));
