// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-callers.test.ts ("who may call a tool"), as
// harness.callers.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - `Type.Object({})` is ToolRegistration.EmptyParameters; a probe stores what each nested call returned by tool name.
// - Upstream runs these on MemoryStorage only; so do they here.
// Authored, after them (harness.callers.deferred.*): deferred tools without tool_search, whose tool and its tool-search.test.ts cases are
// work list area k: what Agent.Tools, Agent.Deferred and Agent.Callable list for a deferred tool, loaded by modelTools' array or its add,
// removed, and loaded by control.addTools, which writes {add} (upstream agent.ts addTools and offeredTools).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessCallersCases() => CallersTests.Cases("harness.callers.");
}

/// <summary>The harness.callers.* cases (upstream harness-callers.test.ts).</summary>
internal static class CallersTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static ToolRegistration Tool(string name, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration
        {
            Name = name,
            Description = name,
            Parameters = ToolRegistration.EmptyParameters,
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonValue.Array(FauxText($"{name} ran")) }),
        });
        return extra is null ? tool : extra(tool);
    }

    private static readonly JsonObjectValue Done = FauxAssistantMessage("done");

    private static JsonObjectValue Call(string name, string id = "c1") =>
        FauxAssistantMessage([FauxToolCall(name, JsonObjectValue.Empty, id)], stopReason: "toolUse");

    /// <summary>A tool that calls each of <paramref name="names"/> as a nested call and records what it got back.</summary>
    private static (ToolRegistration Registration, ConcurrentDictionary<string, NestedToolExecutionResult> Received) Prober(params string[] names)
    {
        var received = new ConcurrentDictionary<string, NestedToolExecutionResult>(StringComparer.Ordinal);
        var registration = Define.Tool(new ToolRegistration
        {
            Name = "probe",
            Description = "probe",
            Parameters = ToolRegistration.EmptyParameters,
            Execute = async (_, api, callContext) =>
            {
                foreach (var name in names) received[name] = await api.ExecuteToolAsync(name, JsonObjectValue.Empty, callContext);
                return new ToolExecutionResult();
            },
        });
        return (registration, received);
    }

    private static async Task<List<string>> Offered(IConversation root) => [.. (await root.AgentAsync(Context)).Tools.Select(each => each.Name)];

    private static async Task<List<string>> Callable(IConversation root) => [.. (await root.AgentAsync(Context)).Callable.Select(each => each.Name)];

    private static async Task<List<string>> DeferredTools(IConversation root) => [.. (await root.AgentAsync(Context)).Deferred.Select(each => each.Name)];

    private static ChatSetup SetupWith(params ToolRegistration[] tools)
    {
        var setup = Setup();
        foreach (var each in tools) AddTool(setup.Registry, each);
        return setup;
    }

    private static string? FirstCode(NestedToolExecutionResult? result) => result?.Diagnostics.FirstOrDefault()?.Code;

    private static async Task<JsonValue?> ModelTools(IHarness harness, IConversation root) =>
        (await harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context)) is { } agent && agent.TryGetValue("modelTools", out var filter) ? filter : null;

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "offers-the-model-only-tools-it-may-call-and-lets-tools-call-only-tools-they-may-call", async () =>
        {
            var script = Tool("script", tool => tool with { Callers = [ToolCaller.Model] });
            var hidden = Tool("hidden", tool => tool with { Callers = [ToolCaller.Tools] });
            var both = Tool("both");
            var probe = Prober("script", "hidden", "both");
            var setup = SetupWith(script, hidden, both, probe.Registration);
            setup.Faux.SetResponses(Call("probe"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            Seq(["script", "both", "probe"], await Offered(root), "offered");
            Seq(["hidden", "both", "probe"], await Callable(root), "callable");
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal("tool_unavailable", FirstCode(probe.Received.GetValueOrDefault("script")), "script");
            MatchJson("""{"isError":false,"structuredOutput":"hidden ran"}""", probe.Received["hidden"].ToJson(), "hidden");
            MatchJson("""{"isError":false,"structuredOutput":"both ran"}""", probe.Received["both"].ToJson(), "both");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "narrows-what-the-model-is-offered-with-modeltools-leaving-the-rest-callable-by-tools", async () =>
        {
            var read = Tool("read");
            var bash = Tool("bash");
            var probe = Prober("read", "bash");
            var setup = SetupWith(read, bash, probe.Registration);
            setup.Faux.SetResponses(Call("probe"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Only(probe.Registration) }, Context);
            Seq(["probe"], await Offered(root), "offered");
            Seq(["read", "bash", "probe"], await Callable(root), "callable");
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal(false, probe.Received["read"].IsError, "read");
            Equal(false, probe.Received["bash"].IsError, "bash");

            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Edit(remove: [bash]) }, Context);
            Seq(["read", "probe"], await Offered(root), "offered after remove");
            SameJson("""{"remove":["bash"]}""", await ModelTools(harness, root), "stored modelTools");
            await root.ConfigureAsync(new AgentChange { ModelTools = Patch<ModelToolSelection>.Clear }, Context);
            Seq(["read", "bash", "probe"], await Offered(root), "offered after clear");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "never-offers-a-tool-that-is-not-enabled-nor-lets-tools-call-it", async () =>
        {
            var read = Tool("read");
            var bash = Tool("bash");
            var probe = Prober("bash");
            var setup = SetupWith(read, bash, probe.Registration);
            setup.Faux.SetResponses(Call("probe"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            // modelTools cannot bring back what tools disabled.
            await root.ConfigureAsync(new AgentChange
            {
                Tools = ToolSelection.Without(bash),
                ModelTools = ModelToolSelection.Only(bash, probe.Registration),
            }, Context);
            Seq(["probe"], await Offered(root), "offered");
            Seq(["read", "probe"], await Callable(root), "callable");
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            Equal("tool_unavailable", FirstCode(probe.Received.GetValueOrDefault("bash")), "bash");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "answers-a-model-issued-call-of-a-tool-it-was-not-offered-as-unavailable", async () =>
        {
            var read = Tool("read");
            var bash = Tool("bash");
            var setup = SetupWith(read, bash);
            setup.Faux.SetResponses(Call("bash"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Only(read) }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var entries = await root.EntriesAsync(new EntryQueryBounds(), 100, null, Context);
            var result = entries.Items.FirstOrDefault(entry => entry.Kind == "pi.tool-result");
            Check(result!.Model!.ToJson().Contains("Tool bash is not available", StringComparison.Ordinal), "result: " + result.Model!.ToJson());
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "applies-addtools-to-modeltools-too-so-the-model-is-offered-the-added-tools", async () =>
        {
            var extra = Tool("extra");
            var grow = Tool("grow", tool => tool with
            {
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty, Control = new ToolControl { AddTools = ["extra"] } }),
            });
            var setup = SetupWith(extra, grow);
            setup.Faux.SetResponses(Call("grow"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Only(grow) }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameJson("""["grow","extra"]""", await ModelTools(harness, root), "stored modelTools");
            Seq(["grow", "extra"], await Offered(root), "offered");
            await harness.CloseAsync(Context);
        });

        // ─── Authored: deferred tools (see the file header) ─────────────────

        yield return Case(prefix + "deferred.keeps-a-deferred-tool-from-the-model-lists-it-as-deferred-and-lets-tools-call-it", async () =>
        {
            var alpha = Tool("alpha");
            var issue = Tool("issue", tool => tool with { Deferred = true });
            var repos = Tool("repos", tool => tool with { Deferred = true });
            var probe = Prober("issue");
            var setup = SetupWith(alpha, issue, repos, probe.Registration);
            setup.Faux.SetResponses(Call("probe"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            Seq(["alpha", "probe"], await Offered(root), "offered");
            Seq(["issue", "repos"], await DeferredTools(root), "deferred");
            Seq(["alpha", "issue", "repos", "probe"], await Callable(root), "callable");
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            MatchJson("""{"isError":false,"structuredOutput":"issue ran"}""", probe.Received["issue"].ToJson(), "nested call of a deferred tool");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "deferred.loads-deferred-tools-in-add-order-after-the-declared-ones-and-never-widens", async () =>
        {
            var alpha = Tool("alpha");
            var issue = Tool("issue", tool => tool with { Deferred = true });
            var repos = Tool("repos", tool => tool with { Deferred = true });
            var omega = Tool("omega");
            var setup = SetupWith(alpha, issue, repos, omega);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            // Names that are not deferred or not registered have no effect in add; remove wins over add.
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Edit(add: [repos, omega, issue]) }, Context);
            Seq(["alpha", "omega", "repos", "issue"], await Offered(root), "offered");
            Seq([], await DeferredTools(root), "deferred");
            SameJson("""{"add":["repos","omega","issue"]}""", await ModelTools(harness, root), "stored modelTools");
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Edit(remove: [issue, alpha], add: [issue]) }, Context);
            Seq(["omega"], await Offered(root), "offered after remove");
            Seq(["issue", "repos"], await DeferredTools(root), "deferred after remove");
            SameJson("""{"remove":["issue","alpha"],"add":["issue"]}""", await ModelTools(harness, root), "stored edit");
            // An array offers exactly its tools, deferred ones too.
            await root.ConfigureAsync(new AgentChange { ModelTools = ModelToolSelection.Only(repos, alpha) }, Context);
            Seq(["repos", "alpha"], await Offered(root), "offered by an array");
            Seq(["issue"], await DeferredTools(root), "deferred with an array");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "deferred.loads-a-deferred-tool-with-control-addtools-into-modeltools-add", async () =>
        {
            var issue = Tool("issue", tool => tool with { Deferred = true });
            var load = Tool("load", tool => tool with
            {
                Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty, Control = new ToolControl { AddTools = ["issue"] } }),
            });
            var setup = SetupWith(issue, load);
            setup.Faux.SetResponses(Call("load"), Done);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            SameJson("""{"add":["issue"]}""", await ModelTools(harness, root), "stored modelTools");
            Seq(["load", "issue"], await Offered(root), "offered");
            Seq([], await DeferredTools(root), "deferred");
            // The request after the round offers the loaded tool: a pi.system entry appends its declaration to the declared ones.
            var added = (await AllEntries(root)).Where(entry => entry.Kind == "pi.system")
                .Select(entry => ((JsonObjectValue)entry.Model![0]).TryGetValue("toolsAdded", out var tools)
                    ? string.Join(",", ((JsonArrayValue)tools).Select(tool => S(((JsonObjectValue)tool)["name"]))) : "").ToList();
            Seq(["load", "issue"], added, "declarations added by each pi.system entry");
            // Loading it again writes nothing new.
            await root.CommitAsync(tx => Agents.AddToolsAsync(tx, root.Id, ["issue"]), Context);
            SameJson("""{"add":["issue"]}""", await ModelTools(harness, root), "stored modelTools after a second load");
            await harness.CloseAsync(Context);
        });
    }
}
