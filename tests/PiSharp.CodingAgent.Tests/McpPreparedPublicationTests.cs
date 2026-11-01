using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

internal static class McpPreparedPublicationTests
{
    internal const string Prefix = "mcp-prepared-publication.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual durable catalog and final policy reach admitted channel identity", Pipeline),
        (Prefix + "normal close stops actual native MCP call before session idle join", () => ActiveCallStop(false)),
        (Prefix + "owning shutdown stops actual native MCP call before session idle join", () => ActiveCallStop(true)),
        (Prefix + "schema and final denial prevent actual tools-call", Denial),
        (Prefix + "same-name refresh replaces metadata and preserves manual inactive selection", Refresh),
        (Prefix + "close withdraws actual declarations and repeats original task", Withdrawal),
        (Prefix + "close retains unrelated native adapter and hooks", UnrelatedWithdrawal),
        (Prefix + "held refresh racing close joins original and withdraws last committed subset", HeldRefreshClose),
        (Prefix + "cleanup and withdrawal failures retain originals and catalog state", FailedWithdrawal),
        (Prefix + "actual owning shutdown joins channel and withdraws before attachment retirement", OwningShutdown),
        (Prefix + "held reporter cancellation joins preparation without catalog commit", HeldPreparation),
        (Prefix + "failed in-memory publication preserves original exception without faulting session", FailedCommit),
        (Prefix + "connected failed publication keeps catalog and held close still withdraws it", ConnectedFailedCommit)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("MCP prepared publication contract failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected failure."); }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal bool Allowed = true; internal int Calls; internal ToolInvocation? Invocation; internal PreparedToolAction? Action;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; Invocation = invocation; Action = action; return ValueTask.FromResult(new ToolActionAuthorization(Allowed)); }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal int Version = 1, Calls, Closes; internal McpInvocationIdentity? Identity; internal JsonData? Arguments;
        internal Func<Task>? CloseAction; internal bool HoldCalls;
        internal readonly TaskCompletionSource CallEntered = Gate(), CallRelease = Gate(), StopEntered = Gate();
        private readonly Lazy<Task> close;
        internal Channel() { close = new(CloseCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication); }
        private async Task CloseCoreAsync() { Closes++; StopEntered.TrySetResult(); if (CloseAction is { } run) await run(); }
        public ValueTask StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (method == "initialize") return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"authored\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"));
            if (method == "tools/list") return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new { tools = new[] {
                new { name = "a", description = "version " + Version, inputSchema = new { type = "object", properties = new { value = new { type = "string" } }, required = new[] { "value" } } } } })));
            Check(method == "tools/call" && parameters!.Value.GetProperty("name").GetString() == "a");
            Calls++; Identity = options.InvocationIdentity; Arguments = JsonData.FromElement(parameters!.Value.GetProperty("arguments"));
            return new(CompleteCallAsync());
        }
        private async Task<JsonData> CompleteCallAsync()
        {
            CallEntered.TrySetResult();
            if (HoldCalls) { await StopEntered.Task; await CallRelease.Task; }
            return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"admitted result\"}]}");
        }
        public Task CloseAsync() => close.Value;
    }
    private sealed class Hooks : IPreparedToolHooks
    {
        public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
            => ValueTask.FromResult(new PreparedToolCallHookResult());
        public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction action, ToolResult result, bool isError, CancellationToken token)
            => ValueTask.FromResult<JsonData?>(null);
    }
    private sealed class UnrelatedAdapter : IInvocationPreparedToolAdapter
    {
        public string Name => "read";
        internal int Executions; internal ToolInvocation? Invocation; internal PreparedToolAction? Action;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) =>
            ValueTask.FromResult(new PreparedToolAction(Name, "invoke", PreparedToolActionKind.Extension,
                "authored-unrelated-native", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(action.ToolName == Name && action.Target == "authored-unrelated-native");
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) =>
            throw new InvalidOperationException("Unrelated native invocation identity was lost.");
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action,
            ToolProgressCallback progress, CancellationToken token)
        {
            Executions++; Invocation = invocation; Action = action;
            return ValueTask.FromResult(new ToolResult([new("same unrelated underlying adapter")], JsonData.EmptyObject));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly StartupSettingsTests.Fixture Files = new();
        internal readonly Policy Policy = new(); internal readonly Channel Channel = new();
        internal readonly ExtensionRegistry Extensions = new();
        internal OfflineSessionProfile Profile = null!; internal PersistentAgentSession Session = null!;
        internal ReplaceableAgentSession Owner = null!; internal McpPreparedServer Server = null!;
        internal NativeCallingTransport? CallTransport;
        internal string Path => System.IO.Path.Combine(Files.Root, "catalog.jsonl");
        internal bool ReporterEnabled; internal Func<CancellationToken, ValueTask>? Reporter;
        internal Exception? ExpectedCloseFailure;
        internal readonly Hooks NativeHooks = new();
        internal readonly UnrelatedAdapter Unrelated = new();
        internal static async Task<Fixture> Create(bool withUnrelated = false, bool nativeCall = false)
        {
            var f = new Fixture();
            try
            {
                f.Profile = await OfflineSessionProfile.CreateAsync(f.Files.Root, f.Path, null, [], [], [], default);
                IChatTransport transport = nativeCall ? f.CallTransport = new(f.Profile.SelectedModel) :
                    f.Profile.Registry.Resolve(f.Profile.SelectedModel, []).Configuration.Transport;
                ImmutableArray<SessionRegisteredTool> tools = withUnrelated ? [new(JsonData.Parse(
                    "{\"name\":\"read\",\"description\":\"unrelated native control\",\"parameters\":{\"type\":\"object\"}}"), f.Unrelated)] : [];
                var registry = new SessionRuntimeRegistry([new(f.Profile.SelectedModel, transport)], tools, f.Policy,
                    new() { BindNestedCallsToSessionOwner = true, PreparedToolHooks = withUnrelated ? f.NativeHooks : null, DrainLoadoutDiagnostics = token =>
                        f.ReporterEnabled && f.Reporter is { } reporter ? reporter(token) : ValueTask.CompletedTask });
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "catalog",
                    timestamp = "2026-10-05T00:00:00.000Z", cwd = f.Files.Root }));
                var ids = 0;
                f.Session = await PersistentAgentSession.CreateAsync(f.Path, header, registry, f.Profile.SelectedModel, () => 1, () => "catalog-" + ++ids);
                f.Owner = new(f.Session, (_, _) => Task.FromException<PersistentAgentSession>(new InvalidOperationException("No replacement open admitted.")));
                var scope = await f.Extensions.ActivateAsync("mcp-owner", new EmptyExtension());
                var config = McpConfigurationReader.Validate("demo", JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}").Value).Config!;
                f.Server = new(new("demo", config, "authored", McpConfigurationScope.Extension), f.Extensions, scope, f.Owner, f.Policy,
                    (_, arguments, _) => ValueTask.FromResult(arguments.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String),
                    (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(f.Channel), new(f.Owner.Current.Generation, "0.99.1"),
                    (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            ReporterEnabled = false;
            try { if (Server is not null) await Server.CloseAsync(); }
            catch (Exception error) when (ReferenceEquals(error, ExpectedCloseFailure)) { }
            finally
            {
                try { if (Owner is not null) await Owner.DisposeAsync(); else if (Session is not null) await Session.DisposeAsync(); }
                catch (Exception error) when (ExpectedCloseFailure is not null &&
                    OriginalFailures(error).SequenceEqual(OriginalFailures(ExpectedCloseFailure), ReferenceEqualityComparer.Instance)) { }
                finally { try { await Extensions.DisposeAsync(); } finally { try { if (Profile is not null) await Profile.DisposeAsync(); } finally { Files.Dispose(); } } }
            }
        }
    }
    private static IEnumerable<Exception> OriginalFailures(Exception error)
    {
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.SelectMany(OriginalFailures);
        return [error];
    }
    private static ImmutableArray<SessionEntry> InitialEntries(Fixture f)
    {
        var initial = f.Session.Snapshot.Log.Entries;
        Check(initial.Select(entry => entry.Kind).SequenceEqual([SessionEntryKind.ModelChange, SessionEntryKind.ThinkingLevelChange]));
        return initial;
    }
    // Source _refreshToolRegistry: a catalog publication or withdrawal applies in memory and writes nothing; the next request
    // records the loadout's difference from the declared tools (agent-loop declareToolChanges).
    private static void Unwritten(Fixture f, ImmutableArray<SessionEntry> written) => Check(f.Session.Snapshot.Log.Entries.SequenceEqual(written));
    private static JsonElement Registered(Fixture f, string name) =>
        f.Session.CaptureToolCatalogRegistry().RegisteredTools.Single(tool => tool.Adapter.Name == name).Declaration.Value;
    private static void Declaration(JsonElement declaration, string name, int version)
    {
        Check(declaration.GetProperty("name").GetString() == name && declaration.GetProperty("description").GetString()!.Contains("version " + version, StringComparison.Ordinal));
        var schema = declaration.GetProperty("parameters");
        Check(schema.EnumerateObject().Count() == 3 && schema.GetProperty("type").GetString() == "object" && schema.GetProperty("properties").EnumerateObject().Count() == 1 &&
            schema.GetProperty("properties").GetProperty("value").EnumerateObject().Count() == 1 &&
            schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString() == "string" &&
            schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["value"]));
    }
    /// <summary>The tool records written after <paramref name="initial"/>, each before the user message of its request.</summary>
    private static JsonElement[] ToolRecords(Fixture f, ImmutableArray<SessionEntry> initial)
    {
        var messages = f.Session.Snapshot.Log.Entries.Skip(initial.Length).Select(entry => entry.WireBody.Value)
            .Where(body => body.TryGetProperty("message", out _)).Select(body => body.GetProperty("message")).ToArray();
        var records = new List<JsonElement>();
        for (var index = 0; index < messages.Length; index++)
        {
            if (!messages[index].TryGetProperty("toolsAdded", out _) && !messages[index].TryGetProperty("toolsRemoved", out _)) continue;
            Check(messages[index].GetProperty("role").GetString() == "system" && index + 1 < messages.Length &&
                messages[index + 1].GetProperty("role").GetString() == "user");
            records.Add(messages[index]);
        }
        return records.ToArray();
    }
    private static TranscriptEntry User(string text) =>
        new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 1 })));
    private static ToolInvocation Call(string name, JsonData arguments)
    {
        var call = new ToolCallContent("native-call", name, arguments);
        return new(new AssistantMessage("openai-responses", "fixture", "model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    // The agent's configured tools: the in-memory loadout, which the transcript declares only at the next request.
    private static IToolExecutor Invoker(Fixture f) => f.Session.Snapshot.Agent.Tools.Single().Executor;
    /// <summary>Calls <see cref="ToolName"/> in its first response when one is set, and answers with text otherwise.</summary>
    private sealed class NativeCallingTransport(ModelDescriptor model) : IChatTransport
    {
        internal string ToolName = ""; private int requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var useTool = Interlocked.Increment(ref requests) == 1 && ToolName.Length != 0;
            AssistantContent content = useTool ? new ToolCallContent("native-call", ToolName, JsonData.Parse("{\"value\":\"original\"}")) : new TextContent("done");
            var final = new AssistantMessage(model.Api, model.Provider, model.Id, 1, [content], TokenUsage.Zero, useTool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (content is ToolCallContent call) { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            await Task.CompletedTask;
            yield return new StreamDone(final.StopReason, final);
        }
    }
    private static async Task ActiveCallStop(bool shutdown)
    {
        await using var f = await Fixture.Create(nativeCall: true); await f.Server.ConnectAsync();
        f.CallTransport!.ToolName = f.Extensions.CaptureSnapshot().Tools.Single().Name;
        f.Channel.HoldCalls = true; var closeRelease = Gate();
        f.Channel.CloseAction = () => closeRelease.Task;
        var attachment = f.Owner.Current;
        var run = f.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"invoke\",\"timestamp\":1}")));
        Task? closing = null;
        try
        {
            await f.Channel.CallEntered.Task;
            closing = shutdown ? f.Owner.StopAdmissionAndJoinAsync() : f.Server.CloseAsync();
            await f.Channel.StopEntered.Task;
            Check(!run.IsCompleted && !closing.IsCompleted && f.Channel.Closes == 1 && !attachment.LifetimeToken.IsCancellationRequested);
            if (!shutdown) Check(ReferenceEquals(closing, f.Server.CloseAsync()));
        }
        finally
        {
            f.Channel.CallRelease.TrySetResult(); closeRelease.TrySetResult();
            try { await run; } catch (OperationCanceledException) when (shutdown) { }
            if (closing is not null) await closing;
        }
        Check(f.Channel.Calls == 1 && f.Policy.Calls == 1 && f.Channel.Closes == 1 && f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty);
        if (shutdown) Check(attachment.LifetimeToken.IsCancellationRequested);
        else Check(!attachment.LifetimeToken.IsCancellationRequested && f.Session.GetActiveTools().IsEmpty);
    }
    private static async Task Pipeline()
    {
        await using var f = await Fixture.Create(nativeCall: true); var initial = InitialEntries(f); await f.Server.ConnectAsync();
        var name = f.Extensions.CaptureSnapshot().Tools.Single().Name;
        Check(f.Session.GetActiveTools().SequenceEqual([name])); Unwritten(f, initial); Declaration(Registered(f, name), name, 1);
        var invocation = Call(name, JsonData.Parse("{\"value\":\"original\"}"));
        var result = await Invoker(f).ExecuteAsync(invocation, default);
        Check(!result.IsError && f.Policy.Calls == 1 && ReferenceEquals(f.Policy.Invocation!.Call, invocation.Call) &&
            ReferenceEquals(f.Policy.Invocation.AssistantMessage, invocation.AssistantMessage));
        Check(f.Channel.Calls == 1 && f.Channel.Identity is { OwnerId: "mcp-owner", SessionGeneration: 1, ToolCallId: "native-call" });
        Check(f.Channel.Arguments!.ToString() == f.Policy.Action!.Arguments.ToString());
        // The next request records the connected catalog before its user message.
        await f.Session.PromptAsync(User("declare"));
        var records = ToolRecords(f, initial);
        Check(records.Length == 1 && !records[0].TryGetProperty("toolsRemoved", out _) && records[0].GetProperty("toolsAdded").GetArrayLength() == 1);
        Declaration(records[0].GetProperty("toolsAdded")[0], name, 1);
    }
    private static async Task Denial()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var name = f.Extensions.CaptureSnapshot().Tools.Single().Name;
        var malformed = await Invoker(f).ExecuteAsync(Call(name, JsonData.EmptyObject), default);
        Check(malformed.IsError && f.Policy.Calls == 0 && f.Channel.Calls == 0);
        f.Policy.Allowed = false;
        var denied = await Invoker(f).ExecuteAsync(Call(name, JsonData.Parse("{\"value\":\"valid\"}")), default);
        Check(denied.Failure?.Kind == ToolFailureKind.Blocked && f.Policy.Calls == 1 && f.Channel.Calls == 0);
    }
    private static async Task Refresh()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var name = f.Extensions.CaptureSnapshot().Tools.Single().Name;
        f.Channel.Version = 2; await f.Server.RefreshToolsAsync();
        Declaration(Registered(f, name), name, 2);
        Check(f.Session.GetActiveTools().SequenceEqual([name]));
        await f.Session.SetActiveToolsAsync([]); f.Channel.Version = 3; await f.Server.RefreshToolsAsync();
        Check(f.Session.GetActiveTools().IsEmpty && f.Session.CaptureToolCatalogRegistry().RegisteredTools.Single().Declaration.Value.GetProperty("description").GetString()!.Contains("version 3", StringComparison.Ordinal));
    }
    private static async Task Withdrawal()
    {
        await using var f = await Fixture.Create(nativeCall: true); var initial = InitialEntries(f); await f.Server.ConnectAsync();
        var name = f.Extensions.CaptureSnapshot().Tools.Single().Name; Unwritten(f, initial); Declaration(Registered(f, name), name, 1);
        await f.Session.PromptAsync(User("declare")); var declared = f.Session.Snapshot.Log.Entries;
        var original = f.Server.CloseAsync(); Check(ReferenceEquals(original, f.Server.CloseAsync())); await original;
        Check(f.Channel.Closes == 1 && f.Extensions.CaptureSnapshot().Tools.IsEmpty && f.Session.GetActiveTools().IsEmpty &&
            f.Session.CaptureToolCatalogRegistry().RegisteredTools.IsEmpty && f.Server.CatalogWithdrawalAcknowledged);
        // The withdrawal applies in memory too; the next request records the removal.
        Unwritten(f, declared); await f.Session.PromptAsync(User("withdrawn"));
        var records = ToolRecords(f, initial);
        Check(records.Length == 2 && records[0].GetProperty("toolsAdded").GetArrayLength() == 1 && !records[1].TryGetProperty("toolsAdded", out _) &&
            records[1].GetProperty("toolsRemoved").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual([name]));
        Declaration(records[0].GetProperty("toolsAdded")[0], name, 1);
    }
    private static async Task UnrelatedWithdrawal()
    {
        await using var f = await Fixture.Create(withUnrelated: true);
        await f.Session.SetActiveToolsAsync(["read"]); await f.Server.ConnectAsync();
        var retainedDeclaration = f.Session.CaptureToolCatalogRegistry().RegisteredTools.Single(tool => tool.Adapter.Name == "read").Declaration.ToString();
        await f.Server.CloseAsync(); var current = f.Session.CaptureToolCatalogRegistry();
        Check(f.Server.CatalogWithdrawalAcknowledged && f.Session.GetActiveTools().SequenceEqual(["read"]) &&
            current.RegisteredTools.Single().Declaration.ToString() == retainedDeclaration && ReferenceEquals(current.PreparedToolHooks, f.NativeHooks));
        Check(f.Extensions.CaptureSnapshot().Tools.IsEmpty);
        var invocation = Call("read", JsonData.Parse("{\"control\":\"unrelated\"}"));
        var result = await Invoker(f).ExecuteAsync(invocation, default);
        Check(!result.IsError && f.Unrelated.Executions == 1 && f.Channel.Calls == 0 && f.Policy.Calls == 1 &&
            ReferenceEquals(f.Unrelated.Invocation!.Call, invocation.Call) && ReferenceEquals(f.Unrelated.Action, f.Policy.Action));
    }
    private static async Task HeldRefreshClose()
    {
        await using var f = await Fixture.Create(); var initial = InitialEntries(f); await f.Server.ConnectAsync();
        var name = f.Extensions.CaptureSnapshot().Tools.Single().Name; Unwritten(f, initial); Declaration(Registered(f, name), name, 1);
        var committed = Registered(f, name).GetRawText();
        var entered = Gate(); var release = Gate(); var canceled = Gate(); var visits = 0;
        CancellationToken reporterToken = default;
        CancellationTokenRegistration cancellationObservation = default;
        f.Reporter = async token =>
        {
            if (Interlocked.Increment(ref visits) == 1)
            {
                reporterToken = token;
                cancellationObservation = token.UnsafeRegister(_ => canceled.TrySetResult(), null);
                entered.TrySetResult(); await release.Task;
            }
        };
        f.ReporterEnabled = true; f.Channel.Version = 2;
        var refresh = f.Server.RefreshToolsAsync(); Task? closing = null, repeatedClose = null;
        var failures = new List<Exception>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            closing = f.Server.CloseAsync();
            // The returned owner receipt may precede its scheduled physical stop callback.
            // Keep the reporter held until physical stop and cancellation propagation
            // to this actual linked publication token have both been observed.
            await f.Channel.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            repeatedClose = f.Server.CloseAsync();
            Check(reporterToken.IsCancellationRequested && !release.Task.IsCompleted && !refresh.IsCompleted && !closing.IsCompleted &&
                ReferenceEquals(closing, repeatedClose) && f.Channel.Closes == 1);
            Unwritten(f, initial); Check(Registered(f, name).GetRawText() == committed);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            release.TrySetResult();
            try { closing ??= f.Server.CloseAsync(); }
            catch (Exception error) { failures.Add(error); }
            // Preserve expected refresh failure, but independently join close even if
            // that assertion fails. Unexpected task faults retain their full inventory.
            try { await Failure(refresh); }
            catch (Exception error) { failures.Add(refresh.IsFaulted ? refresh.Exception! : error); }
            if (closing is not null)
                try { await closing; }
                catch (Exception error) { f.ExpectedCloseFailure ??= error; failures.Add(closing.IsFaulted ? closing.Exception! : error); }
            if (repeatedClose is not null && !ReferenceEquals(repeatedClose, closing))
                try { await repeatedClose; }
                catch (Exception error) { failures.Add(repeatedClose.IsFaulted ? repeatedClose.Exception! : error); }
            try { cancellationObservation.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
        // The held version 2 never reached the session; close withdrew the last committed catalog (version 1), in memory.
        Check(f.Channel.Closes == 1 && f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty &&
            f.Session.GetActiveTools().IsEmpty && f.Session.CaptureToolCatalogRegistry().RegisteredTools.IsEmpty);
        Unwritten(f, initial);
    }
    private static async Task FailedWithdrawal()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var read = new IOException("original channel close"); var write = new IOException("original withdrawal reporter");
        var channelClose = Task.FromException(read);
        f.Channel.CloseAction = () => channelClose;
        f.Reporter = _ => ValueTask.FromException(write); f.ReporterEnabled = true;
        var before = f.Session.Snapshot.Log.Entries.Length; var closing = f.Server.CloseAsync();
        var error = await Failure(closing); f.ExpectedCloseFailure = error;
        Check(closing.IsFaulted && !closing.IsCanceled && error.GetType() == typeof(AggregateException));
        var aggregate = (AggregateException)error;
        Check(aggregate.InnerExceptions.Count == 2 &&
            aggregate.InnerExceptions[0].GetType() == typeof(ReplaceableAgentSession.OwnedResourceStopException) &&
            ReferenceEquals(aggregate.InnerExceptions[1], write));
        var stop = (ReplaceableAgentSession.OwnedResourceStopException)aggregate.InnerExceptions[0];
        // McpPreparedServer registers runtime.CloseAsync as its physical stop body.
        // The wrapper captures that runtime original, distinct from this channel task.
        var runtimeClose = stop.OriginalTask;
        Check(runtimeClose is { IsFaulted: true, IsCanceled: false } && !ReferenceEquals(runtimeClose, channelClose) &&
            ReferenceEquals(stop.ObservedException, read));
        var actualFaults = runtimeClose!.Exception!;
        var recordedFaults = stop.OriginalTaskException;
        Check(actualFaults.InnerExceptions.Count == 1 && ReferenceEquals(actualFaults.InnerExceptions[0], read) &&
            recordedFaults is { InnerExceptions.Count: 1 } && ReferenceEquals(recordedFaults.InnerExceptions[0], read) &&
            stop.InnerExceptions.Count == 1 && ReferenceEquals(stop.InnerExceptions[0], read));
        Check(ReferenceEquals(await Failure(runtimeClose), read) && ReferenceEquals(await Failure(channelClose), read));
        var repeated = await Failure(f.Server.CloseAsync());
        Check(ReferenceEquals(error, repeated));
        var repeatedStop = (ReplaceableAgentSession.OwnedResourceStopException)((AggregateException)repeated).InnerExceptions[0];
        Check(ReferenceEquals(stop, repeatedStop) && ReferenceEquals(runtimeClose, repeatedStop.OriginalTask) &&
            ReferenceEquals(recordedFaults, repeatedStop.OriginalTaskException) && ReferenceEquals(read, repeatedStop.ObservedException));
        Check(ReferenceEquals(closing, f.Server.CloseAsync()) && ReferenceEquals(await Failure(f.Server.CloseAsync()), error) &&
            !f.Server.CatalogWithdrawalAcknowledged && f.Channel.Closes == 1 && f.Extensions.CaptureSnapshot().Tools.Length == 1 &&
            f.Session.GetActiveTools().Length == 1 && f.Session.Snapshot.Log.Entries.Length == before);
    }
    private static async Task OwningShutdown()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var attachment = f.Owner.Current; var entered = Gate(); var release = Gate();
        f.Channel.CloseAction = async () => { Check(!attachment.LifetimeToken.IsCancellationRequested); entered.TrySetResult(); await release.Task; };
        var shutdown = f.Owner.StopAdmissionAndJoinAsync();
        try { await entered.Task; Check(!shutdown.IsCompleted && !attachment.LifetimeToken.IsCancellationRequested && !f.Server.CatalogWithdrawalAcknowledged); }
        finally { release.TrySetResult(); await shutdown; }
        Check(f.Channel.Closes == 1 && f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty && attachment.LifetimeToken.IsCancellationRequested);
        var closing = f.Server.CloseAsync(); Check(ReferenceEquals(closing, f.Server.CloseAsync())); await closing;
    }
    private static async Task HeldPreparation()
    {
        await using var f = await Fixture.Create(); var entered = Gate(); var release = Gate(); var canceled = Gate();
        using var stop = new CancellationTokenSource(); var originalError = new IOException("original reporter");
        f.Reporter = async token => { using var registration = token.UnsafeRegister(_ => canceled.TrySetResult(), null);
            entered.TrySetResult(); await release.Task; throw originalError; }; f.ReporterEnabled = true;
        var before = f.Session.Snapshot.Log.Entries.Length;
        var expected = f.Session.CaptureToolCatalogRegistry(); var replacement = expected.WithToolCatalog([], null); var commits = 0;
        var operation = f.Owner.PublishToolCatalogAsync(f.Owner.Current, expected, replacement, [], () => commits++, stop.Token);
        try { await entered.Task; stop.Cancel(); await canceled.Task; Check(!operation.IsCompleted && commits == 0); }
        finally { release.TrySetResult(); await Failure(operation); f.ReporterEnabled = false; }
        Check(ReferenceEquals(await Failure(operation), originalError) && commits == 0 && f.Session.Snapshot.Log.Entries.Length == before && f.Session.Snapshot.Fault is null);
    }
    // A catalog publication writes nothing before its registry commit (the next request records the loadout), so a failing commit
    // leaves the session as it was: unfaulted, unwritten and still publishable, with the original exception.
    private static async Task FailedCommit()
    {
        await using var f = await Fixture.Create(); var expected = f.Session.CaptureToolCatalogRegistry(); var before = f.Session.Snapshot;
        var original = new IOException("original publication commit");
        var operation = f.Owner.PublishToolCatalogAsync(f.Owner.Current, expected, expected.WithToolCatalog([], null), [], () => throw original);
        Check(ReferenceEquals(await Failure(operation), original) && operation.IsFaulted && ReferenceEquals(await Failure(operation), original));
        Check(f.Session.Snapshot.Fault is null && ReferenceEquals(f.Session.CaptureToolCatalogRegistry(), expected) &&
            f.Session.Snapshot.Log.Entries.SequenceEqual(before.Log.Entries) && f.Session.Snapshot.Agent.Tools.SequenceEqual(before.Agent.Tools));
        var commits = 0;
        await f.Owner.PublishToolCatalogAsync(f.Owner.Current, expected, expected.WithToolCatalog([], null), [], () => commits++);
        Check(commits == 1 && f.Session.Snapshot.Fault is null && f.Session.Snapshot.Log.Entries.SequenceEqual(before.Log.Entries));
        // No catalog was ever connected: close stops no channel and needs no withdrawal.
        await f.Server.CloseAsync();
        Check(f.Channel.Closes == 0 && f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty);
    }
    private static async Task ConnectedFailedCommit()
    {
        await using var f = await Fixture.Create(); await f.Server.ConnectAsync();
        var expected = f.Session.CaptureToolCatalogRegistry(); var before = f.Session.Snapshot;
        var declarations = f.Extensions.CaptureSnapshot().Tools; var name = declarations.Single().Name;
        var original = new IOException("original connected publication commit");
        var operation = f.Owner.PublishToolCatalogAsync(f.Owner.Current, expected, expected.WithToolCatalog([], null), [], () => throw original);
        Check(ReferenceEquals(await Failure(operation), original) && operation.IsFaulted && f.Session.Snapshot.Fault is null &&
            ReferenceEquals(f.Session.CaptureToolCatalogRegistry(), expected) && f.Session.GetActiveTools().SequenceEqual([name]) &&
            f.Session.Snapshot.Log.Entries.SequenceEqual(before.Log.Entries) && f.Session.Snapshot.Agent.Tools.SequenceEqual(before.Agent.Tools));
        // The server's own catalog is intact, so its close joins the held physical stop and then withdraws it.
        var release = Gate(); f.Channel.CloseAction = () => release.Task;
        var closing = f.Server.CloseAsync();
        try
        {
            await f.Channel.StopEntered.Task;
            Check(!closing.IsCompleted && ReferenceEquals(closing, f.Server.CloseAsync()) && f.Channel.Closes == 1 &&
                !f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.SequenceEqual(declarations));
        }
        finally { release.TrySetResult(); }
        await closing;
        Check(f.Channel.Closes == 1 && f.Server.CatalogWithdrawalAcknowledged && f.Extensions.CaptureSnapshot().Tools.IsEmpty &&
            f.Session.GetActiveTools().IsEmpty && f.Session.Snapshot.Fault is null && f.Session.Snapshot.Log.Entries.SequenceEqual(before.Log.Entries));
    }
}
