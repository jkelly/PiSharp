// Offline allocation/time comparison; run Release with DOTNET_TieredCompilation=0.
// The session append setup runs outside the measured interval on every repetition.
// Results are cumulative allocated bytes, not peak resident memory or network timings.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

if (args.Contains("--pi-comparison")) { await PiComparison.Run(args); return; }

var delta = new string('x', 32);
var completionChunk = JsonData.Parse(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = delta } } } }));
var completionFinish = JsonData.Parse("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""");
var mistralChunk = JsonData.Parse(JsonSerializer.Serialize(new { id = "test", choices = new[] { new { delta = new { content = delta } } } }));
var completionRequest = new ChatRequest(new ModelDescriptor("offline", "openai-completions", "offline"), [], 0);
var mistralRequest = new ChatRequest(new ModelDescriptor("offline", "mistral-conversations", "offline"), [], 0);
var mistralOptions = new MistralTextOptions(new Uri("https://offline.invalid/"), true, new(0, 0, 0, 0), "offline");
async IAsyncEnumerable<JsonData> Chunks(int count, [EnumeratorCancellation] CancellationToken token = default)
{ for (int i = 0; i < count; i++) { token.ThrowIfCancellationRequested(); yield return completionChunk; } yield return completionFinish; }
async Task Direct(int count)
{
    var mapper = new OpenAICompletionsWireSource((_, token) => Chunks(count, token));
    StreamTerminalEvent? last = null;
    await foreach (var frame in mapper.StreamAsync(completionRequest)) if (frame is StreamTerminalEvent end) last = end;
    if (last is not StreamDone || ((TextContent)last.Message.Content[0]).Text.Length != count * 32) throw new Exception("direct output mismatch");
}
async Task CanonicalRun(int count)
{
    var mapper = new OpenAICompletionsWireSource((_, token) => Chunks(count, token));
    await using var run = new CompletionsRun(mapper, completionRequest, new CompletionsHttpSseOptions(), default, false, true);
    StreamTerminalEvent? last = null;
    await foreach (var frame in run.ReadCanonicalEventsAsync(default)) if (frame is StreamTerminalEvent end) last = end;
    if (last is not StreamDone || ((TextContent)last.Message.Content[0]).Text.Length != count * 32) throw new Exception("canonical output mismatch");
}
Task Mistral(int count)
{
    var state = new TextState(mistralRequest, mistralOptions);
    for (int i = 0; i < count; i++) state.Convert(mistralChunk);
    if (((TextContent)state.Message(StopReason.Pending).Content[0]).Text.Length != count * 32) throw new Exception("mistral output mismatch");
    return Task.CompletedTask;
}
async Task Measure(string name, int size, Func<Task> action, Action? prepare = null)
{
    prepare?.Invoke(); await action();
    var runs = new List<(double Ms, long Bytes)>();
    for (int repeat = 0; repeat < 3; repeat++)
    {
        prepare?.Invoke();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
        await action(); timer.Stop(); var allocated = GC.GetTotalAllocatedBytes(true) - before;
        runs.Add((timer.Elapsed.TotalMilliseconds, allocated));
    }
    var middle = runs.OrderBy(x => x.Ms).ElementAt(1);
    Console.WriteLine(JsonSerializer.Serialize(new { name, size, median_ms = middle.Ms, allocated_bytes = middle.Bytes }));
}
Console.WriteLine("Runtime=" + Environment.Version + "; synthetic, offline, Release; 32 characters/delta; median of 3 after warmup; process-wide allocations");
foreach (var n in new[] { 500, 1000, 2000 })
{
    await Measure("completions-direct-mapper", n, () => Direct(n));
    await Measure("completions-canonical-run", n, () => CanonicalRun(n));
    await Measure("mistral-state", n, () => Mistral(n));
}
var codec = new SessionEntryCodec();
foreach (var n in new[] { 100, 500, 1000, 2000 })
{
    var entries = ImmutableArray.CreateBuilder<SessionEntry>(n);
    for (int i = 0; i < n; i++) entries.Add(codec.Parse(JsonSerializer.Serialize(new { type = "message", id = "e" + i, parentId = i == 0 ? null : "e" + (i - 1), timestamp = "2024-01-01T00:00:00.000Z", message = new { role = "user", content = new string('x', 256), timestamp = 0 } })));
    var all = entries.MoveToImmutable(); var projector = new SessionContextProjector();
    await Measure("session-full-projection", n, () => { var value = new SessionContextProjector().ProjectLatest(all); if (value.Messages.Length != n) throw new Exception("projection mismatch"); return Task.CompletedTask; });
    var prefix = all.RemoveAt(all.Length - 1);
    await Measure("session-append-projection", n, () => { var value = projector.ProjectLatest(all); if (value.Messages.Length != n) throw new Exception("append mismatch"); return Task.CompletedTask; },
     () => { projector = new SessionContextProjector(); projector.ProjectLatest(prefix); });
}
