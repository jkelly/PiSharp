using PiSharp.Cli.Models;

// Ported from packages/coding-agent/test/resolve-config-value.test.ts (v1.1.0). Commands run as upstream (owner decision 0004).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ConfigValueCases() =>
    [
        ("config-value.literals-templates-and-escapes", Sync(ConfigTemplates)),
        ("config-value.scoped-environment-before-process", Sync(ConfigScoped)),
        ("config-value.shell-commands-trimmed-and-failures-unresolved", Sync(ConfigShellCommands)),
        ("config-value.shell-commands-cached-until-cleared-and-uncached-rerun", ConfigShellCaching),
        ("config-value.windows-default-shell-fallback", Sync(ConfigDefaultShellFallback)),
        ("config-value.injected-runner-caching", Sync(ConfigCommands)),
        ("config-value.resolve-or-throw-messages", Sync(ConfigErrors)),
    ];

    private static void ConfigTemplates()
    {
        var resolver = new ConfigValueResolver(Env(("TEST_CONFIG_LEFT", "left"), ("TEST_CONFIG_RIGHT", "right")));
        Equal("literal-key", resolver.Resolve("literal-key"), "literal");
        Equal("left", resolver.Resolve("$TEST_CONFIG_LEFT"), "bare");
        Equal("left_right", resolver.Resolve("${TEST_CONFIG_LEFT}_$TEST_CONFIG_RIGHT"), "mixed");
        Equal("$TEST_CONFIG_LEFT", resolver.Resolve("$$TEST_CONFIG_LEFT"), "$$ escape");
        Equal("!literal-right", resolver.Resolve("$!literal-$TEST_CONFIG_RIGHT"), "$! escape");
        Equal("a$", resolver.Resolve("a$"), "trailing $");
        Equal("${bad-name}", resolver.Resolve("${bad-name}"), "invalid braced name is literal");
        Equal("${OPEN", resolver.Resolve("${OPEN"), "unclosed brace");
        Equal("$1x", resolver.Resolve("$1x"), "$ before a digit");
        Equal<string?>(null, resolver.Resolve("$MISSING"), "missing variable");
        Equal<string?>(null, new ConfigValueResolver(Env(("EMPTY", ""))).Resolve("$EMPTY"), "empty is missing");
        Equal("TEST_CONFIG_LEFT", ConfigValueResolver.GetEnvVarName("$TEST_CONFIG_LEFT"), "single name");
        Equal<string?>(null, ConfigValueResolver.GetEnvVarName("x$TEST_CONFIG_LEFT"), "not a single reference");
        Names(["A", "B"], ConfigValueResolver.GetEnvVarNames("$A-${B}-$A"), "distinct names");
        Names([], ConfigValueResolver.GetEnvVarNames("!echo $A"), "commands have no env names");
        Names(["B"], resolver.GetMissingEnvVarNames("$TEST_CONFIG_LEFT$B"), "missing names");
    }

    private static void ConfigScoped()
    {
        var resolver = new ConfigValueResolver(Env(("TEST_CONFIG_SCOPED", "process")));
        Equal("credential", resolver.Resolve("$TEST_CONFIG_SCOPED", new Dictionary<string, string> { ["TEST_CONFIG_SCOPED"] = "credential" }), "scoped first");
        Equal("process", resolver.Resolve("$TEST_CONFIG_SCOPED", new Dictionary<string, string> { ["TEST_CONFIG_SCOPED"] = "" }), "empty scoped falls through");
        var changing = "first";
        var dynamic = new ConfigValueResolver(name => name == "DYN" ? changing : null);
        Equal("first", dynamic.Resolve("$DYN"), "first read"); changing = "second";
        Equal("second", dynamic.Resolve("$DYN"), "environment values are not cached");
    }

    private static bool HasBash()
    {
        try { PiSharp.Tools.Processes.ShellDiscovery.Resolve(); return true; }
        catch (PiSharp.Tools.Processes.ShellDiscoveryException) { return false; }
    }

    // Upstream "executes shell commands and trims their output" and "returns undefined when command resolution fails". The configured
    // shell is bash (Git Bash on Windows), so the upstream command texts run unchanged.
    private static void ConfigShellCommands()
    {
        if (OperatingSystem.IsWindows() && !HasBash()) return; // The default-shell fallback case covers hosts without bash.
        var resolver = new ConfigValueResolver(Env());
        var tag = Guid.NewGuid().ToString("N");
        Equal("spaced-key", resolver.Resolve("!echo '  spaced-key  ' # " + tag), "trimmed stdout");
        Equal("line1\nline2", resolver.Resolve("!printf 'line1\\nline2' # " + tag), "multiline output");
        Equal("hello-world", resolver.Resolve("!echo 'hello world' | tr ' ' '-' # " + tag), "shell features");
        foreach (var command in new[] { "!exit 1", "!nonexistent-command-12345", "!printf ''" })
            Equal<string?>(null, resolver.ResolveUncached(command), command);
        Equal("Failed to resolve API key for provider \"p\" from shell command: exit 1",
            Throws<InvalidOperationException>(() => resolver.ResolveOrThrow("!exit 1", "API key for provider \"p\""), "failing command").Message, "failure text");
    }

    // Upstream "caches successful and failed commands until explicitly cleared" and "uncached resolution executes a command on every call".
    private static Task ConfigShellCaching() => WithTemp("config-cache", root =>
    {
        if (OperatingSystem.IsWindows() && !HasBash()) return Task.CompletedTask;
        var counter = Path.Combine(root, "counter"); File.WriteAllText(counter, "0");
        var escaped = counter.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
        var resolver = new ConfigValueResolver(Env());
        var success = $"!sh -c 'count=$(cat \"{escaped}\"); echo $((count + 1)) > \"{escaped}\"; echo value'";
        Equal("value", resolver.Resolve(success), "first"); Equal("value", resolver.Resolve(success), "cached");
        Equal("1", File.ReadAllText(counter).Trim(), "ran once");
        resolver.ClearCache();
        Equal("value", resolver.Resolve(success), "after clear"); Equal("2", File.ReadAllText(counter).Trim(), "ran again");
        var failure = $"!sh -c 'count=$(cat \"{escaped}\"); echo $((count + 1)) > \"{escaped}\"; exit 1'";
        Equal<string?>(null, resolver.Resolve(failure), "failure"); Equal<string?>(null, resolver.Resolve(failure), "failure cached");
        Equal("3", File.ReadAllText(counter).Trim(), "failure ran once");
        Equal("value", resolver.ResolveUncached(success), "uncached 1"); Equal("value", resolver.ResolveUncached(success), "uncached 2");
        Equal("5", File.ReadAllText(counter).Trim(), "uncached runs every call");
        resolver.ClearCache();
        return Task.CompletedTask;
    });

    // executeCommandUncached on Windows: without a configured (bash) shell the command falls back to execSync's default shell (cmd.exe).
    private static void ConfigDefaultShellFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new PiSharp.Tools.Processes.ShellHost(true, name => name == "ComSpec" ? Environment.GetEnvironmentVariable("ComSpec") : null,
            _ => false, Path.GetTempPath());
        Equal("fallback-value", ConfigValueResolver.RunShellCommand("echo fallback-value", host), "cmd fallback");
        Equal<string?>(null, ConfigValueResolver.RunShellCommand("exit 1", host), "cmd failure");
    }

    private static void ConfigCommands()
    {
        var runs = new List<string>();
        var outputs = new Queue<string?>(["value", "value-2", null, "uncached-1", "uncached-2"]);
        var resolver = new ConfigValueResolver(Env(), command => { runs.Add(command); return outputs.Dequeue(); });
        Check(ConfigValueResolver.IsCommand("!x") && !ConfigValueResolver.IsCommand("$!x"), "command detection");
        Equal("value", resolver.Resolve("!fetch key"), "first run");
        Equal("value", resolver.Resolve("!fetch key"), "cached");
        resolver.ClearCache();
        Equal("value-2", resolver.Resolve("!fetch key"), "after clear");
        Equal<string?>(null, resolver.Resolve("!fail"), "failure"); Equal<string?>(null, resolver.Resolve("!fail"), "failure cached");
        Equal("uncached-1", resolver.ResolveUncached("!again"), "uncached"); Equal("uncached-2", resolver.ResolveUncached("!again"), "uncached runs again");
        Names(["fetch key", "fetch key", "fail", "again", "again"], runs, "command text without !");
        Equal("Failed to resolve token from shell command: broken",
            Throws<InvalidOperationException>(() => new ConfigValueResolver(Env(), _ => null).ResolveOrThrow("!broken", "token"), "failed command").Message, "failed command message");
    }

    private static void ConfigErrors()
    {
        var resolver = new ConfigValueResolver(Env(("SET", "v")));
        Equal("Failed to resolve key from environment variable: ONE", Throws<InvalidOperationException>(() => resolver.ResolveOrThrow("$ONE", "key"), "one").Message, "one");
        Equal("Failed to resolve key from environment variables: ONE, TWO",
            Throws<InvalidOperationException>(() => resolver.ResolveOrThrow("$ONE-$SET-${TWO}", "key"), "two").Message, "two");
        Equal("v", resolver.ResolveOrThrow("$SET", "key"), "resolved");
        Equal(null, resolver.ResolveHeaders(new Dictionary<string, string> { ["A"] = "$MISSING" }), "unresolved headers dropped");
        Equal("v", resolver.ResolveHeaders(new Dictionary<string, string> { ["A"] = "$SET", ["B"] = "$MISSING" })!["A"], "resolved header kept");
        Equal("Failed to resolve provider \"p\" header \"B\" from environment variable: MISSING",
            Throws<InvalidOperationException>(() => resolver.ResolveHeadersOrThrow(new Dictionary<string, string> { ["B"] = "$MISSING" }, "provider \"p\""), "header").Message, "header");
    }
}
