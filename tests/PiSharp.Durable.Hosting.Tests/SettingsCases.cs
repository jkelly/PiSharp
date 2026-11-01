using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SettingsCases() =>
    [
        Case("settings.read-only-removes-writers-and-blocks-their-calls", async () =>
        {
            await using var device = await TestDevice.OpenAsync("settings-readonly");
            var session = await device.CreateAsync("readonly");
            var before = await session.ToolsAsync(1);
            Check(before.All(tool => tool.Offered), "every tool is offered at first");
            Check(!(await session.ConfigurationAsync(1)).ReadOnly, "not read-only at first");

            var configuration = await session.ConfigureAsync(1, new AgentConfigurationChange { ReadOnly = true });
            Check(configuration.ReadOnly, "read-only");
            var tools = (await session.ToolsAsync(1)).ToDictionary(tool => tool.Name);
            Check(tools["read"].Offered && tools["ask_user_question"].Offered, "readers stay");
            Check(!tools["bash"].Offered && !tools["write"].Offered && !tools["edit"].Offered, "writers leave");
            Equal(ToolEffect.Exec, tools["bash"].Effect, "bash's effect");
            Equal(ToolReplay.Unsafe, tools["bash"].Replay, "bash's replay policy");

            var blocked = await session.SendAsync(1, new SendRequest("!echo hi", "ro-1") { Wait = Answer });
            Check(blocked.AnswerText?.Contains("Tool bash is not available") == true, "a removed tool's call is refused: " + blocked.AnswerText);

            await device.ReopenAsync();
            var reopened = device.Host.ResolveSession(session.Id);
            Check((await reopened.ConfigurationAsync(1)).ReadOnly, "read-only survives a reopen");
            var back = await reopened.ConfigureAsync(1, new AgentConfigurationChange { ReadOnly = false });
            Check(!back.ReadOnly, "read-only off");
            Check((await reopened.ToolsAsync(1)).Single(tool => tool.Name == "bash").Offered, "bash is back");
        }),

        Case("settings.model-and-thinking-changes", async () =>
        {
            await using var device = await TestDevice.OpenAsync("settings-model");
            var session = await device.CreateAsync("model");
            var changed = await session.ConfigureAsync(1, new AgentConfigurationChange { Model = new ModelRef("faux", "echo-small"), ThinkingLevel = "minimal" });
            Equal("echo-small", changed.Model?.ModelId, "model");
            Equal("minimal", changed.ThinkingLevel, "thinking");
            Equal("echo-small", session.Model?.ModelId, "the session records Main's model");
            await ThrowsAsync<HostInvalidException>(() => session.ConfigureAsync(1, new AgentConfigurationChange { Model = new ModelRef("faux", "gone") }), "an unknown model");
            await ThrowsAsync<HostInvalidException>(() => session.ConfigureAsync(1, new AgentConfigurationChange { ThinkingLevel = "max" }), "an unknown level");
            await ThrowsAsync<HostInvalidException>(() => session.ConfigureAsync(1, new AgentConfigurationChange { Cwd = "missing" }), "a missing directory");
            await device.ReopenAsync();
            Equal("echo-small", device.Host.ResolveSession(session.Id).Model?.ModelId, "after a reopen");
        }),

        Case("settings.compaction-defaults-follow-the-window", () =>
        {
            Equal(new SessionCompaction(true, 16384, 32768, 20000), SessionCompaction.DefaultFor(128_000), "128k");
            Equal(new SessionCompaction(true, 22000, 50000, 20000), SessionCompaction.DefaultFor(272_000), "272k");
            Equal(new SessionCompaction(true, 750000, 50000, 20000), SessionCompaction.DefaultFor(1_000_000), "1M");
            Equal(SessionCompaction.PiDurableDefaults, SessionCompaction.DefaultFor(40_000), "a small window");
            Equal(SessionCompaction.PiDurableDefaults, SessionCompaction.DefaultFor(0), "an unknown window");
            Equal(200_000.0, SessionCompaction.DefaultFor(272_000).AutoAt(272_000), "auto-compact at 200k");
            Equal(250_000.0, SessionCompaction.DefaultFor(272_000).WaitAt(272_000), "wait at 250k");
            Equal(null, (SessionCompaction.PiDurableDefaults with { BackgroundTokens = 0 }).AutoAt(128_000), "background compaction off");
        }),

        Case("settings.compaction-updates-persist-and-refit", async () =>
        {
            await using var device = await TestDevice.OpenAsync("settings-compaction");
            var session = await device.CreateAsync("compaction");
            Equal(SessionCompaction.DefaultFor(128_000), session.Compaction, "the echo window's defaults");
            Check(!session.HasCustomCompaction, "not custom");
            var updated = session.UpdateCompaction(new SessionCompactionChange { ReserveTokens = 20000, BackgroundTokens = 0 });
            Equal(new SessionCompaction(true, 20000, 0, 20000), updated, "a partial update keeps the other fields");
            Throws<HostInvalidException>(() => session.UpdateCompaction(new SessionCompactionChange { ReserveTokens = 10 }), "reserve below 1024");
            Throws<HostInvalidException>(() => session.UpdateCompaction(new SessionCompactionChange { KeepRecentTokens = 1500.5 }), "a fraction");
            Equal(updated, session.Compaction, "a refused update changes nothing");

            await device.ReopenAsync();
            var reopened = device.Host.ResolveSession(session.Id);
            Equal(updated, reopened.Compaction, "stored across a reopen");

            // 40000 − 20000 leaves less than 32768 tokens: the settings go back to the new window's defaults.
            await reopened.ConfigureAsync(1, new AgentConfigurationChange { Model = new ModelRef("faux", "echo-small") });
            Check(!reopened.HasCustomCompaction, "refit to the defaults");
            Equal(SessionCompaction.DefaultFor(40_000), reopened.Compaction, "the small window's defaults");
            reopened.UpdateCompaction(new SessionCompactionChange { ReserveTokens = 4096 });
            await reopened.ConfigureAsync(1, new AgentConfigurationChange { Model = FauxDurableModels.EchoRef });
            Check(reopened.HasCustomCompaction, "settings that still fit are kept");
            await reopened.ConfigureAsync(1, new AgentConfigurationChange { ThinkingLevel = "low" });
            Equal(4096.0, reopened.Compaction.ReserveTokens, "kept");
        }),

        Case("settings.unknown-models-are-rerouted-on-open", async () =>
        {
            await using var device = await TestDevice.OpenAsync("settings-reroute");
            var session = await device.CreateAsync("reroute", model: new ModelRef("faux", "echo-small"));
            var second = await session.CreateAgentAsync();
            await device.ReopenAsync(new DeviceSetup { Models = new FauxDurableModels([FauxDurableModels.Echo]) });
            var reopened = device.Host.ResolveSession(session.Id);
            Equal(FauxDurableModels.EchoRef, (await reopened.ConfigurationAsync(1)).Model, "Main moved to the default model");
            Equal(FauxDurableModels.EchoRef, (await reopened.ConfigurationAsync(second.Id)).Model, "so did agent 2");
            Equal(FauxDurableModels.EchoRef, reopened.Model, "and the session's record");
            var reply = await reopened.SendAsync(1, new SendRequest("ok?", "reroute-1") { Wait = Answer });
            Equal("Echo: ok?", reply.AnswerText, "it answers on the default model");
        }),
    ];
}
