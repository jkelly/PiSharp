using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Authored only / UNEXECUTED. No personal settings, environment mutation or live calls.
internal static class TerminalNavigationSettingsCases
{
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("navigation-settings-global-project-default-and-null-precedence", Precedence),
        ("navigation-settings-typed-validation-duplicate-bom-and-unrelated-fields", Validation),
        ("navigation-settings-bounds-unreadable-scope-and-foreign-reader-fault", Bounds),
        ("navigation-settings-immutable-reload-fixed-paths-and-explicit-policy", Reload),
        ("navigation-settings-actual-rpc-startup-workspace-and-invalid-arguments", Workspace),
        ("navigation-settings-real-owned-files-configured-native-fork", e => Native(args[1], e, fork: true)),
        ("navigation-settings-real-owned-files-configured-native-none", e => Native(args[1], e, fork: false))
    ];
    private static TerminalNavigationSettings Read(string? global, string? project) =>
        TerminalNavigationSettingsLoader.Load(Path.Combine(Path.GetTempPath(), "authored-agent"), Path.Combine(Path.GetTempPath(), "authored-workspace"),
            path => path.Contains("authored-agent", StringComparison.Ordinal) ? global : project);
    private static Task Precedence(ConsumerEvidence e)
    {
        foreach (var (global, project, action, source) in new (string?, string?, TerminalDoubleEscapeAction, string)[]
        {
            (null, null, TerminalDoubleEscapeAction.Tree, "default"),
            ("{\"doubleEscapeAction\":\"fork\"}", "{}", TerminalDoubleEscapeAction.Fork, "global"),
            ("{\"doubleEscapeAction\":\"none\"}", "{\"doubleEscapeAction\":\"fork\"}", TerminalDoubleEscapeAction.Fork, "project"),
            ("{\"doubleEscapeAction\":\"fork\"}", "{\"doubleEscapeAction\":null}", TerminalDoubleEscapeAction.Tree, "project"),
            ("{\"doubleEscapeAction\":\"none\"}", "broken JSON", TerminalDoubleEscapeAction.None, "global")
        })
        {
            var result = Read(global, project); Check(result.Action == action && result.Source == source, "Scope/default precedence differs.");
            e.Observe("settings-scope-precedence", new { action = result.Action.ToString(), result.Source, result.GlobalStatus, result.ProjectStatus });
        }
        return Task.CompletedTask;
    }
    private static Task Validation(ConsumerEvidence e)
    {
        foreach (var rejected in new[] { "\"FORK\"", "\"invalid\"", "true", "1", "[]", "{}" })
        {
            var result = Read("{\"doubleEscapeAction\":\"none\"}", "{\"doubleEscapeAction\":" + rejected + "}");
            Check(result.Action == TerminalDoubleEscapeAction.Tree && result.Source == "project" && result.ProjectStatus == "invalid-value-default",
                "Invalid project value was coerced or revealed lower-scope policy.");
        }
        var duplicate = Read(null, "\uFEFF{\"unrelated\":{\"opaque\":true},\"doubleEscapeAction\":\"none\",\"doubleEscapeAction\":\"fork\"}");
        Check(duplicate.Action == TerminalDoubleEscapeAction.Fork, "BOM/last duplicate property behavior differs.");
        foreach (var root in new[] { "null", "[]", "true", "1", "\"fork\"" })
            Check(Read("{\"doubleEscapeAction\":\"none\"}", root).Action == TerminalDoubleEscapeAction.None, "Invalid root overrode valid scope.");
        e.Observe("settings-strict-enum-bom-last-property", new { duplicate.Action, duplicate.ProjectStatus }); return Task.CompletedTask;
    }
    private static Task Bounds(ConsumerEvidence e)
    {
        var huge = Read("{\"doubleEscapeAction\":\"none\"}", new string(' ', TerminalNavigationSettingsLoader.MaximumCharacters + 1));
        Check(huge.Action == TerminalDoubleEscapeAction.None && huge.ProjectStatus == "unreadable-invalid-or-bounded", "Oversize scope did not preserve global.");
        var deep = Read(null, new string('[', 65) + "0" + new string(']', 65));
        Check(deep.Action == TerminalDoubleEscapeAction.Tree && deep.ProjectStatus == "unreadable-invalid-or-bounded", "Depth bound was not enforced.");
        var denied = TerminalNavigationSettingsLoader.Load("authored-agent", "authored-project", _ => throw new UnauthorizedAccessException());
        Check(denied.Action == TerminalDoubleEscapeAction.Tree && denied.GlobalStatus == "unreadable-invalid-or-bounded", "Unreadable scope failed startup.");
        try { TerminalNavigationSettingsLoader.Load("authored-agent", "authored-project", _ => throw new InvalidOperationException("foreign")); throw new Exception("Reader bug swallowed."); }
        catch (InvalidOperationException error) when (error.Message == "foreign") { }
        e.Observe("bounded-settings-scopes", new { huge.ProjectStatus, depth = deep.ProjectStatus, denied.GlobalStatus }); return Task.CompletedTask;
    }
    private static Task Reload(ConsumerEvidence e)
    {
        var reads = new List<string>(); var global = "{\"doubleEscapeAction\":\"fork\"}"; var project = "{}";
        var keybindings = TerminalKeybindingConfigurationLoader.Load("authored-agent", "win32", readText: _ => "{}");
        string? Reader(string path) { reads.Add(path); return path.Contains("authored-agent", StringComparison.Ordinal) ? global : project; }
        var first = TerminalStartupConfigurationLoader.Load(keybindings, "authored-project", Reader);
        project = "{\"doubleEscapeAction\":\"none\"}"; var second = first.Reload();
        Check(first.DoubleEscapeAction == TerminalDoubleEscapeAction.Fork && second.DoubleEscapeAction == TerminalDoubleEscapeAction.None &&
            first.NavigationSettings!.ProjectPath == second.NavigationSettings!.ProjectPath && reads.Count == 4,
            "Reload changed prior immutable policy, recaptured a different workspace or failed to reload scopes.");
        var explicitTree = new TerminalKeybindingConfiguration(keybindings.AgentDirectory, keybindings.ConfigPath, "win32", "missing", false,
            [], [], () => keybindings) { DoubleEscapeAction = TerminalDoubleEscapeAction.Tree };
        var configured = TerminalStartupConfigurationLoader.Load(explicitTree, "authored-project", Reader);
        Check(configured.DoubleEscapeAction == TerminalDoubleEscapeAction.Tree && configured.DoubleEscapeSource == "explicit" &&
            configured.Reload().DoubleEscapeAction == TerminalDoubleEscapeAction.Tree, "Explicit Tree override was confused with default Tree.");
        try { _ = new TerminalKeybindingConfiguration("agent", "keys", "win32", "missing", false, [], [], () => keybindings)
            { DoubleEscapeAction = (TerminalDoubleEscapeAction)99 }; throw new Exception("Undefined enum accepted."); }
        catch (ArgumentOutOfRangeException) { }
        e.Observe("immutable-settings-reload-and-explicit-override", new { reads, original = first.DoubleEscapeSource,
            reloaded = second.DoubleEscapeSource, configured.DoubleEscapeSource }); return Task.CompletedTask;
    }
    private static Task Workspace(ConsumerEvidence e)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "authored-actual-workspace");
        string[] args = ["session", "rpc", "--session", Path.Combine(workspace, "session.jsonl"), "--workspace", workspace,
            "--offline-script", Path.Combine(workspace, "script.json"), "--offline-api", "openai-responses"];
        Check(RpcSessionCommand.ResolveStartupWorkspace(args) == Path.GetFullPath(workspace), "Settings workspace differs from actual RPC parser.");
        try { RpcSessionCommand.ResolveStartupWorkspace(args.Concat(new[] { "--workspace", workspace }).ToArray()); throw new Exception("Duplicate workspace accepted."); }
        catch (SessionCommandException) { }
        try { RpcSessionCommand.ResolveStartupWorkspace(["session", "rpc", "--workspace", "relative"]); throw new Exception("Invalid startup admitted."); }
        catch (SessionCommandException) { }
        e.Observe("actual-startup-workspace-parser", new { workspace }); return Task.CompletedTask;
    }
    private static async Task Native(string reviewRoot, ConsumerEvidence e, bool fork)
    {
        var files = await StartupOwnedFiles.Create(reviewRoot, plugin: false, e);
        var globalPath = Path.Combine(files.Settings, "settings.json"); var projectRoot = Path.Combine(files.Root, ".pi");
        Directory.CreateDirectory(projectRoot); var projectPath = Path.Combine(projectRoot, "settings.json");
        await File.WriteAllTextAsync(globalPath, "{\"doubleEscapeAction\":\"none\"}");
        await File.WriteAllTextAsync(projectPath, fork ? "{\"doubleEscapeAction\":\"fork\"}" : "{}");
        var codec = new SessionEntryCodec();
        await using (var log = await SessionLogStore.OpenAsync(files.Session))
            await log.AppendAsync([codec.Parse("""{"type":"message","id":"settings-u","parentId":null,"timestamp":"2026-10-03T00:00:00.000Z","message":{"role":"user","content":"persistent fork text","timestamp":0}}""")]);
        var policy = TerminalStartupConfigurationLoader.Load(files.Configuration, files.Root);
        Check(policy.DoubleEscapeAction == (fork ? TerminalDoubleEscapeAction.Fork : TerminalDoubleEscapeAction.None), "Actual file-backed startup policy differs.");
        var globalHash = StartupOwnedFiles.Hash(globalPath); var projectHash = StartupOwnedFiles.Hash(projectPath);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace); using var error = new StringWriter();
        using var stop = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, error, trace.Observe,
            policy, stop.Token, kittyProtocolActive: true);
        try
        {
            await trace.WaitRecord(record => record.Value.GetProperty("type").GetString() == "response" &&
                record.Value.GetProperty("id").GetString() == "chat-start");
            await terminal.Feed("\u001b[27;1:1u\u001b[27;1:1u");
            if (fork)
            {
                await terminal.WaitWrite("Fork from user message");
                await terminal.Feed("\n"); // Owned configuration binds ctrl+j; original decoder admits LF provenance.
                await trace.WaitRecord(record => record.Value.GetProperty("type").GetString() == "session_switched");
                await terminal.Feed("\u001b[113;3u");
                var actualEditor = await trace.WaitRecord(record => record.Value.GetProperty("type").GetString() == "response" &&
                    record.Value.GetProperty("command").GetString() == "pisharp_restore_queue");
                Check(actualEditor.Value.GetProperty("data").GetProperty("count").GetInt32() == 0 &&
                    actualEditor.Value.GetProperty("data").GetProperty("text").GetString() == "persistent fork text",
                    "File-backed fork receipt did not reach the actual native editor.");
            }
            else
            {
                await terminal.Feed("persistent-none-sentinel");
                await terminal.WaitWrite("persistent-none-sentinel");
            }
            // EOF joins input/host and deliberately avoids synthesizing a shutdown gesture.
            terminal.End(); var result = await original;
            Check(result == 0 && error.ToString().Length == 0, "Actual configured command did not join successfully.");
            var records = trace.Records(); var selections = records.Where(record => record.Value.GetProperty("type").GetString() == "response" &&
                record.Value.GetProperty("command").GetString() == "pisharp_select_navigation").ToArray();
            Check(fork ? selections.Length == 1 && selections[0].Value.GetProperty("data").GetProperty("editorText").GetString() == "persistent fork text" :
                records.All(record => record.Value.GetProperty("type").GetString() != "session_switched" &&
                    (record.Value.GetProperty("type").GetString() != "response" || record.Value.GetProperty("command").GetString() != "pisharp_capture_navigation")),
                "Persistent policy did not control the actual native/RPC route.");
            Check(globalHash == StartupOwnedFiles.Hash(globalPath) && projectHash == StartupOwnedFiles.Hash(projectPath), "Read-only settings loading modified owned files.");
            if (fork)
            {
                var target = records.Single(record => record.Value.GetProperty("type").GetString() == "session_switched").Value.GetProperty("sessionFile").GetString()!;
                await using var released = await SessionLogStore.OpenAsync(target);
                Check(released.Snapshot.Header.Id == selections[0].Value.GetProperty("data").GetProperty("sessionId").GetString(),
                    "Original host retained its actual fork writer or receipt named a different session.");
            }
            await files.Complete(e); e.Observe("actual-file-backed-native-policy", new { fork, policy.DoubleEscapeSource,
                globalHash, projectHash, result, trace = trace.Rows(), terminal = terminal.Evidence });
        }
        finally { stop.Cancel(); terminal.Release(); terminal.End(); try { await original; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
