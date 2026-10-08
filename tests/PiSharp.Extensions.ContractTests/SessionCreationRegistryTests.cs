using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class SessionCreationRegistryTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("session-creation-registry. optional feature requires configured creation broker", Availability),
        ("session-creation-registry. explicit opaque session capability preserves numeric spelling while executable input remains finite", OpaqueSessionRecords),
        ("session-creation-registry. typed veto is ordered and failure remains distinct from cancellation", OrderedDecisions),
        ("session-creation-registry. shutdown joins admitted veto handler cleanup and rejects old snapshot", Shutdown)
    ];
    private static ExtensionSessionCreationEvent Proposal => new("source", ExtensionSessionCreationKind.ForkBefore, "user", null);
    private static async Task Availability()
    {
        await using var absent = new ExtensionRegistry();
        await using var replacementOnly = new ExtensionRegistry(null, null, new ReplacementProvider());
        await using var creation = new ExtensionRegistry(null, null, new CreationProvider());
        Check(!absent.AvailableFeatures.Contains(ExtensionSessionActionFeatures.Creation) &&
            !replacementOnly.AvailableFeatures.Contains(ExtensionSessionActionFeatures.Creation) &&
            creation.AvailableFeatures.Contains(ExtensionSessionActionFeatures.Creation), "Creation feature was inferred from replacement-only services.");
        var entered = false;
        await creation.ActivateAsync("capability", new Plugin((registry, _) =>
        {
            Check(registry is IExtensionSessionCreationRegistry && registry.Features.Contains(ExtensionSessionActionFeatures.Creation), "Configured registration scope lost its optional capability.");
            registry.RegisterCommand(new("command", "capability", "", async (_, context, _) =>
            {
                Check(context is IExtensionSessionCreationCommandContext, "Creation command context lost its optional API.");
                try { await ((IExtensionSessionCreationCommandContext)context).CreateSessionAsync(new(ExtensionSessionCreationKind.New));
                    throw new InvalidOperationException("Unavailable captured attachment was silently accepted."); }
                catch (InvalidOperationException error) when (error.Message.Contains("unavailable", StringComparison.Ordinal)) { entered = true; }
            }));
            return ValueTask.CompletedTask;
        }));
        await creation.InvokeCommandAsync(creation.CaptureSnapshot(), "capability", JsonData.Null);
        Check(entered, "Command did not exercise absent attachment admission.");
    }
    private static async Task OrderedDecisions()
    {
        await using var registry = new ExtensionRegistry(); var order = new List<string>();
        var decision = ExtensionSessionSwitchDecision.Cancel; var fail = false;
        await registry.ActivateAsync("first", new Plugin((entries, _) =>
        {
            ((IExtensionSessionCreationRegistry)entries).RegisterSessionCreationHandler(new("veto", (proposal, context, _) =>
            {
                Check(proposal == Proposal && context.OwnerId == "first", "Typed pre-effect proposal or owner changed.");
                order.Add("first"); if (fail) throw new IOException("authored typed creation failure");
                return ValueTask.FromResult(decision);
            })); return ValueTask.CompletedTask;
        }));
        await registry.ActivateAsync("second", new Plugin((entries, _) =>
        {
            ((IExtensionSessionCreationRegistry)entries).RegisterSessionCreationHandler(new("continue", (_, _, _) =>
            { order.Add("second"); return ValueTask.FromResult(ExtensionSessionSwitchDecision.Continue); }));
            return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot();
        Check(captured.Registrations.Select(row => row.Kind).SequenceEqual(new[] { "SessionCreationHandler", "SessionCreationHandler" }), "Handlers lost transactional snapshot ownership.");
        Check(!await registry.BeforeSessionCreationAsync(captured, Proposal) && order.SequenceEqual(new[] { "first" }), "Cancel failed to short-circuit later owners.");
        order.Clear(); decision = ExtensionSessionSwitchDecision.Continue;
        Check(await registry.BeforeSessionCreationAsync(captured, Proposal) && order.SequenceEqual(new[] { "first", "second" }), "Continue changed deterministic owner order.");
        order.Clear(); fail = true;
        try { await registry.BeforeSessionCreationAsync(captured, Proposal); throw new Exception("Handler failure became a veto."); }
        catch (IOException error) when (error.Message == "authored typed creation failure") { }
        fail = false; decision = (ExtensionSessionSwitchDecision)99;
        try { await registry.BeforeSessionCreationAsync(captured, Proposal); throw new Exception("Invalid decision was accepted."); }
        catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.InvalidDescriptor) { }
        decision = ExtensionSessionSwitchDecision.Continue;
        Check(await registry.BeforeSessionCreationAsync(captured, Proposal), "Failed dispatch leaked its admission.");
    }
    private static async Task OpaqueSessionRecords()
    {
        var raw = JsonData.Parse("{\"type\":\"future\",\"id\":\"opaque\",\"parentId\":null,\"timestamp\":\"2026-10-02T00:00:00.000Z\",\"data\":{\"huge\":1.00e400,\"nil\":null}}");
        await using var registry = new ExtensionRegistry(null, null, new OpaqueProvider(raw)); var calls = 0;
        await registry.ActivateAsync("opaque", new Plugin((entries, _) =>
        {
            Check(entries.Features.Contains(ExtensionSessionActionFeatures.OpaqueRecords), "Opaque capability was not advertised by the configured host.");
            entries.RegisterCommand(new("command", "opaque", "", (_, context, _) =>
            {
                var snapshot = ((IExtensionSessionContext)context).SessionSnapshot!;
                Check(snapshot.BranchEntries.Single().ToString() == raw.ToString() &&
                    snapshot.BranchEntries[0].Value.GetProperty("data").GetProperty("huge").GetRawText() == "1.00e400", "Opaque numeric token changed.");
                calls++; return ValueTask.CompletedTask;
            })); return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot(); await registry.InvokeCommandAsync(captured, "opaque", JsonData.Null);
        try { await registry.InvokeCommandAsync(captured, "opaque", JsonData.Parse("{\"n\":1.00e400}")); throw new Exception("Opaque capability broadened executable command input."); }
        catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.InvalidDescriptor) { }
        Check(calls == 1, "Rejected executable input entered the plugin.");
    }
    private static async Task Shutdown()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var cleaning = Gate(); var release = Gate(); var cleaned = false;
        var scope = await registry.ActivateAsync("held", new Plugin((entries, _) =>
        {
            ((IExtensionSessionCreationRegistry)entries).RegisterSessionCreationHandler(new("held", async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return ExtensionSessionSwitchDecision.Continue; }
                finally { cleaning.TrySetResult(); await release.Task; cleaned = true; }
            })); return ValueTask.CompletedTask;
        }));
        var captured = registry.CaptureSnapshot(); var dispatch = registry.BeforeSessionCreationAsync(captured, Proposal).AsTask();
        Task? closing = null;
        try
        {
            await entered.Task; closing = scope.DisposeAsync().AsTask(); await cleaning.Task;
            Check(!closing.IsCompleted && !dispatch.IsCompleted, "Owner shutdown detached admitted creation cleanup.");
            release.TrySetResult();
            try { await dispatch; throw new Exception("Shutdown cancellation became a veto decision."); } catch (OperationCanceledException) { }
            await closing; Check(cleaned, "Physical handler cleanup was not joined.");
            try { await registry.BeforeSessionCreationAsync(captured, Proposal); throw new Exception("Old creation snapshot remained admitted."); }
            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.StaleSnapshot) { }
        }
        finally { release.TrySetResult(); try { await dispatch; } catch (Exception) { } if (closing is not null) await closing; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Plugin(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private class ReplacementProvider : IExtensionSessionActionProvider
    {
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => null;
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) => throw new Exception("No attached session in feature-only probe.");
    }
    private sealed class CreationProvider : ReplacementProvider, IExtensionSessionCreationProvider { }
    private sealed class OpaqueProvider(JsonData record) : IExtensionSessionOpaqueViewProvider
    {
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => new("session", 1, "opaque", [record]);
    }
}
