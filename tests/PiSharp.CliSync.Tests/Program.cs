using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

// Authored expectations for the Pi v1.1.0 sync rows cli.*, prompt.hidden-tools and tui.* (work packages 2c, 2d, 2h).
// Expected text is written from the upstream v1.1.0 sources; no upstream execution or genuine source capture credit.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new (string Id, Func<Task> Run)[]
        {
            ("cli.tools.parse-patterns-modifiers-and-no-mcp", Sync(ParseToolFlags)),
            ("cli.tools.exact-upstream-errors-through-commands", CommandErrors),
            ("cli.provider-requires-model-exact-error", ProviderRequiresModel),
            ("cli.tools.resolve-modifiers-from-available-defaults", Sync(ResolveModifiers)),
            ("selection.patterns-are-anchored-star-globs", Sync(Patterns)),
            ("selection.allowlist-keeps-unnamed-mcp-tools", Sync(McpKept)),
            ("selection.modifiers-change-the-default-selection", Sync(Modifiers)),
            ("selection.initial-patterns-and-tool-search-gate", Sync(InitialSelection)),
            ("selection.registry-drops-unnamed-direct-mcp-activation", Sync(RegistryActivation)),
            ("reload.default-tools-additions-only", Sync(ReloadAdditions)),
            ("reload.modifiers-allowlists-and-patterns", Sync(ReloadPolicies)),
            ("reload.planner-host-selector", ReloadPlannerSelector),
            ("prompt.hidden-tools-byte-exact-sections", Sync(HiddenPrompt)),
            ("prompt.skills-hint-reader-fallback-and-indirect", Sync(SkillsHint)),
            ("prompt.unhidden-docs-line-and-options", Sync(DocsAndOptions)),
            ("prompt.registry-passes-prepared-hidden-declarations", Sync(RegistryHidden)),
            ("keybindings.home-end-editor-only-defaults", Sync(Keybindings)),
            ("osc7501.format-byte-sequences", Sync(FormatStatus)),
            ("osc7501.query-and-reply-detection", Sync(Replies)),
            ("osc7501.channel-override-negotiation-and-restart", Sync(Channel)),
            ("osc7501.reporter-run-dialog-and-compaction-lifecycle", Sync(Reporter)),
            ("osc7501.terminal-view-start-report-and-stop", TerminalView)
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run(); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", failures, genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}].");
    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
    private static string Temp(string name) => Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-cli-sync", name);
}
