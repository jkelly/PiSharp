using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PublishedConsumer;

internal static class RegistrationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("consumer async initialization publishes one immutable transaction", ConsumerTransaction),
        ("failure and cancellation rollback join cleanup without disturbing another owner", RollbackAndCancellation),
        ("owner IDs, registration IDs, names and core reservations have deterministic conflicts", DeterministicConflicts),
        ("captured observations survive removal and reject dispatch-dependent disposal", CapturedObservations),
        ("scope disposal stops admission, cancels and joins callbacks and cleanup once", DisposalSettlement),
        ("generation checks, callback errors and registry cleanup settle owned work", GenerationAndRegistryCleanup),
        ("staged, active and retired resources obey inclusive cumulative bounds", ResourceBounds)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ExtensionToolDescriptor Tool(string id = "tool", string name = "native-tool",
        ExtensionToolCallback? callback = null, JsonData? schema = null) =>
        new(id, name, "", schema ?? JsonData.EmptyObject, callback ?? ((value, _, _) => ValueTask.FromResult(value)));
    private static ExtensionCommandDescriptor Command(string id = "command", string name = "native-command") =>
        new(id, name, "", (_, _, _) => ValueTask.CompletedTask);
    private static ExtensionObservationDescriptor Observer(string id = "observer", string topic = "notice",
        ExtensionObservationCallback? callback = null) => new(id, topic, callback ?? ((_, _, _) => ValueTask.CompletedTask));

    private static async Task ConsumerTransaction()
    {
        await using var registry = new ExtensionRegistry();
        var consumer = new PublishedConsumerExtension { HoldInitialization = true };
        var old = registry.CaptureSnapshot();
        var activation = registry.ActivateAsync("consumer", consumer);
        await consumer.InitializationEntered.Task;
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        False(activation.IsCompleted, "The initializer remains gated.");
        var entries = consumer.Registry ?? throw new InvalidOperationException("The consumer has no admitted registry.");
        Equal(ExperimentalExtensionContract.Profile, entries.ContractProfile);
        True(entries.Features.Contains("transactional-registration"));
        consumer.ContinueInitialization.SetResult();
        var scope = await activation;
        var committed = registry.CaptureSnapshot();
        Equal(0, old.Registrations.Length);
        Equal(3, committed.Registrations.Length);
        True(committed.Revision > old.Revision);
        Equal("tool,command,observer", string.Join(',', committed.Registrations.Select(item => item.RegistrationId)));
        True(committed.Registrations.All(item => item.OwnerId == "consumer" && item.OwnerGeneration == scope.OwnerGeneration));

        using var operation = new CancellationTokenSource();
        using var session = new CancellationTokenSource();
        var raw = "{ \"n\":1.0, \"big\":9007199254740993, \"nil\":null, \"opaque\":\"\\u0000\" }";
        var input = JsonData.Parse(raw);
        var result = await registry.InvokeToolAsync(committed, "sample-echo", input, operation.Token, session.Token);
        Equal("{\"echo\":" + raw + "}", result.ToString());
        Equal(raw, input.ToString());
        var toolContext = consumer.LastToolContext!;
        Context(toolContext, scope, operation.Token, session.Token);
        False(toolContext is IExtensionCommandContext);
        await registry.InvokeCommandAsync(committed, "sample-count", JsonData.Null, operation.Token, session.Token);
        Equal(1, consumer.CommandCalls);
        Context(consumer.LastCommandContext!, scope, operation.Token, session.Token);
        False(consumer.LastCommandContext is IExtensionToolContext);
        await registry.DispatchObservationsAsync(committed, "sample-notice", input, operation.Token, session.Token);
        Equal(1, consumer.ObservationCalls);
        Context(consumer.LastObservationContext!, scope, operation.Token, session.Token);
        False(consumer.LastObservationContext is IExtensionCommandContext or IExtensionToolContext);
        await scope.DisposeAsync();
        Equal(1, consumer.DisposalCalls);
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
    }

    private static async Task RollbackAndCancellation()
    {
        await using var registry = new ExtensionRegistry();
        var notices = 0;
        await registry.ActivateAsync("healthy", new DelegateExtension((entries, _) =>
        {
            entries.Observe(Observer(callback: (_, _, _) => { notices++; return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        for (var point = 1; point <= 3; point++)
        {
            var failurePoint = point;
            var cleaned = 0;
            var owner = "fail-" + point;
            var extension = new DelegateExtension(async (entries, _) =>
            {
                entries.RegisterTool(Tool());
                if (failurePoint == 1) throw new InvalidOperationException("authored initialization failure");
                entries.RegisterCommand(Command());
                if (failurePoint == 2) throw new InvalidOperationException("authored initialization failure");
                entries.Observe(Observer());
                await Task.Yield();
                throw new InvalidOperationException("authored initialization failure");
            }, () => { cleaned++; return ValueTask.CompletedTask; });
            await Failure(() => registry.ActivateAsync(owner, extension), ExtensionRegistrationFailure.InitializationFailed, owner, "initialize");
            Equal(1, cleaned);
            Equal(1, registry.CaptureSnapshot().Registrations.Length);
            await registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "notice", JsonData.Null);
            var replacement = await registry.ActivateAsync(owner, new DelegateExtension((_, _) => ValueTask.CompletedTask));
            await replacement.DisposeAsync();
        }
        Equal(3, notices);

        // A clean cancellation remains cancellation, with no staged registration reaching the public snapshot.
        using (var cancel = new CancellationTokenSource())
        {
            var entered = Gate();
            var cleaned = false;
            var activation = registry.ActivateAsync("canceled", new DelegateExtension(async (entries, token) =>
            {
                entries.RegisterTool(Tool());
                entered.SetResult();
                await Gate().Task.WaitAsync(token);
            }, () => { cleaned = true; return ValueTask.CompletedTask; }), cancel.Token);
            await entered.Task;
            cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => activation);
            True(cleaned);
            Equal(1, registry.CaptureSnapshot().Registrations.Length);
        }

        // Lifetime listener failure cannot abandon rollback or return before asynchronous plugin disposal.
        using var cancellation = new CancellationTokenSource();
        var initializationEntered = Gate();
        var cleanupEntered = Gate();
        var cleanupRelease = Gate();
        CancellationTokenRegistration listener = default;
        var failedCancellation = registry.ActivateAsync("listener", new DelegateExtension(async (entries, token) =>
        {
            entries.RegisterTool(Tool());
            listener = entries.ExtensionLifetimeCancellationToken.Register(() => throw new InvalidOperationException("authored cancellation listener failure"));
            initializationEntered.SetResult();
            await Gate().Task.WaitAsync(token);
        }, async () => { cleanupEntered.SetResult(); await cleanupRelease.Task; }), cancellation.Token);
        await initializationEntered.Task;
        cancellation.Cancel();
        await cleanupEntered.Task;
        False(failedCancellation.IsCompleted, "Rollback must join plugin cleanup even after a cancellation listener throws.");
        Equal(1, registry.CaptureSnapshot().Registrations.Length);
        cleanupRelease.SetResult();
        await Failure(() => failedCancellation, ExtensionRegistrationFailure.CleanupFailed, "listener", "initialize");
        listener.Dispose();
        var retry = await registry.ActivateAsync("listener", new DelegateExtension((_, _) => ValueTask.CompletedTask));
        await retry.DisposeAsync();
    }

    private static async Task DeterministicConflicts()
    {
        await using var registry = new ExtensionRegistry();
        var owner = await registry.ActivateAsync("owner", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(name: "shared"));
            entries.RegisterCommand(Command(name: "shared")); // Distinct namespaces.
            return ValueTask.CompletedTask;
        }));
        await Failure(() => registry.ActivateAsync("owner", new DelegateExtension((_, _) => ValueTask.CompletedTask)),
            ExtensionRegistrationFailure.DuplicateOwner, "owner", "activate");
        var before = registry.CaptureSnapshot();
        var first = Throws<ExtensionRegistrationException>(() => owner.RegisterCommand(Command(id: "tool", name: "unused")));
        var second = Throws<ExtensionRegistrationException>(() => owner.RegisterCommand(Command(id: "tool", name: "unused")));
        Equal(ExtensionRegistrationFailure.DuplicateRegistrationId, first.Failure);
        Equal(first.Message, second.Message);
        Equal("owner", first.OwnerId);
        Equal("register-command", first.Operation);
        await Failure(() => registry.ActivateAsync("other", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(name: "shared"));
            return ValueTask.CompletedTask;
        })), ExtensionRegistrationFailure.DuplicateName, "other", "register-tool");
        Equal(ExtensionRegistrationFailure.ReservedName,
            Throws<ExtensionRegistrationException>(() => owner.RegisterTool(Tool(id: "reserved-tool", name: "read"))).Failure);
        Equal(ExtensionRegistrationFailure.ReservedName,
            Throws<ExtensionRegistrationException>(() => owner.RegisterCommand(Command(id: "reserved-command", name: "trust"))).Failure);
        Equal(ExtensionRegistrationFailure.InvalidDescriptor,
            Throws<ExtensionRegistrationException>(() => owner.RegisterTool(Tool(name: "bad\0name"))).Failure);
        Equal(ExtensionRegistrationFailure.InvalidDescriptor,
            Throws<ExtensionRegistrationException>(() => owner.RegisterCommand(Command() with { Description = "bad\0description" })).Failure);
        Equal(string.Join(',', before.Registrations), string.Join(',', registry.CaptureSnapshot().Registrations));
        Equal("{\"ok\":true}", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "shared", JsonData.Parse("{\"ok\":true}"))).ToString());
    }

    private static async Task CapturedObservations()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate();
        var release = Gate();
        var order = new ConcurrentQueue<string>();
        var calls = 0;
        IExtensionRegistration? secondHandle = null;
        RegistrationScope? other = null;
        var scope = await registry.ActivateAsync("first-owner", new DelegateExtension((entries, _) =>
        {
            entries.Observe(Observer("first", callback: async (_, _, _) =>
            {
                order.Enqueue("first");
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // This owner is also leased by this dispatch. Awaiting its disposal would wait on this callback.
                    var denied = Throws<ExtensionRegistrationException>(() => other!.DisposeAsync().AsTask());
                    Equal(ExtensionRegistrationFailure.ReentrantDisposal, denied.Failure);
                    entered.SetResult();
                    await release.Task;
                    entries.RegisterCommand(Command(name: "from-callback")); // No registry lock is held across the callback.
                }
            }));
            secondHandle = entries.Observe(Observer("second", callback: (_, _, _) =>
            { order.Enqueue("second"); return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        other = await registry.ActivateAsync("other-owner", new DelegateExtension((entries, _) =>
        {
            entries.Observe(Observer("other", callback: (_, _, _) => { order.Enqueue("other"); return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot();
        var dispatch = registry.DispatchObservationsAsync(captured, "notice", JsonData.Null).AsTask();
        await entered.Task;
        secondHandle!.Dispose();
        var replacement = scope.Observe(Observer("second", callback: (_, _, _) =>
        { order.Enqueue("replacement"); return ValueTask.CompletedTask; }));
        secondHandle!.Dispose();
        Equal(3, captured.Registrations.Length);
        False(dispatch.IsCompleted);
        release.SetResult();
        await dispatch;
        Equal("first,second,other", string.Join(',', order));
        await registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "notice", JsonData.Null);
        Equal("first,second,other,first,replacement,other", string.Join(',', order));
        Equal(ExtensionRegistrationFailure.StaleSnapshot,
            (await ThrowsAsync<ExtensionRegistrationException>(() => registry.DispatchObservationsAsync(captured, "notice", JsonData.Null).AsTask())).Failure);
        replacement.Dispose();
    }

    private static async Task DisposalSettlement()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate();
        var canceled = Gate();
        var callbackRelease = Gate();
        var cleanupEntered = Gate();
        var cleanupRelease = Gate();
        var cleanupCalls = 0;
        var scope = await registry.ActivateAsync("drain", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(callback: async (input, context, token) =>
            {
                Equal(entries.ExtensionLifetimeCancellationToken, context.ExtensionLifetimeCancellationToken);
                using var listener = token.Register(() => canceled.SetResult());
                entered.SetResult();
                await callbackRelease.Task;
                return input;
            }));
            return ValueTask.CompletedTask;
        }, async () => { cleanupCalls++; cleanupEntered.SetResult(); await cleanupRelease.Task; }));
        var captured = registry.CaptureSnapshot();
        var invocation = registry.InvokeToolAsync(captured, "native-tool", JsonData.Null).AsTask();
        await entered.Task;
        var first = scope.DisposeAsync().AsTask();
        var second = scope.DisposeAsync().AsTask();
        True(ReferenceEquals(first, second), "Concurrent scope disposal must share actual settlement.");
        await canceled.Task;
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        False(first.IsCompleted);
        False(cleanupEntered.Task.IsCompleted, "Plugin disposal waits for the admitted callback.");
        await Failure(() => registry.ActivateAsync("drain", new DelegateExtension((_, _) => ValueTask.CompletedTask)),
            ExtensionRegistrationFailure.DuplicateOwner, "drain", "activate");
        await Failure(() => registry.InvokeToolAsync(captured, "native-tool", JsonData.Null).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "drain", "dispatch-tool");
        callbackRelease.SetResult();
        Equal("null", (await invocation).ToString());
        await cleanupEntered.Task;
        False(first.IsCompleted);
        cleanupRelease.SetResult();
        await Task.WhenAll(first, second);
        Equal(1, cleanupCalls);
        Equal(ExtensionRegistrationFailure.InactiveScope,
            Throws<ExtensionRegistrationException>(() => scope.RegisterCommand(Command())).Failure);
    }

    private static async Task GenerationAndRegistryCleanup()
    {
        var registry = new ExtensionRegistry();
        RegistrationScope? scope = null;
        var observedSelfDenial = false;
        scope = await registry.ActivateAsync("generation", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(callback: (input, _, _) =>
            {
                Equal(ExtensionRegistrationFailure.ReentrantDisposal,
                    Throws<ExtensionRegistrationException>(() => scope!.DisposeAsync().AsTask()).Failure);
                Equal(ExtensionRegistrationFailure.ReentrantDisposal,
                    Throws<ExtensionRegistrationException>(() => registry.DisposeAsync().AsTask()).Failure);
                observedSelfDenial = true;
                return ValueTask.FromResult(input);
            }));
            return ValueTask.CompletedTask;
        }));
        var old = registry.CaptureSnapshot();
        await registry.InvokeToolAsync(old, "native-tool", JsonData.Null);
        True(observedSelfDenial);
        await scope.DisposeAsync();
        var replacementCalls = 0;
        var replacement = await registry.ActivateAsync("generation", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(callback: (input, _, _) => { replacementCalls++; return ValueTask.FromResult(input); }));
            return ValueTask.CompletedTask;
        }));
        True(replacement.OwnerGeneration > scope.OwnerGeneration);
        await Failure(() => registry.InvokeToolAsync(old, "native-tool", JsonData.Null).AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "generation", "dispatch-tool");
        Equal(0, replacementCalls);
        await registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.Null);
        Equal(1, replacementCalls);
        using (var canceledOperation = new CancellationTokenSource())
        {
            canceledOperation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => registry.InvokeToolAsync(registry.CaptureSnapshot(),
                "native-tool", JsonData.Null, operationToken: canceledOperation.Token).AsTask());
        }
        using (var canceledSession = new CancellationTokenSource())
        {
            canceledSession.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => registry.InvokeToolAsync(registry.CaptureSnapshot(),
                "native-tool", JsonData.Null, sessionToken: canceledSession.Token).AsTask());
        }
        Equal(1, replacementCalls);
        await replacement.DisposeAsync();

        var failureCalls = 0;
        await registry.ActivateAsync("callback-errors", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(Tool(callback: (_, _, _) => throw new InvalidOperationException("authored callback failure")));
            entries.Observe(Observer("fails", callback: (_, _, _) => { failureCalls++; throw new OperationCanceledException(); }));
            entries.Observe(Observer("later", callback: (_, _, _) => { failureCalls += 100; return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        await ThrowsAsync<InvalidOperationException>(() => registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.Null).AsTask());
        await ThrowsAsync<OperationCanceledException>(() => registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "notice", JsonData.Null).AsTask());
        Equal(1, failureCalls); // The prototype observation policy propagates failure; it is not an event-family reducer.

        var failedShutdown = await registry.ActivateAsync("failed-shutdown", new DelegateExtension((_, _) => ValueTask.CompletedTask,
            () => throw new InvalidOperationException("authored shutdown failure")));
        await Failure(() => failedShutdown.DisposeAsync().AsTask(), ExtensionRegistrationFailure.CleanupFailed, "failed-shutdown", "dispose");
        await Failure(() => registry.ActivateAsync("failed-shutdown", new DelegateExtension((_, _) => ValueTask.CompletedTask)),
            ExtensionRegistrationFailure.DuplicateOwner, "failed-shutdown", "activate");

        var cleanupEntered = Gate();
        var cleanupRelease = Gate();
        var cleanupFinished = false;
        await registry.ActivateAsync("throwing-cleanup", new DelegateExtension((_, _) => ValueTask.CompletedTask,
            () => throw new InvalidOperationException("authored disposal failure")));
        await registry.ActivateAsync("gated-cleanup", new DelegateExtension((_, _) => ValueTask.CompletedTask,
            async () => { cleanupEntered.SetResult(); await cleanupRelease.Task; cleanupFinished = true; }));
        var first = registry.DisposeAsync().AsTask();
        var second = registry.DisposeAsync().AsTask();
        True(ReferenceEquals(first, second));
        await cleanupEntered.Task;
        False(first.IsCompleted, "Another owner's failure cannot abandon gated cleanup.");
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        cleanupRelease.SetResult();
        await Failure(() => first, ExtensionRegistrationFailure.CleanupFailed, "registry", "dispose");
        await Failure(() => second, ExtensionRegistrationFailure.CleanupFailed, "registry", "dispose");
        True(cleanupFinished);
    }

    private static async Task ResourceBounds()
    {
        // owner 'o' + ID 't' + name 'n' + raw schema '{}' is exactly five UTF-16 characters.
        foreach (var budget in new[] { 4, 5 })
        {
            await using var registry = new ExtensionRegistry(new() { MaximumMetadataCharacters = budget });
            var activation = registry.ActivateAsync("o", new DelegateExtension((entries, _) =>
            { entries.RegisterTool(Tool("t", "n")); return ValueTask.CompletedTask; }));
            if (budget == 4)
            {
                await Failure(() => activation, ExtensionRegistrationFailure.LimitExceeded, "o", "register-tool");
                Equal(0, registry.CaptureSnapshot().Registrations.Length);
            }
            else
            {
                await activation;
                Equal("{}", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "n", JsonData.EmptyObject)).ToString());
            }
        }

        await using (var registry = new ExtensionRegistry(new()
        { MaximumRegistrations = 1, MaximumRegistrationsPerOwner = 1, MaximumConcurrentDispatches = 1 }))
        {
            var entered = Gate();
            var release = Gate();
            var effects = 0;
            IExtensionRegistration? handle = null;
            var scope = await registry.ActivateAsync("bounded", new DelegateExtension((entries, _) =>
            {
                handle = entries.RegisterTool(Tool(callback: async (input, _, _) =>
                { effects++; entered.TrySetResult(); await release.Task; return input; }));
                return ValueTask.CompletedTask;
            }));
            var call = registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.Null).AsTask();
            await entered.Task;
            await Failure(() => registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.Null).AsTask(),
                ExtensionRegistrationFailure.LimitExceeded, "registry", "dispatch-tool");
            Equal(1, effects);
            handle!.Dispose();
            Equal(ExtensionRegistrationFailure.LimitExceeded,
                Throws<ExtensionRegistrationException>(() => scope.RegisterTool(Tool("new", "replacement"))).Failure);
            release.SetResult();
            await call;
            scope.RegisterTool(Tool("new", "replacement"));
            Equal("null", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "replacement", JsonData.Null)).ToString());
        }

        await using (var registry = new ExtensionRegistry(new() { MaximumOwners = 3, MaximumRegistrations = 2 }))
        {
            var firstRelease = Gate();
            var secondRelease = Gate();
            var firstEntered = Gate();
            var secondEntered = Gate();
            Task<RegistrationScope> Start(string owner, TaskCompletionSource entered, TaskCompletionSource release) => registry.ActivateAsync(owner,
                new DelegateExtension(async (entries, token) =>
                { entries.RegisterTool(Tool(name: owner)); entered.SetResult(); await release.Task.WaitAsync(token); }));
            var first = Start("one", firstEntered, firstRelease);
            var second = Start("two", secondEntered, secondRelease);
            await Task.WhenAll(firstEntered.Task, secondEntered.Task);
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            await Failure(() => registry.ActivateAsync("three", new DelegateExtension((entries, _) =>
            { entries.RegisterTool(Tool(name: "one")); return ValueTask.CompletedTask; })),
                ExtensionRegistrationFailure.DuplicateName, "three", "register-tool");
            await Failure(() => registry.ActivateAsync("three", new DelegateExtension((entries, _) =>
            { entries.RegisterTool(Tool(name: "three")); return ValueTask.CompletedTask; })),
                ExtensionRegistrationFailure.LimitExceeded, "three", "register-tool");
            secondRelease.SetResult();
            await second;
            Equal("two", string.Join(',', registry.CaptureSnapshot().Registrations.Select(item => item.Name)));
            firstRelease.SetResult();
            await first;
            Equal(2, registry.CaptureSnapshot().Registrations.Length);
            Equal("one,two", string.Join(',', registry.CaptureSnapshot().Registrations.Select(item => item.Name)));
        }

        await using (var registry = new ExtensionRegistry(new() { MaximumOwners = 1, MaximumRegistrationsPerOwner = 1 }))
        {
            var scope = await registry.ActivateAsync("only", new DelegateExtension((entries, _) =>
            { entries.RegisterTool(Tool()); return ValueTask.CompletedTask; }));
            await Failure(() => registry.ActivateAsync("excess", new DelegateExtension((_, _) => ValueTask.CompletedTask)),
                ExtensionRegistrationFailure.LimitExceeded, "excess", "activate");
            Equal(ExtensionRegistrationFailure.LimitExceeded,
                Throws<ExtensionRegistrationException>(() => scope.RegisterCommand(Command())).Failure);
            Equal("null", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.Null)).ToString());
        }

        await using (var registry = new ExtensionRegistry(new() { MaximumJsonCharacters = 2, MaximumJsonDepth = 2 }))
        {
            var scope = await registry.ActivateAsync("json", new DelegateExtension((entries, _) =>
            { entries.RegisterTool(Tool()); return ValueTask.CompletedTask; }));
            Equal("{}", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.EmptyObject)).ToString());
            await Failure(() => registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", JsonData.Parse("{ }")).AsTask(),
                ExtensionRegistrationFailure.InvalidDescriptor, "registry", "dispatch-tool");
            Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                Throws<ExtensionRegistrationException>(() => scope.RegisterTool(Tool("larger", "larger", schema: JsonData.Parse("{ }")))).Failure);
            scope.RegisterTool(Tool("output", "output", callback: (_, _, _) => ValueTask.FromResult(JsonData.Parse("{ }"))));
            await Failure(() => registry.InvokeToolAsync(registry.CaptureSnapshot(), "output", JsonData.EmptyObject).AsTask(),
                ExtensionRegistrationFailure.InvalidDescriptor, "json", "tool-result");
        }

        await using (var registry = new ExtensionRegistry(new() { MaximumJsonDepth = 2 }))
        {
            var scope = await registry.ActivateAsync("strict", new DelegateExtension((_, _) => ValueTask.CompletedTask));
            foreach (var raw in new[] { "{\"type\":\"object\",}", "{ /*retained*/ \"type\":\"object\" }", "{\"n\":1e400}", "{\"s\":\"\\ud800\"}", "{\"a\":{\"b\":{}}}" })
            {
                using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var retained = JsonData.FromElement(document.RootElement);
                var retainedRaw = retained.ToString();
                Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                    Throws<ExtensionRegistrationException>(() => scope.RegisterTool(Tool(schema: retained))).Failure);
                Equal(retainedRaw, retained.ToString());
                Equal(0, registry.CaptureSnapshot().Registrations.Length);
            }
            using var duplicate = JsonDocument.Parse("{\"x\":1,\"x\":2}");
            Throws<JsonException>(() => JsonData.FromElement(duplicate.RootElement));
            Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                Throws<ExtensionRegistrationException>(() => scope.RegisterTool(Tool(schema: JsonData.Null))).Failure);
            var original = "{ \"nested\":{}, \"number\":1.0, \"big\":9007199254740993, \"nil\":null, \"\\u0000\":\"x\\u0000\", \"unicode\":\"\\ud83d\\ude00\" }";
            var owned = JsonData.Parse(original);
            scope.RegisterTool(Tool(schema: owned));
            var returned = await registry.InvokeToolAsync(registry.CaptureSnapshot(), "native-tool", owned);
            True(ReferenceEquals(owned, returned));
            Equal(original, returned.ToString());
            Equal(JsonValueKind.Null, returned.Value.GetProperty("nil").ValueKind);
            False(returned.Value.TryGetProperty("missing", out _));
        }

        await using (var registry = new ExtensionRegistry(new()
        {
            MaximumIdentifierCharacters = 4, MaximumDescriptionCharacters = 2,
            ReservedToolNames = ["read"], ReservedCommandNames = ["quit"]
        }))
        {
            var scope = await registry.ActivateAsync("own", new DelegateExtension((entries, _) =>
            { entries.RegisterTool(Tool("id", "tool") with { Description = "ok" }); return ValueTask.CompletedTask; }));
            Equal("null", (await registry.InvokeToolAsync(registry.CaptureSnapshot(), "tool", JsonData.Null)).ToString());
            Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                Throws<ExtensionRegistrationException>(() => scope.RegisterTool(Tool("new", "longer"))).Failure);
            Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                Throws<ExtensionRegistrationException>(() => scope.RegisterCommand(Command("new", "cmd") with { Description = "bad" })).Failure);
            Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                Throws<ExtensionRegistrationException>(() => scope.RegisterCommand(Command("new", "cmd") with { Description = "\ud800" })).Failure);
        }
    }

    private static void Context(IExtensionContext context, RegistrationScope scope, CancellationToken operation, CancellationToken session)
    {
        Equal(scope.OwnerId, context.OwnerId);
        Equal(scope.OwnerGeneration, context.OwnerGeneration);
        Equal(operation, context.OperationCancellationToken);
        Equal(session, context.SessionCancellationToken);
        Equal(scope.ExtensionLifetimeCancellationToken, context.ExtensionLifetimeCancellationToken);
        False(operation == session);
        False(operation == scope.ExtensionLifetimeCancellationToken);
        False(session == scope.ExtensionLifetimeCancellationToken);
    }

    private static async Task Failure(Func<Task> action, ExtensionRegistrationFailure expected, string owner, string operation)
    {
        var error = await ThrowsAsync<ExtensionRegistrationException>(action);
        Equal(expected, error.Failure);
        Equal(owner, error.OwnerId);
        Equal(operation, error.Operation);
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
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

    private sealed class DelegateExtension(
        Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => initialize(registry, cancellationToken);
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }

}
