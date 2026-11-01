using System.Security.Cryptography;
using PiSharp.CustomAgents.Plugins;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;
using PiSharp.Durable.Storage;

// Decision 0012, P9: plugin DLLs and the native extension adapter.
internal static partial class Program
{
    private sealed class TestServices(params (Type Type, object Service)[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.FirstOrDefault(service => service.Type == serviceType).Service;
    }

    /// <summary>A fixture project's build output, beside this test program's (same configuration and target).</summary>
    private static string FixtureOutput(string project)
    {
        var bin = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var tests = bin.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(tests, project, "bin", bin.Parent!.Name, bin.Name);
    }

    /// <summary>Copies a fixture's output into <paramref name="folder"/>, over what is there (a rebuild, as a host sees it).</summary>
    private static string CopyFixture(string project, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(FixtureOutput(project))) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        return folder;
    }

    private const string V1 = "PiSharp.CustomAgents.PluginFixture.V1";
    private const string V2 = "PiSharp.CustomAgents.PluginFixture.V2";

    /// <summary>A team whose orchestrator selects <paramref name="sources"/> and calls the tool its input names: <c>call &lt;tool&gt; &lt;json&gt;</c>.</summary>
    private static (AgentTeam Team, FauxAgentModels Models) PluginTeam(params string[] sources)
    {
        var models = new FauxAgentModels();
        var team = Team(models, "lead");
        team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Calls plugin tools", ToolSources = sources });
        models.On(request => Is(request, "lead") && !request.AfterTools && request.LastUserText!.StartsWith("call ", StringComparison.Ordinal), request =>
        {
            var parts = request.LastUserText!.Split(' ', 3);
            return FauxReply.ToolCall(parts[1], parts.Length > 2 ? parts[2] : "{}");
        });
        models.On(request => Is(request, "lead") && request.AfterTools, request => (request.ToolResults[0].IsError ? "error: " : "") + request.LastToolResult);
        models.On(request => Is(request, "lead"), request => "tools: " + string.Join(",", Own(request.Tools)));
        return (team, models);
    }

