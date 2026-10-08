using System.Text.Json;
using PiSharp.Rpc.Protocol;
using PiSharp.Rpc;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class NativeProfileLifecycleHandoffTests
{
    // Caller supplies a genuinely published/approved minimal plugin configuration enabling "handoff".
    // No plugin bytes, trust approval or native reload admission are manufactured by these controls.
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(
        NativeExtensionConfiguration admitted, string workspace, string caseParent)
    {
        yield return ("lifecycle.actual-held-callback-before-shutdown-intent", () => Held(admitted, workspace, caseParent, false));
        yield return ("lifecycle.actual-held-mode-stop-close-fence-and-original", () => Held(admitted, workspace, caseParent, true));
        yield return ("lifecycle.settled-origin-leaves-held-second-origin-queued", () => Held(admitted, workspace, caseParent, false, heldSecond: true));
        yield return ("lifecycle.sync-post-hook-retains-unknown-sibling-after-output-fatal", () => Held(admitted, workspace, caseParent, false, syncHookFailure: true));
    }
    private static void ImportProfile(OfflineSessionProfile profile)
    {
        lock (gate)
            foreach (var row in profile.CapturedLifecycleOriginals)
            {
                if (!raw.ContainsKey(row.Original)) raw.Add(row.Original, (row.Phase, row.Aggregate, row.Direct));
                if (!ownerAliases.Any(alias => alias.Phase == row.Phase && ReferenceEquals(alias.Original, row.Original)
                    && ReferenceEquals(alias.Aggregate, row.Aggregate) && ReferenceEquals(alias.Direct, row.Direct)))
                    ownerAliases.Add(row); // Append exact owner cache aliases without replacing earlier observations.
            }
    }
    private static readonly object gate = new();
    private static readonly List<(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)> ownerAliases = [];
    private static readonly List<(string Phase, Exception Direct)> synchronous = [];
    internal static (string Phase, Exception Direct)[] CapturedSynchronousFailures { get { lock (gate) return synchronous.ToArray(); } }
    private static readonly Dictionary<Task, (string Phase, AggregateException? Aggregate, Exception? Direct)> raw = new(ReferenceEqualityComparer.Instance);
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (gate) return raw.Select(pair => (pair.Value.Phase, pair.Key, pair.Value.Aggregate, pair.Value.Direct)).Concat(ownerAliases).ToArray(); } }
    private static async Task Observe(Task original, string phase)
    {
        Exception? direct = null;
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; throw; }
        finally { lock (gate) if (!raw.ContainsKey(original)) raw.Add(original, (phase, original.IsFaulted ? original.Exception : null, direct)); }
    }
    private static async Task<T> Observe<T>(Task<T> original, string phase)
    { await Observe((Task)original, phase).ConfigureAwait(false); return original.Result; }
    private static void Require(bool value) { if (!value) throw new IOException("Actual profile lifecycle handoff assertion failed."); }
    private sealed class NoProviders : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new IOException("No provider admission."); }
    private static async Task Held(NativeExtensionConfiguration configuration, string workspace, string parent, bool holdStop, bool heldSecond = false, bool syncHookFailure = false)
    {
        Require(configuration.EnabledCommands.Contains("handoff"));
        var folder = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.jsonl");
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OfflineSessionProfile? profile = null; Task? submit = null, drain = null, modeOriginal = null, second = null;
        RpcSessionDispatcher? dispatcher = null; var callbackCalls = 0;
        IExtensionLifecycleHandoffFacade? stale = null; var stopCalls = 0; var failures = new List<Exception>();
        try
        {
            var initializer = new NativeExtensionInitializerInstallation(
                registry => new(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()), [], new NoProviders()),
                (_, facade) => facade.Scope.RegisterCommand(ExtensionCommandFacade.CreateCommand("handoff", "handoff", "held lifecycle fixture",
                    async (_, view, _) =>
                    {
                        if (Interlocked.Increment(ref callbackCalls) == 2 && heldSecond)
                        {
                            var secondView = view as IExtensionLifecycleHandoffFacade ?? throw new IOException("No second handoff facade.");
                            secondView.RequestShutdown(); secondEntered.TrySetResult();
                            await Observe(releaseSecond.Task, "fixture-second-callback-release").ConfigureAwait(false);
                            return;
                        }
                        stale = view as IExtensionLifecycleHandoffFacade ?? throw new IOException("No actual installed handoff facade.");
                        stale.RequestShutdown();
                        callbackEntered.TrySetResult();
                        await Observe(releaseCallback.Task, "fixture-callback-release").ConfigureAwait(false);
                    })));
            profile = await Observe(OfflineSessionProfile.CreateAsync(workspace, path, null, [], [], [], default,
                extension: configuration, configuredInitializerInstallation: initializer), "actual-lifecycle-profile-create");
            var lifecycle = profile.CreateLifecycle(() => 0, () => Guid.NewGuid().ToString("N"), backend: new SessionStorageBackend(folder, SessionStorageMode.InMemory));
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = Guid.NewGuid().ToString("N"), timestamp = "2026-10-07T00:00:00Z", cwd = workspace }));
            var session = await Observe(lifecycle.CreateAsync(path, header, profile.SelectedModel), "actual-lifecycle-session-create");
            await Observe(profile.AttachOwnerAsync(session, lifecycle: lifecycle), "actual-lifecycle-profile-attach");
            profile.ConfigureLifecycleModeStop(() => modeOriginal = Stop());
            async Task Stop()
            {
                stopCalls++; stopEntered.TrySetResult();
                if (holdStop) await Observe(releaseStop.Task, "fixture-mode-stop-release").ConfigureAwait(false);
            }
            if (syncHookFailure)
            {
                releaseCallback.TrySetResult();
                var outputLeaf = new IOException("unknown output sibling");
                var hookLeaf = new IOException("unknown synchronous post-hook sibling");
                var model = profile.SelectedModel;
                dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(new FailedOutput(outputLeaf)), () => 0,
                    [new(model, JsonData.Parse(JsonSerializer.Serialize(new { id = model.Id, api = model.Api, provider = model.Provider,
                        name = model.Id, baseUrl = "https://offline.invalid", reasoning = false, input = new[] { "text" },
                        contextWindow = 128000, maxTokens = 1024, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } })))],
                    sessionOwnership: RpcSessionOwnership.Borrowed, inputAdmission: profile.InputAdmission,
                    postInputSettlement: _ => throw hookLeaf);
                submit = dispatcher.SubmitAsync(JsonData.Parse("{\"type\":\"prompt\",\"id\":\"handoff\",\"message\":\"/handoff\"}"));
                Exception? directSubmit = null;
                try { await Observe(submit, "actual-rpc-output-failed-submit"); } catch (Exception error) { directSubmit = error; }
                var close = dispatcher.DisposeAsync().AsTask(); Exception? directClose = null;
                try { await Observe(close, "actual-rpc-sync-hook-shutdown"); } catch (Exception error) { directClose = error; }
                Require(directSubmit is not null && directClose is not null);
                Require(Contains(directClose!, outputLeaf) && Contains(directClose!, hookLeaf));
                HashSet<Exception> actualCarriers;
                lock (gate) actualCarriers = CaptureActualCarriers(raw[close].Aggregate, raw[close].Direct);
                Require(OnlyKnownLeaves(directClose!, actualCarriers, outputLeaf, hookLeaf));
                Require(!OnlyKnownLeaves(new IOException("foreign wrapper", outputLeaf), actualCarriers, outputLeaf, hookLeaf));
                Require(!OnlyKnownLeaves(new RpcDispatchException(RpcDispatchFailure.OutputFailed, outputLeaf), actualCarriers, outputLeaf, hookLeaf));
                Require(!OnlyKnownLeaves(new AggregateException(), actualCarriers, outputLeaf, hookLeaf));
                Require(!OnlyKnownLeaves(new AggregateException(directClose!, new IOException("extra cleanup sibling")), actualCarriers, outputLeaf, hookLeaf));
                Require(dispatcher.CapturedPostOriginSynchronousFailures.Any(row => ReferenceEquals(row.Direct, hookLeaf)));
                lock (gate) synchronous.AddRange(dispatcher.CapturedPostOriginSynchronousFailures);
                ImportProfile(profile);
                dispatcher = null; // Exact completed close already joined and expected leaves asserted.
                submit = null;
            }
            else
            {
            submit = session.SubmitInputAsync(new("/handoff"), profile.InputAdmission);
            var witness = await Observe(Task.WhenAny(callbackEntered.Task, submit).WaitAsync(TimeSpan.FromSeconds(10)), "fixture-callback-start-race").ConfigureAwait(false);
            Require(ReferenceEquals(witness, callbackEntered.Task) && !submit.IsCompleted && stopCalls == 0);
            // Admission ACK did not invent a retired generation or close task.
            releaseCallback.TrySetResult(); await Observe(submit, "actual-lifecycle-input-submission");
            Require(stopCalls == 0 && profile.Sessions!.Current.Generation == 1);
            var refused = false; try { stale!.RequestShutdown(); } catch (InvalidOperationException) { refused = true; }
            Require(refused && stopCalls == 0);
            if (heldSecond)
            {
                second = profile.InputAdmission.ReduceAsync(new("/handoff"), default).AsTask();
                var secondWitness = await Observe(Task.WhenAny(secondEntered.Task, second).WaitAsync(TimeSpan.FromSeconds(10)), "fixture-second-origin-start-race");
                Require(ReferenceEquals(secondWitness, secondEntered.Task) && !second.IsCompleted);
            }
            drain = profile.DrainLifecycleHandoffsAsync(session);
            if (holdStop)
            {
                witness = await Observe(Task.WhenAny(stopEntered.Task, drain).WaitAsync(TimeSpan.FromSeconds(10)), "fixture-stop-start-race").ConfigureAwait(false);
                Require(ReferenceEquals(witness, stopEntered.Task) && !drain.IsCompleted && stopCalls == 1);
                var closeRefused = false;
                try { _ = profile.DisposeAsync(); } catch (InvalidOperationException) { closeRefused = true; }
                Require(closeRefused); releaseStop.TrySetResult();
            }
            await Observe(drain, "actual-lifecycle-post-origin-drain");
            ImportProfile(profile);
            Require(stopCalls == 1 && modeOriginal is { IsCompletedSuccessfully: true } && profile.LifecycleShutdownRequested);
            Require(profile.CapturedLifecycleOriginals.Any(row => row.Phase == "mode-stop-intent" && ReferenceEquals(row.Original, modeOriginal)));
            await Observe(profile.DrainLifecycleHandoffsAsync(session), "actual-lifecycle-no-replay-drain"); Require(stopCalls == 1);
            if (heldSecond)
            {
                Require(second is { IsCompleted: false }); releaseSecond.TrySetResult();
                await Observe(second!, "actual-second-native-origin");
                await Observe(profile.DrainLifecycleHandoffsAsync(session), "actual-second-settled-origin-drain");
                ImportProfile(profile); Require(stopCalls == 2);
                Require(profile.CapturedLifecycleOriginals.Count(row => row.Phase == "native-command-origin" && row.Original.IsCompletedSuccessfully) == 2);
                await Observe(profile.DrainLifecycleHandoffsAsync(session), "actual-two-origin-no-replay"); Require(stopCalls == 2);
            }
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            releaseCallback.TrySetResult(); releaseStop.TrySetResult(); releaseSecond.TrySetResult();
            if (profile is not null) ImportProfile(profile);
            foreach (var pending in new[] { submit, drain, modeOriginal, second }.Where(value => value is not null).Distinct())
                try { await Observe(pending!, "actual-lifecycle-finally-join"); } catch (Exception error) { failures.Add(error); }
            if (dispatcher is not null)
                try { await Observe(dispatcher.DisposeAsync().AsTask(), "actual-rpc-finally-close"); } catch (Exception error) { failures.Add(error); }
            if (profile is not null)
            {
                try { await Observe(profile.DisposeAsync().AsTask(), "actual-lifecycle-profile-close"); } catch (Exception error) { failures.Add(error); }
                finally { ImportProfile(profile); }
            }
        }
        if (failures.Count != 0) throw new AggregateException("Lifecycle fixture originals and cleanup.", failures);
    }
    private static HashSet<Exception> CaptureActualCarriers(AggregateException? aggregate, Exception? direct)
    {
        var captured = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>();
        if (aggregate is not null) pending.Push(aggregate); if (direct is not null) pending.Push(direct);
        while (pending.TryPop(out var current))
        {
            if (!captured.Add(current)) continue;
            if (current is AggregateException many) foreach (var child in many.InnerExceptions) pending.Push(child);
            else if (current.InnerException is { } child) pending.Push(child);
        }
        return captured;
    }
    private static bool OnlyKnownLeaves(Exception root, HashSet<Exception> actualCarriers, params Exception[] leaves)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (leaves.Any(leaf => ReferenceEquals(leaf, current))) continue;
            if (!actualCarriers.Contains(current)) return false; // Must belong to this exact cached close, not share a type or leaf.
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count != 0)
                foreach (var child in aggregate.InnerExceptions) pending.Push(child);
            else if ((current is RpcDispatchException or JsonlTransportException) && current.InnerException is { } child) pending.Push(child);
            else return false;
        }
        return true;
    }
    private static bool Contains(Exception root, Exception leaf)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>(); pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue; if (ReferenceEquals(current, leaf)) return true;
            if (current is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) pending.Push(child);
            else if (current.InnerException is { } child) pending.Push(child);
        }
        return false;
    }
    private sealed class FailedOutput(Exception failure) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => ValueTask.FromException(failure);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.FromException(failure);
        public override void Write(byte[] buffer, int offset, int count) => throw failure;
    }
}
