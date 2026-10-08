using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;

internal static class RegisteredEventDispatchTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("registered reducers and tools publish atomically and rollback together", AtomicPublication),
        ("registered input handlers preserve owner order and admitted removal semantics", InputOrderAndRemoval),
        ("registered tool calls retain exact replacements and first-block decisions", ToolCallDecisions),
        ("registered tool result patches preserve null and complete result admission", ToolResultPresence),
        ("registered callback leases guard participant disposal and retired capacity", ParticipantLeases),
        ("registered scope cancellation joins callbacks and throwing lifetime cleanup", CancellationAndCleanup),
        ("registered dispatch retains shared admission and recursive limits", DispatchLimits),
        ("registered typed admission rejects stale generations and invalid descriptors", AdmissionAndGeneration)
    ];

    private static RegisteredExtensionEventDispatcher Dispatcher(ExtensionRegistry registry,
        ExtensionEventDispatchOptions? options = null, int maximumHandlers = 256) =>
        new(registry, value => { _ = ToolResultValueCodec.Read(value); }, options, maximumHandlers);
    private static ExtensionInputHandlerDescriptor Input(string id,
        ExtensionReducerCallback<ExtensionInputEvent, ExtensionInputPatch>? callback = null) =>
        new(id, callback ?? ((_, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(null)));
    private static ExtensionToolDescriptor Tool(string id = "tool", string name = "native-hook-tool") =>
        new(id, name, "", JsonData.EmptyObject, (args, _, _) => ValueTask.FromResult(args));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task AtomicPublication()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate(); var calls = 0;
        var empty = registry.CaptureSnapshot();
        var activation = registry.ActivateAsync("owner", new Extension(async (entries, _) =>
        {
            entries.RegisterTool(Tool());
            entries.RegisterInputHandler(Input("input", (input, _, _) =>
            { calls++; return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|owner")); }));
            entries.RegisterToolCallHandler(new("call", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(null)));
            entries.RegisterToolResultHandler(new("result", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(null)));
            entered.TrySetResult(); await release.Task;
        }));
        try
        {
            await Await(entered.Task);
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            var untouched = await Dispatcher(registry).DispatchInputAsync(empty, new("x", ExtensionInputSource.Rpc));
            Equal("x", untouched.Event.Text); Equal(0, calls);
            release.TrySetResult();
            var scope = await activation;
            var committed = registry.CaptureSnapshot();
            Equal(4, committed.Registrations.Length); Equal(1, committed.Tools.Length);
            Equal(1, committed.InputHandlers.Length); Equal(1, committed.ToolCallHandlers.Length);
            Equal(1, committed.ToolResultHandlers.Length);
            True(committed.Revision > empty.Revision);
            True(committed.Registrations.All(row => row.OwnerGeneration == scope.OwnerGeneration && row.OwnerId == "owner"));
            Equal("x|owner", (await Dispatcher(registry).DispatchInputAsync(committed, new("x", ExtensionInputSource.Rpc))).Event.Text);
            Equal("{}", (await registry.InvokeToolAsync(committed, "native-hook-tool", JsonData.EmptyObject)).ToString());
            var beforeFailure = registry.CaptureSnapshot();
            await RegistrationFailure(() => registry.ActivateAsync("failed", new Extension((entries, _) =>
            {
                entries.RegisterInputHandler(Input("hook")); entries.RegisterTool(Tool(name: "rolled-back"));
                throw new InvalidOperationException("authored initialization failure");
            })), ExtensionRegistrationFailure.InitializationFailed);
            Equal(beforeFailure.Registrations.Length, registry.CaptureSnapshot().Registrations.Length);
            Equal(1, registry.CaptureSnapshot().InputHandlers.Length);
            await registry.ActivateAsync("failed", new Extension((entries, _) =>
            { entries.RegisterTool(Tool(name: "rolled-back")); return ValueTask.CompletedTask; }));
            Equal(2, registry.CaptureSnapshot().Tools.Length);
        }
        finally { release.TrySetResult(); await Join(activation); }
    }

    private static async Task InputOrderAndRemoval()
    {
        await using var registry = new ExtensionRegistry();
        var aEntered = Gate(); var aRelease = Gate(); var trace = new List<string>();
        IExtensionRegistration? removed = null; IExtensionRegistry? bEntries = null;
        var changed = false;
        var aActivation = registry.ActivateAsync("A", new Extension(async (entries, _) =>
        {
            entries.RegisterInputHandler(Input("transform", (input, _, _) =>
            {
                trace.Add("A-transform");
                if (!changed)
                {
                    changed = true; removed!.Dispose();
                    bEntries!.RegisterInputHandler(Input("added", (_, _, _) =>
                    { trace.Add("B-added"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Handled)); }));
                }
                return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|A", JsonData.Null));
            }));
            entries.RegisterInputHandler(Input("error", (_, _, _) =>
            { trace.Add("A-error"); throw new InvalidOperationException("SECRET authored input error"); }));
            aEntered.TrySetResult(); await aRelease.Task;
        }));
        try
        {
            await Await(aEntered.Task);
            await registry.ActivateAsync("B", new Extension((entries, _) =>
            {
                bEntries = entries;
                removed = entries.RegisterInputHandler(Input("transform", (input, _, _) =>
                { trace.Add("B-transform"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, input.Text + "|B")); }));
                entries.RegisterInputHandler(Input("tail", (_, _, _) =>
                { trace.Add("B-tail"); return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
                return ValueTask.CompletedTask;
            }));
            Equal("B,B", string.Join(',', registry.CaptureSnapshot().InputHandlers.Select(row => row.OwnerId)));
            aRelease.TrySetResult(); await aActivation;
            var captured = registry.CaptureSnapshot();
            Equal("A,A,B,B", string.Join(',', captured.InputHandlers.Select(row => row.OwnerId)));
            var images = JsonData.Parse("[{\"type\":\"image\",\"data\":\"opaque\",\"mimeType\":\"image/png\"}]");
            var dispatcher = Dispatcher(registry);
            var first = await dispatcher.DispatchInputAsync(captured, new("one", ExtensionInputSource.Rpc, images, "steer"));
            Equal("one|A|B", first.Event.Text); Equal(ExtensionInputAction.Transform, first.Action);
            Equal(images.ToString(), first.Event.Images!.ToString());
            Equal("A-transform,A-error,B-transform,B-tail", string.Join(',', trace));
            Equal(1, first.Diagnostics.Length); Equal("error", first.Diagnostics[0].RegistrationId);
            Equal(ExtensionEventFailure.HandlerFailed, first.Diagnostics[0].Failure);
            False(first.Diagnostics[0].ToString().Contains("SECRET", StringComparison.Ordinal));
            // Removed callbacks run in the admitted dispatch; a stale snapshot cannot acquire a new lease.
            await RegistrationFailure(() => dispatcher.DispatchInputAsync(captured, new("stale", ExtensionInputSource.Rpc)).AsTask(),
                ExtensionRegistrationFailure.StaleSnapshot);
            Equal(4, trace.Count);
            trace.Clear();
            var second = await dispatcher.DispatchInputAsync(registry.CaptureSnapshot(), new("two", ExtensionInputSource.Extension));
            Equal(ExtensionInputAction.Handled, second.Action); Equal("two|A", second.Event.Text);
            Equal("A-transform,A-error,B-tail,B-added", string.Join(',', trace));
        }
        finally { aRelease.TrySetResult(); await Join(aActivation); }
    }

    private static async Task ToolCallDecisions()
    {
        await using var registry = new ExtensionRegistry();
        var trace = new List<string>();
        await registry.ActivateAsync("A", new Extension((entries, _) =>
        {
            entries.RegisterToolCallHandler(new("replace", (_, _, _) =>
            {
                trace.Add("A-replace");
                return ValueTask.FromResult<ExtensionToolCallPatch?>(new(JsonData.Parse("{\"ownerA\":null,\"ordered\":[2,1],\"n\":1.0,\"big\":9007199254740993}")));
            }));
            entries.RegisterToolCallHandler(new("decision", (_, _, _) =>
            { trace.Add("A-decision"); return ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: JsonData.Parse("{\"block\":false,\"reason\":\"A\",\"terminate\":null,\"opaque\":\"A-decision\"}"))); }));
            return ValueTask.CompletedTask;
        }));
        await registry.ActivateAsync("B", new Extension((entries, _) =>
        {
            entries.RegisterToolCallHandler(new("decision", (_, _, _) =>
            { trace.Add("B-decision"); return ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: JsonData.Parse("{\"reason\":\"B\",\"terminate\":false,\"opaque\":null}"))); }));
            entries.RegisterToolCallHandler(new("block-or-error", (input, _, _) =>
            {
                trace.Add("B-block-or-error");
                if (input.ToolCallId == "rejected") throw new InvalidOperationException("SECRET authored tool hook error");
                return ValueTask.FromResult<ExtensionToolCallPatch?>(input.ToolCallId == "blocked"
                    ? new(Decision: JsonData.Parse("{\"block\":true,\"reason\":\"source first block\",\"terminate\":true}")) : null);
            }));
            entries.RegisterToolCallHandler(new("tail", (_, _, _) =>
            { trace.Add("B-tail"); return ValueTask.FromResult<ExtensionToolCallPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var dispatcher = Dispatcher(registry); var snapshot = registry.CaptureSnapshot();
        var raw = "{ \"path\":\"file.txt\", \"nil\":null }"; var original = JsonData.Parse(raw);
        var composed = await dispatcher.DispatchToolCallAsync(snapshot, new("read", "composed", original));
        Equal("A-replace,A-decision,B-decision,B-block-or-error,B-tail", string.Join(',', trace));
        False(composed.Blocked); True(composed.ArgumentsReplaced);
        Equal("1.0", composed.Event.Arguments.Value.GetProperty("n").GetRawText());
        Equal("9007199254740993", composed.Event.Arguments.Value.GetProperty("big").GetRawText());
        Equal(raw, original.ToString());
        Equal("{\"reason\":\"B\",\"terminate\":false,\"opaque\":null}", composed.Decision!.ToString());
        trace.Clear();
        var blocked = await dispatcher.DispatchToolCallAsync(snapshot, new("read", "blocked", original));
        True(blocked.Blocked); False(trace.Contains("B-tail"));
        Equal("source first block", blocked.Decision!.Value.GetProperty("reason").GetString());
        trace.Clear();
        var rejected = await ThrowsAsync<ExtensionEventDispatchException>(() =>
            dispatcher.DispatchToolCallAsync(snapshot, new("read", "rejected", original)).AsTask());
        Equal("B", rejected.Diagnostic.OwnerId); Equal("block-or-error", rejected.Diagnostic.RegistrationId);
        False(rejected.Message.Contains("SECRET", StringComparison.Ordinal)); False(trace.Contains("B-tail"));
        Equal(raw, original.ToString());
    }

    private static async Task ToolResultPresence()
    {
        await using var registry = new ExtensionRegistry();
        var trace = new List<string>(); var admits = 0;
        await registry.ActivateAsync("A", new Extension((entries, _) =>
        {
            entries.RegisterToolResultHandler(new("content", (_, _, _) =>
            { trace.Add("A-content"); return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("{\"content\":[],\"details\":null,\"terminate\":true,\"unknownPatch\":\"retained\"}"))); }));
            entries.RegisterToolResultHandler(new("error", (_, _, _) =>
            { trace.Add("A-error"); throw new InvalidOperationException("SECRET authored result hook error"); }));
            return ValueTask.CompletedTask;
        }));
        await registry.ActivateAsync("B", new Extension((entries, _) =>
        {
            entries.RegisterToolResultHandler(new("null", (_, _, _) =>
            { trace.Add("B-null"); return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("{\"structuredContent\":null,\"isError\":false,\"usage\":null}"))); }));
            return ValueTask.CompletedTask;
        }));
        var raw = "{\"content\":[{\"type\":\"text\",\"text\":\"one\"}],\"details\":{\"old\":true},\"structuredContent\":{\"old\":true},\"isError\":true,\"unknownBase\":1.0}";
        var original = JsonData.Parse(raw);
        var dispatcher = new RegisteredExtensionEventDispatcher(registry, value =>
        { admits++; _ = ToolResultValueCodec.Read(value); });
        var result = await dispatcher.DispatchToolResultAsync(registry.CaptureSnapshot(),
            new("read", "result", JsonData.EmptyObject, original, OutcomeIsError: true));
        Equal("A-content,A-error,B-null", string.Join(',', trace)); Equal(3, admits);
        True(result.Modified); True(result.Event.OutcomeIsError); Equal(2, result.ReturnedPatches.Length);
        var wire = result.Event.Result.Value;
        Equal(0, wire.GetProperty("content").GetArrayLength());
        Equal(JsonValueKind.Null, wire.GetProperty("details").ValueKind);
        Equal(JsonValueKind.Null, wire.GetProperty("structuredContent").ValueKind);
        Equal(JsonValueKind.Null, wire.GetProperty("usage").ValueKind);
        Equal(JsonValueKind.False, wire.GetProperty("isError").ValueKind);
        Equal("1.0", wire.GetProperty("unknownBase").GetRawText());
        False(wire.TryGetProperty("terminate", out _));
        Equal("retained", result.ReturnedPatches[0].Value.GetProperty("unknownPatch").GetString());
        Equal(raw, original.ToString()); Equal(1, result.Diagnostics.Length);
        False(result.Diagnostics[0].ToString().Contains("SECRET", StringComparison.Ordinal));

        await using var invalidRegistry = new ExtensionRegistry();
        await invalidRegistry.ActivateAsync("invalid", new Extension((entries, _) =>
        {
            entries.RegisterToolResultHandler(new("invalid-content", (_, _, _) =>
                ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":7}],\"details\":null}")))));
            return ValueTask.CompletedTask;
        }));
        var invalid = await Dispatcher(invalidRegistry).DispatchToolResultAsync(invalidRegistry.CaptureSnapshot(),
            new("read", "invalid", JsonData.EmptyObject, original));
        False(invalid.Modified); Equal(raw, invalid.Event.Result.ToString());
        Equal(ExtensionEventFailure.InvalidResult, invalid.Diagnostics.Single().Failure);
    }

    private static async Task ParticipantLeases()
    {
        await using var registry = new ExtensionRegistry(new() { MaximumRegistrations = 2 });
        var entered = Gate(); var release = Gate(); var laterCalls = 0;
        RegistrationScope? a = null, b = null; IExtensionRegistration? bHandle = null;
        var protectedParticipants = 0;
        a = await registry.ActivateAsync("A", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(Input("first", async (_, _, _) =>
            {
                foreach (var dispose in new Func<ValueTask>[] { () => a!.DisposeAsync(), () => b!.DisposeAsync(), registry.DisposeAsync })
                {
                    var error = await ThrowsAsync<ExtensionRegistrationException>(() => dispose().AsTask());
                    Equal(ExtensionRegistrationFailure.ReentrantDisposal, error.Failure); protectedParticipants++;
                }
                entered.TrySetResult(); await release.Task; return null;
            }));
            return ValueTask.CompletedTask;
        }));
        b = await registry.ActivateAsync("B", new Extension((entries, _) =>
        {
            bHandle = entries.RegisterInputHandler(Input("later", (_, _, _) =>
            { laterCalls++; return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var snapshot = registry.CaptureSnapshot();
        var dispatch = Dispatcher(registry).DispatchInputAsync(snapshot, new("x", ExtensionInputSource.Rpc)).AsTask();
        try
        {
            await Await(entered.Task); Equal(3, protectedParticipants);
            bHandle!.Dispose(); Equal(1, registry.CaptureSnapshot().InputHandlers.Length);
            Equal(ExtensionRegistrationFailure.LimitExceeded,
                Throws<ExtensionRegistrationException>(() => b.RegisterInputHandler(Input("replacement"))).Failure);
            release.TrySetResult(); await dispatch; Equal(1, laterCalls);
            b.RegisterInputHandler(Input("replacement")); Equal(2, registry.CaptureSnapshot().InputHandlers.Length);
        }
        finally { release.TrySetResult(); await Join(dispatch); }
    }

    private static async Task CancellationAndCleanup()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var callbackCleanupEntered = Gate(); var callbackCleanupRelease = Gate();
        var pluginCleanupEntered = Gate(); var pluginCleanupRelease = Gate();
        var laterCalls = 0; var pluginCleanupFinished = false;
        using var operation = new CancellationTokenSource(); using var session = new CancellationTokenSource();
        RegistrationScope? a = null;
        a = await registry.ActivateAsync("A", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(Input("held", async (_, context, token) =>
            {
                Equal("A", context.OwnerId); Equal(a!.OwnerGeneration, context.OwnerGeneration);
                Equal(operation.Token, context.OperationCancellationToken); Equal(session.Token, context.SessionCancellationToken);
                Equal(a.ExtensionLifetimeCancellationToken, context.ExtensionLifetimeCancellationToken);
                using var listener = context.ExtensionLifetimeCancellationToken.Register(() =>
                    throw new InvalidOperationException("SECRET lifetime listener failure"));
                entered.TrySetResult();
                try { await Gate().Task.WaitAsync(token); }
                finally { callbackCleanupEntered.TrySetResult(); await callbackCleanupRelease.Task; }
                return null;
            }));
            return ValueTask.CompletedTask;
        }, async () =>
        { pluginCleanupEntered.TrySetResult(); await pluginCleanupRelease.Task; pluginCleanupFinished = true; }));
        await registry.ActivateAsync("B", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(Input("later", (_, _, _) =>
            { laterCalls++; return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var dispatcher = Dispatcher(registry);
        var dispatch = dispatcher.DispatchInputAsync(registry.CaptureSnapshot(), new("x", ExtensionInputSource.Rpc), operation.Token, session.Token).AsTask();
        Task? closing = null;
        try
        {
            await Await(entered.Task);
            closing = a.DisposeAsync().AsTask();
            True(ReferenceEquals(closing, a.DisposeAsync().AsTask()));
            await Await(callbackCleanupEntered.Task);
            False(closing.IsCompleted); False(dispatch.IsCompleted); False(pluginCleanupEntered.Task.IsCompleted);
            Equal("B", registry.CaptureSnapshot().InputHandlers.Single().OwnerId);
            callbackCleanupRelease.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(() => dispatch);
            await Await(pluginCleanupEntered.Task); False(closing.IsCompleted);
            pluginCleanupRelease.TrySetResult();
            var error = await ThrowsAsync<ExtensionRegistrationException>(() => closing!);
            Equal(ExtensionRegistrationFailure.CleanupFailed, error.Failure);
            False(error.Message.Contains("SECRET", StringComparison.Ordinal));
            True(pluginCleanupFinished); Equal(0, laterCalls);
            await registry.ActivateAsync("A", new Extension((_, _) => ValueTask.CompletedTask));
            await dispatcher.DispatchInputAsync(registry.CaptureSnapshot(), new("after", ExtensionInputSource.Rpc));
            Equal(1, laterCalls);
        }
        finally
        {
            callbackCleanupRelease.TrySetResult(); pluginCleanupRelease.TrySetResult();
            await Join(dispatch); if (closing is not null) await Join(closing);
        }

        // Native requested cancellation stops the chain, unlike upstream ctx.abort's retained signal flag.
        await using var canceledRegistry = new ExtensionRegistry(); using var cancel = new CancellationTokenSource();
        var afterCancel = 0;
        await canceledRegistry.ActivateAsync("cancel", new Extension((entries, _) =>
        {
            entries.RegisterInputHandler(Input("cancel", (_, _, _) =>
            { cancel.Cancel(); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, "changed")); }));
            entries.RegisterInputHandler(Input("tail", (_, _, _) =>
            { afterCancel++; return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        await ThrowsAsync<OperationCanceledException>(() => Dispatcher(canceledRegistry).DispatchInputAsync(
            canceledRegistry.CaptureSnapshot(), new("before", ExtensionInputSource.Rpc), cancel.Token).AsTask());
        Equal(0, afterCancel);
    }

    private static async Task DispatchLimits()
    {
        await using (var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 }))
        {
            var entered = Gate(); var release = Gate(); var calls = 0;
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterInputHandler(Input("held", async (_, _, _) =>
                { calls++; entered.TrySetResult(); await release.Task; return null; }));
                return ValueTask.CompletedTask;
            }));
            var captured = registry.CaptureSnapshot(); var first = Dispatcher(registry);
            var dispatch = first.DispatchInputAsync(captured, new("x", ExtensionInputSource.Rpc)).AsTask();
            try
            {
                await Await(entered.Task);
                await RegistrationFailure(() => Dispatcher(registry).DispatchInputAsync(captured, new("overlap", ExtensionInputSource.Rpc)).AsTask(),
                    ExtensionRegistrationFailure.LimitExceeded);
                Equal(1, calls); release.TrySetResult(); await dispatch;
                await first.DispatchInputAsync(captured, new("next", ExtensionInputSource.Rpc)); Equal(2, calls);
            }
            finally { release.TrySetResult(); await Join(dispatch); }
        }
        await using (var registry = new ExtensionRegistry())
        {
            var entered = Gate(); var release = Gate(); var calls = 0;
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterInputHandler(Input("held", async (_, _, _) =>
                { calls++; entered.TrySetResult(); await release.Task; return null; }));
                return ValueTask.CompletedTask;
            }));
            var dispatcher = Dispatcher(registry, new(MaximumConcurrentDispatches: 1));
            var captured = registry.CaptureSnapshot();
            var dispatch = dispatcher.DispatchInputAsync(captured, new("x", ExtensionInputSource.Rpc)).AsTask();
            try
            {
                await Await(entered.Task);
                var error = await ThrowsAsync<ExtensionEventDispatchException>(() =>
                    dispatcher.DispatchInputAsync(captured, new("overlap", ExtensionInputSource.Rpc)).AsTask());
                Equal(ExtensionEventFailure.LimitExceeded, error.Diagnostic.Failure);
                Equal(1, calls); release.TrySetResult(); await dispatch;
                await dispatcher.DispatchInputAsync(captured, new("next", ExtensionInputSource.Rpc)); Equal(2, calls);
            }
            finally { release.TrySetResult(); await Join(dispatch); }
        }
        await using (var registry = new ExtensionRegistry())
        {
            RegisteredExtensionEventDispatcher? dispatcher = null; var depth = 0; var reentrantRejections = 0;
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterInputHandler(Input("recursive", async (input, _, _) =>
                {
                    depth++;
                    try
                    {
                        if (depth == 2)
                        {
                            var error = await ThrowsAsync<ExtensionEventDispatchException>(() => dispatcher!.DispatchInputAsync(
                                registry.CaptureSnapshot(), input).AsTask());
                            Equal(ExtensionEventFailure.ReentrantLimit, error.Diagnostic.Failure); reentrantRejections++;
                        }
                        else await dispatcher!.DispatchInputAsync(registry.CaptureSnapshot(), input);
                        return null;
                    }
                    finally { depth--; }
                }));
                return ValueTask.CompletedTask;
            }));
            dispatcher = Dispatcher(registry, new(MaximumDispatchDepth: 2));
            await dispatcher.DispatchInputAsync(registry.CaptureSnapshot(), new("x", ExtensionInputSource.Rpc));
            Equal(1, reentrantRejections); Equal(0, depth);
            await dispatcher.DispatchInputAsync(registry.CaptureSnapshot(), new("next", ExtensionInputSource.Rpc));
            Equal(2, reentrantRejections);
        }
        await using (var registry = new ExtensionRegistry())
        {
            var calls = 0;
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                foreach (var id in new[] { "one", "two" }) entries.RegisterInputHandler(Input(id, (_, _, _) =>
                { calls++; return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
                return ValueTask.CompletedTask;
            }));
            await RegistrationFailure(() => Dispatcher(registry, maximumHandlers: 1).DispatchInputAsync(
                registry.CaptureSnapshot(), new("x", ExtensionInputSource.Rpc)).AsTask(), ExtensionRegistrationFailure.LimitExceeded);
            Equal(0, calls);
            await Dispatcher(registry, maximumHandlers: 2).DispatchInputAsync(registry.CaptureSnapshot(), new("x", ExtensionInputSource.Rpc));
            Equal(2, calls);
        }
    }

    private static async Task AdmissionAndGeneration()
    {
        foreach (var characters in new[] { 6, 7 })
        {
            // Owner 'o', registration 'i', and fixed event name 'input' consume exactly seven characters.
            await using var registry = new ExtensionRegistry(new() { MaximumMetadataCharacters = characters });
            var activation = registry.ActivateAsync("o", new Extension((entries, _) =>
            { entries.RegisterInputHandler(Input("i")); return ValueTask.CompletedTask; }));
            if (characters == 6)
            { await RegistrationFailure(() => activation, ExtensionRegistrationFailure.LimitExceeded); Equal(0, registry.CaptureSnapshot().Registrations.Length); }
            else { await activation; Equal(1, registry.CaptureSnapshot().InputHandlers.Length); }
        }
        await using var main = new ExtensionRegistry(); await using var foreign = new ExtensionRegistry();
        var calls = 0; IExtensionRegistration? handle = null;
        var scope = await main.ActivateAsync("owner", new Extension((entries, _) =>
        {
            handle = entries.RegisterInputHandler(Input("shared", (_, _, _) =>
            { calls++; return ValueTask.FromResult<ExtensionInputPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        Equal(ExtensionRegistrationFailure.DuplicateRegistrationId, Throws<ExtensionRegistrationException>(() =>
            scope.RegisterToolCallHandler(new("shared", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(null)))).Failure);
        Equal(ExtensionRegistrationFailure.InvalidDescriptor, Throws<ExtensionRegistrationException>(() =>
            scope.RegisterToolResultHandler(new("bad", null!))).Failure);
        var dispatcher = Dispatcher(main); var old = main.CaptureSnapshot();
        await RegistrationFailure(() => dispatcher.DispatchInputAsync(foreign.CaptureSnapshot(), new("x", ExtensionInputSource.Rpc)).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => dispatcher.DispatchInputAsync(old, new("x", ExtensionInputSource.Rpc),
            sessionCancellationToken: canceled.Token).AsTask());
        Equal(0, calls);
        await ThrowsAsync<ArgumentException>(() => dispatcher.DispatchInputAsync(old, new("\ud800", ExtensionInputSource.Rpc)).AsTask());
        await dispatcher.DispatchInputAsync(old, new("valid", ExtensionInputSource.Rpc)); Equal(1, calls);
        handle!.Dispose();
        await RegistrationFailure(() => dispatcher.DispatchInputAsync(old, new("x", ExtensionInputSource.Rpc)).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot);
        scope.RegisterInputHandler(Input("shared"));
        await RegistrationFailure(() => dispatcher.DispatchInputAsync(old, new("x", ExtensionInputSource.Rpc)).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot);
        var sameIdNewEntry = main.CaptureSnapshot();
        await scope.DisposeAsync();
        var replacement = await main.ActivateAsync("owner", new Extension((entries, _) =>
        { entries.RegisterInputHandler(Input("shared")); return ValueTask.CompletedTask; }));
        True(replacement.OwnerGeneration > scope.OwnerGeneration);
        await RegistrationFailure(() => dispatcher.DispatchInputAsync(sameIdNewEntry, new("x", ExtensionInputSource.Rpc)).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot);
        await dispatcher.DispatchInputAsync(main.CaptureSnapshot(), new("valid", ExtensionInputSource.Rpc));
    }

    private static async Task RegistrationFailure(Func<Task> run, ExtensionRegistrationFailure expected)
    { Equal(expected, (await ThrowsAsync<ExtensionRegistrationException>(run)).Failure); }
    private static async Task Join(Task task)
    { try { await Await(task); } catch (Exception) when (task.IsCompleted) { } }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static T Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Registered dispatch assertion failed."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize,
        Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token);
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
