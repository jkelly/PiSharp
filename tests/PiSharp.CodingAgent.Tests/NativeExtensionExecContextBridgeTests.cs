using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Extensions.Execution;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools.Processes;

internal static class NativeExtensionExecContextBridgeTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("exec.context.actual-view-ignored-task-joins-and-late-refusal", () => Exercise(false, false, false)),
        ("exec.context.actual-actions-ignored-task-joins-and-late-refusal", () => Exercise(true, false, false)),
        ("exec.context.actual-view-held-admission-replacement-no-process", () => Exercise(false, true, false)),
        ("exec.context.actual-actions-held-admission-replacement-no-process", () => Exercise(true, true, false)),
        ("exec.context.actual-actions-sync-oce-fault-full-cause", () => Exercise(true, false, true)),
        ("exec.context.actual-owner-generation-token-refusal-before-admission", WrongOwner)
    ];
    private static readonly NativeExtensionExecOriginals Originals = new();
    private static readonly List<NativeExtensionExecOriginal> Installed = [];
    private static readonly List<Exception> CollectorEnvelopes = [];
    internal static NativeExtensionExecOriginal[] RawCapturedOriginals()
    { lock (Installed) return Installed.Concat(Originals.Capture()).ToArray(); }
    private static void Require(bool value) { if (!value) throw new IOException("Exec originating context assertion failed."); }
    private static async Task<T> Observe<T>(Task<T> original, string phase)
    {
        Exception? direct = null; T value = default!;
        try { value = await original; } catch (Exception error) { direct = error; }
        Originals.Record(phase, original, direct);
        if (direct is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(direct).Throw();
        return value;
    }
    private static async Task Join(Task original, string phase, List<Exception> failures)
    {
        Exception? direct = null; try { await original; } catch (Exception error) { direct = error; }
        if (Originals.Record(phase, original, direct) is { } retained)
        { CollectorEnvelopes.Add(retained); failures.Add(retained); }
    }
    private static void Throw(List<Exception> failures) { if (failures.Count > 0) throw new AggregateException(failures); }
    private static ProcessRequest Admit(NativeExtensionExecInvocation invocation) => new(
        Path.Combine(invocation.WorkingDirectory, "supplied-context-executable.exe"), invocation.Arguments, invocation.WorkingDirectory,
        ImmutableDictionary<string, string>.Empty, Path.Combine(invocation.WorkingDirectory, Guid.NewGuid().ToString("N") + ".spill"), invocation.TimeoutSeconds);
    private sealed class Runner : ISeparatedProcessRunner
    {
        internal int Calls;
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken token = default)
        {
            Calls++; token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SeparatedProcessRunResult(new(ProcessRunStatus.Exited, 0, 19, true, true, true,
                new("", default!, 0, 0, null), new("merged", false), 0, []), new("literal-output", false), new("", false)));
        }
    }
    private sealed class Configuration : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new IOException("No providers admitted."); }
    private sealed class Transport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("No provider execution admitted.")); yield break; }
    }
    private sealed class Plugin(ExtensionCommandDescriptor command) : IPiSharpExtension
    {
        internal int Initializes, Disposes;
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { Initializes++; registry.RegisterCommand(command); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
    private static async Task<PersistentAgentSession> Session(string root, string name)
    {
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
            id = name, timestamp = "2026-10-07T00:00:00Z", cwd = root }));
        var sequence = 0;
        return await Observe(PersistentAgentSession.CreateAsync(Path.Combine(root, name + ".jsonl"), header,
            new AgentConfiguration(new("offline", "openai-responses", "admitted-offline"), new Transport(), []),
            () => 1, () => name + "-" + ++sequence), "context-actual-session-create");
    }
    // Only source-proven expected leaf identities may be acknowledged; unexpected cleanup siblings remain failures.
    private static HashSet<Exception> Nodes(NativeExtensionExecOriginal[] rows)
    {
        var nodes = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>();
        foreach (var row in rows) { if (row.Aggregate is { } aggregate) pending.Push(aggregate); if (row.Direct is { } direct) pending.Push(direct); }
        while (pending.TryPop(out var node))
        {
            if (!nodes.Add(node)) continue;
            if (nodes.Count > 8192) throw new IOException("Fixture fault graph bound exceeded.");
            if (node is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) pending.Push(child);
            else if (node.InnerException is { } inner) pending.Push(inner);
        }
        return nodes;
    }
    private static bool Evidence(Task original, Exception evidence, NativeExtensionExecOriginal[] rows) => rows.Any(row =>
        ReferenceEquals(row.Original, original) && (ReferenceEquals(row.Aggregate, evidence) || ReferenceEquals(row.Direct, evidence) ||
            evidence is AggregateException supplied && row.Aggregate is { } cached && supplied.InnerExceptions.Count == cached.InnerExceptions.Count &&
            supplied.InnerExceptions.Zip(cached.InnerExceptions).All(pair => ReferenceEquals(pair.First, pair.Second))));
    private static bool Allowed(Exception error, Exception[] leaves, NativeExtensionExecOriginal[] rows, HashSet<Exception> nodes)
    {
        if (leaves.Any(leaf => ReferenceEquals(leaf, error))) return nodes.Contains(error);
        var captured = nodes.Contains(error);
        if (error is AggregateException aggregate)
            return captured && aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(child => Allowed(child, leaves, rows, nodes));
        if (error is NativeExtensionExecFault execution)
        {
            var exact = execution.Original is { } original
                ? (captured || CollectorEnvelopes.Any(value => ReferenceEquals(value, error))) && Evidence(original, execution.Evidence, rows)
                : captured && execution.Message == "Native exec synchronous executable admission failed." &&
                    leaves.Any(leaf => ReferenceEquals(leaf, execution.Evidence));
            return exact && ReferenceEquals(execution.InnerException, execution.Evidence) && Allowed(execution.Evidence, leaves, rows, nodes);
        }
        if (!captured || error.InnerException is not { } inner) return false;
        var sourceMetadata = error switch
        {
            ExtensionProviderOriginalFaultException provider => provider.Original is { } original &&
                provider.Message == "Admitted extension provider exec failed." && ReferenceEquals(provider.Evidence, inner) && Evidence(original, inner, rows),
            ExtensionFacadeOriginalFaultException facade => ReferenceEquals(facade.OriginalException, inner) && Evidence(facade.OriginalTask, inner, rows),
            ExtensionFacadeCanceledOriginalException facade => facade.OriginalTask.IsCanceled && Evidence(facade.OriginalTask, inner, rows),
            ExtensionProviderCanceledOriginalException provider => provider.Original.IsCanceled && Evidence(provider.Original, inner, rows),
            ExtensionRegistrationException registration => registration.Failure == ExtensionRegistrationFailure.CleanupFailed &&
                registration.OwnerId is "context-owner" or "registry" && registration.Operation == "dispose",
            _ => false
        };
        return sourceMetadata && Allowed(inner, leaves, rows, nodes);
    }
    private static async Task JoinReferencedRuntimeOriginals(NativeExtensionExecOriginal[] rows, List<Exception> collected)
    {
        var seen = new HashSet<Task>(rows.Select(row => row.Original), ReferenceEqualityComparer.Instance);
        foreach (var node in Nodes(rows))
        {
            var original = node switch
            {
                ExtensionProviderOriginalFaultException provider => provider.Original,
                ExtensionProviderCanceledOriginalException provider => provider.Original,
                ExtensionFacadeOriginalFaultException facade => facade.OriginalTask,
                ExtensionFacadeCanceledOriginalException facade => facade.OriginalTask,
                _ => null
            };
            if (original is not null && seen.Add(original))
                await Join(original, "actual-runtime-envelope-original-join", collected);
        }
    }
    private static async Task Exercise(bool actions, bool replace, bool synchronousFault)
    {
        var root = Path.Combine(Path.GetTempPath(), "exec-context-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var failures = new List<Exception>(); var runner = new Runner(); var supplied = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new OperationCanceledException("Unowned callback OCE", CancellationToken.None);
        PersistentAgentSession? session = null; ReplaceableAgentSession? owner = null; ExtensionRegistry? registry = null;
        ExtensionProviderRegistrationHost? providers = null; NativeExtensionExecInstallation? installation = null;
        NativeExtensionExecContextHost? execution = null; Plugin? plugin = null;
        Task? commandOriginal = null; Task<ExtensionExecResult>? ignored = null; Func<Task<ExtensionExecResult>>? late = null;
        var collected = new List<Exception>();
        try
        {
            session = await Session(root, "initial");
            owner = new(session, async (request, _) => await Session(root, Path.GetFileNameWithoutExtension(request.Path)));
            var reads = new NativeExtensionContextFacadeHost(); reads.Attach(owner);
            var views = new NativeSessionSnapshotProvider(); views.Attach(owner);
            execution = new(reads, () => installation);
            registry = new(null, null, views, new NativeExtensionRegistrationFacadeHost(reads,
                new NativeExistingSessionRegistrationActions(reads, () => throw new IOException("No input admitted.")), execution));
            providers = new(registry, [], new Configuration());
            async Task<ProcessRequest> HeldAdmission(NativeExtensionExecInvocation invocation)
            { entered.TrySetResult(); await release.Task; return Admit(invocation); }
            installation = new(registry, runner, (invocation, _) =>
            {
                supplied++; if (synchronousFault) throw injected;
                return new ValueTask<ProcessRequest>(HeldAdmission(invocation));
            }, root, (_, _) => { });
            ExtensionCommandDescriptor command;
            if (actions)
                command = ExtensionRegistrationCommands.Create("exec-context", "exec-context", "actual owned actions", providers,
                    (_, capability, _, _) =>
                    {
                        var exact = (IExtensionExecRegistrationActions)capability;
                        late = () => exact.ExecAsync("literal", ["a b"]).AsTask(); ignored = late(); return ValueTask.CompletedTask;
                    });
            else
                command = ExtensionCommandFacade.CreateCommand("exec-context", "exec-context", "actual owned View",
                    (_, capability, _) =>
                    {
                        var exact = (IExtensionExecCommandFacade)capability;
                        late = () => exact.ExecAsync("literal", ["a b"]).AsTask(); ignored = late(); return ValueTask.CompletedTask;
                    });
            plugin = new(command);
            await Observe(registry.ActivateAsync("context-owner", installation.Decorate(registry, plugin)), "context-actual-native-activation");
            commandOriginal = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "exec-context", JsonData.EmptyObject).AsTask();
            if (!synchronousFault)
            {
                var first = await Task.WhenAny(entered.Task, commandOriginal).WaitAsync(TimeSpan.FromSeconds(5));
                Require(ReferenceEquals(first, entered.Task) && !commandOriginal.IsCompleted && supplied == 1 && runner.Calls == 0);
                if (replace)
                {
                    var replacement = await Observe(owner.SwitchAsync(owner.Current, new(Path.Combine(root, "replacement.jsonl"))), "context-actual-session-replacement");
                    Require(replacement is not null && replacement.Current.Generation == 2);
                }
                release.TrySetResult();
            }
            await Join(commandOriginal, "context-actual-command-original", collected);
            if (ignored is not null) await Join(ignored, "context-ignored-mapped-original", collected);
            Require(plugin.Initializes == 1 && ignored is not null && ignored.IsCompleted && supplied == 1);
            if (!replace && !synchronousFault)
                Require(commandOriginal.IsCompletedSuccessfully && ignored!.IsCompletedSuccessfully && runner.Calls == 1 && ignored.Result.Stdout == "literal-output");
            else if (synchronousFault)
            {
                Require(commandOriginal.IsFaulted && !commandOriginal.IsCanceled && ignored!.IsFaulted && !ignored.IsCanceled);
                var cached = installation!.Originals.Capture().Concat(execution!.Originals.Capture()).Concat(Originals.Capture()).ToArray();
                Require(Nodes(cached).OfType<NativeExtensionExecFault>().Any(fault => fault.Original is null &&
                    fault.Message == "Native exec synchronous executable admission failed." && ReferenceEquals(fault.Evidence, injected) &&
                    ReferenceEquals(fault.InnerException, injected)));
            }
            else Require(commandOriginal.IsFaulted || commandOriginal.IsCanceled);
            if (replace || synchronousFault) Require(runner.Calls == 0);
            var refused = false; try { _ = late!(); } catch (InvalidOperationException) { refused = true; } Require(refused);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            release.TrySetResult();
            if (commandOriginal is not null) await Join(commandOriginal, "context-final-command-join", collected);
            if (ignored is not null) await Join(ignored, "context-final-ignored-join", collected);
            if (registry is not null) await Join(registry.DisposeAsync().AsTask(), "context-actual-registry-close", collected);
            if (owner is not null) await Join(owner.DisposeAsync().AsTask(), "context-actual-owner-close", collected);
            else if (session is not null) await Join(session.DisposeAsync().AsTask(), "context-actual-session-close", collected);
            try { providers?.Dispose(); } catch (Exception error) { failures.Add(error); }
            var expected = new List<Exception>();
            if (synchronousFault) expected.Add(injected);
            if (replace && execution is not null)
                expected.AddRange(execution.Originals.Capture().Where(row => row.Original.IsCanceled && row.Direct is OperationCanceledException).Select(row => row.Direct!));
            var cached = Originals.Capture().Concat(installation?.Originals.Capture() ?? []).Concat(execution?.Originals.Capture() ?? []).ToArray();
            await JoinReferencedRuntimeOriginals(cached, collected);
            cached = Originals.Capture().Concat(installation?.Originals.Capture() ?? []).Concat(execution?.Originals.Capture() ?? []).ToArray();
            var nodes = Nodes(cached);
            if (synchronousFault)
            {
                if (Allowed(new NativeExtensionExecFault("synchronous executable admission", null, injected), expected.ToArray(), cached, nodes) ||
                    Allowed(new ExtensionProviderOriginalFaultException("exec", commandOriginal, injected), expected.ToArray(), cached, nodes) ||
                    Allowed(new AggregateException(), expected.ToArray(), cached, nodes))
                    failures.Add(new IOException("Unknown same-type envelope or empty aggregate was acknowledged."));
            }
            foreach (var failure in collected) if (!Allowed(failure, expected.ToArray(), cached, nodes)) failures.Add(failure);
            if (installation is not null) lock (Installed) Installed.AddRange(installation.Originals.Capture());
            if (execution is not null) lock (Installed) Installed.AddRange(execution.Originals.Capture());
            if (plugin is not null && plugin.Disposes != 1) failures.Add(new IOException("Actual plugin disposal not exactly once."));
            try { Directory.Delete(root, true); } catch (Exception error) { failures.Add(error); }
        }
        Throw(failures);
    }
    private sealed class Forged(IExtensionCommandContext actual, string id, long generation, CancellationToken lifetime) : IExtensionCommandContext
    {
        public string OwnerId => id; public long OwnerGeneration => generation;
        public CancellationToken OperationCancellationToken => actual.OperationCancellationToken;
        public CancellationToken SessionCancellationToken => actual.SessionCancellationToken;
        public CancellationToken ExtensionLifetimeCancellationToken => lifetime;
    }
    private static async Task WrongOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "exec-context-owner-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var failures = new List<Exception>(); var runner = new Runner(); var admissions = 0;
        using var wrongLifetime = new CancellationTokenSource();
        ExtensionRegistry? registry = null; NativeExtensionExecInstallation? installation = null;
        try
        {
            // ResolveContext validates generation authority before any attachment or external supplier.
            var reads = new NativeExtensionContextFacadeHost(); var execution = new NativeExtensionExecContextHost(reads, () => installation);
            registry = new(); installation = new(registry, runner, (invocation, _) => { admissions++; return ValueTask.FromResult(Admit(invocation)); }, root, (_, _) => { });
            var command = new ExtensionCommandDescriptor("wrong-owner", "wrong-owner", "actual identity refusal", (_, actual, _) =>
            {
                foreach (var forged in new[] { new Forged(actual, "other", actual.OwnerGeneration, actual.ExtensionLifetimeCancellationToken),
                    new Forged(actual, actual.OwnerId, actual.OwnerGeneration + 1, actual.ExtensionLifetimeCancellationToken),
                    new Forged(actual, actual.OwnerId, actual.OwnerGeneration, wrongLifetime.Token) })
                {
                    var refused = false; try { _ = execution.ExecAsync(forged, "literal", []); } catch (InvalidOperationException) { refused = true; }
                    Require(refused);
                }
                Require(admissions == 0 && runner.Calls == 0); return ValueTask.CompletedTask;
            });
            await Observe(registry.ActivateAsync("wrong-owner-scope", installation.Decorate(registry, new Plugin(command))), "context-owner-actual-activation");
            await Join(registry.InvokeCommandAsync(registry.CaptureSnapshot(), "wrong-owner", JsonData.EmptyObject).AsTask(), "context-owner-actual-command", failures);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (registry is not null) await Join(registry.DisposeAsync().AsTask(), "context-owner-actual-close", failures);
            if (installation is not null) lock (Installed) Installed.AddRange(installation.Originals.Capture());
            try { Directory.Delete(root, true); } catch (Exception error) { failures.Add(error); }
        }
        Throw(failures);
    }
}
