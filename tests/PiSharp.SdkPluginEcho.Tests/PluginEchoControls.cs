using System.Reflection;
using System.Runtime.Loader;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;

namespace PiSharp.SdkPluginEcho.Tests;

/// <summary>Future root harness supplies exact published package approvals; this fixture creates no trust.</summary>
public sealed record EchoPackageAdmission(JsonData Metadata, string Root, ExtensionSourceScope SourceScope,
    string EffectiveScopeId, ExtensionTrustDecision Inspection, PluginExecutionDecision Execution);

/// <summary>Exact retained original identities, not serialized substitutes for the raw fault roots.</summary>
public sealed record EchoOriginal(string Phase, Task? Original, AggregateException? Aggregate, Exception? Direct);

public static class PluginEchoControls
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(PluginAssemblyLoaderOptions options,
        EchoPackageAdmission sender, EchoPackageAdmission receiver, Action<EchoOriginal> retain) =>
    [
        ("sdk-plugin-echo.actual-two-loader-contexts-shared-object-mutation-and-retirement",
            () => Run(options, sender, receiver, retain, false)),
        ("sdk-plugin-echo.actual-held-listener-original-joins-owner-retirement",
            () => Run(options, sender, receiver, retain, true))
    ];

    private static async Task Run(PluginAssemblyLoaderOptions options, EchoPackageAdmission sender,
        EchoPackageAdmission receiver, Action<EchoOriginal> retain, bool held)
    {
        var failures = new List<Exception>();
        var records = new List<EchoOriginal>();
        var joined = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        ExtensionRegistry? registry = null;
        PluginAssemblyLoader? loader = null;
        var subsidiary = new List<(string Phase, Task Original)>();
        LoadedExtension? a = null, b = null;
        Task? close = null, listener = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!held) release.SetResult();
        var payload = new Dictionary<string, object?>
        {
            ["count"] = 0, ["order"] = new List<string>(), ["listenerOriginal"] = release.Task,
            ["captureListener"] = (Action<Task>)(original => listener = original)
        };
        var previous = AppContext.GetData("PiSharp.SdkPluginEcho.Payload");
        try
        {
            registry = new ExtensionRegistry();
            loader = new PluginAssemblyLoader(options);
            AppContext.SetData("PiSharp.SdkPluginEcho.Payload", payload);
            a = await JoinValue("load-sender", () => Load(sender));
            b = await JoinValue("load-receiver", () => Load(receiver));
            Require(a.IsCollectible && b.IsCollectible && a.SnapshotDirectory != b.SnapshotDirectory,
                "Two actual private collectible loader generations required.");
            await Join("command-first", () => registry!.InvokeCommandAsync(registry.CaptureSnapshot(),
                "sdk-echo-send", JsonData.EmptyObject).AsTask());
            Require(ReferenceEquals(payload, payload.GetValueOrDefault("received")) &&
                ReferenceEquals(payload, payload.GetValueOrDefault("returned")), "Object alias lost across actual plugins.");
            Require((string?)payload.GetValueOrDefault("mutation") == "changed-by-real-receiver" &&
                (int)payload["count"]! == 1, "Actual receiver mutation/count missing.");
            Require(((List<string>)payload["order"]!).SequenceEqual(
                new[] { "sender-emit", "receiver-mutate", "sender-return" }), "Synchronous start/echo order changed.");
            Require(ReferenceEquals(listener, release.Task), "Actual listener original was substituted.");
            var senderContext = AssemblyLoadContext.GetLoadContext((Assembly)payload["senderAssembly"]!);
            var receiverContext = AssemblyLoadContext.GetLoadContext((Assembly)payload["receiverAssembly"]!);
            Require(senderContext is { IsCollectible: true } && receiverContext is { IsCollectible: true } &&
                !ReferenceEquals(senderContext, receiverContext), "Fixture types were not loaded through distinct real contexts.");
            close = b.DisposeAsync().AsTask();
            if (held) Require(!close.IsCompleted, "Owner retired before held user original settled.");
            release.TrySetResult();
            await Join("listener-original", () => listener!);
            await Join("receiver-retirement", () => close!);
            await Join("command-after-retirement", () => registry!.InvokeCommandAsync(registry.CaptureSnapshot(),
                "sdk-echo-send", JsonData.EmptyObject).AsTask());
            Require((int)payload["count"]! == 1 && ((List<string>)payload["order"]!).Last() == "sender-emit",
                "Retired physical listener entered again without a user unsubscribe.");
            Require(b.UnloadRequested, "Actual loaded owner did not finish retirement.");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            release.TrySetResult();
            if (listener is not null) await Cleanup("listener-final", () => listener!);
            if (close is not null) await Cleanup("receiver-close-final", () => close!);
            if (b is not null) await Cleanup("receiver-final", () => b.DisposeAsync().AsTask());
            if (a is not null) await Cleanup("sender-final", () => a.DisposeAsync().AsTask());
            if (loader is not null) await Cleanup("loader-final", () => loader.DisposeAsync().AsTask());
            if (registry is not null) await Cleanup("registry-final", () => registry.DisposeAsync().AsTask());
            foreach (var item in subsidiary) await Cleanup(item.Phase, () => item.Original);
            try { AppContext.SetData("PiSharp.SdkPluginEcho.Payload", previous); }
            catch (Exception error) { failures.Add(error); }
            // Reporting callbacks are borrowed and may fail; do not let them hide earlier originals.
            foreach (var record in records)
                try { retain(record); } catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Actual plugin echo and cleanup original inventory.", failures);

        Task<LoadedExtension> Load(EchoPackageAdmission package) => loader!.LoadAsync(package.Metadata, package.Root,
            package.SourceScope, package.EffectiveScopeId, package.Inspection, package.Execution, registry!,
            activateOwner: Activate, retireOwner: Retire);

        Task<RegistrationScope> Activate(string owner, IPiSharpExtension extension, CancellationToken token)
        {
            var original = registry!.ActivateAsync(owner, extension, token);
            subsidiary.Add(("native-activation-" + owner, original));
            return original; // Genuine production owner activation, unchanged original.
        }
        Task Retire(RegistrationScope scope)
        {
            var original = scope.DisposeAsync().AsTask();
            subsidiary.Add(("native-retirement-" + scope.OwnerId, original));
            return original; // Loader still ensures exact native scope disposal after this joined hook.
        }

        async Task<T> JoinValue<T>(string phase, Func<Task<T>> invoke)
        {
            var original = CaptureValue(phase, invoke);
            try { var value = await original.ConfigureAwait(false); records.Add(new(phase, original, null, null)); joined.Add(original); return value; }
            catch (Exception direct) { RecordFailure(phase, original, direct); throw; }
        }
        async Task Join(string phase, Func<Task> invoke)
        {
            var original = Capture(phase, invoke);
            if (joined.Contains(original)) return;
            try { await original.ConfigureAwait(false); records.Add(new(phase, original, null, null)); joined.Add(original); }
            catch (Exception direct) { RecordFailure(phase, original, direct); throw; }
        }
        Task<T> CaptureValue<T>(string phase, Func<Task<T>> invoke)
        {
            try { return invoke() ?? throw new InvalidOperationException("Missing original Task."); }
            catch (Exception error) { records.Add(new(phase, null, null, error)); throw; }
        }
        Task Capture(string phase, Func<Task> invoke)
        {
            try { return invoke() ?? throw new InvalidOperationException("Missing original Task."); }
            catch (Exception error) { records.Add(new(phase, null, null, error)); throw; }
        }
        void RecordFailure(string phase, Task original, Exception direct)
        {
            var aggregate = original.Exception;
            records.Add(new(phase, original, aggregate, direct)); joined.Add(original);
            if (aggregate is not null) failures.Add(aggregate);
        }
        async Task Cleanup(string phase, Func<Task> invoke)
        {
            try { await Join(phase, invoke); } catch (Exception direct) { failures.Add(direct); }
        }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
