using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> AgentCases() =>
    [
        Case("agents.sessions-resolve-by-id-or-name", async () =>
        {
            await using var device = await TestDevice.OpenAsync("agents-sessions");
            var first = await device.CreateAsync("Twin");
            await device.CreateAsync("twin");
            Equal(first.Id, device.Host.ResolveSession(first.Id.ToUpperInvariant()).Id, "by ID, any case");
            Throws<HostAmbiguousException>(() => device.Host.ResolveSession("TWIN"), "a shared name");
            Throws<HostNotFoundException>(() => device.Host.ResolveSession("nope"), "an unknown name");
        }),

        Case("agents.resolve-by-number-main-or-name", async () =>
        {
            await using var device = await TestDevice.OpenAsync("agents-names");
            var session = await device.CreateAsync("names");
            var second = await session.CreateAgentAsync();
            // Conversation numbers come from a sequence other records share, so they are not 1, 2, 3.
            Equal($"Agent #{second.Id}", second.Name, "a second root's derived name uses its number");
            Equal(AgentKind.Root, second.Kind, "kind");
            Check(!second.Named, "not a custom name");
            Equal(1L, (await session.ResolveAgentAsync("MAIN")).Id, "main");
            Equal(second.Id, (await session.ResolveAgentAsync(second.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).Id, "a number");
            Equal(second.Id, (await session.ResolveAgentAsync($"agent #{second.Id}")).Id, "a derived name");

            await session.RenameAgentAsync(1, "Planner");
            Equal(1L, (await session.ResolveAgentAsync("main")).Id, "main still reaches agent 1 after a rename");
            Equal("Planner", (await session.ResolveAgentAsync("planner")).Name, "a custom name");
            await ThrowsAsync<HostInvalidException>(() => session.RenameAgentAsync(second.Id, "42"), "an all-digit name");
            await ThrowsAsync<HostInvalidException>(() => session.RenameAgentAsync(second.Id, "Main"), "main for another agent");
            await ThrowsAsync<HostNotFoundException>(() => session.RenameAgentAsync(99999, "x"), "an unknown agent");

            var third = await session.CreateAgentAsync(agentName: "planner");
            Equal("planner", third.Name, "a custom name at creation");
            await ThrowsAsync<HostAmbiguousException>(() => session.ResolveAgentAsync("Planner"), "a shared name");
            await ThrowsAsync<HostNotFoundException>(() => session.ResolveAgentAsync("99999"), "an unknown number");

            var restored = await session.RenameAgentAsync(1, "   ");
            Equal("Main", restored.Name, "a blank name restores the derived one");
            Check(!restored.Named, "no longer a custom name");
            await device.ReopenAsync();
            Equal("planner", (await device.Host.ResolveSession(session.Id).ResolveAgentAsync(third.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).Name, "custom names are stored");
        }),

        Case("agents.new-agents-copy-the-source-agent", async () =>
        {
            await using var device = await TestDevice.OpenAsync("agents-copy");
            var session = await device.CreateAsync("copy");
            await session.ConfigureAsync(1, new AgentConfigurationChange { ThinkingLevel = "high", Instructions = "Be brief." });
            var copy = await session.CreateAgentAsync();
            var configuration = await session.ConfigurationAsync(copy.Id);
            Equal("high", configuration.ThinkingLevel, "thinking");
            Equal("Be brief.", configuration.Instructions, "instructions");
            Equal(FauxDurableModels.EchoRef, configuration.Model, "model");
            Equal(device.Work, configuration.Cwd, "working directory");
            var cleared = await session.ConfigureAsync(copy.Id, new AgentConfigurationChange { Instructions = "" });
            Equal(null, cleared.Instructions, "the empty string removes instructions");
        }),

        Case("agents.fork-at-an-entry", async () =>
        {
            await using var device = await TestDevice.OpenAsync("agents-fork");
            var session = await device.CreateAsync("fork");
            var sent = await session.SendAsync(1, new SendRequest("hello", "fork-1") { Wait = Answer });
            Equal(SendStatus.Done, sent.Status, "the first answer");
            var at = sent.AnswerEntryId!.Value.Value;

            var fork = await session.ForkAsync(1, at, "Alternative");
            Equal(AgentKind.Fork, fork.Kind, "kind");
            Equal(1L, fork.ParentId, "parent");
            Equal(at, fork.ForkedAt, "fork entry");
            Equal("Alternative", fork.Name, "name");
            var unnamed = await session.ForkAsync(1, at);
            Equal($"Fork #{unnamed.Id}", unnamed.Name, "a fork's derived name");

            var reply = await session.SendAsync(fork.Id, new SendRequest("again", "fork-2") { Wait = Answer });
            Equal("Echo: again", reply.AnswerText, "the fork answers on its own");
            await ThrowsAsync<HostInvalidException>(() => session.ForkAsync(1, 9999), "an entry the agent does not have");
        }),

        Case("agents.busy-while-a-tool-runs", async () =>
        {
            var gate = new TestGate();
            await using var device = await TestDevice.OpenAsync("agents-busy", new DeviceSetup { Registry = RegistryWith(gate) });
            var session = await device.CreateAsync("busy");
            await session.SendAsync(1, new SendRequest("tool: hang {}", "busy-1"));
            await gate.Started.Task.WaitAsync(Answer);
            Check((await session.AgentsAsync()).Single(agent => agent.Id == 1).Busy, "Main is busy while its tool runs");
            Check((await session.SummaryAsync()).Busy, "and so is its session");
            gate.Release.TrySetResult();
            await WaitUntilAsync(async () => !(await session.AgentsAsync()).Single(agent => agent.Id == 1).Busy, "Main is idle");
        }),
    ];
}
