using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Compatibility.Node.Pi;
using static Expect;

internal static class ProviderBackpressureCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    { yield return ("extensions.provider-bounded-progress-order-multiplexing-and-cancel", BoundedProgress); }

    private static async Task BoundedProgress()
    {
        var node = PiNodeHost.FindNode(Environment.GetEnvironmentVariable) ?? throw new SkipCaseException("Node.js is unavailable.");
        var script = PiNodeHost.FindHostScript(_ => null) ?? throw new InvalidOperationException("Bundled Node host missing.");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var extension = Path.Combine(root, "provider.mjs");
        await File.WriteAllTextAsync(extension, """
            import { appendFileSync } from 'node:fs';
            export default function(pi) {
              pi.registerProvider('bounded', { api: 'bounded-api', models: [],
                async *streamSimple(model, context, options) {
                  try {
                    for (let i = 0; i < options.count; i++) yield { type: 'text_delta', contentIndex: 0, delta: String(i) };
                  } finally { appendFileSync(process.cwd() + '/finished.txt', 'finished\n'); }
                }
              });
            }
            """);
        try
        {
            await using var host = await PiNodeHost.StartAsync(new(node, script, root), new Peer(), deadline.Token);
            await host.RequestAsync("init", new { cwd = root, agentDir = root, mode = "print", hasUI = false, version = "offline", theme = "dark" }, deadline.Token);
            var loaded = await host.RequestAsync("load", new { paths = new[] { extension } }, deadline.Token);
            Check(!loaded!.Value.GetProperty("results")[0].TryGetProperty("error", out _), "Provider fixture failed to load.");
            const int window = 32, count = 1000;
            var frames = Channel.CreateBounded<(JsonElement Value, Func<Task> Ack)>(window);
            var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = 0;
            var call = host.RequestStreamAsync("provider.stream", Parameters(count), (value, ack) =>
            {
                Check(frames.Writer.TryWrite((value, ack)), "Producer exceeded bounded queue capacity.");
                if (Interlocked.Increment(ref received) == window) full.TrySetResult();
            }, deadline.Token, window);
            _ = call.ContinueWith(_ => frames.Writer.TryComplete(), TaskScheduler.Default);
            await full.Task.WaitAsync(deadline.Token);
            // A second request settles while provider progress is blocked on credits.
            await host.RequestAsync("flags.set", new { values = new { probe = true } }, deadline.Token);
            Equal(window, Volatile.Read(ref received), "unacknowledged frame high-water mark");
            var index = 0;
            await foreach (var (value, ack) in frames.Reader.ReadAllAsync(deadline.Token))
            { Equal((index++).ToString(), value.GetProperty("delta").GetString(), "ordered delta"); await ack(); }
            await call; Equal(count, index, "lossless event count");
            // The former transport pattern: a stalled reader retains every progress frame.
            var unbounded = Channel.CreateUnbounded<JsonElement>();
            await host.RequestAsync("provider.stream", Parameters(count), deadline.Token,
                value => unbounded.Writer.TryWrite(value.Clone()));
            var legacyQueued = unbounded.Reader.Count;
            Equal(count, legacyQueued, "unbounded control retained all frames");
            while (unbounded.Reader.TryRead(out _)) { }
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var outstanding = 0;
            var cancelled = host.RequestStreamAsync("provider.stream", Parameters(1_000_000), (_, _) =>
            { if (Interlocked.Increment(ref outstanding) == 2) blocked.TrySetResult(); }, cancel.Token, 2);
            await blocked.Task.WaitAsync(deadline.Token); cancel.Cancel();
            try { await cancelled; throw new InvalidOperationException("Blocked provider ignored cancellation."); }
            catch (OperationCanceledException) { }
            await host.RequestAsync("flags.set", new { values = new { probe = false } }, deadline.Token);
            var finished = Path.Combine(root, "finished.txt");
            while (!File.Exists(finished) || File.ReadAllLines(finished).Length < 3)
            {
                if (deadline.IsCancellationRequested) throw new InvalidOperationException("Provider cleanup markers: " + (File.Exists(finished) ? File.ReadAllText(finished) : "missing"));
                await Task.Delay(10);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "extension-slow-consumer", events = count,
                before_queued_frames = legacyQueued, maximum_queued_frames = window, lost_frames = count - index }));
        }
        finally { Directory.Delete(root, true); }
    }
    private static object Parameters(int count) => new { provider = "bounded", api = "bounded-api", model = new { id = "offline" }, context = new { messages = Array.Empty<object>() }, options = new { count } };
    private sealed class Peer : IPiNodeHostPeer
    {
        public ValueTask<JsonNode?> RequestAsync(PiNodeHostRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<JsonNode?>(null);
        public ValueTask NotifyAsync(string method, JsonElement parameters) => ValueTask.CompletedTask;
    }
}
