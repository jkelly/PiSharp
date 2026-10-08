using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class SessionCreationPreflightRegistryTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("session-creation-preflight.same-eventual-count-size-depth-identity-number-policy", EventualPolicy);
        yield return ("session-creation-preflight.older-broker-fails-before-effects", OlderBroker);
        yield return ("session-creation-preflight.cancelled-validation-keeps-source-scope", Cancellation);
        yield return ("session-creation-preflight.postcommit-context-error-has-receipt-and-joined-cleanup", CommittedFailure);
    }
    private static ExtensionSessionSnapshot Target(ImmutableArray<JsonData> entries) => new("target", 2, null, entries);
    private static async Task EventualPolicy()
    {
        var empty = Target([]); var objectRecord = JsonData.Parse("{}");
        var cases = new List<(string Name, ExtensionSessionSnapshot Snapshot, ExtensionRegistryOptions? Options)>
        {
            ("count", Target(Enumerable.Repeat(objectRecord, 4097).ToImmutableArray()), null),
            ("per-record", Target([JsonData.Parse("{\"s\":\"" + new string('x', 33) + "\"}")]), new() { MaximumJsonCharacters = 32 }),
            ("aggregate-characters", Target(Enumerable.Repeat(JsonData.Parse("{\"s\":\"" + new string('x', 260) + "\"}"), 4096).ToImmutableArray()), null),
            ("aggregate-utf8", Target(Enumerable.Repeat(JsonData.Parse("{\"s\":\"" + new string('é', 180) + "\"}"), 3000).ToImmutableArray()), null),
            ("depth", Target([JsonData.Parse("{\"a\":{\"b\":{\"c\":1}}}")]), new() { MaximumJsonDepth = 2 }),
            ("session-id", empty with { SessionId = "bad identity" }, null),
            ("generation", empty with { Generation = 0 }, null),
            ("selected-leaf", empty with { SelectedLeafId = "bad leaf" }, null),
            ("non-object-entry", Target([JsonData.Parse("[]")]), null),
            ("ordinary-provider-huge-number", Target([JsonData.Parse("{\"n\":1.00e400}")]), null)
        };
        foreach (var test in cases)
        {
            var provider = new Provider(test.Snapshot); await using var registry = new ExtensionRegistry(test.Options, null, provider);
            await Activate(registry, async context =>
            {
                try { await context.CreateSessionAsync(new(ExtensionSessionCreationKind.New)); throw new Exception("Invalid staged snapshot admitted: " + test.Name); }
                catch (ExtensionRegistrationException error) when (error.Operation == "capture-session-snapshot" &&
                    error.Failure is ExtensionRegistrationFailure.LimitExceeded or ExtensionRegistrationFailure.InvalidDescriptor) { }
                await context.AppendSessionEntryAsync("still-source", 1, JsonData.Parse("{}"));
            });
            await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "create", JsonData.Null);
            Check(provider.ValidationCalls == 1 && !provider.Committed && provider.SourceWrites == 1 && provider.Fresh is null,
                "Staged rejection crossed effects or lost source scope: " + test.Name);
        }
        var raw = JsonData.Parse("{\"n\":1.00e400}"); var opaque = new OpaqueProvider(Target([raw]));
        await using var accepted = new ExtensionRegistry(null, null, opaque);
        await Activate(accepted, async context =>
        {
            var result = await context.CreateSessionAsync(new(ExtensionSessionCreationKind.New));
            Check(result?.Context.SessionSnapshot?.BranchEntries[0].ToString() == raw.ToString(), "Trusted opaque target changed its exact numeric token.");
        });
        await accepted.InvokeCommandAsync(accepted.CaptureSnapshot(), "create", JsonData.Null);
        Check(opaque.ValidationCalls == 1 && opaque.Committed && opaque.Fresh?.Closed == true, "Valid opaque creation was rejected or its callback scope escaped cleanup.");
        try { await accepted.InvokeCommandAsync(accepted.CaptureSnapshot(), "create", raw); throw new Exception("Opaque host view enabled executable huge-number input."); }
        catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.InvalidDescriptor) { }
        Check(opaque.ValidationCalls == 1, "Rejected executable argument reached the creation callback.");
    }
    private static async Task OlderBroker()
    {
        var provider = new Provider(Target([])) { Legacy = true }; await using var registry = new ExtensionRegistry(null, null, provider);
        await Activate(registry, async context =>
        {
            try { await context.CreateSessionAsync(new(ExtensionSessionCreationKind.New)); throw new Exception("Old broker ran without staged validation."); }
            catch (InvalidOperationException error) when (error.Message.Contains("staged SDK snapshot", StringComparison.Ordinal)) { }
            await context.AppendSessionEntryAsync("still-source", 1, JsonData.Parse("{}"));
        });
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "create", JsonData.Null);
        Check(provider.LegacyCalls == 0 && provider.ValidationCalls == 0 && !provider.Committed && provider.SourceWrites == 1,
            "Default staged overload fell through to an old effectful broker.");
    }
    private static async Task Cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        var provider = new Provider(Target([])) { BeforeValidation = cancelled.Cancel };
        await using var registry = new ExtensionRegistry(null, null, provider);
        await Activate(registry, async context =>
        {
            try { await context.CreateSessionAsync(new(ExtensionSessionCreationKind.New), cancelled.Token); throw new Exception("Cancelled staging committed."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            await context.AppendSessionEntryAsync("still-source", 1, JsonData.Parse("{}"));
        });
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "create", JsonData.Null);
        Check(!provider.Committed && provider.SourceWrites == 1 && provider.Fresh is null, "Validation cancellation lost original scope authority.");
    }
    private static async Task CommittedFailure()
    {
        var provider = new Provider(Target([])) { AfterCommit = Target(Enumerable.Repeat(JsonData.Parse("{}"), 4097).ToImmutableArray()) };
        await using var registry = new ExtensionRegistry(null, null, provider); var observed = false;
        await Activate(registry, async context =>
        {
            try { await context.CreateSessionAsync(new(ExtensionSessionCreationKind.New)); throw new Exception("Postcommit invalid context was admitted."); }
            catch (ExtensionSessionCreationCommittedException error)
            {
                Check(error.Receipt.SessionId == "target" && error.Receipt.Generation == 2 &&
                    error.InnerException is ExtensionRegistrationException { Failure: ExtensionRegistrationFailure.LimitExceeded } &&
                    provider.Fresh?.Closed == true, "Committed failure omitted actual receipt or escaped scope cleanup."); observed = true;
            }
        });
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "create", JsonData.Null);
        Check(observed && provider.ValidationCalls == 1 && provider.Committed, "Postcommit failure was disguised as a precommit veto.");
    }
    private static Task Activate(ExtensionRegistry registry, Func<IExtensionSessionCreationCommandContext, Task> action) =>
        registry.ActivateAsync("preflight", new Plugin((entries, _) =>
        {
            entries.RegisterCommand(new("create", "create", "", async (_, context, _) => await action((IExtensionSessionCreationCommandContext)context)));
            return ValueTask.CompletedTask;
        }));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Plugin(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private class Provider(ExtensionSessionSnapshot target) : IExtensionSessionCreationProvider
    {
        internal ExtensionSessionSnapshot Target { get; } = target;
        internal bool Legacy, Committed;
        internal int LegacyCalls, ValidationCalls, SourceWrites;
        internal Action? BeforeValidation;
        internal ExtensionSessionSnapshot? AfterCommit;
        internal StagedScope? Fresh;
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => new("source", 1, null, []);
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) =>
            Legacy ? new LegacyScope(this, snapshot) : new StagedScope(this, snapshot);
    }
    private sealed class OpaqueProvider(ExtensionSessionSnapshot target) : Provider(target), IExtensionSessionOpaqueViewProvider { }
    private abstract class Scope(Provider provider, ExtensionSessionSnapshot snapshot) : IExtensionSessionActionScope
    {
        protected Provider Provider { get; } = provider;
        public ExtensionSessionSnapshot Snapshot { get; } = snapshot;
        public CancellationToken SessionCancellationToken => CancellationToken.None;
        internal bool Closed;
        public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int version, JsonData data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(!Closed && Snapshot.Generation == 1 && !Provider.Committed, "Original fake scope lost authority."); Provider.SourceWrites++;
            return ValueTask.FromResult(new ExtensionSessionEntryAcknowledgment("source", 1, data, 1, 0, 1, null));
        }
        public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    }
    private sealed class LegacyScope(Provider provider, ExtensionSessionSnapshot snapshot) : Scope(provider, snapshot), IExtensionSessionCreationScope
    {
        public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request, CancellationToken token)
        { Provider.LegacyCalls++; Provider.Committed = true; return ValueTask.FromResult<ExtensionSessionCreationScopeResult?>(null); }
    }
    private sealed class StagedScope(Provider provider, ExtensionSessionSnapshot snapshot) : Scope(provider, snapshot), IExtensionSessionCreationScope
    {
        public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request, CancellationToken token) => throw new Exception("SDK invoked the old non-preflight overload.");
        public async ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request,
            Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validate, CancellationToken token)
        {
            Provider.ValidationCalls++; Provider.BeforeValidation?.Invoke(); await validate(Provider.Target, token); token.ThrowIfCancellationRequested();
            Provider.Committed = true; Provider.Fresh = new(Provider, Provider.AfterCommit ?? Provider.Target);
            return new(Provider.Fresh, null);
        }
    }
}
