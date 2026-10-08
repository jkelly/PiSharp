using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;

internal static class SourceProgressRpcTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("source-rpc-model", "openai-responses", "offline-authored");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc.default-source-progress-overlap-owned-write-fault-cleanup-and-ignored-late-callback", DefaultSourceProgress);
    }

    private static async Task DefaultSourceProgress()
    {
        await SuccessfulDelivery();
        foreach (var failure in new[] { "write", "flush" }) await FailedDelivery(failure);
    }

    private static async Task SuccessfulDelivery()
    {
        await using var fixture = await Fixture.Create(null);
        await fixture.Send("prompt", "source-success", "run tools");
        await Stage(fixture.Tool.Entered.Task, fixture.Dispatcher.Completion, "source tool execution");
        fixture.Tool.ReportRelease.TrySetResult();
        await Stage(fixture.Output.FirstUpdateEntered.Task, fixture.Dispatcher.Completion, "actual first update write");
        await Stage(fixture.Tool.ReportsReturned.Task, fixture.Dispatcher.Completion, "both source callback returns");
        Equal(2, fixture.Tool.ImmediateReturns); Equal(2, fixture.Probe.Updates);
        Check(fixture.Probe.SecondDuringFirstWrite, "The second source listener did not enter while the first RPC delivery was pending.");
        Check(!fixture.Dispatcher.WaitForIdleAsync().IsCompleted && !fixture.Tool.CleanupEntered.Task.IsCompleted,
            "Source callback return was confused with execution or RPC settlement.");
        Check(!fixture.Output.Records().Any(record => Type(record) is "tool_execution_update" or "tool_execution_end"),
            "The blocked write was reported as an acknowledged progress/end frame.");
        fixture.Output.FirstUpdateRelease.TrySetResult();
        await Stage(fixture.Output.TwoUpdatesFlushed.Task, fixture.Dispatcher.Completion, "both complete update frames flushed");
        var updates = fixture.Output.Records().Where(record => Type(record) == "tool_execution_update").ToArray(); Equal(2, updates.Length);
        for (var index = 0; index < updates.Length; index++)
        {
            var value = updates[index].Value;
            Equal("source-call", value.GetProperty("toolCallId").GetString()); Equal("read", value.GetProperty("toolName").GetString());
            Equal(index + 1, value.GetProperty("partialResult").GetProperty("details").GetProperty("sequence").GetInt32());
            Equal("1.0", value.GetProperty("partialResult").GetProperty("details").GetProperty("scale").GetRawText());
            Equal(JsonValueKind.Null, value.GetProperty("partialResult").GetProperty("details").GetProperty("nil").ValueKind);
        }
        fixture.Tool.FinishRelease.TrySetResult(); await Stage(fixture.Tool.CleanupEntered.Task, fixture.Dispatcher.Completion, "actual executor cleanup");
        Check(!fixture.Dispatcher.WaitForIdleAsync().IsCompleted && !fixture.Output.Records().Any(record => Type(record) == "tool_execution_end"),
            "Tool end or public settlement bypassed actual cleanup.");
        fixture.Tool.CleanupRelease.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        var records = fixture.Output.Records();
        var updateEnd = Array.FindLastIndex(records, record => Type(record) == "tool_execution_update");
        var toolEnd = Array.FindIndex(records, record => Type(record) == "tool_execution_end");
        var messageEnd = Array.FindIndex(records, record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult");
        Check(updateEnd < toolEnd && toolEnd < messageEnd, "Joined progress/end/durable message order changed.");
        Equal(1, records.Count(record => Type(record) == "agent_settled")); Equal(1, fixture.Provider.Requests); Equal(1, fixture.Tool.Executions);
        Equal(1, fixture.Output.MaximumWrites);
        Equal("final only", fixture.Session.Snapshot.Context.Messages.Single(value => value.Role == "toolResult").WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        foreach (var entry in fixture.Session.Snapshot.Log.Entries) Check(!entry.WireBody.ToString().Contains("partial-", StringComparison.Ordinal), "Partial output became durable context.");
        var count = records.Length;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(fixture.Tool.Retained!(null!, canceled.Token).IsCompletedSuccessfully, "Late source callback performed admission after actual settlement.");
        Equal(count, fixture.Output.Records().Length);
    }

    private static async Task FailedDelivery(string failure)
    {
        await using var fixture = await Fixture.Create(failure);
        await fixture.Send("prompt", "source-fault", "run tools");
        await Stage(fixture.Tool.Entered.Task, fixture.Dispatcher.Completion, "source tool execution before queued inputs");
        // These are actual accepted RPC queues before the output fault, rather than unobserved test-side enqueue calls.
        await fixture.Send("steer", "keep-steering", "preserved steering");
        await fixture.Send("follow_up", "keep-followup", "preserved followup");
        Equal(1, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
        Equal(1, fixture.Session.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
        fixture.Tool.ReportRelease.TrySetResult();
        await Stage(fixture.Output.FirstUpdateEntered.Task, fixture.Dispatcher.Completion, "faulting update write/flush");
        await Stage(fixture.Tool.ReportsReturned.Task, fixture.Dispatcher.Completion, "default source callbacks return during pending output");
        Equal(2, fixture.Tool.ImmediateReturns); Check(fixture.Probe.SecondDuringFirstWrite, "Default source progress was silently serialized at callback admission.");
        fixture.Output.FirstUpdateRelease.TrySetResult();
        await Stage(fixture.Output.DisposeEntered.Task, fixture.Dispatcher.Completion, "actual owned output cleanup");
        Check(!fixture.Dispatcher.Completion.IsCompleted, "Dispatcher fault returned before owned stream cleanup.");
        fixture.Output.DisposeRelease.TrySetResult();
        await Stage(fixture.Tool.CleanupEntered.Task, fixture.Dispatcher.Completion, "actual write fault aborts execution and enters cleanup");
        Check(fixture.Tool.CancellationAtCleanup && fixture.Session.Snapshot.Agent.CancellationRequested,
            "Write fault cancellation witnesses: registeredCallback=" + fixture.Tool.CancellationObserved +
            "; executionTokenAtCleanup=" + fixture.Tool.CancellationAtCleanup +
            "; publicAgent=" + fixture.Session.Snapshot.Agent.CancellationRequested + "; fault=" + failure + ".");
        // WaitAsync may resume into finally before an older token observer runs. CleanupRelease
        // keeps execution/its using registration alive until the actual observer gate settles.
        await Stage(fixture.Tool.CancellationWitness.Task, fixture.Dispatcher.Completion, "actual registered tool cancellation observer");
        Check(fixture.Tool.CancellationObserved, "Registered tool cancellation observer did not run before registration/cleanup settlement.");
        var first = fixture.Dispatcher.DisposeAsync().AsTask(); var concurrent = fixture.Dispatcher.DisposeAsync().AsTask();
        Check(!first.IsCompleted && !concurrent.IsCompleted && !fixture.Dispatcher.Completion.IsCompleted,
            "Concurrent fault/disposal skipped admitted work or tool cleanup.");
        Check(!fixture.Output.Records().Any(record => Type(record) is "tool_execution_update" or "tool_execution_end" or "agent_settled") &&
            !fixture.Session.Snapshot.Context.Messages.Any(value => value.Role == "toolResult"), "Failed output acquired a final acknowledgement.");
        fixture.Tool.CleanupRelease.TrySetResult();
        var error = await Throws<RpcDispatchException>(() => fixture.Dispatcher.Completion);
        Equal(RpcDispatchFailure.OutputFailed, error.Failure); Check(!error.Message.Contains("private", StringComparison.Ordinal), "Output fault exposed private diagnostics.");
        Equal(RpcDispatchFailure.OutputFailed, (await Throws<RpcDispatchException>(() => first)).Failure);
        Equal(RpcDispatchFailure.OutputFailed, (await Throws<RpcDispatchException>(() => concurrent)).Failure);
        Equal(1, fixture.Provider.Requests); Equal(1, fixture.Tool.Executions); Equal(0, fixture.Tool.EffectsAfterFailure);
        Equal(1, fixture.Output.Disposals); Equal(1, fixture.Output.MaximumWrites); Check(fixture.Tool.Cleaned, "Executor cleanup was abandoned.");
        Check(fixture.Session.Snapshot.IsDisposed && !fixture.Session.Snapshot.Agent.IsRunning, "Owned durable session did not finish shared shutdown.");
        var queues = fixture.Session.GetPendingInputQueueSnapshot(); Equal(1, queues.SteeringMessages.Length); Equal(1, queues.FollowUpMessages.Length);
        Equal(1, fixture.Output.Records().Count(record => Response(record, "source-fault")));
        var count = fixture.Output.Records().Length;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check(fixture.Tool.Retained!(null!, canceled.Token).IsCompletedSuccessfully, "Late callback after failed settlement was not ignored.");
        Equal(count, fixture.Output.Records().Length);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory; private readonly IDisposable _probe;
        public PersistentAgentSession Session { get; }
        public RpcSessionDispatcher Dispatcher { get; }
        public Output Output { get; }
        public Adapter Tool { get; }
        public Provider Provider { get; }
        public Probe Probe { get; }
        private Fixture(string directory, PersistentAgentSession session, RpcSessionDispatcher dispatcher, Output output, Adapter tool, Provider provider, Probe probe, IDisposable subscription)
        { _directory = directory; Session = session; Dispatcher = dispatcher; Output = output; Tool = tool; Provider = provider; Probe = probe; _probe = subscription; }
        public static async Task<Fixture> Create(string? failure)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PiSharp-source-rpc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "source-header", timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
            var output = new Output(failure); var tool = new Adapter(failure is not null); var provider = new Provider(); var ids = 0;
            var invoker = new ToolInvoker([tool], new Policy());
            // No AgentOptions/profile override: this proves the public default traverses the durable/RPC boundary.
            var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header,
                new(Model, provider, [new("read", invoker)], Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))),
                () => 123, () => "source-entry-" + Interlocked.Increment(ref ids));
            var probe = new Probe(output); var subscription = session.Subscribe(probe);
            var model = JsonData.Parse("""{"id":"source-rpc-model","api":"openai-responses","provider":"offline-authored","name":"Authored source RPC model","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":4096,"maxTokens":128,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
            var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output, new(MaximumPendingWrites: 1), JsonlStreamOwnership.Owned),
                () => 123, [new(Model, model)]);
            return new(directory, session, dispatcher, output, tool, provider, probe, subscription);
        }
        public Task Send(string type, string id, string message) => Dispatcher.SubmitAsync(JsonData.FromElement(JsonSerializer.SerializeToElement(new { type, id, message })));
        public async ValueTask DisposeAsync()
        {
            Tool.ReportRelease.TrySetResult(); Tool.FinishRelease.TrySetResult(); Tool.CleanupRelease.TrySetResult();
            Output.FirstUpdateRelease.TrySetResult(); Output.DisposeRelease.TrySetResult(); Session.Abort();
            try { await Dispatcher.DisposeAsync(); } catch (RpcDispatchException) { }
            await Session.DisposeAsync(); _probe.Dispose();
            var target = Path.GetFullPath(_directory); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(target) != temporary || !Path.GetFileName(target).StartsWith("PiSharp-source-rpc-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned source RPC fixture.");
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class Adapter(bool failing) : IPreparedToolAdapter
    {
        public string Name => "read";
        public readonly TaskCompletionSource Entered = Gate(), ReportRelease = Gate(), ReportsReturned = Gate(), FinishRelease = Gate(), CleanupEntered = Gate(), CleanupRelease = Gate(), CancellationWitness = Gate();
        public ToolProgressCallback? Retained; public int Executions, ImmediateReturns, EffectsAfterFailure; public bool CancellationObserved, CancellationAtCleanup, Cleaned;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "read",
            PreparedToolActionKind.Path, "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ExecuteAsync(action, (_, _) => ValueTask.CompletedTask, token);
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token)
        {
            Retained = progress; Executions++; Entered.TrySetResult();
            using var registration = token.Register(() => { CancellationObserved = true; CancellationWitness.TrySetResult(); });
            try
            {
                await ReportRelease.Task.WaitAsync(token);
                for (var index = 1; index <= 2; index++)
                {
                    var returned = progress(ToolResult.Success("partial-" + index) with
                        { Details = JsonData.Parse("{\"sequence\":" + index + ",\"scale\":1.0,\"nil\":null}") }, token);
                    Check(returned.IsCompletedSuccessfully, "Default source callback returned a pending delivery receipt."); ImmediateReturns++;
                }
                ReportsReturned.TrySetResult();
                if (failing) { await Gate().Task.WaitAsync(token); EffectsAfterFailure++; }
                else await FinishRelease.Task.WaitAsync(token);
                return ToolResult.Success("final only", terminate: true);
            }
            finally { CancellationAtCleanup = token.IsCancellationRequested; CleanupEntered.TrySetResult(); await CleanupRelease.Task; Cleaned = true; }
        }
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Probe(Output output) : IAgentEventSink
    {
        public int Updates; public bool SecondDuringFirstWrite;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        {
            if (observation is ToolExecutionUpdated && Interlocked.Increment(ref Updates) == 2)
                SecondDuringFirstWrite = output.FirstUpdateEntered.Task.IsCompleted && !output.FirstUpdateRelease.Task.IsCompleted;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Provider : IChatTransport
    {
        public int Requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var first = Interlocked.Increment(ref Requests) == 1;
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123,
                first ? [new ToolCallContent("source-call", "read", JsonData.Parse("{\"path\":\"/authored\"}"))] : [new TextContent("unexpected provider continuation")],
                TokenUsage.Zero, first ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (first)
            {
                var call = (ToolCallContent)final.Content[0]; yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject });
                yield return new ToolCallDelta(0, call.Arguments.ToString()); yield return new ToolCallEnded(0, call);
            }
            else
            { yield return new TextStarted(0, new("")); yield return new TextDelta(0, "unexpected provider continuation"); yield return new TextEnded(0, "unexpected provider continuation"); }
            yield return new StreamDone(final.StopReason, final); await Task.CompletedTask;
        }
    }
    private sealed class Output(string? failure) : Stream
    {
        private readonly object _gate = new(); private readonly List<JsonData> _records = []; private JsonData? _written; private int _writes, _updates;
        public readonly TaskCompletionSource FirstUpdateEntered = Gate(), FirstUpdateRelease = Gate(), TwoUpdatesFlushed = Gate(), DisposeEntered = Gate(), DisposeRelease = Gate();
        public int MaximumWrites, Disposals;
        public JsonData[] Records() { lock (_gate) return _records.ToArray(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            MaximumWrites = Math.Max(MaximumWrites, Interlocked.Increment(ref _writes));
            try
            {
                var text = Encoding.UTF8.GetString(bytes.Span);
                Check(text.EndsWith('\n') && text.Count(character => character == '\n') == 1, "Concurrent source deliveries interleaved JSONL bytes.");
                var record = JsonData.Parse(text);
                if (Type(record) == "tool_execution_update" && !FirstUpdateEntered.Task.IsCompleted)
                {
                    FirstUpdateEntered.TrySetResult(); await FirstUpdateRelease.Task;
                    if (failure == "write") throw new IOException("private source write failure");
                }
                _written = record;
            }
            finally { Interlocked.Decrement(ref _writes); }
        }
        public override Task FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var record = _written!; _written = null;
            if (Type(record) == "tool_execution_update" && failure == "flush") throw new IOException("private source flush failure");
            lock (_gate) _records.Add(record);
            if (Type(record) == "tool_execution_update" && Interlocked.Increment(ref _updates) == 2) TwoUpdatesFlushed.TrySetResult();
            return Task.CompletedTask;
        }
        public override async ValueTask DisposeAsync()
        { Interlocked.Increment(ref Disposals); DisposeEntered.TrySetResult(); if (failure is not null) await DisposeRelease.Task; }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static bool Response(JsonData value, string id) => Type(value) == "response" && value.Value.TryGetProperty("id", out var field) && field.GetString() == id;
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stage(Task witness, Task completion, string stage)
    {
        await Task.WhenAny(witness, completion).WaitAsync(Deadline);
        if (!witness.IsCompleted) { await completion; throw new InvalidOperationException("RPC settled before actual stage: " + stage); }
        await witness;
    }
    private static async Task<T> Throws<T>(Func<Task> run) where T : Exception
    { try { await run().WaitAsync(Deadline); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
