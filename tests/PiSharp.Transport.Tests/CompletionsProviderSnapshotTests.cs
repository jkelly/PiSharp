using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsProviderSnapshotTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly ChatRequest Request = new(new("hook-snapshot", "openai-completions", "openai"), []);
    private const string Finish = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}";
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-provider-snapshot.actual-raw-bits-and-ecmascript-json-in-both-views", PairedViews);
        yield return ("completions-provider-snapshot.awaited-after-legacy-before-mapper-and-retainable", Awaited);
        yield return ("completions-provider-snapshot.held-callback-cancel-retains-physical-owner", HeldCancel);
        yield return ("completions-provider-snapshot.legacy-and-snapshot-faults-sanitized-and-joined", Faults);
        yield return ("completions-provider-snapshot.donetoken-errors-falsy-and-thread-envelopes", EventAdmission);
        yield return ("completions-provider-snapshot.character-and-utf8-envelope-limits", Limits);
    }

    private static async Task PairedViews()
    {
        const string value = "{\"error\":-0,\"10\":\"ten\",\"2\":\"two\",\"wide\":9007199254740993,\"small\":1e-7,\"null\":null,\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
        const string serialized = "{\"2\":\"two\",\"10\":\"ten\",\"error\":0,\"wide\":9007199254740992,\"small\":1e-7,\"null\":null,\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
        foreach (var source in new[] {false,true})
        {
            JsonData? legacy = null; CompletionsSourceSnapshot? snapshot = null; var order = new List<string>();
            using var fixture = new Fixture(Data(value), new()
            {
                OnProviderStreamEvent = (chunk, model, token) => { Check(model == Request.Model && !token.IsCancellationRequested, "Raw hook lost invocation context."); legacy = chunk; order.Add("raw"); return ValueTask.CompletedTask; },
                OnProviderStreamEventSnapshot = (value, model, token) => { Check(model == Request.Model && !token.IsCancellationRequested, "Snapshot hook lost invocation context."); snapshot = value; order.Add("snapshot"); return ValueTask.CompletedTask; }
            });
            var result = await Complete(fixture.Transport, source);
            Check(result.Message.StopReason == StopReason.Stop && order.SequenceEqual(new[] {"raw","snapshot"}), "Hooks were duplicated, reordered or lost mapper success.");
            Check(legacy!.ToString() == value && Bits(legacy.Value.GetProperty("error")) == "8000000000000000", "The original callback value was normalized or borrowed.");
            Check(snapshot!.Raw.Value.GetProperty("value").GetRawText() == value && Bits(snapshot.Raw.Value.GetProperty("value").GetProperty("error")) == "8000000000000000" &&
                snapshot.SerializedJson == serialized && snapshot.OwnUndefinedPaths.IsEmpty, "Raw binary64 and serialized JSON views were conflated.");
            fixture.Joined();
            // These retained owned views remain usable after all physical ownership has ended.
            using var projected = JsonDocument.Parse(snapshot.SerializedJson);
            Check(projected.RootElement.GetProperty("error").GetRawText() == "0" && snapshot.Raw.Value.GetProperty("value").GetProperty("wide").GetRawText() == "9007199254740993",
                "Retained snapshot lost the separately owned raw input.");
        }
    }

    private static async Task Awaited()
    {
        var entered = Gate(); var release = Gate(); var order = new ConcurrentQueue<string>();
        using var fixture = new Fixture(Data(Finish), new()
        {
            OnProviderStreamEvent = (_,_,_) => { order.Enqueue("raw"); return ValueTask.CompletedTask; },
            OnProviderStreamEventSnapshot = async (_,_,_) => { order.Enqueue("snapshot-enter"); entered.TrySetResult(); await release.Task; order.Enqueue("snapshot-return"); }
        });
        await using var run = await fixture.Transport.StartAsync(Request);
        var first = await run.NextAsync(); Check(first.Value!.Type == "start", "Start was not delivered before the event hook.");
        var next = run.NextAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Check(!next.IsCompleted && !run.SourceResult.IsCompleted && !run.CanonicalCompletion.IsCompleted && fixture.Body.AsyncCloses == 0 &&
                order.SequenceEqual(new[] {"raw","snapshot-enter"}), "The mapper or owner advanced past an unjoined snapshot callback.");
            release.TrySetResult(); await next.WaitAsync(Deadline); await Drain(run); await run.CanonicalCompletion.WaitAsync(Deadline);
            Check(order.SequenceEqual(new[] {"raw","snapshot-enter","snapshot-return"}), "Awaited callback order changed."); fixture.Joined();
        }
        finally { release.TrySetResult(); run.Cancel(); await run.DisposeAsync(); }
    }

    private static async Task HeldCancel()
    {
        var entered = Gate(); var release = Gate(); var cancelled = Gate();
        using var fixture = new Fixture(Data(Finish), new()
        {
            OnProviderStreamEventSnapshot = async (_,_,token) => { using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; }
        }, holdClose:true);
        await using var run = await fixture.Transport.StartAsync(Request); var drain = Drain(run);
        try
        {
            await entered.Task.WaitAsync(Deadline); run.Cancel(); await cancelled.Task.WaitAsync(Deadline);
            var semantic = await run.SourceResult.WaitAsync(Deadline);
            Check(semantic.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "aborted" &&
                !run.CanonicalCompletion.IsCompleted && fixture.Body.AsyncCloses == 0 && !fixture.Content.Disposed,
                "A held callback lost invocation ownership or granted canonical authority on semantic cancellation.");
            release.TrySetResult(); await fixture.Body.CloseEntered.Task.WaitAsync(Deadline);
            Check(!run.CanonicalCompletion.IsCompleted && !fixture.Content.Disposed, "Canonical completion skipped held physical cleanup.");
            fixture.Body.ReleaseClose.TrySetResult(); await drain.WaitAsync(Deadline); await run.DisposeAsync().AsTask().WaitAsync(Deadline);
            Check((await run.CanonicalCompletion).Failure?.Kind == ChatFailureKind.Cancelled, "Snapshot cancellation lost its canonical failure."); fixture.Joined();
        }
        finally { release.TrySetResult(); fixture.Body.ReleaseClose.TrySetResult(); run.Cancel(); await run.DisposeAsync(); await drain; }
    }

    private static async Task Faults()
    {
        foreach (var source in new[] {false,true}) foreach (var failRaw in new[] {false,true})
        {
            var raw = 0; var snapshots = 0;
            using var fixture = new Fixture(Data(Finish), new()
            {
                OnProviderStreamEvent = (_,_,_) => { raw++; if (failRaw) throw new IOException("PRIVATE_RAW_HOOK"); return ValueTask.CompletedTask; },
                OnProviderStreamEventSnapshot = (_,_,_) => { snapshots++; throw new IOException("PRIVATE_SNAPSHOT_HOOK"); }
            });
            var result = await Complete(fixture.Transport, source);
            Check(raw == 1 && snapshots == (failRaw ? 0 : 1) && result.Failure is not null && result.Message.StopReason == StopReason.Error &&
                !PiWireJson.WriteMessage(result.Message).ToString().Contains("PRIVATE_", StringComparison.Ordinal), "Callback failure escaped sanitization or bypassed owned cleanup."); fixture.Joined();
        }
        using var stop = new CancellationTokenSource(); var typed = 0;
        using var cancelled = new Fixture(Data(Finish), new()
        {
            OnProviderStreamEvent = (_,_,_) => { stop.Cancel(); return ValueTask.CompletedTask; },
            OnProviderStreamEventSnapshot = (_,_,_) => { typed++; return ValueTask.CompletedTask; }
        });
        var frames = await Collect(cancelled.Transport.StreamAsync(Request, stop.Token));
        Check(typed == 0 && frames.OfType<StreamTerminalEvent>().Single().Message.StopReason == StopReason.Aborted, "Legacy cancellation admitted a new snapshot callback."); cancelled.Joined();
    }

    private static async Task EventAdmission()
    {
        var wires = new[] {
            ("data: [DONE]\n\n",0),
            ("event: error\ndata: " + Finish + "\n\n",0),
            ("data: {\"error\":true}\n\n",0),
            (Data("{\"error\":-0,\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"),1),
            ("event: thread.message.delta\ndata: {\"n\":-0}\n\n" + Data(Finish),2)
        };
        foreach (var (wire,count) in wires)
        {
            var raw = 0; var snapshots = new List<CompletionsSourceSnapshot>();
            using var fixture = new Fixture(wire,new()
            {
                OnProviderStreamEvent = (_,_,_) => { raw++; return ValueTask.CompletedTask; },
                OnProviderStreamEventSnapshot = (value,_,_) => { snapshots.Add(value); return ValueTask.CompletedTask; }
            });
            await Complete(fixture.Transport,true);
            Check(raw == count && snapshots.Count == count, "Sentinel/error admission or callback cardinality changed.");
            if (count == 2) Check(snapshots[0].SerializedJson == "{\"event\":\"thread.message.delta\",\"data\":{\"n\":0}}" &&
                Bits(snapshots[0].Raw.Value.GetProperty("value").GetProperty("data").GetProperty("n")) == "8000000000000000", "The real thread envelope lost raw data or source JSON.");
            fixture.Joined();
        }
    }

    private static async Task Limits()
    {
        foreach (var byteLimit in new[] {false,true})
        {
            var raw = 0; var snapshots = 0; var opaque = new string(byteLimit ? '\u03c0' : 'x', byteLimit ? 600 : 1400);
            var chunk = "{\"opaque\":\"" + opaque + "\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}";
            using var fixture = new Fixture(Data(chunk), new()
            {
                OnProviderStreamEvent = (_,_,_) => { raw++; return ValueTask.CompletedTask; },
                OnProviderStreamEventSnapshot = (_,_,_) => { snapshots++; return ValueTask.CompletedTask; }
            }, options: new(){MaximumSourceValueCharacters = byteLimit ? 2000 : 1000, MaximumSourceValueBytes = byteLimit ? 1000 : 4000});
            var result = await Complete(fixture.Transport, false);
            Check(raw == 1 && snapshots == 0 && result.Failure is not null && result.Message.StopReason == StopReason.Error, "Snapshot envelope limits were bypassed or failed before actual hook admission."); fixture.Joined();
        }
    }

    private static string Data(string value) => "data: " + value + "\n\ndata: [DONE]\n\n";
    private static string Bits(JsonElement value) => unchecked((ulong)BitConverter.DoubleToInt64Bits(value.GetDouble())).ToString("x16");
    private static async Task<ChatResult> Complete(CompletionsHttpSseTransport transport,bool source)
    {
        if (!source) { await using var native = await new ChatClient(transport).StartAsync(Request); await Collect(native.ReadEventsAsync()); return await native.Completion.WaitAsync(Deadline); }
        await using var run = await transport.StartAsync(Request); await Drain(run).WaitAsync(Deadline); return await run.CanonicalCompletion.WaitAsync(Deadline);
    }
    private static async Task Drain(CompletionsRun run) { while (!(await run.NextAsync()).Done) { } }
    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> stream) { var frames = new List<StreamEvent>(); await foreach (var frame in stream) frames.Add(frame); return frames; }
    private static void Check(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IDisposable
    {
        public Body Body { get; }
        public StreamProbeContent Content { get; }
        private readonly FakeHttpHandler _handler;
        private readonly HttpClient _client;
        public CompletionsHttpSseTransport Transport { get; }
        public Fixture(string wire,CompletionsLifecycleHooks hooks,bool holdClose=false,CompletionsHttpSseOptions? options=null)
        {
            Body = new(Encoding.UTF8.GetBytes(wire),holdClose); Content = new(Body);
            _handler = new((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=Content})); _client = new(_handler);
            Transport = new(_client,(_,_) => new(HttpMethod.Post,"https://snapshot.invalid/completions"),(options ?? new()) with {Hooks=hooks});
        }
        public void Joined() => Check(Body.AsyncCloses == 1 && Content.DisposeCalls == 1 && !_handler.Disposed, "Snapshot ownership did not close exactly once or disposed the borrowed client.");
        public void Dispose() { Body.ReleaseClose.TrySetResult(); _client.Dispose(); }
    }
    private sealed class Body(byte[] bytes,bool holdClose) : Stream
    {
        private int _offset; private Task? _close; public int AsyncCloses { get; private set; }
        public TaskCompletionSource CloseEntered { get; } = Gate(); public TaskCompletionSource ReleaseClose { get; } = Gate();
        public override ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken token=default) { token.ThrowIfCancellationRequested(); var count=Math.Min(1,Math.Min(destination.Length,bytes.Length-_offset)); bytes.AsMemory(_offset,count).CopyTo(destination); _offset+=count; return ValueTask.FromResult(count); }
        public override ValueTask DisposeAsync() => new(_close ??= CloseCoreAsync());
        private async Task CloseCoreAsync() { AsyncCloses++; CloseEntered.TrySetResult(); if (holdClose) await ReleaseClose.Task; }
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false; public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();} public override void Flush()=>throw new NotSupportedException();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException(); public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
