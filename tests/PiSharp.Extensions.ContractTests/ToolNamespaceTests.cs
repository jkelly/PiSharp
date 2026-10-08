using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;

internal static partial class ToolActivationTests
{
    private static IEnumerable<(string Name, Func<Task> Run)> NamespaceCases() =>
    [
        (Prefix + "namespace lookup matches source absent unknown exact name and optional description", NamespaceLookup),
        (Prefix + "namespace capture stays immutable across registry replacement and later binding", NamespaceCapture),
        (Prefix + "namespace metadata preserves session activation ordering and final policy authority", NamespaceAuthority),
        (Prefix + "namespace metadata uses existing registration and session size budgets", NamespaceBudgets)
    ];

    private static async Task NamespaceLookup()
    {
        var group = new ToolNamespace("mcp__docs", "Authored related tools \u03c0");
        var empty = new ToolNamespace("", ""); var unspecified = new ToolNamespace("optional");
        var captures = new List<ToolLoadout>();
        await using var fixture = await Fixture.Create((_, loadout) => { captures.Add(loadout); return null; }, namespaceForTool:
            name => name switch { "direct" or "hidden" => group, "code" => empty, "deferred" => unspecified, _ => null });
        Check(captures.Count == 2 && ReferenceEquals(captures[0], captures[1]), "Actual callbacks did not share captured metadata.");
        var original = captures[0];
        Names(["direct", "outer"], original.Declared.Select(tool => tool.Name));
        Names(["direct", "code", "deferred", "denied"], original.Callable.Select(tool => tool.Name));
        Names(["direct", "outer", "inactive", "code", "deferred", "denied", "hidden"], original.Registered.Select(tool => tool.Name));
        Check(ReferenceEquals(group, original.GetNamespace("direct")) && ReferenceEquals(group, original.GetNamespace("hidden")) &&
            original.GetNamespace("code") is { Name: "", Description: "" } && original.GetNamespace("deferred")?.Description is null &&
            original.GetNamespace("outer") is null && original.GetNamespace("unknown") is null &&
            original.GetNamespace("DIRECT") is null && original.GetNamespace("mcp__docs") is null,
            "Lookup guessed a namespace, required activation, changed optional description, or lost original author metadata.");
        Check(original.Registered.All(tool => !tool.Declaration.Value.TryGetProperty("namespace", out _)), "Grouping leaked into execution declarations.");
    }

    private static async Task NamespaceCapture()
    {
        await using var registry = new ExtensionRegistry(); IExtensionRegistry? live = null; IExtensionRegistration? target = null;
        var oldGroup = new ToolNamespace("mcp__old", "original"); var newGroup = oldGroup with { Name = "mcp__new", Description = "replacement" };
        var captured = new List<ToolLoadout>();
        await registry.ActivateAsync("namespace-owner", new Extension(host =>
        {
            live = host;
            host.RegisterTool(Tool("watch", ToolExposure.Direct) with { PrepareLoadout = loadout => { captured.Add(loadout); return null; } });
            target = host.RegisterTool(Tool("target", ToolExposure.Deferred) with { Namespace = oldGroup });
        }));
        var before = new ExtensionAgentBinding(registry, new Policy(), Validate); var loadout = captured.Single();
        target!.Dispose(); live!.RegisterTool(Tool("target", ToolExposure.Deferred) with { Namespace = newGroup });
        captured.Clear(); var after = new ExtensionAgentBinding(registry, new Policy(), Validate);
        Check(before.Snapshot.Revision < after.Snapshot.Revision && ReferenceEquals(oldGroup, before.Registrations.Single(tool => tool.Name == "target").Namespace) &&
            ReferenceEquals(oldGroup, loadout.GetNamespace("target")) && ReferenceEquals(newGroup, captured.Single().GetNamespace("target")),
            "Registry mutation rewrote captured metadata or later preparation missed the new revision.");
        Check(before.Registrations.Single(tool => tool.Name == "watch").Namespace is null && loadout.GetNamespace("missing") is null,
            "Absent namespace acquired synthetic state after registry mutation.");
    }

