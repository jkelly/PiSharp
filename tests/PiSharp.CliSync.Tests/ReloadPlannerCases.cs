using System.Collections.Immutable;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;

// The host selector route through HostReloadPlanner that NativeHostReloadCoordinator uses for Pi 0.99.2 reload tools.
internal static partial class Program
{
    private sealed class Settings(string name) { internal string Name { get; } = name; }
    private static readonly CancellationTokenSource HostLifetime = new();

    private static HostReloadPlanner<Settings> Planner(Settings old, Settings staged, ImmutableArray<string> active,
        Func<Settings, Settings, ImmutableArray<string>, ImmutableArray<HostReloadTool>, IEnumerable<string>>? select,
        IEnumerable<string>? allowed = null)
    {
        static ValueTask Done() => ValueTask.CompletedTask;
        var operations = new HostReloadOperations<Settings>
        {
            StageSettingsAsync = _ => ValueTask.FromResult(staged), SyncQueueModesAsync = (_, _) => Done(), ResetApiProvidersAsync = (_, _) => Done(),
            ReloadResourcesAsync = (_, _) => Done(),
            DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([],
                [new("read", false, HostReloadToolExposure.Direct), new("grep", false, HostReloadToolExposure.Direct),
                 new("mcp__srv__a", true, HostReloadToolExposure.Direct), new("hidden", true, HostReloadToolExposure.Hidden)])),
            BuildRuntimeAsync = (_, _) => Done(), SessionShutdownAsync = (_, _, _) => Done(), InvalidateAndDrainAsync = _ => Done(),
            PublishAsync = (previous, candidate) => ValueTask.FromResult(new HostReloadPublicationReceipt(previous.Id, candidate.Id, HostReloadPublicationAuthority.New)),
            CleanupAsync = _ => Done(), BeforeSessionStartAsync = (_, _) => Done(), SessionStartAsync = (_, _, _) => Done(),
            ReportUnhandledMcpServersAsync = (_, _) => Done(), ExtendResourcesAsync = (_, _, _) => Done()
        };
        return new(new(1, old, new Dictionary<string, HostReloadFlagValue>(), active, false), 2, new([], []), operations, HostLifetime.Token,
            allowed, selectActiveTools: select);
    }

    private static async Task ReloadPlannerSelector()
    {
        Settings old = new("old"), staged = new("staged"); Settings? seenOld = null, seenStaged = null;
        var receipt = await Planner(old, staged, ["read"], (before, after, active, tools) =>
        {
            seenOld = before; seenStaged = after;
            Names(["read"], active, "selector previous active"); Names(["read", "grep", "mcp__srv__a", "hidden"], tools.Select(tool => tool.Name), "selector tools");
            return ["read", "grep"];
        }).ReloadAsync();
        Equal(ResourceReloadAuthority.New, receipt.Authority, "authority");
        Names(["read", "grep"], receipt.Candidate!.ActiveTools, "selected active tools");
        Check(ReferenceEquals(seenOld, old) && ReferenceEquals(seenStaged, staged), "Selector did not receive the old and staged payloads.");
        foreach (var invalid in new[] { new[] { "hidden" }, ["missing"], ["read", "read"] })
        {
            var rejected = await Planner(old, new("bad"), ["read"], (_, _, _, _) => invalid).ReloadAsync();
            Equal(ResourceReloadAuthority.Old, rejected.Authority, "invalid selection keeps the old generation");
        }
        Throws<ArgumentException>(() => Planner(old, staged, ["read"], (_, _, _, _) => [], allowed: ["read"]), "selector with allowlist");
        Names(["read", "mcp__srv__a"], (await Planner(old, staged, ["read"], null).ReloadAsync()).Candidate!.ActiveTools, "default selection unchanged");
    }
}
