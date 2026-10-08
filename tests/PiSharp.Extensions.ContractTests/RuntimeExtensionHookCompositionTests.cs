using System.Collections.Immutable;
using System.Globalization;
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

internal static class RuntimeExtensionHookCompositionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("composed-hook-model", "openai-responses", "authored-offline");
    private static readonly JsonData Schema = JsonData.Parse("""{"type":"object","properties":{"path":{"type":"string","enum":["before.txt","after.txt"]}},"required":["path"],"additionalProperties":false}""");
    private static readonly JsonData Before = JsonData.Parse("{ \"path\":\"before.txt\" }");
    private static readonly JsonData After = JsonData.Parse("{\"path\":\"after.txt\"}");
    private static readonly JsonData Original = JsonData.Parse("""{"content":[{"type":"text","text":"private output"}],"details":{"number":1.0,"nil":null},"structuredContent":{"private":"stale"},"usage":{"raw":0.125},"opaqueResult":{"wide":9007199254740993},"isError":false}""");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("runtime combined native-extension invoker revalidates final actions and durably projects registered results", DurableMixedTools),
        ("runtime registered hooks cannot bypass initial-final schema first-block errors or exact final policy", Admission),
        ("runtime composed hook snapshot retains current dispatch and requires refreshed binding after removal", SnapshotAndRemoval),
        ("runtime composed hook cancellation joins actual owner callback and durable session cleanup", Cancellation),
        ("runtime prepared hook options preserve legacy path and validate limits before effects", LegacyAndLimits)
    ];

    private static async Task DurableMixedTools()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry();
        var trace = new List<string>(); var native = new NativeAdapter(files.Root, trace); var extensionEffects = 0;
        PersistentAgentSession? session = null; PreparedToolAction? extensionAction = null;
        await registry.ActivateAsync("tool-owner", new Plugin((entries, _) =>
        {
            entries.RegisterTool(Tool(async (arguments, _, token) =>
            {
                Equal("after.txt", PathArgument(arguments)); Check(ReferenceEquals(extensionAction!.Arguments, arguments), "Extension execution lost exact policy-owned arguments.");
                await Acknowledged(session!, token); trace.Add("execute:extension:after.txt");
                await File.WriteAllTextAsync(files.ExtensionEffect, "one extension effect", token); extensionEffects++; return Original;
            })); return ValueTask.CompletedTask;
        }));
        var calls = 0; var results = 0;
        await registry.ActivateAsync("hook-owner", new Plugin((entries, _) =>
        {
            entries.RegisterToolCallHandler(new("replace", async (input, _, token) =>
            {
                await Acknowledged(session!, token); calls++; Equal("before.txt", PathArgument(input.Arguments));
                var history = session!.Snapshot.Context.LlmMessages;
                var committedAssistant = history.Single(message => message.Role == "assistant" && message.WireBody.Value.GetProperty("stopReason").GetString() == "toolUse");
                var committedCall = committedAssistant.WireBody.Value.GetProperty("content").EnumerateArray()
                    .Single(content => content.GetProperty("type").GetString() == "toolCall" && content.GetProperty("id").GetString() == input.ToolCallId);
                Equal(input.ToolName, committedCall.GetProperty("name").GetString());
                Equal("before.txt", committedCall.GetProperty("arguments").GetProperty("path").GetString());
                if (input.ToolName == "native_write") Equal("assistant", history[^1].Role);
                else
                {
                    Equal("toolResult", history[^1].Role); Equal("native_write", history[^1].WireBody.Value.GetProperty("toolName").GetString());
                    Equal("redacted native_write\0\u03c0", history[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
                    Check(!history[^1].WireBody.Value.GetProperty("isError").GetBoolean(), "Sequential native result was not committed successfully before the extension hook.");
                }
                trace.Add("before:" + Short(input.ToolName) + ":before.txt"); return new(After);
            }));
            entries.RegisterToolResultHandler(new("redact", (input, _, _) =>
            {
                results++; Equal("after.txt", PathArgument(input.Arguments)); Check(!input.OutcomeIsError, "Successful execution entered result hooks as an error.");
                trace.Add("result:" + Short(input.ToolName) + ":after.txt");
                return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse(JsonSerializer.Serialize(new
                { content = new[] { new { type = "text", text = "redacted " + input.ToolName + "\0\u03c0" } }, details = new { kept = (string?)null, number = 1.0 }, usage = new { hook = 0.5, nil = (string?)null } }))));
            })); return ValueTask.CompletedTask;
        }));
        var standalonePolicy = new Policy(static (_, _, _) => throw new InvalidOperationException("Already-authorized binding.Tools must not be wrapped."));
        var binding = new ExtensionAgentBinding(registry, standalonePolicy, (tool, arguments, token) =>
        { trace.Add("validate:extension:" + PathArgumentOrInvalid(arguments)); return Validate(tool, arguments, token); });
        Check(!binding.Adapters.IsDefault && binding.Adapters.Length == 1 && binding.PreparedHooks is not null, "Captured composition seam was not exposed.");
        Check(ReferenceEquals(binding.PreparedHooks, binding.PreparedHooks) && ReferenceEquals(binding.Adapters[0], binding.Adapters[0]), "Binding recreated captured execution objects.");
        var target = ExtensionTarget(binding.Registrations.Single());
        var policy = new Policy((invocation, action, _) =>
        {
            Equal("before.txt", PathArgument(invocation.Call.Arguments)); Equal("after.txt", PathArgument(action.Arguments));
            trace.Add("policy:" + Short(action.ToolName) + ":after.txt");
            if (action.Kind == PreparedToolActionKind.Path) { Equal(files.NativeAfter, action.Target); native.Authorized = action; }
            else { Equal(PreparedToolActionKind.Extension, action.Kind); Equal(target, action.Target); extensionAction = action; }
            return ValueTask.FromResult(new ToolActionAuthorization(true));
        });
        var source = new Source([Assistant("native_write", "extension_write"), TextAssistant()]); var audit = new StorageFactory();
        var runtime = Runtime(binding, native, policy, source); var declaration = Declarations(binding);
        var selected = runtime.Resolve(Model, [declaration]);
        Check(ReferenceEquals(selected.Configuration.Tools[0].Executor, selected.Configuration.Tools[1].Executor), "Mixed tools did not share one invoker.");
        Equal(0, standalonePolicy.Calls); await Seed(files, declaration);
        session = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, runtime, () => 123, files.NextId,
            new(SessionLogStoreOptions: new(StorageFactory: audit)), fallbackModel: Model);
        var ends = new List<ToolOutcome>(); var progress = new List<string>(); var delivery = Gate(); var deliveryRelease = Gate();
        using var observer = session.Subscribe(new Sink(async (observation, token) =>
        {
            if (observation is ToolExecutionUpdated update) progress.Add(update.PartialResult.Content.Single().Text);
            if (observation is ToolExecutionEnded end) ends.Add(end.Outcome);
            if (observation is ToolResultMessageEnded && ends.Count == 2)
            { await Acknowledged(session, token); delivery.TrySetResult(); await deliveryRelease.Task; }
        }));
        source.BeforeSend = async (index, _, token) => { if (index == 1) await Acknowledged(session, token); };
        var run = session.PromptAsync(User()); ImmutableArray<TranscriptEntry> retained = []; long acknowledged = 0;
        try
        {
            await Stage(delivery.Task, run, "committed mixed tool result observer");
            Check(calls == 2 && results == 2 && policy.Calls == 2,
                "Composed pipeline counts: before=" + calls + "; result=" + results + "; policy=" + policy.Calls + "; outcomes=" +
                string.Join(',', ends.Select(end => end.Invocation.Call.Name + ":" + (end.Result.Failure?.Kind.ToString() ?? "none") + ":" + end.IsError)) + "; trace=" + string.Join(',', trace));
            Equal(1, source.Requests.Count); Equal(2, calls); Equal(2, results); Equal(2, policy.Calls); Equal(0, standalonePolicy.Calls);
            Equal(1, native.Effects); Equal(1, extensionEffects); Equal("native progress", progress.Single());
            Check(ReferenceEquals(native.Authorized, native.Executed), "Native execution did not receive the policy's identical final action.");
            Check(!File.Exists(files.NativeBefore), "Argument replacement executed the original native target.");
            Equal("one native effect", await File.ReadAllTextAsync(files.NativeAfter)); Equal("one extension effect", await File.ReadAllTextAsync(files.ExtensionEffect));
            Equal("prepare:native:before.txt,validate:native:before.txt,before:native:before.txt,prepare:native:after.txt,validate:native:after.txt,policy:native:after.txt,execute:native:after.txt,result:native:after.txt,validate:extension:before.txt,before:extension:before.txt,validate:extension:after.txt,policy:extension:after.txt,execute:extension:after.txt,result:extension:after.txt", string.Join(',', trace));
            foreach (var outcome in ends)
            {
                Check(!outcome.IsError && outcome.Result.StructuredContent is null, "Result redaction retained stale programmatic output or changed disposition.");
                Equal("9007199254740993", outcome.Result.Property("opaqueResult")!.Value.GetProperty("wide").GetRawText());
                Equal("0.5", outcome.Result.Usage!.Value.GetProperty("hook").GetRawText());
            }
            Check(!run.IsCompleted, "Run skipped actual result-observer ownership."); deliveryRelease.TrySetResult();
            Equal(AgentLoopStopReason.Completed, (await run.WaitAsync(Deadline)).Reason); Equal(2, source.Requests.Count);
            var canonical = session.Snapshot.Context.LlmMessages.Where(message => message.Role == "toolResult").ToArray(); Equal(2, canonical.Length);
            foreach (var message in canonical)
            {
                Equal("redacted " + message.WireBody.Value.GetProperty("toolName").GetString() + "\0\u03c0", message.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
                Check(message.WireBody.Value.GetProperty("details").GetProperty("kept").ValueKind == JsonValueKind.Null, "Explicit result detail null was lost.");
                Equal("0.5", message.WireBody.Value.GetProperty("usage").GetProperty("hook").GetRawText());
                Check(!message.WireBody.Value.TryGetProperty("opaqueResult", out _) && !message.WireBody.Value.TryGetProperty("structuredContent", out _), "Model history acquired programmatic result fields.");
                Equal(message.WireBody.ToString(), source.Requests[1].Messages.Single(value => value.Role == "toolResult" && value.WireBody.Value.GetProperty("toolName").GetString() == message.WireBody.Value.GetProperty("toolName").GetString()).WireBody.ToString());
            }
            var assistant = session.Snapshot.Context.LlmMessages.Single(message => message.Role == "assistant" && message.WireBody.Value.GetProperty("stopReason").GetString() == "toolUse");
            foreach (var call in assistant.WireBody.Value.GetProperty("content").EnumerateArray()) Equal("before.txt", call.GetProperty("arguments").GetProperty("path").GetString());
            Equal("{ \"path\":\"before.txt\" }", Before.ToString()); retained = session.Snapshot.Context.LlmMessages; acknowledged = session.Snapshot.Log.CommittedByteLength;
            await Acknowledged(session, CancellationToken.None);
        }
        finally { deliveryRelease.TrySetResult(); session.Abort(); await Join(run); await session.DisposeAsync(); }
        Equal(1, audit.Storage!.Disposals);
        var resumed = new Source([TextAssistant()]); var reopenedRuntime = Runtime(binding, native, policy, resumed);
        await using (var reopened = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, reopenedRuntime, () => 123, files.NextId, fallbackModel: Model))
        {
            Equal(acknowledged, reopened.Snapshot.Log.CommittedByteLength); Prefix(retained, reopened.Snapshot.Context.LlmMessages);
            await reopened.PromptAsync(User("resume")); Prefix(retained, resumed.Requests.Single().Messages);
            Equal(1, native.Effects); Equal(1, extensionEffects); Equal(2, policy.Calls);
        }
        await using var closed = await SessionLogStore.OpenAsync(files.Path); Equal(new FileInfo(files.Path).Length, closed.Snapshot.CommittedByteLength);
    }

    private static async Task Admission()
    {
        foreach (var mode in new[] { "initial-schema", "final-schema", "block", "throw", "policy" })
        {
            using var files = new Files(); await using var registry = new ExtensionRegistry(); var hookCalls = 0; var tails = 0; var resultCalls = 0; var extensionEffects = 0;
            await registry.ActivateAsync("owner", new Plugin((entries, _) =>
            {
                entries.RegisterTool(Tool((_, _, _) => { extensionEffects++; return ValueTask.FromResult(Original); }));
                entries.RegisterToolCallHandler(new("first", (_, _, _) =>
                {
                    hookCalls++; if (mode == "throw") throw new InvalidOperationException("private handler data");
                    return ValueTask.FromResult<ExtensionToolCallPatch?>(mode == "block" ? new(Decision: JsonData.Parse("{\"block\":true,\"terminate\":true}")) :
                        new(mode == "final-schema" ? JsonData.Parse("{\"path\":\"after.txt\",\"unknown\":null}") : After));
                }));
                entries.RegisterToolCallHandler(new("tail", (_, _, _) => { tails++; return ValueTask.FromResult<ExtensionToolCallPatch?>(null); }));
                entries.RegisterToolResultHandler(new("result", (_, _, _) => { resultCalls++; return ValueTask.FromResult<ExtensionToolResultPatch?>(null); })); return ValueTask.CompletedTask;
            }));
            var unused = new Policy(static (_, _, _) => throw new InvalidOperationException("binding executor bypass"));
            var binding = new ExtensionAgentBinding(registry, unused, Validate); var native = new NativeAdapter(files.Root, []);
            var policy = new Policy((_, action, _) => { Equal("after.txt", PathArgument(action.Arguments)); return ValueTask.FromResult(new ToolActionAuthorization(false)); });
            var selection = Runtime(binding, native, policy, new Source([])).Resolve(Model, [Declarations(binding)]);
            foreach (var name in new[] { "native_write", "extension_write" })
            {
                var result = await Execute(selection, name, mode == "initial-schema" ? JsonData.EmptyObject : Before);
                Equal(mode is "initial-schema" or "final-schema" ? ToolFailureKind.InvalidArguments : mode == "throw" ? ToolFailureKind.HookError : ToolFailureKind.Blocked, result.Result.Failure!.Kind);
                Check(result.IsError, "Rejected tool gained a success disposition."); Equal(mode == "block", result.Result.Terminate);
            }
            Equal(mode == "initial-schema" ? 0 : 2, hookCalls); Equal(mode is "initial-schema" or "block" or "throw" ? 0 : 2, tails);
            Equal(mode == "policy" ? 2 : 0, policy.Calls); Equal(0, unused.Calls); Equal(0, native.Effects); Equal(0, extensionEffects); Equal(0, resultCalls);
            Check(!File.Exists(files.NativeBefore) && !File.Exists(files.NativeAfter), "Rejected composed invocation produced a native effect.");
        }
    }

    private static async Task SnapshotAndRemoval()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry(); var effects = 0;
        await registry.ActivateAsync("tool-owner", new Plugin((entries, _) => { entries.RegisterTool(Tool((_, _, _) => { effects++; return ValueTask.FromResult(Original); })); return ValueTask.CompletedTask; }));
        var entered = Gate(); var release = Gate(); var oldCalls = 0; var newCalls = 0;
        var owner = await registry.ActivateAsync("hook-owner", new Plugin((entries, _) =>
        {
            entries.RegisterToolCallHandler(new("old", async (_, _, _) => { oldCalls++; entered.TrySetResult(); await release.Task; return new(After); })); return ValueTask.CompletedTask;
        }));
        var binding = new ExtensionAgentBinding(registry, Allow(), Validate); var native = new NativeAdapter(files.Root, []); var policy = Allow();
        var oldRuntime = Runtime(binding, native, policy, new Source([])); var selected = oldRuntime.Resolve(Model, [Declarations(binding)]);
        var running = Execute(selected, "native_write", Before);
        try
        {
            await Stage(entered.Task, running, "actual captured registered hook");
            var next = await registry.ActivateAsync("late-hook", new Plugin((entries, _) =>
            { entries.RegisterToolCallHandler(new("new", (_, _, _) => { newCalls++; return ValueTask.FromResult<ExtensionToolCallPatch?>(new(After)); })); return ValueTask.CompletedTask; }));
            release.TrySetResult(); Check(!(await running.WaitAsync(Deadline)).IsError, "Current admitted hook snapshot failed after an unrelated addition.");
            Equal(1, oldCalls); Equal(0, newCalls); Equal(1, policy.Calls); Equal(1, native.Effects);
            var added = new ExtensionAgentBinding(registry, Allow(), Validate); Check(!ReferenceEquals(added.PreparedHooks, binding.PreparedHooks), "Refreshed binding reused an obsolete dispatcher.");
            var fresh = Runtime(added, native, policy, new Source([])).Resolve(Model, [Declarations(added)]);
            Check(!(await Execute(fresh, "extension_write", Before)).IsError, "New captured hook snapshot did not invoke its extension tool."); Equal(2, oldCalls); Equal(1, newCalls); Equal(1, effects);
            await owner.DisposeAsync(); var stale = await Execute(selected, "native_write", Before); Equal(ToolFailureKind.HookError, stale.Result.Failure!.Kind);
            Equal(2, policy.Calls); Equal(1, native.Effects); Equal(2, oldCalls); Equal(1, newCalls);
            await next.DisposeAsync(); var removed = new ExtensionAgentBinding(registry, Allow(), Validate); Check(removed.PreparedHooks is null, "Removed registered hooks remained enabled in a refreshed binding.");
            var clean = Runtime(removed, native, policy, new Source([])).Resolve(Model, [Declarations(removed)]);
            Check(!(await Execute(clean, "extension_write", Before)).IsError, "Tool adapter was lost when unrelated hooks were removed."); Equal(2, effects); Equal(3, policy.Calls);
        }
        finally { release.TrySetResult(); await Join(running); }
    }

    private static async Task Cancellation()
    {
        foreach (var closeOwner in new[] { false, true })
        {
            using var files = new Files(); await using var registry = new ExtensionRegistry(); var entered = Gate(); var cleanup = Gate(); var release = Gate(); var cleaned = false; var actualCanceled = false;
            await registry.ActivateAsync("tool-owner", new Plugin((entries, _) => { entries.RegisterTool(Tool((_, _, _) => throw new InvalidOperationException("Hook cancellation must precede extension execution."))); return ValueTask.CompletedTask; }));
            var owner = await registry.ActivateAsync("hook-owner", new Plugin((entries, _) =>
            {
                entries.RegisterToolCallHandler(new("held", async (_, _, token) =>
                {
                    entered.TrySetResult(); var observed = Gate(); using var registration = token.UnsafeRegister(_ => observed.TrySetResult(), null);
                    try { await observed.Task; token.ThrowIfCancellationRequested(); return null; }
                    finally { actualCanceled = token.IsCancellationRequested; cleanup.TrySetResult(); await release.Task; cleaned = true; }
                })); return ValueTask.CompletedTask;
            }));
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate); var native = new NativeAdapter(files.Root, []); var policy = Allow();
            var source = new Source([Assistant("native_write"), TextAssistant()]); var storage = new StorageFactory();
            await Seed(files, Declarations(binding)); var runtime = Runtime(binding, native, policy, source);
            var session = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, runtime, () => 123, files.NextId,
                new(SessionLogStoreOptions: new(StorageFactory: storage)), fallbackModel: Model);
            ToolOutcome? outcome = null; using var listener = session.Subscribe(new Sink((observation, _) =>
            { if (observation is ToolExecutionEnded end) outcome = end.Outcome; return ValueTask.CompletedTask; }));
            var run = session.PromptAsync(User()); Task? ownerClosing = null; Task? sessionClosing = null;
            try
            {
                await Stage(entered.Task, run, "actual registered before hook"); Equal(0, policy.Calls); Equal(0, native.Effects);
                if (closeOwner) ownerClosing = owner.DisposeAsync().AsTask(); else sessionClosing = session.DisposeAsync().AsTask();
                await Stage(cleanup.Task, run, "actual registered hook cancellation finally");
                Check(actualCanceled && !cleaned && !run.IsCompleted, "Cancellation abandoned the actual admitted registered hook.");
                Check(ownerClosing is null || !ownerClosing.IsCompleted, "Owner disposal skipped registered callback cleanup.");
                Check(sessionClosing is null || !sessionClosing.IsCompleted, "Session disposal skipped registered callback cleanup.");
                if (closeOwner) session.Abort(); release.TrySetResult(); await run.WaitAsync(Deadline);
                if (ownerClosing is not null) await ownerClosing.WaitAsync(Deadline); if (sessionClosing is not null) await sessionClosing.WaitAsync(Deadline);
                Equal(0, policy.Calls); Equal(0, native.Effects); Equal(1, source.Requests.Count); Check(cleaned && outcome!.IsError, "Cancelled callback did not join or reach one terminal disposition.");
                Equal(ToolFailureKind.Canceled, outcome!.Result.Failure!.Kind); await Acknowledged(session, CancellationToken.None);
            }
            finally { release.TrySetResult(); session.Abort(); await Join(run); await session.DisposeAsync(); if (ownerClosing is not null) await Join(ownerClosing); }
            Equal(1, storage.Storage!.Disposals); Check(!File.Exists(files.NativeAfter) && !File.Exists(files.NativeBefore), "Canceled prepared hook executed a file action.");
            await using var closed = await SessionLogStore.OpenAsync(files.Path); Equal(new FileInfo(files.Path).Length, closed.Snapshot.CommittedByteLength);
            Check(closed.Snapshot.Entries.Any(entry => entry.Type == "message" && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult"), "Joined cancellation lost its durable tool-result checkpoint.");
        }
    }

    private static async Task LegacyAndLimits()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry(); var extensionEffects = 0;
        await registry.ActivateAsync("tool-owner", new Plugin((entries, _) => { entries.RegisterTool(Tool((_, _, _) => { extensionEffects++; return ValueTask.FromResult(Original); })); return ValueTask.CompletedTask; }));
        var binding = new ExtensionAgentBinding(registry, Allow(), Validate); Check(binding.PreparedHooks is null, "An empty hook snapshot acquired a dispatcher.");
        var native = new NativeAdapter(files.Root, []); var policy = Allow(); var source = new Source([]);
        var legacy = Runtime(binding, native, policy, source).Resolve(Model, [Declarations(binding)]);
        Check(!(await Execute(legacy, "native_write", Before)).IsError, "Default runtime invoker behavior changed."); Equal(1, policy.Calls); Equal(1, native.Effects); Equal(0, extensionEffects);
        var hooks = new ProbeHooks(); var emptyOptions = new SessionRuntimeRegistryOptions(ToolInvokerOptions: new(MaximumTransforms: -1)) { PreparedToolHooks = hooks };
        Throws<ArgumentOutOfRangeException>(() => new SessionRuntimeRegistry([new(Model, source)], [], policy, emptyOptions));
        Equal(0, hooks.Calls); Equal(1, policy.Calls);
        var limited = Runtime(binding, native, policy, source, new(ToolInvokerOptions: new(MaximumTools: 1)) { PreparedToolHooks = hooks });
        Throws<ArgumentException>(() => limited.Resolve(Model, [Declarations(binding)])); Equal(0, hooks.Calls); Equal(1, policy.Calls); Equal(1, native.Effects);
        var explicitOptions = new SessionRuntimeRegistryOptions { PreparedToolHooks = hooks };
        var explicitRuntime = Runtime(binding, native, policy, source, explicitOptions);
        var removed = new TranscriptEntry("system", JsonData.Parse("""{"role":"system","content":"remove native","toolsRemoved":[{"name":"native_write"}],"timestamp":123}"""));
        var onlyExtension = explicitRuntime.Resolve(Model, [Declarations(binding), removed]); Equal(1, onlyExtension.Configuration.Tools.Length);
        Equal("extension_write", onlyExtension.Configuration.Tools[0].Name); Check(!(await Execute(onlyExtension, "extension_write", Before)).IsError, "Active loadout removal broke remaining adapter admission.");
        Equal(2, hooks.Calls); Equal(2, policy.Calls); Equal(1, extensionEffects);
    }

    private static SessionRuntimeRegistry Runtime(ExtensionAgentBinding binding, NativeAdapter native, Policy policy, Source source, SessionRuntimeRegistryOptions? options = null)
    {
        var rows = ImmutableArray.CreateBuilder<SessionRegisteredTool>(); rows.Add(new(Declaration(native.Name), native, ToolExecutionMode.Sequential));
        var declared = binding.CreateDeclarationMessage("extension declarations", 123).WireBody.Value.GetProperty("toolsAdded").EnumerateArray().ToArray();
        for (var index = 0; index < binding.Adapters.Length; index++) rows.Add(new(JsonData.FromElement(declared[index]), binding.Adapters[index], ToolExecutionMode.Sequential));
        return new([new(Model, source, ExecutionMode: ToolExecutionMode.Sequential)], rows.ToImmutable(), policy,
            options ?? new SessionRuntimeRegistryOptions { PreparedToolHooks = binding.PreparedHooks });
    }
    private static TranscriptEntry Declarations(ExtensionAgentBinding binding)
    {
        var added = binding.CreateDeclarationMessage("captured", 123).WireBody.Value.GetProperty("toolsAdded").EnumerateArray().Select(JsonData.FromElement);
        return new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"combined declarations\",\"toolsAdded\":[" + string.Join(',', added.Prepend(Declaration("native_write"))) + "],\"timestamp\":123}"));
    }
    private static JsonData Declaration(string name) => JsonData.Parse(JsonSerializer.Serialize(new { name, description = "complete composed path schema", parameters = Schema.Value }));
    private static ExtensionToolDescriptor Tool(ExtensionToolCallback execute) => new("extension-tool", "extension_write", "complete composed path schema", Schema, execute);
    private static ValueTask<bool> Validate(ExtensionToolRegistrationInfo tool, JsonData value, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(tool.Parameters.ToString() == Schema.ToString() && ValidArguments(value)); }
    private static bool ValidArguments(JsonData value) => value.Value.ValueKind == JsonValueKind.Object && value.Value.EnumerateObject().Count() == 1 &&
        value.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() is "before.txt" or "after.txt";
    private static string PathArgument(JsonData value) => value.Value.GetProperty("path").GetString()!;
    private static string PathArgumentOrInvalid(JsonData value) => value.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString()! : "invalid";
    private static string Short(string name) => name == "native_write" ? "native" : "extension";
    private static string ExtensionTarget(ExtensionToolRegistrationInfo row) => row.OwnerId + "/" + row.OwnerGeneration.ToString(CultureInfo.InvariantCulture) + "/" + row.RegistrationId;
    private static Policy Allow() => new(static (_, _, _) => ValueTask.FromResult(new ToolActionAuthorization(true)));
    private static async Task<FinalizedToolExecution> Execute(SessionRuntimeSelection selected, string name, JsonData arguments)
    {
        var call = new ToolCallContent("direct-" + name, name, arguments); var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [call], TokenUsage.Zero, StopReason.ToolUse);
        var executor = (IFinalizedToolExecutor)selected.Configuration.Tools.Single(tool => tool.Name == name).Executor;
        return await executor.ExecuteFinalizedAsync(new(message, call, 0), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
    }
    private static AssistantMessage Assistant(params string[] tools) => new(Model.Api, Model.Provider, Model.Id, 123,
        tools.Select((tool, index) => (AssistantContent)new ToolCallContent("composed-call-" + index, tool, Before)).ToImmutableArray(), TokenUsage.Zero, StopReason.ToolUse);
    private static AssistantMessage TextAssistant() => new(Model.Api, Model.Provider, Model.Id, 123, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
    private static TranscriptEntry User(string text = "run composed tools") => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 123 })));
    private static async Task Seed(Files files, TranscriptEntry declaration)
    {
        var codec = new SessionEntryCodec(); var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "composed-session", timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Root }));
        await using var store = await SessionLogStore.CreateNewAsync(files.Path, header);
        await store.AppendAsync([codec.Parse(JsonSerializer.Serialize(new { type = "message", id = "declaration", parentId = (string?)null, timestamp = "2026-10-01T00:00:00.000Z", message = declaration.WireBody.Value }))]);
    }
    private static async Task Acknowledged(PersistentAgentSession session, CancellationToken token)
    {
        await using var input = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        var read = await new SessionLogReader().ReadAsync(input, leaveOpen: true, cancellationToken: token);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "Actual durable reader checkpoint was incomplete.");
        Equal(session.Snapshot.Log.CommittedByteLength, new FileInfo(session.Path).Length); Equal(session.Snapshot.Log.CommittedByteLength, (long)read.ValidatedPrefixByteLength);
        Equal(session.Snapshot.Log.Entries.Length + 1, read.ValidatedPrefix.Length);
    }
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= expected.Length, "Durable context shrank."); for (var index = 0; index < expected.Length; index++) Equal(expected[index].WireBody.ToString(), actual[index].WireBody.ToString()); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stage(Task stage, Task operation, string name)
    { var ready = await Task.WhenAny(stage, operation).WaitAsync(Deadline); if (ready != stage) { await operation; throw new InvalidOperationException("Operation settled before " + name); } await stage; }
    private static async Task Join(Task operation) { try { await operation.WaitAsync(Deadline); } catch (OperationCanceledException) { } }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Expected " + expected + "; actual " + actual);

    private sealed class Plugin(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>> authorize) : IToolActionPolicy
    { public int Calls; public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return authorize(invocation, action, token); } }
    private sealed class ProbeHooks : IPreparedToolHooks
    {
        public int Calls;
        public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(new PreparedToolCallHookResult()); }
        public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction action, ToolResult result, bool error, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult<JsonData?>(null); }
    }
    private sealed class NativeAdapter(string root, List<string> trace) : IPreparedToolAdapter
    {
        public string Name => "native_write"; public int Effects; public PreparedToolAction? Authorized, Executed;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(ReferenceEquals(invocation.AssistantMessage.Content[invocation.SourceIndex], invocation.Call), "Reprepare lost final invocation identity.");
            var path = PathArgumentOrInvalid(invocation.Call.Arguments); trace.Add("prepare:native:" + path);
            return ValueTask.FromResult(new PreparedToolAction(Name, "write", PreparedToolActionKind.Path, System.IO.Path.Combine(root, path), invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); trace.Add("validate:native:" + PathArgumentOrInvalid(action.Arguments));
            return ValueTask.FromResult(ValidArguments(action.Arguments) && action.ToolName == Name && action.Operation == "write" && action.Kind == PreparedToolActionKind.Path &&
                action.Target == System.IO.Path.Combine(root, PathArgument(action.Arguments)) && action.CommandArguments.IsEmpty && action.Environment.IsEmpty && action.WorkingDirectory is null);
        }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ExecuteAsync(action, static (_, _) => ValueTask.CompletedTask, token);
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Executed = action; trace.Add("execute:native:" + PathArgument(action.Arguments));
            await ToolProgressDelivery.ReportAndWaitAsync(progress, ToolResult.Success("native progress"), token);
            await File.WriteAllTextAsync(action.Target, "one native effect", token); Effects++; return ToolResult.FromJson(Original);
        }
    }
    private sealed class Source(AssistantMessage[] messages) : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = []; public Func<int, ChatRequest, CancellationToken, ValueTask>? BeforeSend; public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var index = Requests.Count; Check(index < messages.Length, "Unexpected provider continuation.");
            if (BeforeSend is { } before) await before(index, request, token); var message = messages[index]; Requests.Add(request);
            try
            {
                yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
                for (var item = 0; item < message.Content.Length; item++)
                    if (message.Content[item] is ToolCallContent call) { yield return new ToolCallStarted(item, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(item, call); }
                    else { yield return new TextStarted(item, new("")); yield return new TextEnded(item, ((TextContent)message.Content[item]).Text); }
                yield return new StreamDone(message.StopReason, message); await Task.CompletedTask;
            }
            finally { Cleanups++; }
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public Storage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        public int Disposals; public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk(); public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync() { await inner.DisposeAsync(); Disposals++; }
    }
    private sealed class Files : IDisposable
    {
        private int sequence;
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-runtime-composed-hooks-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "session.jsonl"); public string NativeBefore => System.IO.Path.Combine(Root, "before.txt"); public string NativeAfter => System.IO.Path.Combine(Root, "after.txt"); public string ExtensionEffect => System.IO.Path.Combine(Root, "extension-effect.txt");
        public Files() => Directory.CreateDirectory(Root); public string NextId() => "composed-entry-" + Interlocked.Increment(ref sequence);
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Root); var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(target) != parent || !System.IO.Path.GetFileName(target).StartsWith("PiSharp-runtime-composed-hooks-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned composed-hook cleanup target.");
            Directory.Delete(target, recursive: true);
        }
    }
}
