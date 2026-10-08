using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

internal static partial class ToolActivationTests
{
    private static IEnumerable<(string Name, Func<Task> Run)> LoadoutCases() =>
    [
        (Prefix + "loadout callbacks share original metadata and later active descriptions win", LoadoutOrder),
        (Prefix + "hidden declarations retain execution and durable activation across reopen", LoadoutPersistence),
        (Prefix + "loadout errors continue and invalid results merge no partial changes", LoadoutFailures),
        (Prefix + "hidden projection filters additions and removals after forced system context", LoadoutFinalProjection)
    ];

    private static async Task LoadoutOrder()
    {
        var calls = new List<string>(); var originals = new List<ToolLoadout>();
        await using var fixture = await Fixture.Create((name, loadout) =>
        {
            calls.Add(name); originals.Add(loadout);
            Names(["direct", "outer"], loadout.Declared.Select(tool => tool.Name));
            Names(["direct", "code", "deferred", "denied"], loadout.Callable.Select(tool => tool.Name));
            Check(loadout.Registered.Length == 7 && loadout.GetExposure("outer") == ToolExposure.ModelOnly,
                "Original metadata classification differs.");
            Check(loadout.Declared.All(tool => tool.Description == "activation fixture"), "A callback saw another callback's mutation.");
            return new() { Descriptions = ImmutableDictionary<string, string>.Empty.Add("direct", name + " description") };
        });
        Names(["direct", "outer"], calls);
        Check(ReferenceEquals(originals[0], originals[1]), "Active callbacks did not share the same original loadout.");
        var session = await fixture.New("order"); await using var owner = fixture.Owner(session);
        calls.Clear(); originals.Clear();
        await session.PromptAsync(Input());
        Check(calls.Count == 0, "Validation replay reran preparation callbacks.");
        var tools = new SessionSystemReplay().Replay(fixture.Source.Requests[0].Messages).Tools;
        Check(tools[0].Value.GetProperty("description").GetString() == "outer description", "Last active callback did not win.");
        var canonical = new SessionSystemReplay().Replay(session.Snapshot.Context.LlmMessages).Tools;
        Check(canonical[0].Value.GetProperty("description").GetString() == "activation fixture", "Request presentation mutated canonical history.");
    }

    private static async Task LoadoutPersistence()
    {
        var calls = new List<string>();
        await using var fixture = await Fixture.Create((name, loadout) =>
        {
            calls.Add(name);
            return name == "outer" ? new() { HiddenDeclarations = ["direct"] } :
                new() { Descriptions = ImmutableDictionary<string, string>.Empty.Add(name, "prepared " + name) };
        });
        fixture.Source.FirstTool = "direct";
        var session = await fixture.New("persist");
        await using (var owner = fixture.Owner(session))
        {
            await session.PromptAsync(Input());
            Names(["outer"], Declared(fixture.Source.Requests[0]));
            Check(fixture.Effects.Contains("direct"), "Hiding a declaration revoked root execution.");
            Names(["direct", "outer"], session.GetActiveTools());
            Names(["direct", "outer"], new SessionSystemReplay().Replay(session.Snapshot.Context.LlmMessages).Tools
                .Select(tool => tool.Value.GetProperty("name").GetString()!));
            calls.Clear();
            await session.SetActiveToolsAsync(["code"]);
            Names(["code"], calls);
            fixture.Source.FirstTool = null; fixture.Source.Requests.Clear();
            await session.PromptAsync(Input());
            Names(["code"], Declared(fixture.Source.Requests[0]));
            calls.Clear(); await session.SetActiveToolsAsync(["code"]);
            Check(calls.Count == 0, "No-op activation reran preparation.");
        }
        calls.Clear(); fixture.Source.Requests.Clear();
        var reopened = await fixture.Open(fixture.PathFor("persist"), default); await using var reopenedOwner = fixture.Owner(reopened);
        Names(["code"], reopened.GetActiveTools());
        await reopened.PromptAsync(Input());
        Names(["code"], Declared(fixture.Source.Requests[0]));
        Check(new SessionSystemReplay().Replay(fixture.Source.Requests[0].Messages).Tools[0].Value
            .GetProperty("description").GetString() == "prepared code", "Restored active loadout lost preparation.");
    }

