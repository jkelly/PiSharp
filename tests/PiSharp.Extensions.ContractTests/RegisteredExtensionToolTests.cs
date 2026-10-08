using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RegisteredExtensionToolTests
{
    private static readonly ModelDescriptor Model = new("registered-hook-model", "openai-responses", "offline-authored");
    private static readonly JsonData Schema = JsonData.Parse("""{"type":"object","properties":{"value":{"type":"string","enum":["before","after"]}},"required":["value"],"additionalProperties":false}""");
    private static readonly JsonData Before = JsonData.Parse("{ \"value\":\"before\" }");
    private static readonly JsonData After = JsonData.Parse("{ \"value\":\"after\" }");
    private static readonly JsonData OriginalResult = JsonData.Parse("""{"content":[{"type":"text","text":"private output"}],"details":{"keep":1.0,"nil":null},"structuredContent":{"private":"stale"},"usage":{"counter":0.125,"nil":null},"opaqueResult":{"wide":9007199254740993},"isError":true}""");

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("registered hooks traverse real two-turn durable assistant and result barriers", DurableTwoTurns),
        ("registered hooks cannot bypass full schema, final policy or first block", AdmissionDenials),
        ("prepared replacements re-resolve the same adapter before exact final action policy", ReprepareAndPolicy),
        ("registered result projection retains raw status and nullish source after-hook behavior", ResultProjection),
        ("registered callback cancellation and removal join real durable session cleanup", CancellationAndRemoval),
        ("durable assistant and result checkpoint failure stop registered continuation", CheckpointFailures)
    ];

    private static async Task DurableTwoTurns()
    {
        using var files = new Files(); using var deadline = Deadline();
        await using var registry = new ExtensionRegistry();
        var suppliedAssistant = Message();
        var suppliedCall = suppliedAssistant.Content.OfType<ToolCallContent>().Single();
        var source = new Source([suppliedAssistant, Message(tool: false)]); var storage = new StorageFactory();
        PersistentAgentSession? session = null; PreparedToolAction? authorized = null;
        TranscriptEntry? committedBeforeHooks = null;
        var calls = 0; var effects = 0; var resultCalls = 0; var validates = new List<string>();
        await registry.ActivateAsync("owner", new Extension((entries, _) =>
        {
            entries.RegisterTool(Descriptor(async (arguments, _, token) =>
            {
                Equal("after", arguments.Value.GetProperty("value").GetString());
                Check(ReferenceEquals(arguments, authorized!.Arguments), "Execution did not receive policy's identical final arguments.");
                await Acknowledged(session!, token); effects++; return OriginalResult;
            }));
            entries.RegisterToolCallHandler(new("call", async (input, _, token) =>
            {
                await Acknowledged(session!, token);
                Equal("assistant", session!.Snapshot.Context.LlmMessages[^1].Role);
                Check(committedBeforeHooks is not null, "Registered call hook preceded the committed assistant observer.");
                Equal(committedBeforeHooks!.WireBody.ToString(), session.Snapshot.Context.LlmMessages[^1].WireBody.ToString());
                Equal("{\"value\":\"before\"}", CommittedArguments(committedBeforeHooks!));
                Equal("{ \"value\":\"before\" }", suppliedCall.Arguments.ToString());
                Equal("before", input.Arguments.Value.GetProperty("value").GetString());
                calls++; return new(After);
            }));
            entries.RegisterToolResultHandler(new("redact", (input, _, _) =>
            {
                Equal("after", input.Arguments.Value.GetProperty("value").GetString());
                Equal(committedBeforeHooks!.WireBody.ToString(), session!.Snapshot.Context.LlmMessages[^1].WireBody.ToString());
                Equal("{ \"value\":\"before\" }", suppliedCall.Arguments.ToString());
                Check(input.OutcomeIsError && input.Result.Value.GetProperty("isError").GetBoolean(), "Execution disposition was not seeded separately.");
                resultCalls++;
                return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("""{"content":[{"type":"text","text":"redacted\u0000\u6587"}],"details":null,"usage":null,"terminate":true,"opaquePatch":1.0}""")));
            }));
            entries.RegisterToolResultHandler(new("error-disposition", (_, _, _) =>
                ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("{\"isError\":false,\"structuredContent\":null}")))));
            return ValueTask.CompletedTask;
        }));
        var policy = new Policy((_, action, _) =>
        { authorized = action; return ValueTask.FromResult(new ToolActionAuthorization(true)); });
        var binding = new ExtensionAgentBinding(registry, policy, (tool, arguments, token) =>
        { validates.Add(arguments.Value.GetProperty("value").GetString()!); return Validate(tool, arguments, token); });
        session = await Create(files, binding, source, storage, deadline.Token);
        var assistantBarrier = storage.Storage!.Arm(skip: 2); StorageBarrier? resultBarrier = null;
        var deliveryEntered = Gate(); var deliveryRelease = Gate(); ToolOutcome? outcome = null;
        using var listener = session.Subscribe(new Sink(async (observation, token) =>
        {
            if (observation is AssistantMessageEnded { Message.StopReason: StopReason.ToolUse })
            {
                await Acknowledged(session, token);
                committedBeforeHooks = session.Snapshot.Context.LlmMessages[^1];
                Equal("assistant", committedBeforeHooks.Role);
                // PiWireJson materializes arguments as JsonNode and serializes compactly before
                // this committed observer or any registered callback. Source input stays exact.
                Equal("{\"value\":\"before\"}", CommittedArguments(committedBeforeHooks));
                Equal("{ \"value\":\"before\" }", suppliedCall.Arguments.ToString());
            }
            if (observation is ToolExecutionEnded end) outcome = end.Outcome;
            if (observation is ToolResultMessageEnded)
            { await Acknowledged(session, token); deliveryEntered.TrySetResult(); await deliveryRelease.Task; }
        }));
        source.BeforeSend = async (index, request, token) =>
        {
            await Acknowledged(session, token);
            if (index == 1)
            {
                Equal("toolResult", session.Snapshot.Context.LlmMessages[^1].Role);
                Equal(committedBeforeHooks!.WireBody.ToString(), ToolAssistant(request.Messages).WireBody.ToString());
                Equal("{ \"value\":\"before\" }", suppliedCall.Arguments.ToString());
            }
        };
        var run = session.PromptAsync([binding.CreateDeclarationMessage("owned declarations", 123), User()], deadline.Token);
        ImmutableArray<TranscriptEntry> retained = []; long acknowledged = 0;
        try
        {
            await Stage(assistantBarrier.Entered.Task, run, deadline.Token);
            Equal(0, calls); Equal(0, effects); Equal(0, policy.Calls); Equal(1, source.Requests.Count);
            Equal("user", session.Snapshot.Context.LlmMessages[^1].Role);
            resultBarrier = storage.Storage!.Arm(); assistantBarrier.Release.TrySetResult();
            await Stage(resultBarrier.Entered.Task, run, deadline.Token);
            Equal(1, calls); Equal(1, effects); Equal(1, resultCalls); Equal(1, policy.Calls);
            Equal("before,after", string.Join(',', validates)); Equal(1, source.Requests.Count);
            False(deliveryEntered.Task.IsCompleted); False(run.IsCompleted);
            resultBarrier.Release.TrySetResult();
            await Stage(deliveryEntered.Task, run, deadline.Token);
            Equal(1, source.Requests.Count); False(session.WaitForIdleAsync().IsCompleted);
            AssertProjected(outcome!, session.Snapshot.Context.LlmMessages[^1]);
            deliveryRelease.TrySetResult();
            var result = await run.WaitAsync(deadline.Token);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(2, result.Turns.Length);
            Equal(2, source.Requests.Count); Equal(2, source.Cleanups);
            var committedAssistant = ToolAssistant(session.Snapshot.Context.LlmMessages);
            Equal(committedBeforeHooks!.WireBody.ToString(), committedAssistant.WireBody.ToString());
            Equal("{\"value\":\"before\"}", CommittedArguments(committedAssistant));
            Check(ReferenceEquals(Before, suppliedCall.Arguments) && ReferenceEquals(suppliedCall, suppliedAssistant.Content[0]),
                "Registered replacement changed original authoritative source call identity.");
            Equal("{ \"value\":\"before\" }", suppliedCall.Arguments.ToString());
            Equal("{ \"value\":\"before\" }", Before.ToString());
            var canonical = session.Snapshot.Context.LlmMessages.Single(message => message.Role == "toolResult");
            Equal(canonical.WireBody.ToString(), source.Requests[1].Messages.Single(message => message.Role == "toolResult").WireBody.ToString());
            retained = session.Snapshot.Context.LlmMessages; acknowledged = session.Snapshot.Log.CommittedByteLength;
            await Acknowledged(session, deadline.Token);
        }
        finally
        {
            assistantBarrier.Release.TrySetResult(); resultBarrier?.Release.TrySetResult(); deliveryRelease.TrySetResult();
            session.Abort(); await Join(run); await session.DisposeAsync();
        }
        Equal(1, storage.Storage!.Disposals);
        var resumed = new Source([Message(tool: false)]);
        await using (var reopened = await PersistentAgentSession.OpenAsync(files.Path, Configuration(binding, resumed), () => 123,
            files.NextId, cancellationToken: deadline.Token))
        {
            Equal(acknowledged, reopened.Snapshot.Log.CommittedByteLength);
            Prefix(retained, reopened.Snapshot.Context.LlmMessages); Equal(1, effects);
            Equal(committedBeforeHooks!.WireBody.ToString(), ToolAssistant(reopened.Snapshot.Context.LlmMessages).WireBody.ToString());
            Equal("{\"value\":\"before\"}", CommittedArguments(ToolAssistant(reopened.Snapshot.Context.LlmMessages)));
            await reopened.PromptAsync(User("resume"), deadline.Token);
            Prefix(retained, resumed.Requests.Single().Messages); Equal(1, effects);
            Equal(committedBeforeHooks!.WireBody.ToString(), ToolAssistant(resumed.Requests.Single().Messages).WireBody.ToString());
            Check(ReferenceEquals(Before, suppliedCall.Arguments) && ReferenceEquals(suppliedCall, suppliedAssistant.Content[0]),
                "Reopen or continuation changed original source call identity.");
            Equal("{ \"value\":\"before\" }", suppliedCall.Arguments.ToString());
            Equal("{ \"value\":\"before\" }", Before.ToString());
        }
        await using var closed = await SessionLogStore.OpenAsync(files.Path, cancellationToken: deadline.Token);
        Equal(new FileInfo(files.Path).Length, closed.Snapshot.CommittedByteLength);
    }

    private static TranscriptEntry ToolAssistant(ImmutableArray<TranscriptEntry> history) => history.Single(message =>
        message.Role == "assistant" && PiWireJson.ReadMessage(message.WireBody.Value).Content.OfType<ToolCallContent>().Any());
    private static string CommittedArguments(TranscriptEntry assistant) => assistant.WireBody.Value.GetProperty("content")
        .EnumerateArray().Single(content => content.GetProperty("type").GetString() == "toolCall").GetProperty("arguments").GetRawText();

    private static void AssertProjected(ToolOutcome outcome, TranscriptEntry canonical)
    {
        False(outcome.IsError); Check(outcome.Result.IsError, "Raw source isError was incorrectly rewritten by disposition.");
        Equal("true", outcome.Result.Property("isError")!.ToString());
        Equal("redacted\0\u6587", outcome.Result.Content.Single().Text);
        Equal("{\"keep\":1.0,\"nil\":null}", outcome.Result.Details.ToString());
        Equal("{\"counter\":0.125,\"nil\":null}", outcome.Result.Usage!.ToString());
        Check(outcome.Result.StructuredContent is null, "Content replacement retained stale structured output.");
        Equal("9007199254740993", outcome.Result.Property("opaqueResult")!.Value.GetProperty("wide").GetRawText());
        False(outcome.Result.Terminate); False(outcome.Result.HasProperty("opaquePatch"));
        Equal("toolResult", canonical.Role); False(canonical.WireBody.Value.GetProperty("isError").GetBoolean());
        Equal("redacted\0\u6587", canonical.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        False(canonical.WireBody.Value.TryGetProperty("structuredContent", out _));
        False(canonical.WireBody.Value.TryGetProperty("opaqueResult", out _));
        Equal("1.0", canonical.WireBody.Value.GetProperty("details").GetProperty("keep").GetRawText());
    }

    private static async Task AdmissionDenials()
    {
        foreach (var mode in new[] { "initial-schema", "final-schema", "policy", "block", "throw" })
        {
            using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry();
            var hookCalls = 0; var tailCalls = 0; var effects = 0; var resultCalls = 0;
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(OriginalResult); }));
                entries.RegisterToolCallHandler(new("first", (_, _, _) =>
                {
                    hookCalls++;
                    if (mode == "throw") throw new InvalidOperationException("SECRET hook payload");
                    return ValueTask.FromResult<ExtensionToolCallPatch?>(mode == "block"
                        ? new(Decision: JsonData.Parse("{\"block\":true,\"terminate\":true,\"reason\":\"SECRET block payload\"}"))
                        : new(mode == "final-schema" ? JsonData.Parse("{\"value\":\"after\",\"extra\":null}") : After));
                }));
                entries.RegisterToolCallHandler(new("tail", (_, _, _) =>
                { tailCalls++; return ValueTask.FromResult<ExtensionToolCallPatch?>(null); }));
                entries.RegisterToolResultHandler(new("result", (_, _, _) =>
                { resultCalls++; return ValueTask.FromResult<ExtensionToolResultPatch?>(null); }));
                return ValueTask.CompletedTask;
            }));
            var policy = new Policy((_, action, _) =>
            { Equal("after", action.Arguments.Value.GetProperty("value").GetString()); return ValueTask.FromResult(new ToolActionAuthorization(false)); });
            var binding = new ExtensionAgentBinding(registry, policy, Validate);
            var source = new Source([Message(arguments: mode == "initial-schema" ? JsonData.EmptyObject : Before), Message(tool: false)]);
            await using var session = await Create(files, binding, source, null, deadline.Token);
            ToolOutcome? outcome = null;
            using var listener = session.Subscribe(new Sink((observation, _) =>
            { if (observation is ToolExecutionEnded end) outcome = end.Outcome; return ValueTask.CompletedTask; }));
            await session.PromptAsync([binding.CreateDeclarationMessage("declared tool", 123), User()], deadline.Token);
            Equal(0, effects); Equal(0, resultCalls); Check(outcome is not null && outcome.IsError, "Denied input became executable or successful.");
            Equal(mode is "initial-schema" or "final-schema" ? ToolFailureKind.InvalidArguments :
                mode == "throw" ? ToolFailureKind.HookError : ToolFailureKind.Blocked, outcome!.Result.Failure!.Kind);
            Equal(mode == "initial-schema" ? 0 : 1, hookCalls);
            Equal(mode == "policy" ? 1 : 0, policy.Calls);
            Equal(mode is "block" or "throw" or "initial-schema" ? 0 : 1, tailCalls);
            False(outcome.Result.Content.Single().Text.Contains("SECRET", StringComparison.Ordinal));
            Equal(mode == "block", outcome.Result.Terminate);
            Equal(mode == "block" ? 1 : 2, source.Requests.Count);
            await Acknowledged(session, deadline.Token);
        }
    }

    private static async Task ReprepareAndPolicy()
    {
        foreach (var allow in new[] { false, true })
        {
            var adapter = new PathAdapter(); PreparedToolAction? authorized = null; JsonData? afterArguments = null;
            var hook = new Hooks((_, action, _) =>
            { Equal("a", action.Target); return ValueTask.FromResult(new PreparedToolCallHookResult(JsonData.Parse("{\"path\":\"b\"}"))); },
                (_, action, result, error, _) =>
                { afterArguments = action.Arguments; False(error); return ValueTask.FromResult<JsonData?>(null); });
            var policy = new Policy((invocation, action, _) =>
            { Equal("a", invocation.Call.Arguments.Value.GetProperty("path").GetString()); authorized = action; return ValueTask.FromResult(new ToolActionAuthorization(allow)); });
            var invoker = ToolInvoker.WithPreparedHooks([adapter], policy, hook,
                transforms: [(_, action, _) => ValueTask.FromResult(action with { Target = "c", Arguments = JsonData.Parse("{\"path\":\"c\"}") })]);
            var original = new ToolCallContent("path-call", adapter.Name, JsonData.Parse("{\"path\":\"a\"}"));
            var assistant = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [original], TokenUsage.Zero, StopReason.ToolUse);
            var execution = await invoker.ExecuteFinalizedAsync(new(assistant, original, 0), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
            Equal("a,b", string.Join(',', adapter.Prepared)); Equal("a,c", string.Join(',', adapter.Validated));
            Equal("c", authorized!.Target); Equal("c", authorized.Arguments.Value.GetProperty("path").GetString());
            Equal(allow ? 1 : 0, adapter.Effects);
            if (allow)
            { Check(ReferenceEquals(authorized, adapter.Executed), "Execution did not retain the exact authorized action."); Check(ReferenceEquals(afterArguments, authorized.Arguments), "After hook did not receive effective final arguments."); False(execution.IsError); }
            else { Equal(ToolFailureKind.Blocked, execution.Result.Failure!.Kind); Check(afterArguments is null, "After hook ran on a pre-execution denial."); }
            Equal("a", original.Arguments.Value.GetProperty("path").GetString());
        }
    }

    private static async Task ResultProjection()
    {
        foreach (var mode in new[] { "none", "null", "bad-then-redact", "diagnostic-fault" })
        {
            await using var registry = new ExtensionRegistry(); var effects = 0; var diagnostics = new List<ExtensionEventDiagnostic>();
            var reported = Gate(); var reportRelease = Gate(); var tailCalls = 0;
            await registry.ActivateAsync("A", new Extension((entries, _) =>
            {
                entries.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(OriginalResult); }));
                entries.RegisterToolResultHandler(new("first", (_, _, _) =>
                {
                    if (mode == "none") return ValueTask.FromResult<ExtensionToolResultPatch?>(null);
                    if (mode == "null") return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("{\"content\":null,\"details\":null,\"structuredContent\":null,\"usage\":null,\"isError\":null}")));
                    if (mode == "diagnostic-fault") throw new InvalidOperationException("SECRET callback error");
                    return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":7}],\"details\":null}")));
                }));
                return ValueTask.CompletedTask;
            }));
            await registry.ActivateAsync("B", new Extension((entries, _) =>
            {
                entries.RegisterToolResultHandler(new("tail", (_, _, _) =>
                {
                    tailCalls++;
                    return ValueTask.FromResult<ExtensionToolResultPatch?>(mode == "bad-then-redact"
                        ? new(JsonData.Parse("{\"content\":[],\"isError\":false}")) : null);
                }));
                return ValueTask.CompletedTask;
            }));
            var options = new ExtensionAgentBindingOptions
            {
                ReportEventDiagnostic = async (diagnostic, _) =>
                {
                    diagnostics.Add(diagnostic);
                    if (mode == "diagnostic-fault")
                    { reported.TrySetResult(); await reportRelease.Task; throw new IOException("SECRET reporter error"); }
                }
            };
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate, options: options);
            var events = new List<AgentEvent>();
            var batch = new ToolBatchScheduler(binding.Tools).RunAsync(Message(), new Sink((observation, _) =>
            { events.Add(observation); return ValueTask.CompletedTask; }));
            try
            {
                if (mode == "diagnostic-fault")
                {
                    using var deadline = Deadline(); await Stage(reported.Task, batch, deadline.Token);
                    False(batch.IsCompleted); False(events.Any(observation => observation is ToolExecutionEnded));
                    Equal(1, effects); reportRelease.TrySetResult();
                }
                var outcome = (await batch).Outcomes.Single(); Equal(1, effects); Equal(1, tailCalls);
                Equal("true", outcome.Result.Property("isError")!.ToString());
                Equal("9007199254740993", outcome.Result.Property("opaqueResult")!.Value.GetProperty("wide").GetRawText());
                Equal("{\"keep\":1.0,\"nil\":null}", outcome.Result.Details.ToString());
                Equal("{\"counter\":0.125,\"nil\":null}", outcome.Result.Usage!.ToString());
                if (mode == "none") { True(outcome.IsError); Check(outcome.Result.StructuredContent is not null, "No-result hook cleared structured output."); Equal(0, diagnostics.Count); }
                if (mode == "null")
                {
                    True(outcome.IsError); Equal("private output", outcome.Result.Content.Single().Text);
                    Check(outcome.Result.StructuredContent is null, "Source session's normalized non-null content did not clear null structured output.");
                    Equal(0, diagnostics.Count);
                }
                if (mode == "bad-then-redact")
                {
                    False(outcome.IsError); Equal(0, outcome.Result.Content.Length); Check(outcome.Result.StructuredContent is null, "Redaction kept structured data.");
                    Equal(ExtensionEventFailure.InvalidResult, diagnostics.Single().Failure);
                }
                if (mode == "diagnostic-fault")
                {
                    True(outcome.IsError); Equal(ToolFailureKind.HookError, outcome.Result.Failure!.Kind);
                    Equal("private output", outcome.Result.Content[0].Text);
                    Check(outcome.Result.StructuredContent is not null, "Reporter failure erased completed output.");
                    False(outcome.Result.Content[^1].Text.Contains("SECRET", StringComparison.Ordinal));
                    Equal(ExtensionEventFailure.HandlerFailed, diagnostics.Single().Failure);
                }
                foreach (var diagnostic in diagnostics) False(diagnostic.ToString().Contains("SECRET", StringComparison.Ordinal));
            }
            finally { reportRelease.TrySetResult(); await Join(batch); }
        }
    }

    private static async Task CancellationAndRemoval()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry();
            var entered = Gate(); var release = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate();
            var effects = 0; var callbackCleanups = 0; var pluginCleanups = 0; IExtensionRegistration? toolHandle = null;
            var scope = await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                toolHandle = entries.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(OriginalResult); }));
                entries.RegisterToolCallHandler(new("held", async (_, _, token) =>
                {
                    entered.TrySetResult();
                    try
                    {
                        if (cancel) await Gate().Task.WaitAsync(token); else await release.Task;
                        return null;
                    }
                    finally { cleanupEntered.TrySetResult(); await cleanupRelease.Task; callbackCleanups++; }
                }));
                return ValueTask.CompletedTask;
            }, () => { pluginCleanups++; return ValueTask.CompletedTask; }));
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate);
            var source = new Source([Message(), Message(tool: false)]); var storage = new StorageFactory();
            var session = await Create(files, binding, source, storage, deadline.Token);
            var run = session.PromptAsync([binding.CreateDeclarationMessage("declared", 123), User()], deadline.Token);
            Task? close = null; Task? ownerClose = null;
            try
            {
                await Stage(entered.Task, run, deadline.Token); Equal(0, effects); Equal(1, source.Requests.Count);
                await Acknowledged(session, deadline.Token);
                if (cancel)
                {
                    session.Abort(); close = session.DisposeAsync().AsTask();
                    Check(ReferenceEquals(close, session.DisposeAsync().AsTask()), "Concurrent coordinator close did not share settlement.");
                    ownerClose = scope.DisposeAsync().AsTask();
                }
                else { toolHandle!.Dispose(); release.TrySetResult(); }
                await Stage(cleanupEntered.Task, run, deadline.Token);
                False(run.IsCompleted); Equal(0, callbackCleanups); Equal(0, effects);
                if (cancel) { False(close!.IsCompleted); False(ownerClose!.IsCompleted); Equal(0, pluginCleanups); Equal(0, storage.Storage!.Disposals); }
                cleanupRelease.TrySetResult();
                await Join(run);
                Equal(1, callbackCleanups); Equal(0, effects);
                if (cancel)
                { await close!; await ownerClose!; Equal(1, source.Requests.Count); Equal(1, pluginCleanups); }
                else
                {
                    Equal(2, source.Requests.Count);
                    var result = session.Snapshot.Agent.CompletedToolOutcomes.Single().Result;
                    Equal(ToolFailureKind.ExecutionError, result.Failure!.Kind);
                    await Acknowledged(session, deadline.Token);
                }
            }
            finally
            {
                release.TrySetResult(); cleanupRelease.TrySetResult(); session.Abort();
                await Join(run); await session.DisposeAsync(); if (ownerClose is not null) await Join(ownerClose);
            }
            Equal(1, storage.Storage!.Disposals); Equal(source.Requests.Count, source.Cleanups);
            await using var lease = await SessionLogStore.OpenAsync(files.Path, cancellationToken: deadline.Token);
            Equal(new FileInfo(files.Path).Length, lease.Snapshot.CommittedByteLength);
        }
    }

    private static async Task CheckpointFailures()
    {
        foreach (var failAssistant in new[] { true, false })
        {
            using var files = new Files(); using var deadline = Deadline(); await using var registry = new ExtensionRegistry();
            var hooks = 0; var effects = 0; var afters = 0;
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(OriginalResult); }));
                entries.RegisterToolCallHandler(new("call", (_, _, _) =>
                { hooks++; return ValueTask.FromResult<ExtensionToolCallPatch?>(new(After)); }));
                entries.RegisterToolResultHandler(new("result", (_, _, _) =>
                { afters++; return ValueTask.FromResult<ExtensionToolResultPatch?>(null); }));
                return ValueTask.CompletedTask;
            }));
            var policy = Allow(); var binding = new ExtensionAgentBinding(registry, policy, Validate);
            var source = new Source([Message(), Message(tool: false)]); var storage = new StorageFactory();
            var session = await Create(files, binding, source, storage, deadline.Token);
            var barrier = storage.Storage!.Arm(skip: failAssistant ? 2 : 3, fail: true);
            var run = session.PromptAsync([binding.CreateDeclarationMessage("declared", 123), User()], deadline.Token);
            try
            {
                await Stage(barrier.Entered.Task, run, deadline.Token); Equal(1, source.Requests.Count);
                Equal(failAssistant ? 0 : 1, effects); Equal(failAssistant ? 0 : 1, hooks); Equal(failAssistant ? 0 : 1, afters);
                Equal(failAssistant ? 0 : 1, policy.Calls); False(run.IsCompleted);
                barrier.Release.TrySetResult();
                await ThrowsAsync<PersistentAgentSessionException>(() => run);
                Check(session.Snapshot.Fault is { Failure: PersistentAgentSessionFailure.AppendFailed, MayHaveWritten: true }, "Uncertain checkpoint failure was not retained.");
                Equal(1, source.Requests.Count); Equal(1, source.Cleanups);
            }
            finally { barrier.Release.TrySetResult(); session.Abort(); await Join(run); await session.DisposeAsync(); }
            Equal(1, storage.Storage!.Disposals);
            // Read-only inspection preserves possibly flushed unacknowledged bytes; it grants no retry.
            var inspected = await new SessionLogReader().ReadFileAsync(files.Path, deadline.Token);
            Equal(SessionLogReadStatus.Complete, inspected.Status); Equal(new FileInfo(files.Path).Length, (long)inspected.OriginalBytes.Length);
        }
    }

    private static ExtensionToolDescriptor Descriptor(ExtensionToolCallback callback) => new("tool", "native_echo", "registered hooks", Schema, callback);
    private static ValueTask<bool> Validate(ExtensionToolRegistrationInfo tool, JsonData arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(tool.Parameters.ToString() == Schema.ToString() && arguments.Value.ValueKind == JsonValueKind.Object &&
            arguments.Value.EnumerateObject().Count() == 1 && arguments.Value.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() is "before" or "after");
    }
    private static Policy Allow() => new((_, _, _) => ValueTask.FromResult(new ToolActionAuthorization(true)));
    private static AgentConfiguration Configuration(ExtensionAgentBinding binding, Source source) => new(Model, source, binding.Tools);
    private static Task<PersistentAgentSession> Create(Files files, ExtensionAgentBinding binding, Source source,
        StorageFactory? storage, CancellationToken token) =>
        PersistentAgentSession.CreateAsync(files.Path, files.Header, Configuration(binding, source), () => 123, files.NextId,
            new(SessionLogStoreOptions: storage is null ? null : new(StorageFactory: storage)), token);
    private static AssistantMessage Message(bool tool = true, JsonData? arguments = null) => new(Model.Api, Model.Provider, Model.Id, 123,
        tool ? [new ToolCallContent("registered-call", "native_echo", arguments ?? Before)] : [new TextContent("done")], TokenUsage.Zero,
        tool ? StopReason.ToolUse : StopReason.Stop);
    private static TranscriptEntry User(string text = "run registered tool") =>
        new("user", JsonData.Parse("{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(text) + ",\"timestamp\":123}"));
    private static async Task Acknowledged(PersistentAgentSession session, CancellationToken token)
    {
        await using var file = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous);
        var observed = await new SessionLogReader().ReadAsync(file, leaveOpen: true, cancellationToken: token);
        Equal(SessionLogReadStatus.Complete, observed.Status);
        Equal((long)observed.OriginalBytes.Length, session.Snapshot.Log.CommittedByteLength);
        Equal(observed.ValidatedPrefix.Length - 1, session.Snapshot.Log.Entries.Length);
    }
    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-registered-tools-" + Guid.NewGuid().ToString("N"));
        private int sequence;
        public string Path => System.IO.Path.Combine(root, "session.jsonl");
        public SessionEntry Header => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "header", timestamp = "2026-10-01T00:00:00.000Z", cwd = root }));
        public string NextId() => "entry-" + Interlocked.Increment(ref sequence);
        public Files() => Directory.CreateDirectory(root);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(root);
            var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(resolved) != parent || !System.IO.Path.GetFileName(resolved).StartsWith("PiSharp-registered-tools-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned registered-tool test directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
    private sealed class Source(AssistantMessage[] messages) : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public Func<int, ChatRequest, CancellationToken, ValueTask>? BeforeSend;
        public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var index = Requests.Count;
            if (BeforeSend is not null) await BeforeSend(index, request, token);
            var message = messages[index]; Requests.Add(request);
            try
            {
                yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
                if (message.Content[0] is ToolCallContent call)
                { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
                else
                { yield return new TextStarted(0, new("")); yield return new TextEnded(0, ((TextContent)message.Content[0]).Text); }
                yield return new StreamDone(message.StopReason, message);
            }
            finally { Cleanups++; }
        }
    }
    private sealed class StorageBarrier(bool fail)
    {
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async ValueTask WaitAsync()
        { Entered.TrySetResult(); await Release.Task; if (fail) throw new IOException("SECRET checkpoint failure"); }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public Storage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private readonly object gate = new(); private StorageBarrier? barrier; private int skip;
        public int Disposals;
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk();
        public StorageBarrier Arm(int skip = 0, bool fail = false)
        { lock (gate) { this.skip = skip; return barrier = new(fail); } }
        public async ValueTask BeforeCheckpointAsync()
        {
            StorageBarrier? selected = null;
            lock (gate) if (barrier is not null && skip-- <= 0) { selected = barrier; barrier = null; }
            await inner.BeforeCheckpointAsync(); if (selected is not null) await selected.WaitAsync();
        }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    private sealed class PathAdapter : IPreparedToolAdapter
    {
        public string Name => "resolved-path";
        public readonly List<string> Prepared = [], Validated = [];
        public PreparedToolAction? Executed; public int Effects;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(ReferenceEquals(invocation.AssistantMessage.Content[invocation.SourceIndex], invocation.Call), "Reprepare clone broke finalized invocation identity.");
            var path = invocation.Call.Arguments.Value.GetProperty("path").GetString()!; Prepared.Add(path);
            return ValueTask.FromResult(new PreparedToolAction(Name, "write", PreparedToolActionKind.Path, path,
                invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Validated.Add(action.Target);
            return ValueTask.FromResult(action.ToolName == Name && action.Operation == "write" && action.Kind == PreparedToolActionKind.Path &&
                action.Arguments.Value.EnumerateObject().Count() == 1 && action.Arguments.Value.GetProperty("path").GetString() == action.Target);
        }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executed = action; Effects++; return ValueTask.FromResult(ToolResult.Success("effect")); }
    }
    private sealed class Hooks(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<PreparedToolCallHookResult>> before,
        Func<ToolInvocation, PreparedToolAction, ToolResult, bool, CancellationToken, ValueTask<JsonData?>> after) : IPreparedToolHooks
    {
        public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => before(invocation, action, token);
        public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction action, ToolResult result, bool error, CancellationToken token) => after(invocation, action, result, error, token);
    }
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>> authorize) : IToolActionPolicy
    {
        public int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return authorize(invocation, action, token); }
    }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token);
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(12));
    private static async Task Stage(Task witness, Task run, CancellationToken token)
    { await Task.WhenAny(witness, run).WaitAsync(token); if (!witness.IsCompleted) { await run; throw new InvalidOperationException("Execution settled before the required gate."); } await witness; }
    private static async Task Join(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(12)); } catch (Exception) when (task.IsCompleted) { } }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { True(actual.Length >= expected.Length); for (var index = 0; index < expected.Length; index++) Equal(expected[index].WireBody.ToString(), actual[index].WireBody.ToString()); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void True(bool value) => Check(value, "Registered tool assertion failed.");
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
