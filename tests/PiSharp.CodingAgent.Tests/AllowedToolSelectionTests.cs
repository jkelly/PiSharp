using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools;

internal static class AllowedToolSelectionTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (StartupToolSelectionTests.Prefix + "policy absent empty explicit settings and exclusions", Projection),
        (StartupToolSelectionTests.Prefix + "policy no-tools modes and explicit precedence", Modes),
        (StartupToolSelectionTests.Prefix + "policy hidden model-only script-only default extension selection", Exposures),
        (StartupToolSelectionTests.Prefix + "policy filters each catalog refresh and ordinal names", Refresh),
        (StartupToolSelectionTests.Prefix + "CLI captures lifetime explicit cap without settings cap", CliCapture),
        (StartupToolSelectionTests.Prefix + "filtered real registry ignores later activation of a filtered tool", Registry)
    ];
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"Expected {string.Join('|', expected)}; actual {string.Join('|', actual)}.");
    private static Task Projection()
    {
        var unrestricted = AllowedToolSelection.Create(); Check(unrestricted.AllowedNames is null && unrestricted.IsAllowed("extension"), "Absent list capped registry.");
        Names(["read", "bash", "edit", "write"], unrestricted.InitialNames);
        var empty = AllowedToolSelection.Create([]); Check(empty.AllowedNames is { Count: 0 } && !empty.IsAllowed("read"), "Empty list was treated as absent.");
        var explicitNames = AllowedToolSelection.Create(["write", "read", "write"], ["read"], configuredDefaults: ["ls"]);
        Names(["write"], explicitNames.InitialNames); Check(!explicitNames.IsAllowed("read") && !explicitNames.IsAllowed("ls"), "Exclusion/explicit cap lost.");
        var settings = AllowedToolSelection.Create(configuredDefaults: ["ls"], excluded: ["write"]);
        Names(["ls"], settings.InitialNames); Check(settings.IsAllowed("read") && !settings.IsAllowed("write"), "Settings accidentally became lifetime cap.");
        return Task.CompletedTask;
    }
    private static Task Modes()
    {
        var all = AllowedToolSelection.Create(noTools: NoToolsMode.All); Names([], all.InitialNames);
        Check(!all.IsAllowed("extension") && !all.IncludeDefaultExtensions, "All mode retained extension reachability.");
        var builtin = AllowedToolSelection.Create(noTools: NoToolsMode.Builtin); Names([], builtin.InitialNames);
        Check(builtin.IsAllowed("read") && builtin.IncludeDefaultExtensions, "Builtin mode became a lifetime restriction.");
        var explicitNames = AllowedToolSelection.Create(["read"], noTools: NoToolsMode.All); Names(["read"], explicitNames.InitialNames);
        Check(explicitNames.IsAllowed("read") && !explicitNames.IsAllowed("write"), "Explicit list did not override all mode.");
        return Task.CompletedTask;
    }
    private static Task Exposures()
    {
        ImmutableArray<ToolSelectionDescriptor> tools = [new("read", ToolExposure.Direct, true, false),
            new("visible", ToolExposure.Direct, true, true), new("model", ToolExposure.ModelOnly, true, true),
            new("disabled", ToolExposure.Direct, false, true), new("hidden", ToolExposure.Hidden, true, true),
            new("script", ToolExposure.Codemode, true, true), new("deferred", ToolExposure.Deferred, true, true)];
        Names(["visible", "model"], AllowedToolSelection.Create(noTools: NoToolsMode.Builtin).SelectInitial(tools));
        var named = AllowedToolSelection.Create(["script", "hidden", "disabled", "model"]);
        Names(["disabled", "model"], named.SelectInitial(tools));
        Names(["model", "disabled", "hidden", "script"], named.FilterCatalog(tools, tool => tool.Name).Select(tool => tool.Name));
        Names([], AllowedToolSelection.Create(noTools: NoToolsMode.All).SelectInitial(tools));
        return Task.CompletedTask;
    }
    private static Task Refresh()
    {
        var policy = AllowedToolSelection.Create(["read", "extension"], ["extension"]);
        Names(["read"], policy.FilterCatalog(ImmutableArray.Create("write", "read", "extension"), name => name));
        Names(["read"], policy.FilterCatalog(ImmutableArray.Create("new", "extension", "Read", "read"), name => name));
        Check(!policy.IsAllowed("Read"), "Ordinal names changed."); return Task.CompletedTask;
    }
    private static async Task CliCapture()
    {
        var selected = ToolSelectionCliConfiguration.Resolve(["read"], null)!;
        Check(selected.LifetimePolicy is not null && selected.LifetimePolicy.IsAllowed("read") && !selected.LifetimePolicy.IsAllowed("write"), "CLI cap not captured.");
        Check(ToolSelectionCliConfiguration.Resolve(null, null) is null, "Absent selection changed.");
        var empty = ToolSelectionCliConfiguration.Resolve([], null)!; Check(empty.LifetimePolicy?.AllowedNames is { Count: 0 }, "Explicit empty cap lost.");
        using var fixture = new StartupSettingsTests.Fixture(); var files = new StartupSettingsTests.Files();
        files.Text[fixture.User] = "{\"defaultTools\":[\"read\"]}";
        var settings = await StartupSettings.LoadAsync(new(UserPath: fixture.User), files);
        Check(ToolSelectionCliConfiguration.Resolve(null, settings)!.LifetimePolicy is null, "Settings-only selection became a CLI lifetime cap.");
    }
    private static async Task Registry()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var path = Path.Combine(fixture.Root, "restricted.jsonl");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var profile = await OfflineSessionProfile.CreateAsync(fixture.Root, path, null, [], [], [], stop.Token);
        var borrowed = profile.Registry.Resolve(profile.SelectedModel, []).Configuration;
        var catalog = new BuiltinToolCatalog(fixture.Root, fixture.Root);
        var policy = AllowedToolSelection.Create(["read"]);
        var registrations = policy.FilterCatalog(catalog.Registered, tool => tool.Name)
            .Select(tool => new SessionRegisteredTool(tool.Declaration, tool.Adapter)).ToImmutableArray();
        var registry = new SessionRuntimeRegistry([new(profile.SelectedModel, borrowed.Transport)], registrations, new Deny(),
            new() { LifetimeToolSelection = policy });
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "restricted",
            timestamp = "2026-10-05T00:00:00.000Z", cwd = fixture.Root })); var sequence = 0;
        await using var session = await PersistentAgentSession.CreateAsync(path, header, registry, profile.SelectedModel, () => 1,
            () => "entry-" + ++sequence, cancellationToken: stop.Token);
        await session.SetActiveToolsAsync(["read"], stop.Token); Names(["read"], session.GetActiveTools());
        var acknowledged = session.Snapshot;
        var before = await ReadAcknowledgedBytes(path, acknowledged.Log.CommittedByteLength, stop.Token);
        // Pi 1.1.0 setActiveToolsByName ignores names the --tools cap kept out of the registry instead of rejecting them,
        // so the filtered write binding is not reactivated and the unchanged selection appends nothing.
        await session.SetActiveToolsAsync(["read", "write"], stop.Token);
        var ignored = session.Snapshot;
        Check(acknowledged.Log.CommittedByteLength == ignored.Log.CommittedByteLength &&
            acknowledged.Log.Sequence == ignored.Log.Sequence && acknowledged.Context.LeafId == ignored.Context.LeafId,
            "Ignored activation changed acknowledged durable state.");
        var after = await ReadAcknowledgedBytes(path, ignored.Log.CommittedByteLength, stop.Token);
        Check(before.SequenceEqual(after), "Ignored activation mutated durable bytes.");
        Names(["read"], session.GetActiveTools()); Check(session.PendingToolNames.IsEmpty, "Filtered binding became pending.");
        // Selecting only the filtered tool is the empty selection, as upstream applies it.
        await session.SetActiveToolsAsync(["write"], stop.Token); Names([], session.GetActiveTools());
    }
    private static async Task<byte[]> ReadAcknowledgedBytes(string path, long committedLength, CancellationToken token)
    {
        Check(committedLength is > 0 and <= 65_536, "Fixture snapshot exceeds bounded read limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(stream.Length == committedLength, "Physical length differs from acknowledged snapshot.");
        var bytes = new byte[checked((int)committedLength)];
        await stream.ReadExactlyAsync(bytes.AsMemory(), token);
        Check(stream.Length == committedLength, "Physical length changed during idle snapshot read.");
        return bytes;
    }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(false)); }
    }
}
