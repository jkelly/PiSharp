// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Pi;
using static Expect;

internal static class Sync152SessionCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("sync152.session.startup-fork-missing-folder", StartupFork);
        foreach (var choice in new[] { "continue", "fork", "cancel" })
            yield return ($"sync152.session.import-missing-folder-{choice}", () => Import(choice));
        yield return ("sync152.session.model-cycle-outside-scope", () =>
        {
            Equal(0, InteractiveMode.NextCycleIndex(-1, 1, "forward"), "sole outside model");
            Equal(0, InteractiveMode.NextCycleIndex(-1, 4, "forward"), "first forward model");
            Equal(3, InteractiveMode.NextCycleIndex(-1, 4, "backward"), "last backward model");
            Check(InteractiveMode.NextCycleIndex(0, 1, "forward") is null, "active sole model does not cycle");
            Check(InteractiveMode.NextCycleIndex(-1, 0, "forward") is null, "empty scope");
            return Task.CompletedTask;
        });
    }

    private static string Source(InteractiveHarness pi) => pi.Write("source/missing.jsonl",
        JsonSerializer.Serialize(new { type = "session", version = 3, id = "0198a2b0-0000-7000-8000-000000000001",
            timestamp = "2026-01-01T00:00:00.000Z", cwd = Path.Combine(pi.Root, "gone") }) + "\n");

    private static async Task StartupFork()
    {
        await using var pi = new InteractiveHarness("sync152-startup-fork");
        var source = Source(pi); var original = File.ReadAllBytes(source);
        pi.MissingCwdChoice = (_, _) => MissingSessionCwdChoice.Fork;
        pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular", "--session", source);
        await pi.WaitFor("escape interrupt");
        var current = pi.Mode!.Session.SessionFile!;
        Check(!PiPaths.PathsEqual(current, source), "fork creates another file");
        var header = PiSessions.ReadHeader(current)!;
        Equal(pi.Cwd, PiSessions.Text(header, "cwd"), "fork cwd");
        Check(PiSessions.Text(header, "parentSession") is { } parent && PiPaths.PathsEqual(source, parent), "fork source link");
        Check(PiSessions.Text(header, "id") != PiSessions.Text(PiSessions.ReadHeader(source)!, "id"), "fork identity");
        Check(original.SequenceEqual(File.ReadAllBytes(source)), "source untouched");
    }

    private static async Task Import(string choice)
    {
        await using var pi = new InteractiveHarness("sync152-import-" + choice);
        var source = Source(pi); var original = File.ReadAllBytes(source);
        pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
        await pi.WaitFor("escape interrupt");
        var previous = pi.Mode!.Session.SessionFile;
        await pi.Submit("/import " + source);
        await pi.WaitFor("Replace current session with"); pi.Type("\r");
        await pi.WaitFor("Session cwd does not exist:");
        if (choice is "fork" or "cancel") pi.Type("\u001b[B");
        if (choice == "cancel") pi.Type("\u001b[B");
        pi.Type("\r");
        await pi.WaitFor(choice == "cancel" ? "Import cancelled" : "Session imported from:");
        Check(original.SequenceEqual(File.ReadAllBytes(source)), "source untouched");
        if (choice == "cancel")
        {
            Equal(previous, pi.Mode.Session.SessionFile, "cancel retains current attachment");
            Equal(0, pi.SessionFiles().Length, "cancel leaves no imported copy");
            return;
        }
        var installed = pi.Mode.Session.SessionFile!;
        Check(!PiPaths.PathsEqual(installed, source), "import/fork attaches a copy");
        Equal(1, pi.SessionFiles().Length, "only one imported file remains");
        var header = PiSessions.ReadHeader(installed)!;
        if (choice == "fork") Check(PiSessions.Text(header, "parentSession") is { } parent && PiPaths.PathsEqual(source, parent), "fork retains imported source provenance");
        Equal(choice == "fork" ? pi.Cwd : Path.Combine(pi.Root, "gone"), PiSessions.Text(header, "cwd"), "stored cwd semantics");
        Check((PiSessions.Text(header, "id") == "0198a2b0-0000-7000-8000-000000000001") == (choice == "continue"), "identity semantics");
    }
}
