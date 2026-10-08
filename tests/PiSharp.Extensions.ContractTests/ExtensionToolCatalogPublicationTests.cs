using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class ExtensionToolCatalogPublicationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("tool-catalog. prepare inert atomic commit and stable published receipt", Atomic),
        ("tool-catalog. withdrawn old handle cannot remove reused id", Reuse),
        ("tool-catalog. stale plans and foreign ownership leave current snapshot unchanged", Stale),
        ("tool-catalog. malformed and colliding batches fail before mutation", Invalid),
        ("tool-catalog. held retired callback remains charged until original settles", HeldLease),
        ("tool-catalog. new old-entry lease after prepare rechecks limits before commit", LateLease)
    ];
    private static ExtensionToolDescriptor Tool(string id, string name, ExtensionToolCallback? callback = null) =>
        new(id, name, "", JsonData.EmptyObject, callback ?? ((data, _, _) => ValueTask.FromResult(data)));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Tool catalog assertion failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Reject(Action action)
    {
        var rejected = false; try { action(); } catch (ExtensionRegistrationException) { rejected = true; }
        Check(rejected);
    }
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task Atomic()
    {
        await using var registry = new ExtensionRegistry();
        var calls = 0;
        var scope = await registry.ActivateAsync("owner", new Extension(api => api.RegisterTool(Tool("old", "old"))));
        var before = registry.CaptureSnapshot();
        var plan = registry.PrepareToolCatalogReplacement(scope, ["old"], [Tool("new", "new", (data, _, _) =>
        { calls++; return ValueTask.FromResult(data); })], before);
        Check(ReferenceEquals(before, registry.CaptureSnapshot()) && calls == 0 && plan.PublishedSnapshot is null);
        Check(plan.PreviewSnapshot.Tools.Single().Name == "new" && plan.PreviewSnapshot.Revision == before.Revision + 1);
        var published = plan.Commit();
        Check(ReferenceEquals(published, plan.PreviewSnapshot) && ReferenceEquals(published, registry.CaptureSnapshot()) &&
            ReferenceEquals(published, plan.Commit()) && ReferenceEquals(published, plan.PublishedSnapshot) && calls == 0);
        await registry.InvokeToolAsync(published, "new", JsonData.Null); Check(calls == 1);
    }
    private static async Task Reuse()
    {
        await using var registry = new ExtensionRegistry(); IExtensionRegistration? handle = null;
        var scope = await registry.ActivateAsync("owner", new Extension(api => handle = api.RegisterTool(Tool("same", "old"))));
        var old = registry.CaptureSnapshot();
        var now = registry.PrepareToolCatalogReplacement(scope, ["same"], [Tool("same", "new")], old).Commit();
        handle!.Dispose(); Check(ReferenceEquals(now, registry.CaptureSnapshot()));
        Reject(() => registry.InvokeToolAsync(old, "old", JsonData.Null).GetAwaiter().GetResult());
        await registry.InvokeToolAsync(now, "new", JsonData.Null);
        var empty = registry.PrepareToolCatalogReplacement(scope, ["same"], [], now).Commit();
        Check(empty.Tools.IsEmpty);
    }
    private static async Task Stale()
    {
        await using var registry = new ExtensionRegistry(); await using var other = new ExtensionRegistry();
        var scope = await registry.ActivateAsync("owner", new Extension(_ => { }));
        var foreign = await other.ActivateAsync("owner", new Extension(_ => { }));
        var before = registry.CaptureSnapshot();
        Reject(() => registry.PrepareToolCatalogReplacement(foreign, [], [], before));
        Reject(() => registry.PrepareToolCatalogReplacement(scope, [], [], other.CaptureSnapshot()));
        var plan = registry.PrepareToolCatalogReplacement(scope, [], [Tool("new", "new")], before);
        scope.RegisterTool(Tool("concurrent", "concurrent")); var concurrent = registry.CaptureSnapshot();
        Reject(() => plan.Commit()); Check(plan.PublishedSnapshot is null && ReferenceEquals(concurrent, registry.CaptureSnapshot()));
        var replayRejected = false; try { plan.Commit(); } catch (InvalidOperationException) { replayRejected = true; } Check(replayRejected);
    }
    private static async Task Invalid()
    {
        await using var registry = new ExtensionRegistry();
        var scope = await registry.ActivateAsync("owner", new Extension(api => api.RegisterTool(Tool("old", "old"))));
        await registry.ActivateAsync("other", new Extension(api => api.RegisterTool(Tool("foreign", "occupied"))));
        var before = registry.CaptureSnapshot();
        foreach (var batch in new[]
        {
            ImmutableArray.Create(Tool("new", "valid"), Tool("bad", "bad") with { Parameters = JsonData.Null }),
            ImmutableArray.Create(Tool("one", "same"), Tool("two", "same")),
            ImmutableArray.Create(Tool("one", "occupied")),
            ImmutableArray.Create(Tool("one", "first"), Tool("one", "second"))
        })
        { Reject(() => registry.PrepareToolCatalogReplacement(scope, ["old"], batch, before)); Check(ReferenceEquals(before, registry.CaptureSnapshot())); }
        Reject(() => registry.PrepareToolCatalogReplacement(scope, ["old", "old"], [], before));
        Reject(() => registry.PrepareToolCatalogReplacement(scope, ["foreign"], [], before));
        Check(ReferenceEquals(before, registry.CaptureSnapshot())); await registry.InvokeToolAsync(before, "old", JsonData.Null);
    }
    private static Task HeldLease() => LeaseCase(late: false);
    private static Task LateLease() => LeaseCase(late: true);
    private static async Task LeaseCase(bool late)
    {
        await using var registry = new ExtensionRegistry(new() { MaximumRegistrations = late ? 1 : 2 });
        var entered = Gate(); var release = Gate();
        var scope = await registry.ActivateAsync("owner", new Extension(api => api.RegisterTool(Tool("old", "old", async (data, _, _) =>
        { entered.TrySetResult(); await release.Task; return data; }))));
        var before = registry.CaptureSnapshot(); ExtensionRegistry.ToolCatalogReplacementPlan? plan = null;
        if (late) plan = registry.PrepareToolCatalogReplacement(scope, ["old"], [Tool("new", "new")], before);
        var original = registry.InvokeToolAsync(before, "old", JsonData.Null).AsTask();
        try
        {
            await entered.Task;
            if (late)
            { Reject(() => plan!.Commit()); Check(ReferenceEquals(before, registry.CaptureSnapshot())); }
            else
            {
                plan = registry.PrepareToolCatalogReplacement(scope, ["old"], [Tool("new", "new")], before);
                var published = plan.Commit(); Check(!original.IsCompleted);
                Reject(() => scope.RegisterTool(Tool("third", "third")));
                Check(ReferenceEquals(published, registry.CaptureSnapshot()));
            }
        }
        finally { release.TrySetResult(); await original; }
        if (late) registry.PrepareToolCatalogReplacement(scope, ["old"], [Tool("new", "new")], before).Commit();
        else scope.RegisterTool(Tool("third", "third"));
    }
}
