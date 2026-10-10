// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/index.ts (builtInExtensions),
// packages/coding-agent/src/core/package-manager.ts (resolve: builtin:<name> enabled unless the user extensions setting excludes it, a
// project +/-/! entry overriding; resolveExtensionSources: -e builtin:<name>), packages/coding-agent/src/core/resource-loader.ts (the
// extension paths with noExtensions and disabledBuiltinExtensions, "Unknown built-in extension", omitReplacedExtensions, reload) and
// packages/coding-agent/src/main.ts (--no-mcp: disabledBuiltinExtensions ["mcp"]; extension load errors stop the run).
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Pi;

// A built-in extension that is not loaded registers nothing: codemode and tool_search are not declared, MCP servers do not connect,
// the llama.cpp provider and /llama do not exist. Authored from the pinned sources by reading; nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> BuiltinExtensionCases() =>
    [
        ("builtins.settings-disable-codemode-and-tool-search-and-a-project-override-wins", BuiltinSettings),
        ("builtins.no-extensions-and-explicit-e-builtin", BuiltinNoExtensions),
        ("builtins.unknown-e-builtin-stops-the-run", BuiltinUnknown),
        ("builtins.mcp-setting-no-mcp-and-e-builtin-mcp", BuiltinMcp),
        ("builtins.replaceable-built-ins-yield-to-an-extension", Sync(BuiltinReplaced)),
        ("builtins.llama-cpp-disabled-registers-no-provider-and-no-command", BuiltinLlama),
        ("builtins.reload-resolves-the-built-in-extensions-again", BuiltinReload),
        ("builtins.reload-loads-a-built-in-extension-enabled-meanwhile", BuiltinReloadEnables),
        ("builtins.rpc-mode-registers-no-disabled-built-in", BuiltinRpc),
    ];

    private static async Task BuiltinRpc()
    {
        using var sandbox = new Sandbox("builtins-rpc");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:llama.cpp","-builtin:codemode"]}""");
        var input = new ScriptedInput();
        input.Send("""{"id":"1","type":"prompt","message":"/llama"}""");
        using var output = new LineOutput(frame =>
        {
            if (frame["type"]?.GetValue<string>() == "agent_end" || frame["success"]?.GetValue<bool>() == false) input.Complete();
        });
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var registration = deadline.Token.Register(input.Complete);
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        Equal(0, await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "read,codemode,tool_search"],
            host, CancellationToken.None), "rpc exit; " + stderr);
        // Without the llama.cpp extension /llama is a prompt; without codemode only read and tool_search are declared.
        Equal(1, sandbox.Requests.Count, "/llama went to the model");
        Check(sandbox.Requests[0].Body!.Contains("/llama", StringComparison.Ordinal), "the prompt text");
        Names(["read", "tool_search"], DeclaredTools(sandbox.Requests[0]), "declared tools");
    }

    private static readonly string[] BuiltinModel = ["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5"];

    private static string[] DeclaredTools(Seen request) => request.Json.TryGetProperty("tools", out var declared)
        ? [.. declared.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)] : [];

    private static async Task<string[]> BuiltinTools(Sandbox sandbox, params string[] extra)
    {
        lock (sandbox.Requests) sandbox.Requests.Clear();
        var (code, _, stderr) = await sandbox.Run([.. BuiltinModel, "--tools", "read,codemode,tool_search", .. extra, "hi"]);
        Equal(0, code, "exit; " + stderr);
        return DeclaredTools(sandbox.Requests.Single());
    }

    private static async Task BuiltinSettings()
    {
        using var sandbox = new Sandbox("builtins-settings");
        Names(["read", "codemode", "tool_search"], await BuiltinTools(sandbox), "every built-in extension loads by default");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:codemode","-builtin:tool-search"]}""");
        Names(["read"], await BuiltinTools(sandbox), "-builtin:<name> in the user extensions setting");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"extensions":["+builtin:codemode"]}""");
        Names(["read", "codemode"], await BuiltinTools(sandbox), "a project +builtin:<name> overrides the user setting");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"extensions":["!builtin:codemode"]}""");
        Names(["read"], await BuiltinTools(sandbox), "a project !builtin:<name>");
        // An entry naming no built-in extension is not one (resolve() lists only the known names).
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["+builtin:nope"]}""");
        File.Delete(Path.Combine(sandbox.Cwd, ".pi", "settings.json"));
        Names(["read", "codemode", "tool_search"], await BuiltinTools(sandbox), "+builtin:<unknown> in settings");
    }

    private static async Task BuiltinNoExtensions()
    {
        using var sandbox = new Sandbox("builtins-no-extensions");
        Names(["read"], await BuiltinTools(sandbox, "--no-extensions"), "--no-extensions disables the built-in extensions");
        Names(["read", "codemode"], await BuiltinTools(sandbox, "-ne", "-e", "builtin:codemode"), "-e builtin:<name> still loads");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:tool-search"]}""");
        Names(["read", "codemode", "tool_search"], await BuiltinTools(sandbox, "-e", "builtin:tool-search"), "-e builtin:<name> over a disabling setting");
    }

    private static async Task BuiltinUnknown()
    {
        using var sandbox = new Sandbox("builtins-unknown");
        var (code, stdout, stderr) = await sandbox.Run([.. BuiltinModel, "-e", "builtin:nope", "hi"]);
        Equal(1, code, "exit");
        Equal("", stdout, "stdout");
        Check(stderr.Contains("Error: Failed to load extension \"builtin:nope\": Unknown built-in extension: builtin:nope\n", StringComparison.Ordinal), "load error: " + stderr);
        Check(stderr.Contains(PiExtensionLoading.LoadFailureHint, StringComparison.Ordinal), "hint: " + stderr);
        Equal(0, sandbox.Requests.Count, "no request");
        // main.ts disabledBuiltinExtensions filters known and unknown names alike before they load.
        Equal(0, (await sandbox.Run([.. BuiltinModel, "--no-mcp", "-e", "builtin:mcp", "hi"])).Code, "--no-mcp with -e builtin:mcp");
    }

    private static async Task<(bool Connected, string[] Tools)> BuiltinMcpRun(Sandbox sandbox, params string[] extra)
    {
        lock (sandbox.Requests) sandbox.Requests.Clear();
        var connected = new List<string>();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null) with
        {
            CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
            {
                CreateChannel = entry => (actual, token) => { lock (connected) connected.Add(entry.Name); return ValueTask.FromResult<PiSharp.Extensions.Mcp.Runtime.IMcpAdmittedRequestChannel>(new FakeMcpChannel("lookup")); }
            }
        };
        Equal(0, await PiCommand.RunAsync([.. BuiltinModel, .. extra, "hi"], host, CancellationToken.None), "exit; " + stderr);
        lock (connected) return (connected.Contains("docs"), DeclaredTools(sandbox.Requests.Single()));
    }

    private static async Task BuiltinMcp()
    {
        using var sandbox = new Sandbox("builtins-mcp");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""");
        var loaded = await BuiltinMcpRun(sandbox);
        Check(loaded.Connected && loaded.Tools.Contains("mcp__docs__lookup"), "the built-in MCP extension connects mcp.json servers: " + string.Join(",", loaded.Tools));
        foreach (var (what, args) in new (string, string[])[] { ("--no-mcp", ["--no-mcp"]), ("--no-mcp -e builtin:mcp", ["--no-mcp", "-e", "builtin:mcp"]),
            ("--no-extensions", ["--no-extensions"]) })
        {
            var off = await BuiltinMcpRun(sandbox, args);
            Check(!off.Connected && !off.Tools.Contains("mcp__docs__lookup"), what + ": no server connects: " + string.Join(",", off.Tools));
        }
        var explicitly = await BuiltinMcpRun(sandbox, "--no-extensions", "-e", "builtin:mcp");
        Check(explicitly.Connected && explicitly.Tools.Contains("mcp__docs__lookup"), "--no-extensions -e builtin:mcp connects");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:mcp"]}""");
        var disabled = await BuiltinMcpRun(sandbox);
        Check(!disabled.Connected && !disabled.Tools.Contains("mcp__docs__lookup"), "-builtin:mcp: no server connects");
        // Without MCP support codemode and tool_search stay (they are built-in extensions of their own).
        Names(["read", "codemode", "tool_search"], await BuiltinTools(sandbox), "codemode and tool_search without MCP support");
    }

    private static void BuiltinReplaced()
    {
        static PiLoadedExtension Extension(int index, string path, string descriptor) =>
            new(index, path, path) { Descriptor = JsonNode.Parse(descriptor)!.AsObject() };
        PiSharp.Cli.Packages.PiResolvedPaths Resolved(params string[] disabled) => new([.. PiBuiltinExtensions.Names.Select(name =>
            new PiSharp.Cli.Packages.PiResolvedResource("builtin:" + name, !disabled.Contains(name), new() { Source = "builtin", Scope = "user", Origin = "top-level" }))], [], [], []);
        var loaded = new[]
        {
            Extension(0, "/ext/a.ts", """{"tools":[{"name":"codemode"}],"commands":[{"name":"mcp"}],"flags":[]}"""),
            Extension(1, "/ext/b.ts", """{"tools":[{"name":"other"}],"commands":[],"flags":[{"name":"verbose"}]}""")
        };
        var resolution = PiBuiltinExtensions.Resolve(null, Resolved(), false, [], loaded);
        Names(["llama.cpp", "tool-search"], resolution.Enabled.Order(StringComparer.Ordinal), "codemode and mcp are replaced");
        Equal(0, resolution.Errors.Length, "no error");
        Names([
            "Extension package \"builtin:codemode\": Extension /ext/a.ts registers tool `codemode`, so built-in extension `codemode` was not loaded. To use `codemode`, run `pi config` and make sure it is enabled under Built-in extensions, then disable or remove the existing extension. We recommend only having one or the other loaded at a time.",
            "Extension package \"builtin:mcp\": Extension /ext/a.ts registers command `/mcp`, so built-in extension `mcp` was not loaded. To use `mcp`, run `pi config` and make sure it is enabled under Built-in extensions, then disable or remove the existing extension. We recommend only having one or the other loaded at a time."
        ], resolution.Warnings.Select(warning => warning.Type + ":" + warning.Message).Select(text => text["warning:".Length..]), "replacement warnings");
        // A built-in extension that is not loaded is not replaced (no warning); llama.cpp is not replaceable.
        var disabled = PiBuiltinExtensions.Resolve(null, Resolved("codemode"), false, [],
            [.. loaded, Extension(2, "/ext/c.ts", """{"tools":[],"commands":[{"name":"llama"}],"flags":[]}""")]);
        Names(["llama.cpp", "tool-search"], disabled.Enabled.Order(StringComparer.Ordinal), "disabled codemode, replaced mcp");
        Equal(1, disabled.Warnings.Length, "only the loaded built-in is reported");
        // The -e built-ins lead, --no-extensions keeps only them, disabledBuiltinExtensions removes them.
        var cli = PiBuiltinExtensions.Resolve(["builtin:mcp", "builtin:x", "./ext.ts"], Resolved(), true, ["llama.cpp"], []);
        Names(["mcp"], cli.Enabled, "-e with --no-extensions");
        Names(["Failed to load extension \"builtin:x\": Unknown built-in extension: builtin:x"], cli.Errors.Select(error => error.Message), "unknown -e name");
    }

    private static async Task BuiltinLlama()
    {
        using var sandbox = new Sandbox("builtins-llama");
        sandbox.Vars["LLAMA_BASE_URL"] = "http://127.0.0.1:9";
        Check((await sandbox.Runtime().CreateModelRegistryAsync(CancellationToken.None)).CheckAuth("llama.cpp") is not null, "the llama.cpp provider is configured");
        Equal(null, (await (sandbox.Runtime() with { LlamaProvider = () => false }).CreateModelRegistryAsync(CancellationToken.None)).CheckAuth("llama.cpp"),
            "without the llama.cpp extension there is no llama.cpp provider");
        // Outside interactive mode the built-in /llama only warns; without the extension /llama is a prompt.
        var (code, _, stderr) = await sandbox.Run([.. BuiltinModel, "/llama"]);
        Equal(0, code, "exit; " + stderr);
        Equal(0, sandbox.Requests.Count, "/llama is the built-in command");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:llama.cpp"]}""");
        (code, _, stderr) = await sandbox.Run([.. BuiltinModel, "/llama"]);
        Equal(0, code, "exit; " + stderr);
        Equal(1, sandbox.Requests.Count, "/llama goes to the model");
        Check(sandbox.Requests[0].Body!.Contains("/llama", StringComparison.Ordinal), "the prompt text");
        // --model llama.cpp/<id> finds no such provider.
        (code, _, stderr) = await sandbox.Run([.. BuiltinModel[..1], "--model", "llama.cpp/qwen", "hi"]);
        Equal(1, code, "llama.cpp model without the extension; " + stderr);
    }

    private static async Task BuiltinReload()
    {
        using var sandbox = new Sandbox("builtins-reload");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""");
        // The first answer turns codemode and MCP support off in the settings; /reload resolves the built-in extensions again.
        sandbox.Respond = (_, index) =>
        {
            if (index == 0) sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:codemode","-builtin:mcp"]}""");
            return AnthropicText("ok");
        };
        var connected = new List<string>();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null) with
        {
            CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
            {
                CreateChannel = entry => (actual, token) => { lock (connected) connected.Add(entry.Name); return ValueTask.FromResult<PiSharp.Extensions.Mcp.Runtime.IMcpAdmittedRequestChannel>(new FakeMcpChannel("lookup")); }
            }
        };
        Equal(0, await PiCommand.RunAsync([.. BuiltinModel, "--tools", "read,codemode,mcp__docs__lookup", "first", "/reload", "second"], host, CancellationToken.None), "exit; " + stderr);
        Equal(2, sandbox.Requests.Count, "two prompts reach the model");
        Names(["codemode", "mcp__docs__lookup", "read"], DeclaredTools(sandbox.Requests[0]).Order(StringComparer.Ordinal), "before the reload");
        Names(["read"], DeclaredTools(sandbox.Requests[1]), "after the reload: no codemode, no MCP tools");
    }

    private static async Task BuiltinReloadEnables()
    {
        using var sandbox = new Sandbox("builtins-reload-enable");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:tool-search","-builtin:mcp"]}""");
        sandbox.Respond = (_, index) =>
        {
            if (index == 0) sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{}");
            return AnthropicText("ok");
        };
        var connected = new List<string>();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null) with
        {
            CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
            {
                CreateChannel = entry => (actual, token) => { lock (connected) connected.Add(entry.Name); return ValueTask.FromResult<PiSharp.Extensions.Mcp.Runtime.IMcpAdmittedRequestChannel>(new FakeMcpChannel("lookup")); }
            }
        };
        Equal(0, await PiCommand.RunAsync([.. BuiltinModel, "--tools", "read,tool_search,mcp__docs__lookup", "first", "/reload", "second"], host, CancellationToken.None), "exit; " + stderr);
        Equal(2, sandbox.Requests.Count, "two prompts reach the model");
        Names(["read"], DeclaredTools(sandbox.Requests[0]), "before the reload");
        // The reload loads tool-search and MCP support: mcp.json is read and its server connects.
        Names(["mcp__docs__lookup", "read", "tool_search"], DeclaredTools(sandbox.Requests[1]).Order(StringComparer.Ordinal), "after the reload");
        lock (connected) Names(["docs"], connected, "the server connected once MCP support loaded");
    }
}