    private static async Task LoadoutFailures()
    {
        await using var fixture = await Fixture.Create((name, _) => name == "direct"
            ? throw new InvalidOperationException("preparation failure")
            : new() { Descriptions = ImmutableDictionary<string, string>.Empty.Add("outer", "valid later callback") });
        var session = await fixture.New("failure"); await using var owner = fixture.Owner(session);
        await session.PromptAsync(Input());
        Check(fixture.LoadoutErrors.Contains("direct"), "Preparation failure was not reported.");
        Check(new SessionSystemReplay().Replay(fixture.Source.Requests[0].Messages).Tools[1].Value
            .GetProperty("description").GetString() == "valid later callback", "An earlier error suppressed later callbacks.");
        var tool = new ToolLoadoutTool(fixture.Binding.RegisteredToolDeclarations[0], ToolExposure.Direct);
        var errors = new List<string>();
        var presentation = ToolLoadoutPresentation.Prepare(new([tool], [tool], [tool]), (_, _) => new()
        {
            Descriptions = ImmutableDictionary<string, string>.Empty.Add("direct", "bad\ud800"), HiddenDeclarations = ["direct"]
        }, (name, _) => errors.Add(name));
        Check(errors.Count == 1 && presentation.HiddenDeclarations.IsEmpty, "Invalid result partially merged its hidden declarations.");
    }

    private static async Task LoadoutFinalProjection()
    {
        await using var fixture = await Fixture.Create((_, _) => new() { HiddenDeclarations = ["direct"] });
        var initial = fixture.Binding.CreateDeclarationMessage("canonical", 1);
        var removed = new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"\",\"timestamp\":2,\"toolsRemoved\":[{\"name\":\"direct\"},{\"name\":\"outer\"}]}"));
        var final = fixture.Binding.Hooks.FinalTransformRequestMessages!;
        var projected = await final([initial, removed], default);
        Check(initial.WireBody.Value.GetProperty("toolsAdded").GetArrayLength() == 2, "Projection rewrote its input.");
        Check(projected[0].WireBody.Value.GetProperty("toolsAdded").GetArrayLength() == 1 &&
            projected[1].WireBody.Value.GetProperty("toolsRemoved")[0].GetProperty("name").GetString() == "outer",
            "Hidden projection failed to filter complete addition/removal history.");
        // The Agent's final projection receives the forced-system output, not its canonical predecessor.
        var forced = new TranscriptEntry("system", JsonData.Parse(initial.WireBody.ToString().Replace("canonical", "forced", StringComparison.Ordinal)));
        var forcedProjection = await final([forced], default);
        Check(forcedProjection[0].WireBody.Value.GetProperty("content").GetString() == "forced" &&
            forcedProjection[0].WireBody.Value.GetProperty("toolsAdded").GetArrayLength() == 1, "Forced text or hidden declaration projection was lost.");
        var hooks = fixture.Binding.Hooks with { BeforePrompt = (_, _) => ValueTask.FromResult(new AgentPromptPreparation([])
        {
            AfterContext = (messages, _) => ValueTask.FromResult(messages.Where(message => message.Role != "system").Prepend(forced).ToImmutableArray())
        }) };
        await using var agent = new PiSharp.Agent.Agent(new(Model, fixture.Source, fixture.Binding.Tools, Hooks: hooks), () => 1, new LoadoutSink());
        agent.ReplaceMessages([initial]);
        await agent.PromptAsync(Input());
        var request = fixture.Source.Requests.Single();
        Names(["outer"], Declared(request));
        Check(request.Messages[0].WireBody.Value.GetProperty("content").GetString() == "forced", "Actual Agent projection ordering differs.");
        Check(agent.Snapshot.Messages[0].WireBody.Value.GetProperty("content").GetString() == "canonical", "Final projection mutated Agent history.");
    }
    private sealed class LoadoutSink : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => ValueTask.CompletedTask; }
}