    internal static IEnumerable<(string, Func<Task>)> PluginCases()
    {
        yield return Case("agents.plugins.load-registers-tool-and-agent", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture(V1, Path.Combine(root, "fixture"));
            var (team, _) = PluginTeam("plugin.fixture");
            var log = new List<string>();
            await using var loader = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(root)], Services = new TestServices((typeof(List<string>), log)) });
            var info = await loader.LoadAsync(folder);
            Equal("fixture", info.Id, "the manifest ID");
            Equal(1, info.Generation, "the first generation");
            Equal("plugin_echo,plugin_slow,plugin_identity", string.Join(",", info.Tools), "its tools");
            Equal("plugin-helper", string.Join(",", info.Agents), "its agent");
            Check(team.Catalog.Find("plugin-helper")!.ToolSources.Contains("plugin.fixture"), "the plugin's agent selects the plugin's tools");
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("v1:hi", (await session.AskAsync("call plugin_echo {\"text\":\"hi\"}")).Text, "the plugin tool runs");
            Equal("registered:v1#1", string.Join(",", log), "registered once");
        });

        yield return Case("agents.plugins.shared-contract-identity", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture(V1, Path.Combine(root, "fixture"));
            var (team, _) = PluginTeam("plugin.fixture");
            await using var loader = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(root)] });
            await loader.LoadAsync(folder);
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("Default|PiSharp.CustomAgents.Plugin:fixture#1", (await session.AskAsync("call plugin_identity")).Text,
                "the contract types are the host's; the plugin has its own context");
        });

        yield return Case("agents.plugins.hot-reload-swaps-in-place", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture(V1, Path.Combine(root, "fixture"));
            var (team, _) = PluginTeam("plugin.fixture");
            var log = new List<string>();
            var errors = new List<Exception>();
            await using var loader = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions
            {
                Trust = [new PluginTrust(root)],
                Services = new TestServices((typeof(List<string>), log)),
                Debounce = TimeSpan.FromMilliseconds(400),
                OnError = errors.Add,
            });
            await loader.LoadAsync(folder);
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("v1:hi", (await session.AskAsync("call plugin_echo {\"text\":\"hi\"}")).Text, "version 1");
            using (loader.Watch("fixture"))
            {
                CopyFixture(V2, folder);
                await WaitUntilAsync(() => Task.FromResult(loader.Plugins.Single().Generation == 2 || errors.Count > 0), TimeSpan.FromSeconds(20));
            }
            Check(errors.Count == 0, "the reload succeeded: " + string.Join("; ", errors.Select(error => error.Message)));
            Equal("v2:hi", (await session.AskAsync("call plugin_echo {\"text\":\"hi\"}")).Text, "version 2 in place");
            Equal("new in v2", (await session.AskAsync("call plugin_new")).Text, "the added tool");
            Check(team.Catalog.Find("plugin-helper")!.Instructions == "PLUGIN v2", "the agent was updated");
            await loader.WhenRetiredAsync();
            lock (log) Check(log.Contains("disposed:v1") && log.Contains("registered:v2#2"), "version 1 retired: " + string.Join(",", log));
        });

        yield return Case("agents.plugins.reload-while-executing-drains", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture(V1, Path.Combine(root, "fixture"));
            var (team, _) = PluginTeam("plugin.fixture");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var loader = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions
            {
                Trust = [new PluginTrust(root)],
                Services = new TestServices((typeof(TaskCompletionSource), gate), (typeof(TaskCompletionSource<string>), started)),
            });
            await loader.LoadAsync(folder);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var slow = session.AskAsync("call plugin_slow");
            Equal("v1", await started.Task.WaitAsync(TimeSpan.FromSeconds(20)), "the call runs version 1");
            CopyFixture(V2, folder);
            Equal(2, (await loader.ReloadAsync("fixture")).Generation, "reloaded while the call runs");
            var retired = loader.WhenRetiredAsync();
            await Task.Delay(200);
            Check(!retired.IsCompleted, "version 1 waits for its running call");
            gate.TrySetResult();
            Equal("v1 slow done", (await slow).Text, "the running call finished on its own code");
            await retired.WaitAsync(TimeSpan.FromSeconds(20));

            Equal("v2:hi", (await session.AskAsync("call plugin_echo {\"text\":\"hi\"}")).Text, "new calls run version 2");
        });

        yield return Case("agents.plugins.trust-denied", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture(V1, Path.Combine(root, "fixture"));
            var (team, _) = PluginTeam("plugin.fixture");
            await using (var elsewhere = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(Temp("other"))] }))
                await ThrowsAsync<PluginTrustException>(() => elsewhere.LoadAsync(folder), "a folder outside the trust list");
            await using (var nobody = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [] }))
                await ThrowsAsync<PluginTrustException>(() => nobody.LoadAsync(folder), "default deny");
            await using (var pinned = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(root, "00")] }))
            {
                var refused = await ThrowsAsync<PluginTrustException>(() => pinned.LoadAsync(folder), "a pinned hash that does not match");
                Check(refused.Message.Contains("SHA-256", StringComparison.Ordinal), refused.Message);
            }
            Check(team.Catalog.FindSource("plugin.fixture") is null, "nothing was installed");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "AgentsPluginFixture.V1.dll"))));
            await using var exact = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(root, hash.ToLowerInvariant())] });
            Equal(1, (await exact.LoadAsync(folder)).Generation, "the matching hash loads");
        });

        yield return Case("agents.plugins.load-failure-rolls-back", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture(V1, Path.Combine(root, "fixture"));
            var (team, _) = PluginTeam("plugin.fixture", "host");
            await using var loader = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(root)] });
            // A conflict with a host tool the same agent selects: the first load is refused and nothing stays installed.
            var host = new DynamicToolSource("host", [AgentTool.Create("plugin_echo", "The host's own", _ => Task.FromResult<AgentToolResult>("host"))]);
            team.Catalog.AddSource(host);
            var conflict = await ThrowsAsync<PluginLoadException>(() => loader.LoadAsync(folder), "a tool name conflict");
            Check(conflict.Message.Contains("two tools named 'plugin_echo'", StringComparison.Ordinal), conflict.Message);
            Check(team.Catalog.FindSource("plugin.fixture") is null && team.Catalog.Find("plugin-helper") is null, "rolled back");
            host.Set([]);
            Equal(1, (await loader.LoadAsync(folder)).Generation, "loads once the conflict is gone");
            // A reload whose entry throws keeps the loaded generation.
            File.WriteAllText(Path.Combine(folder, AgentPluginLoader.ManifestName),
                """{"id":"fixture","contract":"pisharp.agents/1","assembly":"AgentsPluginFixture.V1.dll","settings":{"fail":true}}""");
            var failed = await ThrowsAsync<PluginLoadException>(() => loader.ReloadAsync("fixture"), "a failing entry");
            Check(failed.Message.Contains("the fixture was told to fail", StringComparison.Ordinal), failed.Message);
            Equal(1, loader.Plugins.Single().Generation, "generation 1 stays");
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("v1:hi", (await session.AskAsync("call plugin_echo {\"text\":\"hi\"}")).Text, "and keeps working");
        });

        yield return Case("agents.plugins.pisharp-extension-adapter-maps-result", async () =>
        {
            var root = Temp("plugins");
            var folder = CopyFixture("PiSharp.CustomAgents.ExtensionFixture", Path.Combine(root, "ext"));
            var (team, _) = PluginTeam("plugin.ext");
            await using var loader = new AgentPluginLoader(team.Catalog, new AgentPluginLoaderOptions { Trust = [new PluginTrust(root)] });
            var info = await loader.LoadAsync(folder);
            Equal("ext_upper,ext_fail", string.Join(",", info.Tools), "the extension's tools");
            Check(info.Diagnostics.Any(line => line.Contains("command 'hello' is not supported", StringComparison.Ordinal)), "the command is reported: " +
                string.Join("; ", info.Diagnostics));
            var tools = team.Catalog.FindSource("plugin.ext")!.Tools;
            var upper = tools.Single(tool => tool.Name == "ext_upper");
            var fail = tools.Single(tool => tool.Name == "ext_fail");
            Equal(ToolReplay.Safe, upper.Replay, "listed in replaySafe");
            Equal(ToolExecutionMode.Sequential, upper.ExecutionMode, "sequential");
            Equal(ToolReplay.Unsafe, fail.Replay, "not listed");
            Equal("Model", string.Join(",", fail.Callers!.Value), "model-only exposure");
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("HELLO", (await session.AskAsync("call ext_upper {\"text\":\"hello\"}")).Text, "content maps to the output");
            Equal("error: nope", (await session.AskAsync("call ext_fail {\"text\":\"x\"}")).Text, "isError maps");
        });
    }
}
