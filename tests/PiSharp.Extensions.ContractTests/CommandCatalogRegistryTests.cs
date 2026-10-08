using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class CommandCatalogRegistryTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("command catalogs contain owned metadata and retain the admitted revision", CatalogSnapshots),
        ("completion preserves owned arrays, explicit null and absent callbacks", CompletionValues),
        ("completion denies removed, replaced, foreign and disposed admissions", CompletionAdmission),
        ("owner disposal cancels and joins a held completion before plugin cleanup", CompletionOwnerDisposal),
        ("caller cancellation retains completion admission until callback cleanup ends", CompletionCallerCancellation),
        ("completion rejects invalid scalars, prefix bounds and invalid result values", CompletionValidation),
        ("command provenance and completion callbacks obey descriptor admission bounds", DescriptorValidation),
        ("command contexts preserve catalog and UI identity through awaited scope cleanup", CatalogUiContext)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // This deliberately uses the existing four-argument descriptor constructor.
    private static ExtensionCommandDescriptor Command(string id, string name, string description = "",
        ExtensionCommandCallback? execute = null, ExtensionCommandCompletionCallback? complete = null,
        string? sourcePath = null) =>
        new(id, name, description, execute ?? ((_, _, _) => ValueTask.CompletedTask))
        { GetArgumentCompletionsAsync = complete, SourcePath = sourcePath };

    private static async Task CatalogSnapshots()
    {
        await using var registry = new ExtensionRegistry();
        var empty = registry.CaptureSnapshot();
        Equal(0, empty.Commands.Length);
        Equal(JsonValueKind.Array, empty.CommandCatalog.Value.ValueKind);
        Equal(0, empty.CommandCatalog.Value.GetArrayLength());
        ExtensionRegistrySnapshot? expected = null;
        IExtensionCommandCatalogContext? received = null;
        const string path = "extensions/commands \"local\"\\catalog-\ud83d\ude00.cs";
        var owner = await registry.ActivateAsync("catalog-owner", new DelegateExtension((entries, _) =>
        {
            entries.RegisterTool(new("tool", "tool-only", "", JsonData.EmptyObject,
                (value, _, _) => ValueTask.FromResult(value)));
            entries.Observe(new("observation", "observation-only", (_, _, _) => ValueTask.CompletedTask));
            entries.RegisterCommand(Command("catalog", "native-catalog", "List native commands",
                execute: (_, context, _) =>
                {
                    var admitted = expected ?? throw new InvalidOperationException("Expected catalog revision was not assigned.");
                    received = Catalog(context);
                    Equal(admitted.Revision, received.CommandCatalogRevision);
                    True(ReferenceEquals(admitted.CommandCatalog, received.CommandCatalog));
                    Equal(admitted.Commands.Length, received.CommandCatalog.Value.GetArrayLength());
                    False(context is IExtensionToolContext);
                    return ValueTask.CompletedTask;
                }, complete: (_, _) => ValueTask.FromResult(JsonData.Parse("[]")), sourcePath: path));
            entries.RegisterCommand(Command("plain", "plain-command"));
            return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot();
        var raw = captured.CommandCatalog.ToString();
        Equal(2, captured.Commands.Length);
        Equal(4, captured.Registrations.Length);
        Equal("native-catalog,plain-command", string.Join(',', captured.Commands.Select(row => row.Name)));
        var self = captured.Commands[0];
        Equal(owner.OwnerId, self.OwnerId);
        Equal(owner.OwnerGeneration, self.OwnerGeneration);
        Equal("catalog", self.RegistrationId);
        Equal("List native commands", self.Description);
        Equal(path, self.SourcePath);
        True(self.HasArgumentCompletions);
        False(captured.Commands[1].HasArgumentCompletions);
        Equal<string?>(null, captured.Commands[1].SourcePath);
        Equal(2, captured.CommandCatalog.Value.GetArrayLength());
        CatalogRow(captured.CommandCatalog.Value[0], owner, "catalog", "native-catalog", self.Description, path);
        CatalogRow(captured.CommandCatalog.Value[1], owner, "plain", "plain-command", "", null);
        Equal(0, empty.Commands.Length);
        Equal("[]", empty.CommandCatalog.ToString());

        owner.RegisterCommand(Command("late", "late-command", "Registered after capture", sourcePath: "late.cs"));
        var secondOwner = await registry.ActivateAsync("second-owner", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("other", "other-command", sourcePath: "other.cs"));
            return ValueTask.CompletedTask;
        }));
        var current = registry.CaptureSnapshot();
        True(current.Revision > captured.Revision);
        Equal("native-catalog,plain-command,late-command,other-command",
            string.Join(',', current.Commands.Select(row => row.Name)));
        CatalogRow(current.CommandCatalog.Value[3], secondOwner, "other", "other-command", "", "other.cs");
        Equal(2, captured.Commands.Length);
        Equal(raw, captured.CommandCatalog.ToString());

        // An unrelated publication does not revoke this exact still-live command admission.
        expected = captured;
        await registry.InvokeCommandAsync(captured, "native-catalog", JsonData.Null);
        True(received is not null);
        Equal(2, received!.CommandCatalog.Value.GetArrayLength());
        expected = current;
        await registry.InvokeCommandAsync(current, "native-catalog", JsonData.Null);
        Equal(4, received!.CommandCatalog.Value.GetArrayLength());
        Equal(raw, captured.CommandCatalog.ToString());
    }

    private static async Task CompletionValues()
    {
        var ui = new UiProbeProvider();
        await using var registry = new ExtensionRegistry(uiProvider: ui);
        JsonData choices;
        const string raw = "[ {\"value\":\"hello\",\"label\":\"Hello\",\"description\":null,\"opaque\":\"\\u0000\",\"n\":1.0,\"big\":9007199254740993} ]";
        using (var document = JsonDocument.Parse(raw)) choices = JsonData.FromElement(document.RootElement);
        var prefixes = new List<string>();
        var nullCalls = 0;
        var frameCalls = 0;
        RegistrationScope? owner = null;
        owner = await registry.ActivateAsync("values", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("array", "array-command", complete: (prefix, token) =>
            {
                prefixes.Add(prefix);
                True(token.CanBeCanceled);
                return ValueTask.FromResult(choices);
            }));
            entries.RegisterCommand(Command("empty", "empty-command",
                complete: (_, _) => ValueTask.FromResult(JsonData.Parse("[]"))));
            entries.RegisterCommand(Command("null", "null-command", complete: (_, _) =>
            { nullCalls++; return ValueTask.FromResult(JsonData.Null); }));
            entries.RegisterCommand(Command("absent", "absent-command"));
            entries.RegisterCommand(Command("frame", "frame-command", complete: (_, _) =>
            {
                Equal(ExtensionRegistrationFailure.ReentrantDisposal,
                    Throws<ExtensionRegistrationException>(() => owner!.DisposeAsync().AsTask()).Failure);
                Equal(ExtensionRegistrationFailure.ReentrantDisposal,
                    Throws<ExtensionRegistrationException>(() => registry.DisposeAsync().AsTask()).Failure);
                frameCalls++;
                return ValueTask.FromResult(JsonData.Null);
            }));
            return ValueTask.CompletedTask;
        }));
        var snapshot = registry.CaptureSnapshot();
        const string prefix = "\0\t\"\\\ud83d\ude00";
        var result = await registry.CompleteCommandAsync(snapshot, "array-command", prefix);
        True(ReferenceEquals(choices, result), "Completion must return the owned callback value without a lossy projection.");
        Equal(raw, result.ToString());
        Equal(prefix, prefixes.Single());
        Equal(JsonValueKind.Null, result.Value[0].GetProperty("description").ValueKind);
        Equal("\0", result.Value[0].GetProperty("opaque").GetString());
        Equal("9007199254740993", result.Value[0].GetProperty("big").GetRawText());
        var empty = await registry.CompleteCommandAsync(snapshot, "empty-command", "");
        Equal(JsonValueKind.Array, empty.Value.ValueKind);
        Equal(0, empty.Value.GetArrayLength());
        Equal(JsonValueKind.Null, (await registry.CompleteCommandAsync(snapshot, "null-command", "")).Value.ValueKind);
        Equal(1, nullCalls);
        Equal(JsonValueKind.Null, (await registry.CompleteCommandAsync(snapshot, "absent-command", "")).Value.ValueKind);
        True(snapshot.Commands.Single(row => row.Name == "null-command").HasArgumentCompletions);
        False(snapshot.Commands.Single(row => row.Name == "absent-command").HasArgumentCompletions);
        await registry.CompleteCommandAsync(snapshot, "frame-command", "");
        Equal(1, frameCalls);
        Equal(0, ui.OpenCalls);
        await owner.DisposeAsync(); // The callback frame must have been released after completion.
    }

    private static async Task CompletionAdmission()
    {
        await using var registry = new ExtensionRegistry();
        await using var foreign = new ExtensionRegistry();
        var before = registry.CaptureSnapshot();
        IExtensionRegistration? handle = null;
        var originalCalls = 0;
        var replacementCalls = 0;
        var owner = await registry.ActivateAsync("admission", new DelegateExtension((entries, _) =>
        {
            handle = entries.RegisterCommand(Command("work", "work-command", complete: (_, _) =>
            { originalCalls++; return ValueTask.FromResult(JsonData.Parse("[]")); }));
            return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot();
        await Failure(() => registry.CompleteCommandAsync(before, "work-command", "").AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "registry", "complete-command");
        owner.RegisterCommand(Command("unrelated", "unrelated-command"));
        await registry.CompleteCommandAsync(captured, "work-command", "");
        Equal(1, originalCalls);
        handle!.Dispose();
        owner.RegisterCommand(Command("work", "work-command", complete: (_, _) =>
        { replacementCalls++; return ValueTask.FromResult(JsonData.Parse("[]")); }));
        await Failure(() => registry.CompleteCommandAsync(captured, "work-command", "").AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "admission", "complete-command");
        Equal(0, replacementCalls);
        await registry.CompleteCommandAsync(registry.CaptureSnapshot(), "work-command", "");
        Equal(1, replacementCalls);

        await foreign.ActivateAsync("admission", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("work", "work-command", complete: (_, _) =>
            { replacementCalls += 100; return ValueTask.FromResult(JsonData.Null); }));
            return ValueTask.CompletedTask;
        }));
        await Failure(() => registry.CompleteCommandAsync(foreign.CaptureSnapshot(), "work-command", "").AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "registry", "complete-command");
        Equal(1, replacementCalls);
        var previousGeneration = registry.CaptureSnapshot();
        await owner.DisposeAsync();
        await Failure(() => registry.CompleteCommandAsync(previousGeneration, "work-command", "").AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "admission", "complete-command");
        var next = await registry.ActivateAsync("admission", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("work", "work-command", complete: (_, _) =>
            { replacementCalls++; return ValueTask.FromResult(JsonData.Null); }));
            return ValueTask.CompletedTask;
        }));
        True(next.OwnerGeneration > owner.OwnerGeneration);
        await Failure(() => registry.CompleteCommandAsync(previousGeneration, "work-command", "").AsTask(),
            ExtensionRegistrationFailure.StaleSnapshot, "admission", "complete-command");
        Equal(1, replacementCalls);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => registry.CompleteCommandAsync(
                registry.CaptureSnapshot(), "work-command", "", operationToken: canceled.Token).AsTask());
            await ThrowsAsync<OperationCanceledException>(() => registry.CompleteCommandAsync(
                registry.CaptureSnapshot(), "work-command", "", sessionToken: canceled.Token).AsTask());
        }
        Equal(1, replacementCalls);
        var last = registry.CaptureSnapshot();
        await registry.DisposeAsync();
        await Failure(() => registry.CompleteCommandAsync(last, "work-command", "").AsTask(),
            ExtensionRegistrationFailure.InactiveScope, "registry", "complete-command");
        Equal(1, originalCalls);
        Equal(1, replacementCalls);
    }

    private static async Task CompletionOwnerDisposal()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate();
        var canceled = Gate();
        var callbackRelease = Gate();
        var cleanupEntered = Gate();
        var cleanupRelease = Gate();
        var callbackCleaned = false;
        var cleanupCalls = 0;
        var owner = await registry.ActivateAsync("draining", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("work", "held-command", complete: async (_, token) =>
            {
                using var listener = token.Register(() => canceled.TrySetResult());
                try
                {
                    entered.TrySetResult();
                    await callbackRelease.Task;
                    return JsonData.Parse("[]");
                }
                finally { callbackCleaned = true; }
            }));
            return ValueTask.CompletedTask;
        }, async () =>
        {
            cleanupCalls++;
            cleanupEntered.TrySetResult();
            await cleanupRelease.Task;
        }));
        var captured = registry.CaptureSnapshot();
        var completion = registry.CompleteCommandAsync(captured, "held-command", "prefix").AsTask();
        Task? disposal = null;
        try
        {
            await entered.Task;
            disposal = owner.DisposeAsync().AsTask();
            var second = owner.DisposeAsync().AsTask();
            True(ReferenceEquals(disposal, second));
            await canceled.Task;
            True(owner.ExtensionLifetimeCancellationToken.IsCancellationRequested);
            Equal(0, registry.CaptureSnapshot().Commands.Length);
            Equal("[]", registry.CaptureSnapshot().CommandCatalog.ToString());
            False(completion.IsCompleted, "Cancellation cannot abandon the held callback.");
            False(disposal.IsCompleted);
            False(cleanupEntered.Task.IsCompleted, "Plugin disposal must wait for completion callback cleanup.");
            await Failure(() => registry.CompleteCommandAsync(captured, "held-command", "").AsTask(),
                ExtensionRegistrationFailure.StaleSnapshot, "draining", "complete-command");
            callbackRelease.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(() => completion);
            True(callbackCleaned);
            await cleanupEntered.Task;
            False(disposal.IsCompleted, "Scope disposal must also join plugin cleanup.");
            cleanupRelease.TrySetResult();
            await Task.WhenAll(disposal, second);
            Equal(1, cleanupCalls);
        }
        finally
        {
            callbackRelease.TrySetResult();
            cleanupRelease.TrySetResult();
            if (disposal is not null) await disposal;
        }
        var replacement = await registry.ActivateAsync("draining", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("work", "held-command"));
            return ValueTask.CompletedTask;
        }));
        True(replacement.OwnerGeneration > owner.OwnerGeneration);
        Equal(JsonValueKind.Null,
            (await registry.CompleteCommandAsync(registry.CaptureSnapshot(), "held-command", "")).Value.ValueKind);
    }

    private static async Task CompletionCallerCancellation()
    {
        foreach (var cancelSession in new[] { false, true })
        {
            await using var registry = new ExtensionRegistry(new()
            { MaximumConcurrentDispatches = 1, MaximumRegistrations = 1, MaximumRegistrationsPerOwner = 1 });
            using var operation = new CancellationTokenSource();
            using var session = new CancellationTokenSource();
            var entered = Gate();
            var canceled = Gate();
            var release = Gate();
            var callbackCleaned = false;
            var cleanupCalls = 0;
            var callbackCalls = 0;
            IExtensionRegistration? handle = null;
            var owner = await registry.ActivateAsync("caller-cancel", new DelegateExtension((entries, _) =>
            {
                handle = entries.RegisterCommand(Command("work", "cancel-command", complete: async (_, token) =>
                {
                    callbackCalls++;
                    using var listener = token.Register(() => canceled.TrySetResult());
                    try
                    {
                        entered.TrySetResult();
                        await release.Task;
                        return JsonData.Parse("[]");
                    }
                    finally { callbackCleaned = true; }
                }));
                return ValueTask.CompletedTask;
            }, () => { cleanupCalls++; return ValueTask.CompletedTask; }));
            var captured = registry.CaptureSnapshot();
            var completion = registry.CompleteCommandAsync(captured, "cancel-command", "",
                operation.Token, session.Token).AsTask();
            try
            {
                await entered.Task;
                (cancelSession ? session : operation).Cancel();
                await canceled.Task;
                False(completion.IsCompleted);
                False(callbackCleaned);
                False(owner.ExtensionLifetimeCancellationToken.IsCancellationRequested);
                Equal(0, cleanupCalls);
                await Failure(() => registry.CompleteCommandAsync(captured, "cancel-command", "").AsTask(),
                    ExtensionRegistrationFailure.LimitExceeded, "registry", "complete-command");
                Equal(1, callbackCalls);
                handle!.Dispose();
                Equal(0, registry.CaptureSnapshot().Commands.Length);
                Equal(ExtensionRegistrationFailure.LimitExceeded,
                    Throws<ExtensionRegistrationException>(() =>
                        owner.RegisterCommand(Command("work", "cancel-command"))).Failure);
                release.TrySetResult();
                var error = await ThrowsAsync<OperationCanceledException>(() => completion);
                True(error.CancellationToken.IsCancellationRequested);
                True(callbackCleaned);
                False(owner.ExtensionLifetimeCancellationToken.IsCancellationRequested);
                Equal(0, cleanupCalls);
                owner.RegisterCommand(Command("work", "cancel-command",
                    complete: (_, _) => ValueTask.FromResult(JsonData.Parse("[]"))));
                Equal("[]", (await registry.CompleteCommandAsync(registry.CaptureSnapshot(), "cancel-command", "")).ToString());
                await owner.DisposeAsync();
                Equal(1, cleanupCalls);
            }
            finally { release.TrySetResult(); }
        }
    }

    private static async Task CompletionValidation()
    {
        await using (var registry = new ExtensionRegistry(new()
        { MaximumJsonCharacters = 7, MaximumJsonDepth = 2, MaximumConcurrentDispatches = 1 }))
        {
            var result = JsonData.Parse("[1,2,3]");
            Exception? authoredFailure = null;
            var prefixes = new List<string>();
            await registry.ActivateAsync("validation", new DelegateExtension((entries, _) =>
            {
                entries.RegisterCommand(Command("complete", "validation-command", complete: (prefix, _) =>
                {
                    prefixes.Add(prefix);
                    if (authoredFailure is not null) throw authoredFailure;
                    return ValueTask.FromResult(result);
                }));
                return ValueTask.CompletedTask;
            }));
            var snapshot = registry.CaptureSnapshot();
            foreach (var prefix in new[] { "", "1234567", "\ud83d\ude00", "\0" })
                Equal("[1,2,3]", (await registry.CompleteCommandAsync(snapshot, "validation-command", prefix)).ToString());
            Equal("\0", prefixes.Last());
            foreach (var invalid in new[] { null!, "12345678", "\ud800", "\udc00", "\ud800x", "x\udc00", "\ud800\ud800" })
            {
                await Failure(() => registry.CompleteCommandAsync(snapshot, "validation-command", invalid).AsTask(),
                    ExtensionRegistrationFailure.InvalidDescriptor, "registry", "complete-command");
                Equal(4, prefixes.Count);
                Equal(snapshot.Revision, registry.CaptureSnapshot().Revision);
            }
            // The over-limit result preserves an extra raw character; depth and nonfinite values fit the character budget.
            foreach (var rejected in new JsonData?[]
            {
                JsonData.EmptyObject, JsonData.Parse("\"x\""), JsonData.Parse("true"), JsonData.Parse("1"),
                null, JsonData.Parse("[1,2,3 ]"), JsonData.Parse("[[[]]]"), JsonData.Parse("[1e400]")
            })
            {
                result = rejected!;
                await Failure(() => registry.CompleteCommandAsync(snapshot, "validation-command", "").AsTask(),
                    ExtensionRegistrationFailure.InvalidDescriptor, "validation", "command-completion");
                result = JsonData.Parse("[[]]");
                Equal("[[]]", (await registry.CompleteCommandAsync(snapshot, "validation-command", "")).ToString());
            }
            authoredFailure = new InvalidOperationException("Authored completion failure.");
            True(ReferenceEquals(authoredFailure,
                await ThrowsAsync<InvalidOperationException>(() =>
                    registry.CompleteCommandAsync(snapshot, "validation-command", "").AsTask())));
            authoredFailure = null;
            result = JsonData.Null;
            Equal("null", (await registry.CompleteCommandAsync(snapshot, "validation-command", "")).ToString());
            Equal(snapshot.Revision, registry.CaptureSnapshot().Revision);
        }

        await using (var registry = new ExtensionRegistry(new() { MaximumJsonCharacters = 64 }))
        {
            var result = JsonData.Parse("[\"\\ud800\"]");
            await registry.ActivateAsync("scalar-result", new DelegateExtension((entries, _) =>
            {
                entries.RegisterCommand(Command("complete", "scalar-command",
                    complete: (_, _) => ValueTask.FromResult(result)));
                return ValueTask.CompletedTask;
            }));
            await Failure(() => registry.CompleteCommandAsync(registry.CaptureSnapshot(), "scalar-command", "").AsTask(),
                ExtensionRegistrationFailure.InvalidDescriptor, "scalar-result", "command-completion");
            result = JsonData.Parse("[\"\\ud83d\\ude00\"]");
            Equal("\ud83d\ude00",
                (await registry.CompleteCommandAsync(registry.CaptureSnapshot(), "scalar-command", "")).Value[0].GetString());
        }
    }

    private static async Task DescriptorValidation()
    {
        // Owner 'o' + registration 'c' + name 'n' + source path 'pq' consumes exactly five metadata characters.
        foreach (var budget in new[] { 4, 5 })
        {
            await using var registry = new ExtensionRegistry(new() { MaximumMetadataCharacters = budget });
            var activation = registry.ActivateAsync("o", new DelegateExtension((entries, _) =>
            {
                entries.RegisterCommand(Command("c", "n", sourcePath: "pq"));
                return ValueTask.CompletedTask;
            }));
            if (budget == 4)
            {
                await Failure(() => activation, ExtensionRegistrationFailure.LimitExceeded, "o", "register-command");
                Equal(0, registry.CaptureSnapshot().Commands.Length);
            }
            else
            {
                var owner = await activation;
                CatalogRow(registry.CaptureSnapshot().CommandCatalog.Value[0], owner, "c", "n", "", "pq");
            }
        }

        await using (var registry = new ExtensionRegistry(new() { MaximumDescriptionCharacters = 2 }))
        {
            var calls = 0;
            ExtensionCommandCompletionCallback callback = (_, _) =>
            { calls++; return ValueTask.FromResult(JsonData.Null); };
            var owner = await registry.ActivateAsync("descriptor", new DelegateExtension((entries, _) =>
            {
                entries.RegisterCommand(Command("valid", "valid-command", complete: callback, sourcePath: "pq"));
                return ValueTask.CompletedTask;
            }));
            var captured = registry.CaptureSnapshot();
            foreach (var path in new[] { "pqr", "\0", "\ud800", "\udc00" })
            {
                var error = Throws<ExtensionRegistrationException>(() =>
                    owner.RegisterCommand(Command("invalid", "invalid-command", sourcePath: path)));
                Equal(ExtensionRegistrationFailure.InvalidDescriptor, error.Failure);
                Equal("descriptor", error.OwnerId);
                Equal("register-command", error.Operation);
                Equal(captured.Revision, registry.CaptureSnapshot().Revision);
            }
            Equal(ExtensionRegistrationFailure.InvalidDescriptor,
                Throws<ExtensionRegistrationException>(() =>
                    owner.RegisterCommand(Command("multicast", "multicast-command", complete: callback + callback))).Failure);
            Equal(0, calls);
            Equal(captured.Revision, registry.CaptureSnapshot().Revision);
            await registry.CompleteCommandAsync(captured, "valid-command", "");
            Equal(1, calls);
        }
    }

    private static async Task CatalogUiContext()
    {
        var ui = new UiProbeProvider { HoldClose = true };
        await using var registry = new ExtensionRegistry(uiProvider: ui);
        using var operation = new CancellationTokenSource();
        using var session = new CancellationTokenSource();
        var entered = Gate();
        var release = Gate();
        ExtensionRegistrySnapshot? captured = null;
        RegistrationScope? owner = null;
        IExtensionCommandContext? retained = null;
        owner = await registry.ActivateAsync("ui-catalog", new DelegateExtension((entries, _) =>
        {
            entries.RegisterCommand(Command("catalog", "ui-catalog-command", "UI",
                execute: async (_, context, token) =>
                {
                    retained = context;
                    entered.TrySetResult();
                    await release.Task;
                    var catalog = Catalog(context);
                    var feature = Ui(context);
                    var admittedOwner = owner ?? throw new InvalidOperationException("Command owner was not assigned.");
                    var admittedSnapshot = captured ?? throw new InvalidOperationException("Command snapshot was not assigned.");
                    True(ReferenceEquals(context, ui.OpenedContext));
                    True(ReferenceEquals(feature.Ui, ui.OpenedUi));
                    True(ReferenceEquals(ui.Capabilities, feature.Ui.Capabilities));
                    Equal(ExtensionUiMode.Rpc, feature.Ui.Capabilities.Mode);
                    Equal(3L, feature.Ui.Capabilities.ConnectionGeneration);
                    Equal(7L, feature.Ui.Capabilities.SessionGeneration);
                    True(feature.Ui.Capabilities.Supports(ExtensionUiFeature.Notify));
                    Equal(admittedOwner.OwnerId, context.OwnerId);
                    Equal(admittedOwner.OwnerGeneration, context.OwnerGeneration);
                    Equal(operation.Token, context.OperationCancellationToken);
                    Equal(session.Token, context.SessionCancellationToken);
                    Equal(admittedOwner.ExtensionLifetimeCancellationToken, context.ExtensionLifetimeCancellationToken);
                    Equal(admittedSnapshot.Revision, catalog.CommandCatalogRevision);
                    True(ReferenceEquals(admittedSnapshot.CommandCatalog, catalog.CommandCatalog));
                    Equal(1, catalog.CommandCatalog.Value.GetArrayLength());
                    CatalogRow(catalog.CommandCatalog.Value[0], admittedOwner, "catalog", "ui-catalog-command", "UI", "ui.cs");
                    False(context is IExtensionToolContext);
                    var published = await feature.Ui.PublishAsync(new ExtensionUiNotify(
                        catalog.CommandCatalog.Value[0].GetProperty("name").GetString()!), token);
                    Equal(ExtensionUiOutcomeKind.Value, published.Kind);
                    Equal(ExtensionUiPublication.Published, published.Value);
                }, sourcePath: "ui.cs"));
            return ValueTask.CompletedTask;
        }));
        captured = registry.CaptureSnapshot();
        var invocation = registry.InvokeCommandAsync(captured, "ui-catalog-command", JsonData.Null,
            operation.Token, session.Token).AsTask();
        try
        {
            await entered.Task;
            owner.RegisterCommand(Command("late", "late-ui-command"));
            True(registry.CaptureSnapshot().Revision > captured.Revision);
            release.TrySetResult();
            await ui.CloseEntered.Task;
            False(invocation.IsCompleted, "Command admission must await the actual UI scope disposal receipt.");
            Equal(1, Catalog(retained!).CommandCatalog.Value.GetArrayLength());
            Equal(captured.Revision, Catalog(retained!).CommandCatalogRevision);
            ui.CloseRelease.TrySetResult();
            await invocation;
            Equal(1, ui.OpenCalls);
            Equal(1, ui.CloseCalls);
            Equal(1, ui.Publications);
            Equal("ui-catalog-command", ui.LastNotice!.Message);
            var stale = await Ui(retained!).Ui.PublishAsync(new ExtensionUiNotify("after callback"));
            Equal(ExtensionUiOutcomeKind.Unavailable, stale.Kind);
            Equal<ExtensionUiUnavailableReason?>(ExtensionUiUnavailableReason.StaleContext, stale.UnavailableReason);
            Equal("null", (await registry.CompleteCommandAsync(captured, "ui-catalog-command", "")).ToString());
            Equal(1, ui.OpenCalls);
        }
        finally
        {
            release.TrySetResult();
            ui.CloseRelease.TrySetResult();
        }
    }

    private static IExtensionCommandCatalogContext Catalog(IExtensionCommandContext context) =>
        context as IExtensionCommandCatalogContext ?? throw new InvalidOperationException("Command context lost its catalog feature.");
    private static IExtensionUiContext Ui(IExtensionCommandContext context) =>
        context as IExtensionUiContext ?? throw new InvalidOperationException("Command context lost its UI feature.");

    private static void CatalogRow(JsonElement row, RegistrationScope owner, string registrationId,
        string name, string description, string? path)
    {
        Equal(JsonValueKind.Object, row.ValueKind);
        Equal(name, row.GetProperty("name").GetString());
        Equal(description, row.GetProperty("description").GetString());
        Equal("extension", row.GetProperty("source").GetString());
        Equal(owner.OwnerId, row.GetProperty("ownerId").GetString());
        Equal(owner.OwnerGeneration, row.GetProperty("ownerGeneration").GetInt64());
        Equal(registrationId, row.GetProperty("registrationId").GetString());
        var source = row.GetProperty("sourceInfo");
        Equal("extension", source.GetProperty("source").GetString());
        Equal(path, source.GetProperty("path").GetString());
        if (path is null) Equal(JsonValueKind.Null, source.GetProperty("path").ValueKind);
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

    // Authored host scope for feature identity and awaited cleanup; no renderer or RPC transport claim.
    private sealed class UiProbeProvider : IExtensionUiProvider
    {
        internal bool HoldClose;
        internal int OpenCalls, CloseCalls, Publications;
        internal IExtensionContext? OpenedContext;
        internal IExtensionUi? OpenedUi;
        internal ExtensionUiNotify? LastNotice;
        internal readonly TaskCompletionSource CloseEntered = Gate(), CloseRelease = Gate();
        internal ExtensionUiCapabilities Capabilities { get; } = new(ExtensionUiMode.Rpc, 3, 7, [ExtensionUiFeature.Notify]);

        public IExtensionUiScope OpenScope(IExtensionContext context)
        {
            OpenCalls++;
            OpenedContext = context;
            var scope = new ProbeScope(this, context);
            OpenedUi = scope;
            return scope;
        }

        private sealed class ProbeScope(UiProbeProvider owner, IExtensionContext context) : IExtensionUiScope
        {
            private int closed;
            public ExtensionUiCapabilities Capabilities => owner.Capabilities;
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(
                ExtensionUiNotification notification, CancellationToken cancellationToken = default)
            {
                if (Volatile.Read(ref closed) != 0)
                    return ValueTask.FromResult(ExtensionUiOutcome<ExtensionUiPublication>.Unavailable(ExtensionUiUnavailableReason.StaleContext));
                if (cancellationToken.IsCancellationRequested || context.OperationCancellationToken.IsCancellationRequested ||
                    context.SessionCancellationToken.IsCancellationRequested || context.ExtensionLifetimeCancellationToken.IsCancellationRequested)
                    return ValueTask.FromResult(ExtensionUiOutcome<ExtensionUiPublication>.Cancelled());
                if (notification is not ExtensionUiNotify notice) return Unavailable<ExtensionUiPublication>();
                owner.Publications++;
                owner.LastNotice = notice;
                return ValueTask.FromResult(ExtensionUiOutcome<ExtensionUiPublication>.FromValue(ExtensionUiPublication.Published));
            }
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices,
                ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Unavailable<string>();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message,
                ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Unavailable<bool>();
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null,
                ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Unavailable<string>();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null,
                CancellationToken cancellationToken = default) => Unavailable<string>();
            private static ValueTask<ExtensionUiOutcome<T>> Unavailable<T>() =>
                ValueTask.FromResult(ExtensionUiOutcome<T>.Unavailable(ExtensionUiUnavailableReason.UnsupportedCapability));
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref closed, 1) != 0) return;
                owner.CloseCalls++;
                owner.CloseEntered.TrySetResult();
                if (owner.HoldClose) await owner.CloseRelease.Task;
            }
        }
    }
}
