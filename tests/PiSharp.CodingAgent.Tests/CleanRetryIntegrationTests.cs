using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class CleanRetryIntegrationTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("clean retry integration startup layers migrate before merge and retain agent cap", Layers),
        ("clean retry integration acknowledged tool-use resets budget without replay or raw history loss", AssistantReset),
        ("clean retry integration Bash originals and checkpoint precede retry decision", BashBoundary),
        ("clean retry integration admitted settings original gates replacement and acknowledged preference retention", SettingsRetention),
        ("clean retry integration settings failure rolls back live policy and close joins original", SettingsFailureAndClose),
        ("clean retry integration unsafe settings cancellation joins original and retains body cleanup references", SettingsCancellation),
        ("clean retry integration RPC strict flags state cancellation and original terminal output join", RpcControls),
        ("clean retry integration context overflow is excluded and transient retry precedes threshold", CompactionOrder)
    ];
    private static readonly ModelDescriptor Model = new("clean-retry-integration", "openai-responses", "authored");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"clean-retry-integration","api":"openai-responses","provider":"authored","name":"Authored retry","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":128000,"maxTokens":8192,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"go\",\"timestamp\":1000}"));
    private static AssistantMessage Message(StopReason reason, long timestamp, string? error = null, bool tool = false) =>
        new(Model.Api, Model.Provider, Model.Id, timestamp, tool ? [new ToolCallContent("original-tool", "once", JsonData.EmptyObject)] : [new TextContent("response")],
            TokenUsage.Zero, reason, error is null ? null : JsonFields.Empty.Set("errorMessage", JsonData.FromElement(JsonSerializer.SerializeToElement(error))));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Clean retry integration assertion failed."); }
    private static void Reject(Action enter) { var rejected = false; try { enter(); } catch (InvalidOperationException) { rejected = true; } Check(rejected); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original failure."); }
    private static IEnumerable<Exception> Leaves(Exception error) => error switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => aggregate.InnerExceptions.SelectMany(Leaves),
        PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed,
            InnerException: AggregateException { InnerExceptions.Count: > 0 } cleanup } => cleanup.InnerExceptions.SelectMany(Leaves),
        _ => [error]
    };
    private static async Task Join(Exception? body, Func<Exception, bool>? expected, params Task[] originals)
    {
        var failures = new List<Exception>(); if (body is not null) failures.Add(body);
        foreach (var original in originals.Distinct()) try { await original; }
            catch (Exception error) { if (!(expected?.Invoke(error) ?? false) && !failures.Any(value => ReferenceEquals(value, error))) failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    private static async Task Layers()
    {
        var user = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "retry-user.json"));
        var project = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "retry-project.json"));
        var files = new SettingsFiles(new Dictionary<string, string>
        {
            [user] = "{\"retry\":{\"maxRetries\":7,\"maxDelayMs\":11,\"maxAgentDelayMs\":29,\"provider\":{\"unknown\":true}}}",
            [project] = "{\"retry\":{\"maxDelayMs\":19,\"baseDelayMs\":3}}"
        });
        var snapshot = await StartupSettings.LoadAsync(new(user, project, JsonData.Parse("{\"retry\":{\"enabled\":false}}")), files);
        Check(snapshot.RetryPolicy == new AgentRetryPolicy(false, 7, 3, 29));
        var retry = snapshot.Values.Value.GetProperty("retry");
        Check(!retry.TryGetProperty("maxDelayMs", out _) && retry.GetProperty("provider").GetProperty("maxRetryDelayMs").GetInt32() == 19 && retry.GetProperty("provider").GetProperty("unknown").GetBoolean());
        Check(files.Reads == 2 && snapshot.Diagnostics.IsEmpty);
    }
    private static async Task AssistantReset()
    {
        var transport = new Transport(Message(StopReason.Error, 1, "503"), Message(StopReason.ToolUse, 2, tool: true), Message(StopReason.Error, 3, "503"), Message(StopReason.Stop, 4));
        var tool = new OnceTool(); await using var session = await Open(transport, [new("once", tool)]);
        session.ConfigureAutomaticRetry(new(maxRetries: 1, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        var observations = new List<SessionOperationEvent>(); var firstReset = false;
        using var events = session.SubscribeOperationEvents(new Sink(value =>
        {
            observations.Add(value);
            if (value is SessionAutoRetryEnded { Success: true } && transport.Calls == 2)
            {
                firstReset = true; Check(tool.Calls == 0);
                Check(session.Snapshot.Log.Entries.Last().WireBody.Value.GetProperty("message").GetProperty("stopReason").GetString() == "toolUse");
            }
            return ValueTask.CompletedTask;
        }));
        var result = await session.PromptAsync([Input()]);
        Check(result.Reason == AgentLoopStopReason.Completed && transport.Calls == 4 && tool.Calls == 1 && firstReset);
        Check(observations.OfType<SessionAutoRetryStarted>().Select(value => value.Attempt).SequenceEqual([1, 1]));
        Check(observations.OfType<SessionAutoRetryEnded>().Count(value => value.Success) == 2 && observations.Last() is SessionOperationSettled);
        var raw = session.Snapshot.Log.Entries.Where(entry => entry.Type == "message").Select(entry => entry.WireBody.Value.GetProperty("message")).ToArray();
        Check(raw.Count(message => message.TryGetProperty("stopReason", out var stop) && stop.GetString() == "error") == 2);
        Check(session.Snapshot.Context.LlmMessages.All(message => !message.WireBody.Value.TryGetProperty("stopReason", out var stop) || stop.GetString() != "error"));
        Check(transport.Requests.Skip(1).All(request => request.Messages.All(message => !message.WireBody.Value.TryGetProperty("stopReason", out var stop) || stop.GetString() != "error")));
        Check(transport.Requests[^1].Messages.Count(message => message.Role == "toolResult") == 1);
    }
    private static async Task BashBoundary()
    {
        var transport = new Transport(Message(StopReason.Error, 1, "503"), Message(StopReason.Stop, 2)) { HoldFirst = true };
        await using var session = await Open(transport);
        var executor = new BashExecutor(); var progress = Gate(); var started = Gate();
        session.ConfigureUserBashExecution(executor, _ => progress.Task);
        session.ConfigureAutomaticRetry(new(baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        using var events = session.SubscribeOperationEvents(new Sink(value =>
        {
            if (value is SessionAutoRetryStarted)
            { Check(!session.HasPendingUserBashMessages && session.Snapshot.Log.Entries.Last().WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "bashExecution"); started.TrySetResult(); }
            return ValueTask.CompletedTask;
        }));
        var run = session.PromptAsync([Input()]); Task<UserBashResult>? bash = null; Task? accepted = null; Exception? body = null;
        try
        {
            await transport.FirstEntered.Task; bash = session.ExecuteUserBashAsync("original"); accepted = executor.Progress!("held original delta");
            executor.Result.TrySetResult(new("actual output", 0, false, false)); transport.FirstRelease.TrySetResult();
            var field = typeof(PersistentAgentSession).GetField("_automaticBashBoundaryOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
            while (field.GetValue(session) is null) { if (run.IsCompleted) { await run; throw new Exception("Retry bypassed Bash boundary."); } await Task.Yield(); }
            Check(!started.Task.IsCompleted && !run.IsCompleted && !bash.IsCompleted); Reject(() => session.ExecuteUserBashAsync("new"));
            progress.TrySetResult(); await accepted; await bash; await started.Task; await run;
            Check(transport.Requests[1].Messages.Any(message => message.WireBody.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
                content.EnumerateArray().Any(block => block.TryGetProperty("text", out var text) && text.GetString()!.Contains("actual output", StringComparison.Ordinal))));
        }
        catch (Exception error) { body = error; }
        finally
        {
            progress.TrySetResult(); executor.Result.TrySetResult(new("", null, true, false)); transport.FirstRelease.TrySetResult();
            await Join(body, null, run, bash ?? Task.CompletedTask, accepted ?? Task.CompletedTask, session.WaitForIdleAsync());
        }
    }
    private static async Task SettingsRetention()
    {
        var original = Gate(); var entered = Gate(); var replacementFactory = Gate();
        var initial = await Open(new Transport());
        initial.ConfigureAutomaticRetry(new(maxRetries: 9), (enabled, _) => { Check(!enabled); entered.TrySetResult(); Reject(() => initial.WaitForIdleAsync()); return original.Task; });
        PersistentAgentSession? target = null;
        await using var owner = new ReplaceableAgentSession(initial, async (request, _) =>
        { replacementFactory.TrySetResult(); var opened = await Open(new Transport(), path: request.Path); target = opened; return opened; });
        var write = owner.SetAutoRetryEnabledAsync(owner.Current, false);
        Task<AgentSessionReplacement?>? change = null; Exception? body = null;
        owner.AfterReplacement = replacement =>
        { Check(!replacement.Current.Session.AutoRetryEnabled && replacement.Current.Session.RetryPolicy.MaxRetries == 9); return ValueTask.CompletedTask; };
        try
        {
            await entered.Task; change = owner.SwitchAsync(owner.Current, new(Path.Combine(Path.GetTempPath(), "retry-retained-target.jsonl")));
            Check(!write.IsCompleted && initial.AutoRetryEnabled && !replacementFactory.Task.IsCompleted && !change.IsCompleted);
            original.TrySetResult(); await write; await change;
            Check(target is not null && !target.AutoRetryEnabled && owner.Current.Generation == 2);
        }
        catch (Exception error) { body = error; }
        finally { original.TrySetResult(); await Join(body, null, write, change ?? Task.CompletedTask); }
    }
    private static async Task SettingsFailureAndClose()
    {
        var marker = new IOException("original preference checkpoint"); var original = Gate();
        var session = await Open(new Transport()); var calls = 0;
        session.ConfigureAutomaticRetry(new(), (_, _) => ++calls == 1 ? Task.FromException(marker) : original.Task);
        var rejected = session.SetAutoRetryEnabledAsync(false);
        Check(ReferenceEquals(await Failure(rejected), marker) && session.AutoRetryEnabled);
        using var cancellation = new CancellationTokenSource();
        var pending = session.SetAutoRetryEnabledAsync(false, cancellation.Token); cancellation.Cancel();
        var close = session.StopAdmissionAndJoinAsync(); Exception? body = null;
        try { Check(!pending.IsCompleted && !close.IsCompleted && session.AutoRetryEnabled); original.TrySetResult(); await pending; await close; Check(!session.AutoRetryEnabled); }
        catch (Exception error) { body = error; }
        finally { original.TrySetResult(); await Join(body, null, pending, close, session.DisposeAsync().AsTask()); }
    }
    private static async Task RpcControls()
    {
        var sleep = Gate(); var entered = Gate(); var canceled = Gate(); CancellationToken delayToken = default;
        var session = await Open(new Transport(Message(StopReason.Error, 1, "503")));
        session.ConfigureAutomaticRetry(new(), originalDelay: (_, token) => { delayToken = token; entered.TrySetResult(); return sleep.Task; });
        await using var output = new HeldOutput();
        await using var writer = new JsonlWriter(output);
        await using var rpc = new RpcSessionDispatcher(session, writer, () => 1000, [new(Model, ModelWire)]);
        Task? abort = null; Task? idle = null; Exception? body = null;
        try
        {
            await rpc.SubmitAsync(JsonData.Parse("{\"type\":\"set_auto_retry\",\"id\":\"bad\",\"enabled\":\"false\"}"));
            Check(session.AutoRetryEnabled && Records(output).Any(value => value.Value.GetProperty("type").GetString() == "response" && value.Value.GetProperty("id").GetString() == "bad" && !value.Value.GetProperty("success").GetBoolean()));
            await rpc.SubmitAsync(JsonData.Parse("{\"type\":\"prompt\",\"id\":\"go\",\"message\":\"go\"}")); await entered.Task;
            await rpc.SubmitAsync(JsonData.Parse("{\"type\":\"get_state\",\"id\":\"state\"}"));
            var state = Records(output).Single(value => value.Value.TryGetProperty("id", out var id) && id.GetString() == "state").Value.GetProperty("data");
            Check(state.GetProperty("autoRetryEnabled").GetBoolean() && state.GetProperty("isRetrying").GetBoolean());
            await rpc.SubmitAsync(JsonData.Parse("{\"type\":\"set_auto_retry\",\"id\":\"off\",\"enabled\":false}"));
            Check(!session.AutoRetryEnabled && session.IsRetrying && !delayToken.IsCancellationRequested);
            using var registration = delayToken.UnsafeRegister(_ => { Reject(() => session.WaitForIdleAsync()); canceled.TrySetResult(); }, null);
            output.HoldEnd = true; abort = rpc.SubmitAsync(JsonData.Parse("{\"type\":\"abort_retry\",\"id\":\"abort\"}")); await canceled.Task;
            idle = rpc.WaitForIdleAsync(); Check(!abort.IsCompleted && !idle.IsCompleted);
            sleep.TrySetResult(); await output.EndEntered.Task; Check(!abort.IsCompleted && !idle.IsCompleted && session.IsRetrying);
            output.EndRelease.TrySetResult(); await abort; await idle;
            var records = Records(output); var endIndex = Array.FindIndex(records, value => value.Value.GetProperty("type").GetString() == "auto_retry_end");
            var ackIndex = Array.FindIndex(records, value => value.Value.TryGetProperty("id", out var id) && id.GetString() == "abort");
            var settledIndex = Array.FindIndex(records, value => value.Value.GetProperty("type").GetString() == "pisharp_operation_settled");
            Check(endIndex >= 0 && endIndex < ackIndex && endIndex < settledIndex && !session.IsRetrying);
        }
        catch (Exception error) { body = error; }
        finally { sleep.TrySetResult(); output.EndRelease.TrySetResult(); await Join(body, null, abort ?? Task.CompletedTask, idle ?? Task.CompletedTask, rpc.WaitForIdleAsync()); }
    }
    private static async Task SettingsCancellation()
    {
        var session = await Open(new Transport()); var original = Gate(); var entered = Gate();
        var bodyFault = new IOException("original settings body"); var cancellationFault = new IOException("original settings cancellation");
        CancellationTokenRegistration registration = default;
        ReplaceableAgentSession? owner = null;
        session.ConfigureAutomaticRetry(new(), (_, token) =>
        {
            registration = token.UnsafeRegister(_ =>
            {
                Check(session.IsRetryOwnedCallback); Reject(() => session.WaitForIdleAsync());
                Reject(() => owner!.DisposeAsync().AsTask().GetAwaiter().GetResult()); throw cancellationFault;
            }, null);
            entered.TrySetResult(); return original.Task;
        });
        owner = new(session, (_, _) => Task.FromException<PersistentAgentSession>(new Exception("No replacement admitted.")));
        using var cancellation = new CancellationTokenSource();
        var write = owner.SetAutoRetryEnabledAsync(owner.Current, false, cancellation.Token); Exception? testFailure = null; Task? close = null;
        bool Expected(Exception error)
        {
            var leaves = Leaves(error).ToArray();
            return leaves.Length > 0 && leaves.All(value => ReferenceEquals(value, bodyFault) || ReferenceEquals(value, cancellationFault)) &&
                leaves.Count(value => ReferenceEquals(value, bodyFault)) <= 2 && leaves.Count(value => ReferenceEquals(value, cancellationFault)) <= 2;
        }
        try
        {
            await entered.Task; cancellation.Cancel(); Check(!write.IsCompleted && session.AutoRetryEnabled);
            original.TrySetException(bodyFault); var failure = await Failure(write); var leaves = Leaves(failure).ToArray();
            Check(leaves.Length == 2 && leaves.Any(value => ReferenceEquals(value, bodyFault)) && leaves.Any(value => ReferenceEquals(value, cancellationFault)) && session.AutoRetryEnabled);
            close = owner.DisposeAsync().AsTask(); var closeError = await Failure(close); var closeLeaves = Leaves(closeError).ToArray();
            Check(close.IsFaulted && Expected(closeError) && closeLeaves.Length == 4 &&
                closeLeaves.Count(value => ReferenceEquals(value, bodyFault)) == 2 && closeLeaves.Count(value => ReferenceEquals(value, cancellationFault)) == 2);
            var sessionClose = session.DisposeAsync().AsTask(); var cleanupError = await Failure(sessionClose);
            Check(sessionClose.IsFaulted && cleanupError is PersistentAgentSessionException
                { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: AggregateException { InnerExceptions.Count: > 0 } } cleanup &&
                ReferenceEquals(session.Snapshot.Fault, cleanup.Fault) && session.Snapshot.IsDisposed);
            Check(Leaves(cleanupError).SequenceEqual(new Exception[] { bodyFault, cancellationFault }, ReferenceEqualityComparer.Instance));
            Check(ReferenceEquals(close, owner.DisposeAsync().AsTask()) && ReferenceEquals(await Failure(close), closeError) &&
                ReferenceEquals(sessionClose, session.DisposeAsync().AsTask()) && ReferenceEquals(await Failure(sessionClose), cleanupError));
        }
        catch (Exception error) { testFailure = error; }
        finally
        {
            original.TrySetException(bodyFault); registration.Dispose();
            await Join(testFailure, Expected, original.Task, write, close ?? owner.DisposeAsync().AsTask());
        }
    }
    private static async Task CompactionOrder()
    {
        var overflow = new Transport(Message(StopReason.Error, 1, "input exceeds the context window 503"));
        await using (var excluded = await Open(overflow))
        {
            var delays = 0; excluded.ConfigureAutomaticRetry(new(), contextWindow: 128000, originalDelay: (_, _) => { delays++; return Task.CompletedTask; });
            await excluded.PromptAsync([Input()]); Check(delays == 0 && overflow.Calls == 1);
        }
        var transient = new Transport(Message(StopReason.Error, 1, "503"), Message(StopReason.Stop, 2));
        await using var session = await Open(transient); var summary = new Summary(); var sleep = Gate(); var entered = Gate();
        session.ConfigureAutomaticCompaction(summary, new(ReserveTokens: 1, KeepRecentTokens: 1), contextWindow: 2);
        session.ConfigureAutomaticRetry(new(), contextWindow: 128000, originalDelay: (_, _) => { entered.TrySetResult(); return sleep.Task; });
        var run = session.PromptAsync([Input()]); Exception? body = null;
        try { await entered.Task; Check(summary.Calls == 0 && transient.Calls == 1); sleep.TrySetResult(); await run; }
        catch (Exception error) { body = error; }
        finally { sleep.TrySetResult(); await Join(body, null, run, session.WaitForIdleAsync()); }
    }
    private static JsonData[] Records(MemoryStream output) => Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
    private static Task<PersistentAgentSession> Open(Transport transport, ImmutableArray<ToolDefinition> tools = default, string? path = null)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath()); var ids = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "retry-fixture", timestamp = "2026-10-05T00:00:00.000Z", cwd }));
        return PersistentAgentSession.CreateAsync(path ?? Path.Combine(cwd, "clean-retry-memory.jsonl"), header, new(Model, transport, tools.IsDefault ? [] : tools),
            () => 1000, () => "retry-" + Interlocked.Increment(ref ids), new(SessionLogStoreOptions: new(StorageFactory: new StorageFactory())));
    }
    private sealed class Sink(Func<SessionOperationEvent, ValueTask> callback) : ISessionOperationEventSink
    { public ValueTask EmitAsync(SessionOperationEvent value, CancellationToken token) => callback(value); }
    private sealed class SettingsFiles(Dictionary<string, string> values) : IStartupSettingsFileSystem
    { internal int Reads; public ValueTask<string?> ReadTextAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult<string?>(values[path]); } }
    private sealed class OnceTool : IToolExecutor
    { internal int Calls; public ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, CancellationToken token) { Calls++; return ValueTask.FromResult(new ToolResult([new("original completed effect")], JsonData.EmptyObject)); } }
    private sealed class Summary : ISessionSummaryGenerator
    { internal int Calls; public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default) { Calls++; return ValueTask.FromResult(new SessionGeneratedSummary("summary", TokenUsage.Zero)); } }
    private sealed class BashExecutor : IUserBashExecutor
    {
        internal UserBashProgress? Progress; internal readonly TaskCompletionSource<UserBashResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token) { Progress = progress; return Result.Task; }
    }
    private sealed class Transport(params AssistantMessage[] frames) : IChatTransport
    {
        internal int Calls; internal bool HoldFirst; internal readonly TaskCompletionSource FirstEntered = Gate(), FirstRelease = Gate(); internal readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request); var index = Calls++; if (index >= frames.Length) throw new InvalidOperationException("No provider call beyond authored original frames is admitted.");
            var final = frames[index]; yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (index == 0 && HoldFirst) { FirstEntered.TrySetResult(); await FirstRelease.Task; }
            if (final.Content[0] is ToolCallContent tool) { yield return new ToolCallStarted(0, tool); yield return new ToolCallEnded(0, tool); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "response"); }
            yield return final.StopReason == StopReason.Error ? new StreamError(StopReason.Error, final) : new StreamDone(final.StopReason, final);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    { public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(new Storage()); }
    private sealed class Storage : ISessionLogStorage
    {
        private readonly MemoryStream bytes = new(); public Stream ReadStream => bytes; public SessionLogStorageDurability Durability => SessionLogStorageDurability.VolatileMemory; public long Length => bytes.Length;
        public void PositionForAppend(long length) { if (bytes.Length != length) throw new IOException("Original length changed."); bytes.Position = length; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> value) => bytes.WriteAsync(value); public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public void FlushToDisk() { } public ValueTask BeforeCheckpointAsync() => ValueTask.CompletedTask; public ValueTask DisposeAsync() => bytes.DisposeAsync();
    }
    private sealed class HeldOutput : MemoryStream
    {
        internal bool HoldEnd; internal readonly TaskCompletionSource EndEntered = Gate(), EndRelease = Gate();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            var line = Encoding.UTF8.GetString(buffer.Span);
            if (HoldEnd && line.Contains("\"type\":\"auto_retry_end\"", StringComparison.Ordinal)) { EndEntered.TrySetResult(); await EndRelease.Task; }
            await base.WriteAsync(buffer, token);
        }
    }
}
