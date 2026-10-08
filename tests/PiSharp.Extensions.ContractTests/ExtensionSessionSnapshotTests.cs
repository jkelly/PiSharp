using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;

internal static class ExtensionSessionSnapshotTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("session-snapshot. absent hosts and pure callbacks preserve optional availability", OptionalAvailability),
        ("session-snapshot. concurrent callbacks retain independent owned branch views", FreshOwnedViews),
        ("session-snapshot. UI catalog invocation and reducer capabilities share the captured context", CallbackCapabilities),
        ("session-snapshot. capture failures and caller cancellation release admission before plugin entry", CaptureFailureAndCancellation),
        ("session-snapshot. cancellation after action admission awaits scope cleanup before releasing callback", ActionAdmissionCleanup),
        ("session-snapshot. owner disposal joins a held capture and retained data grants no admission", HeldCaptureDisposal),
        ("session-snapshot. identities JSON and inclusive view budgets reject without truncation", SnapshotValidation)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ExtensionSessionSnapshot View(long generation = 1, string? leaf = "leaf", params JsonData[] entries) =>
        new("session", generation, leaf, entries.ToImmutableArray());
    private static ExtensionSessionSnapshot? Session(IExtensionContext context) =>
        (context as IExtensionSessionContext ??
            throw new InvalidOperationException("Callback lost the optional session context.")).SessionSnapshot;
    private static ExtensionSessionSnapshot RequiredSession(IExtensionContext context) =>
        Session(context) ?? throw new InvalidOperationException("The configured host did not expose a session view.");
    private static ExtensionToolDescriptor Tool(ExtensionToolCallback callback,
        ExtensionToolArgumentPreparationCallback? prepare = null) =>
        new("tool", "session-tool", "", JsonData.EmptyObject, callback) { PrepareInitialArgumentsAsync = prepare };
    private static ExtensionCommandDescriptor Command(ExtensionCommandCallback callback,
        ExtensionCommandCompletionCallback? complete = null) =>
        new("command", "session-command", "", callback) { GetArgumentCompletionsAsync = complete };

    private static async Task OptionalAvailability()
    {
        // The original constructor, callback interfaces and descriptor constructors remain source-compatible.
        await using (var registry = new ExtensionRegistry(new ExtensionRegistryOptions(), null))
        {
            var calls = 0;
            var scope = await registry.ActivateAsync("absent", new DelegateExtension((entries, _) =>
            {
                False(entries.Features.Contains(ExtensionSessionSnapshotLimits.Feature));
                entries.RegisterTool(Tool((value, context, _) =>
                { Equal<ExtensionSessionSnapshot?>(null, Session(context)); calls++; return ValueTask.FromResult(value); }));
                entries.RegisterCommand(Command((_, context, _) =>
                { Equal<ExtensionSessionSnapshot?>(null, Session(context)); calls++; return ValueTask.CompletedTask; }));
                entries.Observe(new("observer", "notice", (_, context, _) =>
                { Equal<ExtensionSessionSnapshot?>(null, Session(context)); calls++; return ValueTask.CompletedTask; }));
                return ValueTask.CompletedTask;
            }));
            False(registry.AvailableFeatures.Contains(ExtensionSessionSnapshotLimits.Feature));
            False(scope.Features.Contains(ExtensionSessionSnapshotLimits.Feature));
            var captured = registry.CaptureSnapshot();
            await registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject);
            await registry.InvokeCommandAsync(captured, "session-command", JsonData.Null);
            await registry.DispatchObservationsAsync(captured, "notice", JsonData.Null);
            Equal(3, calls);
        }

        var provider = new SessionProbe { Current = null };
        await using var configured = new ExtensionRegistry(null, null, provider);
        var prepared = 0;
        var completed = 0;
        var callbacks = 0;
        var configuredScope = await configured.ActivateAsync("configured", new DelegateExtension((entries, _) =>
        {
            True(entries.Features.Contains(ExtensionSessionSnapshotLimits.Feature));
            Equal(0, provider.Captures.Count);
            entries.RegisterTool(Tool((value, context, _) =>
            { Equal<ExtensionSessionSnapshot?>(null, Session(context)); callbacks++; return ValueTask.FromResult(value); },
                (value, _) => { prepared++; return ValueTask.FromResult(value); }));
            entries.RegisterCommand(Command((_, context, _) =>
            { Equal<ExtensionSessionSnapshot?>(null, Session(context)); callbacks++; return ValueTask.CompletedTask; },
                (_, _) => { completed++; return ValueTask.FromResult(JsonData.Parse("[]")); }));
            return ValueTask.CompletedTask;
        }));
        True(configured.AvailableFeatures.SequenceEqual(configuredScope.Features));
        Equal(0, provider.Captures.Count);
        var registrations = configured.CaptureSnapshot();
        Equal("{}", (await configured.PrepareToolArgumentsAsync(registrations, "session-tool", JsonData.EmptyObject)).ToString());
        Equal("[]", (await configured.CompleteCommandAsync(registrations, "session-command", "")).ToString());
        Equal(1, prepared);
        Equal(1, completed);
        Equal(0, provider.Captures.Count);
        await configured.InvokeToolAsync(registrations, "session-tool", JsonData.EmptyObject);
        await configured.InvokeCommandAsync(registrations, "session-command", JsonData.Null);
        Equal(2, callbacks);
        Equal(2, provider.Captures.Count);
        await configuredScope.DisposeAsync();
        Equal(2, provider.Captures.Count);
    }

    private static async Task FreshOwnedViews()
    {
        const string raw = "{ \"type\":\"message\", \"id\":\"leaf\", \"unknown\":{\"n\":1.0,\"big\":9007199254740993,\"nil\":null,\"opaque\":\"\\u0000\"} }";
        JsonData original;
        using (var document = JsonDocument.Parse(raw)) original = JsonData.FromElement(document.RootElement);
        var backing = new[] { original };
        var provider = new SessionProbe
        {
            Current = new("session", 1, "leaf", ImmutableCollectionsMarshal.AsImmutableArray(backing))
        };
        await using var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 2 }, null, provider);
        var entered = Gate();
        var release = Gate();
        var retained = new List<ExtensionSessionSnapshot>();
        var calls = 0;
        var scope = await registry.ActivateAsync("fresh", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(async (value, context, _) =>
            {
                var captured = RequiredSession(context);
                retained.Add(captured);
                if (++calls == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    True(ReferenceEquals(captured, RequiredSession(context)), "A getter must not recapture host state.");
                    Equal(raw, captured.BranchEntries[0].ToString());
                }
                return value;
            }));
            return ValueTask.CompletedTask;
        }));
        var registrations = registry.CaptureSnapshot();
        var first = registry.InvokeToolAsync(registrations, "session-tool", JsonData.EmptyObject).AsTask();
        Task<JsonData>? second = null;
        try
        {
            await entered.Task;
            backing[0] = JsonData.Parse("{\"id\":\"host-mutated\"}");
            // Branch changes within one attachment need a fresh view even when its generation is unchanged.
            provider.Current = View(1, "next", JsonData.Parse("{\"id\":\"next\",\"parentId\":\"leaf\"}"));
            second = registry.InvokeToolAsync(registrations, "session-tool", JsonData.EmptyObject).AsTask();
            await second;
            False(first.IsCompleted, "The earlier actual callback remains held while the next capture completes.");
            Equal(2, provider.Captures.Count);
            Equal(1L, retained[0].Generation);
            Equal(1L, retained[1].Generation);
            Equal("leaf", retained[0].SelectedLeafId);
            Equal("next", retained[1].SelectedLeafId);
            Equal(raw, retained[0].BranchEntries[0].ToString());
            False(ReferenceEquals(retained[0], retained[1]));
            False(ReferenceEquals(retained[0], provider.Current));
        }
        finally
        {
            release.TrySetResult();
            try { await first; }
            finally { if (second is not null) await second; }
        }
        await scope.DisposeAsync();
        Equal(raw, retained[0].BranchEntries[0].ToString());
        Equal(2, provider.Captures.Count);
        await Failure(() => registry.InvokeToolAsync(registrations, "session-tool", JsonData.EmptyObject).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "fresh", "dispatch-tool");
        Equal(2, provider.Captures.Count);
    }

    private static async Task CallbackCapabilities()
    {
        var provider = new SessionProbe { Current = View(1, "leaf", JsonData.Parse("{\"id\":\"leaf\"}")) };
        var ui = new UiProbe();
        await using var registry = new ExtensionRegistry(null, ui, provider);
        using var operation = new CancellationTokenSource();
        using var session = new CancellationTokenSource();
        var contexts = new List<IExtensionContext>();
        ExtensionRegistrySnapshot? registrations = null;
        IExtensionToolInvocationContext? invocation = null;
        ExtensionSessionSnapshot? updateView = null;
        void Check(IExtensionContext context)
        {
            var captured = RequiredSession(context);
            Equal("session", captured.SessionId);
            Equal(1L, captured.Generation);
            Equal("leaf", captured.SelectedLeafId);
            Equal(operation.Token, context.OperationCancellationToken);
            Equal(session.Token, context.SessionCancellationToken);
            True(ReferenceEquals(context, provider.Captures[^1]));
            True(ReferenceEquals(context, ui.OpenedContexts[^1]));
            True(ReferenceEquals(captured, ui.OpenedViews[^1]));
            contexts.Add(context);
        }
        var owner = await registry.ActivateAsync("contexts", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(async (value, context, token) =>
            {
                Check(context);
                False(context is IExtensionCommandContext);
                if (context is IExtensionToolInvocationContext actual)
                {
                    invocation = actual;
                    Equal("actual-call", actual.ToolCallId);
                    await actual.ReportUpdateAsync(JsonData.Parse("{\"content\":[]}"), token);
                }
                return value;
            }));
            entries.RegisterCommand(Command((_, context, _) =>
            {
                Check(context);
                False(context is IExtensionToolContext);
                var catalog = context as IExtensionCommandCatalogContext ??
                    throw new InvalidOperationException("Command session view lost the catalog capability.");
                Equal(registrations!.Revision, catalog.CommandCatalogRevision);
                True(ReferenceEquals(registrations.CommandCatalog, catalog.CommandCatalog));
                return ValueTask.CompletedTask;
            }));
            entries.Observe(new("observer", "notice", (_, context, _) =>
            {
                Check(context);
                False(context is IExtensionToolContext or IExtensionCommandContext);
                return ValueTask.CompletedTask;
            }));
            entries.RegisterInputHandler(new("input", (_, context, _) =>
            {
                Check(context);
                False(context is IExtensionToolContext or IExtensionCommandContext);
                return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Continue));
            }));
            return ValueTask.CompletedTask;
        }));
        registrations = registry.CaptureSnapshot();
        await registry.InvokeToolAsync(registrations, "session-tool", JsonData.EmptyObject, operation.Token, session.Token);
        False(contexts[0] is IExtensionToolInvocationContext);
        await registry.InvokeToolAsync(registrations, "session-tool", JsonData.EmptyObject, "actual-call",
            (_, token) =>
            {
                True(token.CanBeCanceled);
                updateView = RequiredSession(invocation!);
                return ValueTask.CompletedTask;
            }, operation.Token, session.Token);
        True(ReferenceEquals(RequiredSession(invocation!), updateView));
        await registry.InvokeCommandAsync(registrations, "session-command", JsonData.Null, operation.Token, session.Token);
        await registry.DispatchObservationsAsync(registrations, "notice", JsonData.Null, operation.Token, session.Token);
        var dispatcher = new RegisteredExtensionEventDispatcher(registry, _ => { });
        var reduced = await dispatcher.DispatchInputAsync(registrations, new("input", ExtensionInputSource.Interactive),
            operation.Token, session.Token);
        Equal(ExtensionInputAction.Continue, reduced.Action);
        Equal(0, reduced.Diagnostics.Length);
        Equal(5, contexts.Count);
        Equal(5, provider.Captures.Count);
        Equal(5, ui.Closed);
        True(contexts.All(context => context.OwnerId == owner.OwnerId && context.OwnerGeneration == owner.OwnerGeneration &&
            context.ExtensionLifetimeCancellationToken == owner.ExtensionLifetimeCancellationToken));
        Equal(ExtensionRegistrationFailure.InactiveScope,
            (await ThrowsAsync<ExtensionRegistrationException>(() => invocation!.ReportUpdateAsync(JsonData.EmptyObject).AsTask())).Failure);
        Equal("leaf", RequiredSession(invocation!).SelectedLeafId);
    }

    private static async Task CaptureFailureAndCancellation()
    {
        var provider = new SessionProbe { Current = View(1, null) };
        var ui = new UiProbe();
        await using var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 }, ui, provider);
        var calls = 0;
        var owner = await registry.ActivateAsync("capture-failure", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool((value, _, _) => { calls++; return ValueTask.FromResult(value); }));
            entries.RegisterCommand(Command((_, _, _) => { calls++; return ValueTask.CompletedTask; }));
            entries.Observe(new("observer", "notice", (_, _, _) => { calls++; return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot();
        Func<Task>[] callbacks =
        [
            () => registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject).AsTask(),
            () => registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject, "actual-call",
                (_, _) => ValueTask.CompletedTask).AsTask(),
            () => registry.InvokeCommandAsync(captured, "session-command", JsonData.Null).AsTask(),
            () => registry.DispatchObservationsAsync(captured, "notice", JsonData.Null).AsTask()
        ];
        foreach (var callback in callbacks)
        {
            var primary = new InvalidOperationException("Authored host capture failure.");
            var previousCalls = calls;
            var previousUi = ui.OpenedContexts.Count;
            provider.OnCapture = _ => throw primary;
            True(ReferenceEquals(primary, await ThrowsAsync<InvalidOperationException>(callback)));
            Equal(previousCalls, calls);
            Equal(previousUi, ui.OpenedContexts.Count);
            provider.OnCapture = null;
            await callback();
            Equal(previousCalls + 1, calls); // With a max-one budget, this also proves the failed admission was released.
        }
        foreach (var cancelSession in new[] { false, true })
        {
            using var operation = new CancellationTokenSource();
            using var session = new CancellationTokenSource();
            var previousCalls = calls;
            var previousUi = ui.OpenedContexts.Count;
            provider.OnCapture = context =>
            {
                Equal(operation.Token, context.OperationCancellationToken);
                Equal(session.Token, context.SessionCancellationToken);
                if (cancelSession) session.Cancel(); else operation.Cancel();
                return provider.Current;
            };
            var error = await ThrowsAsync<OperationCanceledException>(() => registry.InvokeCommandAsync(captured,
                "session-command", JsonData.Null, operation.Token, session.Token).AsTask());
            Equal(cancelSession ? session.Token : operation.Token, error.CancellationToken);
            Equal(previousCalls, calls);
            Equal(previousUi, ui.OpenedContexts.Count);
            Equal<ExtensionSessionSnapshot?>(null, Session(provider.Captures[^1]));
            provider.OnCapture = null;
            await registry.InvokeCommandAsync(captured, "session-command", JsonData.Null);
            Equal(previousCalls + 1, calls);
        }
        await owner.DisposeAsync();
    }

    private static async Task HeldCaptureDisposal()
    {
        var entered = Gate();
        var release = Gate();
        var lifetimeCancelled = Gate();
        var cleanupEntered = Gate();
        var provider = new SessionProbe { Current = View(1, "leaf", JsonData.Parse("{\"id\":\"leaf\"}")) };
        var ui = new UiProbe();
        await using var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 }, ui, provider);
        var calls = 0;
        var cleanups = 0;
        var owner = await registry.ActivateAsync("held-capture", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool((value, _, _) => { calls++; return ValueTask.FromResult(value); }));
            return ValueTask.CompletedTask;
        }, () => { cleanups++; cleanupEntered.TrySetResult(); return ValueTask.CompletedTask; }));
        using var listener = owner.ExtensionLifetimeCancellationToken.Register(() => lifetimeCancelled.TrySetResult());
        var captured = registry.CaptureSnapshot();
        provider.OnCapture = _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return provider.Current;
        };
        // The synchronous host capture deliberately owns this real dispatch task until its barrier is released.
        var invocation = Task.Run(async () =>
            await registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject));
        Task? firstDisposal = null;
        Task? secondDisposal = null;
        try
        {
            await entered.Task;
            firstDisposal = owner.DisposeAsync().AsTask();
            secondDisposal = owner.DisposeAsync().AsTask();
            await lifetimeCancelled.Task;
            False(invocation.IsCompleted);
            False(firstDisposal.IsCompleted);
            False(secondDisposal.IsCompleted);
            False(cleanupEntered.Task.IsCompleted, "Plugin cleanup must wait for the actual capture admission.");
            Equal(0, calls);
            Equal(0, ui.OpenedContexts.Count);
            True(provider.Captures[0].ExtensionLifetimeCancellationToken.IsCancellationRequested);
            release.TrySetResult();
            var error = await ThrowsAsync<OperationCanceledException>(() => invocation);
            Equal(owner.ExtensionLifetimeCancellationToken, error.CancellationToken);
            await Task.WhenAll(firstDisposal, secondDisposal);
            Equal(1, cleanups);
            Equal(0, calls);
            Equal<ExtensionSessionSnapshot?>(null, Session(provider.Captures[0]));
            await Failure(() => registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject).AsTask(),
                ExtensionRegistrationFailure.StaleSnapshot, "held-capture", "dispatch-tool");
            Equal(1, provider.Captures.Count);
        }
        finally
        {
            release.TrySetResult();
            try { try { await invocation; } catch (OperationCanceledException) { } }
            finally
            {
                try { if (firstDisposal is not null) await firstDisposal; }
                finally { if (secondDisposal is not null) await secondDisposal; }
            }
        }
    }

    private static async Task SnapshotValidation()
    {
        var provider = new SessionProbe { Current = View(1, null) };
        var options = new ExtensionRegistryOptions
        {
            MaximumConcurrentDispatches = 1,
            MaximumJsonCharacters = ExtensionSessionSnapshotLimits.MaximumCharacters,
            MaximumJsonDepth = 4
        };
        await using var registry = new ExtensionRegistry(options, null, provider);
        var calls = 0;
        ExtensionSessionSnapshot? received = null;
        var owner = await registry.ActivateAsync("validation", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool((value, context, _) =>
            { received = RequiredSession(context); calls++; return ValueTask.FromResult(value); }));
            return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot();
        async Task Reject(ExtensionSessionSnapshot invalid, ExtensionRegistrationFailure failure)
        {
            var before = calls;
            provider.Current = invalid;
            await Failure(() => registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject).AsTask(),
                failure, owner.OwnerId, "capture-session-snapshot");
            Equal(before, calls);
            Equal<ExtensionSessionSnapshot?>(null, Session(provider.Captures[^1]));
            provider.Current = View(1, null);
            await registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject);
            Equal(before + 1, calls); // Actual successful admission follows every rejected capture.
        }
        var valid = View(1, null);
        foreach (var id in new[] { "", "with space", "path/name", "\ud800", new string('s', options.MaximumIdentifierCharacters + 1) })
            await Reject(valid with { SessionId = id }, ExtensionRegistrationFailure.InvalidDescriptor);
        await Reject(valid with { SessionId = null! }, ExtensionRegistrationFailure.InvalidDescriptor);
        foreach (var leaf in new[] { "", "bad\nleaf", "\udc00", new string('l', options.MaximumIdentifierCharacters + 1) })
            await Reject(valid with { SelectedLeafId = leaf }, ExtensionRegistrationFailure.InvalidDescriptor);
        foreach (var generation in new[] { 0L, -1L })
            await Reject(valid with { Generation = generation }, ExtensionRegistrationFailure.InvalidDescriptor);
        await Reject(valid with { BranchEntries = default }, ExtensionRegistrationFailure.InvalidDescriptor);
        foreach (var entry in new[] { JsonData.Null, JsonData.Parse("[]"), JsonData.Parse("\"value\""), JsonData.Parse("1") })
            await Reject(View(1, null, entry), ExtensionRegistrationFailure.InvalidDescriptor);
        await Reject(valid with { BranchEntries = [null!] },
            ExtensionRegistrationFailure.InvalidDescriptor);
        await Reject(View(1, null, JsonData.Parse("{\"number\":1e999}")), ExtensionRegistrationFailure.InvalidDescriptor);
        await Reject(View(1, null, JsonData.Parse("{\"text\":\"\\ud800\"}")), ExtensionRegistrationFailure.InvalidDescriptor);
        await Reject(View(1, null, JsonData.Parse("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":0}}}}}")),
            ExtensionRegistrationFailure.InvalidDescriptor);
        using (var permissive = JsonDocument.Parse("{\"value\":1,}", new JsonDocumentOptions { AllowTrailingCommas = true }))
            await Reject(View(1, null, JsonData.FromElement(permissive.RootElement)), ExtensionRegistrationFailure.InvalidDescriptor);

        var maximumCount = Enumerable.Repeat(JsonData.EmptyObject, ExtensionSessionSnapshotLimits.MaximumBranchEntries).ToImmutableArray();
        provider.Current = valid with { BranchEntries = maximumCount };
        await registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject);
        Equal(ExtensionSessionSnapshotLimits.MaximumBranchEntries, received!.BranchEntries.Length);
        await Reject(valid with { BranchEntries = maximumCount.Add(JsonData.EmptyObject) }, ExtensionRegistrationFailure.LimitExceeded);

        const string emptyPayload = "{\"payload\":\"\"}";
        var remainingCharacters = ExtensionSessionSnapshotLimits.MaximumCharacters - valid.SessionId.Length - emptyPayload.Length;
        var exact = JsonData.Parse("{\"payload\":\"" + new string('x', remainingCharacters) + "\"}");
        provider.Current = View(1, null, exact);
        await registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject);
        Equal(exact.ToString(), received!.BranchEntries.Single().ToString());
        await Reject(View(1, null, exact, JsonData.EmptyObject), ExtensionRegistrationFailure.LimitExceeded);
        var tooManyBytes = JsonData.Parse("{\"payload\":\"" +
            new string('\u4e00', ExtensionSessionSnapshotLimits.MaximumUtf8Bytes / 3) + "\"}");
        True(tooManyBytes.ToString().Length < ExtensionSessionSnapshotLimits.MaximumCharacters);
        await Reject(View(1, null, tooManyBytes), ExtensionRegistrationFailure.LimitExceeded);

        provider.Current = new(new string('s', options.MaximumIdentifierCharacters), long.MaxValue,
            new string('l', options.MaximumIdentifierCharacters), [JsonData.EmptyObject]);
        await registry.InvokeToolAsync(captured, "session-tool", JsonData.EmptyObject);
        Equal(options.MaximumIdentifierCharacters, received!.SessionId.Length);
        Equal(options.MaximumIdentifierCharacters, received.SelectedLeafId!.Length);
        Equal(long.MaxValue, received.Generation);

        // The per-entry JSON budget still applies independently of the aggregate branch budget.
        await using var small = new ExtensionRegistry(new() { MaximumJsonCharacters = 32 }, null, provider);
        var smallCalls = 0;
        await small.ActivateAsync("small", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool((value, _, _) => { smallCalls++; return ValueTask.FromResult(value); }));
            return ValueTask.CompletedTask;
        }));
        provider.Current = View(1, null, JsonData.Parse("{\"payload\":\"" + new string('x', 33) + "\"}"));
        await Failure(() => small.InvokeToolAsync(small.CaptureSnapshot(), "session-tool", JsonData.EmptyObject).AsTask(),
            ExtensionRegistrationFailure.LimitExceeded, "small", "capture-session-snapshot");
        Equal(0, smallCalls);
    }

    private static async Task Failure(Func<Task> action, ExtensionRegistrationFailure failure, string owner, string operation)
    {
        var error = await ThrowsAsync<ExtensionRegistrationException>(action);
        Equal(failure, error.Failure);
        Equal(owner, error.OwnerId);
        Equal(operation, error.Operation);
    }
    private static async Task ActionAdmissionCleanup()
    {
        using var cancel = new CancellationTokenSource(); var provider = new ActionProbe(cancel); var calls = 0;
        await using var registry = new ExtensionRegistry(null, null, provider);
        await registry.ActivateAsync("cleanup", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command((_, _, _) => { calls++; return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        var invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "session-command", JsonData.Null, cancel.Token).AsTask();
        try
        {
            await provider.CleanupEntered.Task; False(invocation.IsCompleted, "Canceled admission released before asynchronous action cleanup.");
            Equal(0, calls); provider.Release.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(() => invocation); Equal(1, provider.Closed);
        }
        finally { provider.Release.TrySetResult(); try { await invocation; } catch (OperationCanceledException) { } }
    }
    private sealed class ActionProbe(CancellationTokenSource cancellation) : IExtensionSessionActionProvider
    {
        internal TaskCompletionSource CleanupEntered { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        internal int Closed;
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => View();
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot)
        { cancellation.Cancel(); return new Scope(this, snapshot); }
        private sealed class Scope(ActionProbe owner, ExtensionSessionSnapshot snapshot) : IExtensionSessionActionScope
        {
            public ExtensionSessionSnapshot Snapshot => snapshot;
            public CancellationToken SessionCancellationToken => CancellationToken.None;
            public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int schema, JsonData data, CancellationToken token) =>
                throw new InvalidOperationException("Canceled callback must not append.");
            public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) =>
                throw new InvalidOperationException("Canceled callback must not switch.");
            public async ValueTask DisposeAsync() { owner.CleanupEntered.TrySetResult(); await owner.Release.Task; owner.Closed++; }
        }
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void True(bool value, string message = "Expected true.")
    { if (!value) throw new InvalidOperationException(message); }
    private static void False(bool value, string message = "Expected false.") => True(!value, message);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }

    private sealed class DelegateExtension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize,
        Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => initialize(registry, cancellationToken);
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
    private sealed class SessionProbe : IExtensionSessionViewProvider
    {
        internal ExtensionSessionSnapshot? Current;
        internal Func<IExtensionContext, ExtensionSessionSnapshot?>? OnCapture;
        internal List<IExtensionContext> Captures { get; } = [];
        public ExtensionSessionSnapshot? Capture(IExtensionContext context)
        {
            Captures.Add(context);
            return OnCapture is null ? Current : OnCapture(context);
        }
    }
    private sealed class UiProbe : IExtensionUiProvider
    {
        internal List<IExtensionContext> OpenedContexts { get; } = [];
        internal List<ExtensionSessionSnapshot?> OpenedViews { get; } = [];
        internal int Closed;
        public IExtensionUiScope OpenScope(IExtensionContext context)
        {
            OpenedContexts.Add(context);
            OpenedViews.Add(Session(context));
            return new Scope(this);
        }
        private sealed class Scope(UiProbe owner) : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => ExtensionUiCapabilities.NoUi;
            private static ValueTask<ExtensionUiOutcome<T>> Unavailable<T>() =>
                ValueTask.FromResult(ExtensionUiOutcome<T>.Unavailable(ExtensionUiUnavailableReason.NoUi));
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices,
                ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Unavailable<string>();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message,
                ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Unavailable<bool>();
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null,
                ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Unavailable<string>();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null,
                CancellationToken cancellationToken = default) => Unavailable<string>();
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification,
                CancellationToken cancellationToken = default) => Unavailable<ExtensionUiPublication>();
            public ValueTask DisposeAsync() { owner.Closed++; return ValueTask.CompletedTask; }
        }
    }
}