    private static async Task NamespaceAuthority()
    {
        var group = new ToolNamespace("mcp__trusted", "Metadata claims no permission"); var captures = new List<ToolLoadout>();
        var preparationNames = new List<string>();
        await using var fixture = await Fixture.Create((name, loadout) =>
            { preparationNames.Add(name); captures.Add(loadout); return null; }, namespaceForTool: _ => group);
        fixture.Source.FirstTool = "outer";
        fixture.Outer = async (context, _) =>
        {
            Names(["direct", "code", "deferred", "denied"], context.Tools);
            foreach (var name in new[] { "direct", "code", "deferred", "denied", "inactive", "hidden", "outer" })
                fixture.Outcomes[name] = await context.ExecuteToolAsync(name, JsonData.EmptyObject);
            return Result("namespace outer");
        };
        captures.Clear(); preparationNames.Clear(); await using var session = await fixture.New("namespace-session");
        AssertPreparationPair("configuration"); var configuredLoadout = captures[0];
        captures.Clear(); preparationNames.Clear(); await using var owner = fixture.Owner(session);
        AssertPreparationPair("invocation-owner binding");
        Check(!ReferenceEquals(configuredLoadout, captures[0]) && ReferenceEquals(group, configuredLoadout.GetNamespace("hidden")),
            "Owner rebinding reused or mutated the earlier captured loadout.");
        await session.PromptAsync(Input());
        foreach (var name in new[] { "direct", "code", "deferred" }) Check(!fixture.Outcomes[name].IsError, "Namespace changed callable execution.");
        foreach (var name in new[] { "denied", "inactive", "hidden", "outer" }) Check(fixture.Outcomes[name].IsError, "Namespace conferred execution authority.");
        Check(fixture.Policy.SawFinalCodeArguments && !fixture.Effects.Contains("denied"), "Namespace bypassed final argument policy.");
        Names(["direct", "outer"], Declared(fixture.Source.Requests[0]));
        Check(preparationNames.SequenceEqual(["direct", "outer"], StringComparer.Ordinal), "Ordinary prompt reran loadout preparation.");
        captures.Clear(); preparationNames.Clear(); await session.SetActiveToolsAsync(["hidden", "code", "code", "missing"]);
        Names(["code"], session.GetActiveTools());
        Check(captures.Count == 1 && preparationNames.SequenceEqual(["code"], StringComparer.Ordinal) && captures[0].Declared.Single().Name == "code" &&
            ReferenceEquals(group, captures[0].GetNamespace("direct")) && captures[0].GetExposure("outer") == ToolExposure.ModelOnly,
            "Ordered activation changed namespace lookup, exposure or hidden-name filtering.");

        void AssertPreparationPair(string boundary)
        {
            Check(captures.Count == 2 && preparationNames.SequenceEqual(["direct", "outer"], StringComparer.Ordinal),
                $"Namespace {boundary} preparation expected direct,outer; observed count={captures.Count}, names={string.Join(',', preparationNames)}.");
            Check(ReferenceEquals(captures[0], captures[1]) && captures.All(loadout => ReferenceEquals(group, loadout.GetNamespace("hidden"))),
                $"Namespace {boundary} preparation did not share original metadata or preserve hidden registered namespace identity.");
            Names(["direct", "outer"], captures[0].Declared.Select(tool => tool.Name));
            Names(["direct", "code", "deferred", "denied"], captures[0].Callable.Select(tool => tool.Name));
            Names(["direct", "outer", "inactive", "code", "deferred", "denied", "hidden"], captures[0].Registered.Select(tool => tool.Name));
        }
    }

    private static async Task NamespaceBudgets()
    {
        await using var registry = new ExtensionRegistry(new() { MaximumDescriptionCharacters = 32 });
        await Throws<ExtensionRegistrationException>(() => registry.ActivateAsync("oversized", new Extension(host =>
            host.RegisterTool(Tool("bounded", ToolExposure.Direct) with { Namespace = new("group", new string('x', 33)) }))));
        Check(registry.CaptureSnapshot().Tools.IsEmpty, "Rejected namespace left registration residue.");
        await using var aggregate = new ExtensionRegistry(new() { MaximumMetadataCharacters = 64 });
        await Throws<ExtensionRegistrationException>(() => aggregate.ActivateAsync("aggregate", new Extension(host =>
            host.RegisterTool(Tool("bounded", ToolExposure.Direct) with { Namespace = new(new string('g', 64)) }))));
        Check(aggregate.CaptureSnapshot().Tools.IsEmpty, "Namespace escaped existing aggregate metadata accounting.");
        await using var fixture = await Fixture.Create();
        var tool = new SessionRegisteredTool(fixture.Binding.RegisteredToolDeclarations[0], fixture.Binding.Adapters[0])
            { Namespace = new("group", new string('x', 4096)) };
        await Throws<SessionRuntimeRegistryException>(() => Task.FromResult(new SessionRuntimeRegistry([new(Model, fixture.Source)], [tool],
            fixture.Policy, new(MaximumCharacters: 1024))));
    }
}
