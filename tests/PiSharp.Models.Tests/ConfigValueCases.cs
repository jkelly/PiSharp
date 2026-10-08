using PiSharp.Cli.Models;

// Ported from packages/coding-agent/test/resolve-config-value.test.ts (v1.1.0), with the PiSharp command switch.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ConfigValueCases() =>
    [
        ("config-value.literals-templates-and-escapes", Sync(ConfigTemplates)),
        ("config-value.scoped-environment-before-process", Sync(ConfigScoped)),
        ("config-value.commands-refused-by-default-switch", Sync(ConfigRefused)),
        ("config-value.commands-run-trimmed-and-cached-when-enabled", Sync(ConfigCommands)),
        ("config-value.platform-shell-runner", Sync(ConfigShell)),
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

    private static void ConfigRefused()
    {
        Check(!ConfigValueCommands.RunByDefault, "the switch defaults to the PiSharp refusal");
        var runs = 0;
        var resolver = new ConfigValueResolver(Env(), runCommand: _ => { runs++; return "x"; });
        Check(!resolver.RunsCommands, "default resolver refuses");
        Throws<ConfigValueCommandRefusedException>(() => resolver.Resolve("!echo key"), "refused");
        var error = Throws<ConfigValueCommandRefusedException>(() => resolver.ResolveOrThrow("!echo key", "API key for provider \"p\""), "refused with description");
        Equal("Failed to resolve API key for provider \"p\" from shell command: PiSharp does not run shell commands for config values; store the value or an environment reference instead.",
            error.Message, "refusal message");
        Equal(0, runs, "nothing ran");
        Check(ConfigValueResolver.IsCommand("!x") && !ConfigValueResolver.IsCommand("$!x"), "command detection");
    }

    private static void ConfigCommands()
    {
        var runs = new List<string>();
        var outputs = new Queue<string?>(["value", "value-2", null, "uncached-1", "uncached-2"]);
        var resolver = new ConfigValueResolver(Env(), runCommands: true, runCommand: command => { runs.Add(command); return outputs.Dequeue(); });
        Equal("value", resolver.Resolve("!fetch key"), "first run");
        Equal("value", resolver.Resolve("!fetch key"), "cached");
        resolver.ClearCache();
        Equal("value-2", resolver.Resolve("!fetch key"), "after clear");
        Equal<string?>(null, resolver.Resolve("!fail"), "failure"); Equal<string?>(null, resolver.Resolve("!fail"), "failure cached");
        Equal("uncached-1", resolver.ResolveUncached("!again"), "uncached"); Equal("uncached-2", resolver.ResolveUncached("!again"), "uncached runs again");
        Names(["fetch key", "fetch key", "fail", "again", "again"], runs, "command text without !");
        Equal("Failed to resolve token from shell command: broken",
            Throws<InvalidOperationException>(() => new ConfigValueResolver(Env(), true, _ => null).ResolveOrThrow("!broken", "token"), "failed command").Message, "failed command message");
    }

    private static void ConfigShell()
    {
        var resolver = new ConfigValueResolver(Env(), runCommands: true);
        Equal("spaced-key", resolver.Resolve(OperatingSystem.IsWindows() ? "!echo   spaced-key  " : "!echo '  spaced-key  '"), "trimmed stdout");
        Equal<string?>(null, resolver.Resolve("!exit 1"), "non-zero exit");
        Equal<string?>(null, resolver.Resolve("!nonexistent-command-12345"), "missing command");
        if (!OperatingSystem.IsWindows()) Equal("hello-world", resolver.Resolve("!echo 'hello world' | tr ' ' '-'"), "shell features");
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
