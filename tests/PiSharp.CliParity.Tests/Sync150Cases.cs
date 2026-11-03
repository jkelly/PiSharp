using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/project-trust.ts resolveProjectTrusted
// and main.ts createRuntime (31ade0e0d: for a project without trust-requiring resources, a saved decision wins over auto-trust), after
// test/project-trust-saved-decision.test.ts; utils/paths.ts, core/source-info.ts, core/trust-manager.ts and core/session-manager.ts
// (1be312b46: paths compared without case on Windows). Expectations are written from the sources by reading, never captured from an
// upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.trust-saved-decision-wins-over-auto-trust", SavedTrustBeforeAutoTrust),
        ("sync150.paths-compare-without-case-on-windows", Sync(PathComparisonKeys)),
        ("sync150.trust-and-sessions-match-other-casing-on-windows", Sync(CasingAcrossStores)),
    ];

    // utils/paths.ts getPathComparisonKey, pathsEqual and isPathWithin (1be312b46, after test/paths.test.ts): resolved paths compare
    // without case on Windows and with it elsewhere, containment goes by whole segments, and source-info.ts getResourcePathKey keeps
    // synthetic identifiers as they are.
    private static void PathComparisonKeys()
    {
        using var sandbox = new Sandbox("path-casing", trusted: false);
        var windows = OperatingSystem.IsWindows();
        var lower = Path.Combine(sandbox.Root, "dir"); var upper = Path.Combine(sandbox.Root, "DIR");
        Equal(windows, PiPaths.PathsEqual(lower, upper), "casing");
        Check(PiPaths.PathsEqual(lower, lower + Path.DirectorySeparatorChar) && PiPaths.PathsEqual(lower, Path.Combine(lower, "x", "..")), "resolved paths");
        Check(PiPaths.IsPathWithin(sandbox.Root, Path.Combine(sandbox.Root, "a", "b")) && PiPaths.IsPathWithin(sandbox.Root, sandbox.Root), "within and equal");
        Check(!PiPaths.IsPathWithin(Path.Combine(sandbox.Root, "a"), Path.Combine(sandbox.Root, "ab")), "whole segments");
        Equal(windows, PiPaths.IsPathWithin(upper, Path.Combine(lower, "x")), "within, other casing");
        Check(PiPaths.IsPathWithin(Path.GetPathRoot(sandbox.Root)!, sandbox.Root), "a filesystem root");
        if (windows)
            Check(PiPaths.PathsEqual(@"c:\Users\me", @"C:\USERS\ME\") && PiPaths.IsPathWithin(@"\\Server\Share", @"\\server\share\x") &&
                !PiPaths.IsPathWithin(@"C:\Users\me", @"C:\Users\me2"), "drive letters and UNC");
        else Check(!PiPaths.PathsEqual("/tmp/A", "/tmp/a") && !PiPaths.IsPathWithin("/home/me", "/home/me2"), "POSIX paths keep their case");
        Equal("builtin:Name", PiSharp.Cli.Extensions.Pi.PiSourceInfo.ResourcePathKey("builtin:Name"), "a builtin identifier keeps its case");
        Equal("<inline:X>", PiSharp.Cli.Extensions.Pi.PiSourceInfo.ResourcePathKey("<inline:X>"), "an inline identifier keeps its case");
        Equal(windows, PiSharp.Cli.Extensions.Pi.PiSourceInfo.ResourcePathKey(lower) == PiSharp.Cli.Extensions.Pi.PiSourceInfo.ResourcePathKey(upper),
            "file paths fold case on Windows");
    }

    // trust-manager.ts (after test/trust-manager.test.ts) and session-manager.ts: a trust decision or a session saved with other casing of
    // the same folder is found on Windows, saving a decision replaces the other-casing keys, and ~/.agents/skills stays the user's own
    // whatever the casing of home.
    private static void CasingAcrossStores()
    {
        using var sandbox = new Sandbox("path-casing-stores", trusted: false);
        var windows = OperatingSystem.IsWindows();
        var other = Path.Combine(Path.GetDirectoryName(sandbox.Cwd)!, "PROJECT");
        var store = new ProjectTrustStore(sandbox.AgentDir, sandbox.Home);
        store.Set(sandbox.Cwd, true);
        Equal(windows ? true : null, store.Get(other), "a decision saved with other casing");
        store.Set(other, false);
        var keys = JsonNode.Parse(File.ReadAllText(store.TrustPath))!.AsObject().Select(pair => pair.Key).ToArray();
        Equal(windows ? 1 : 2, keys.Length, "keys after saving with other casing: " + string.Join(", ", keys));
        Equal(windows ? false : true, store.Get(sandbox.Cwd), "the decision for the original casing");

        var home = sandbox.Home; var inside = Path.Combine(home, "work");
        Directory.CreateDirectory(Path.Combine(home, ".agents", "skills")); Directory.CreateDirectory(inside);
        Equal(ProjectTrustStore.HasTrustRequiringProjectResources(inside, home),
            ProjectTrustStore.HasTrustRequiringProjectResources(inside, windows ? home.ToUpperInvariant() : home), "home with other casing");

        var sessions = Path.Combine(sandbox.Root, "sessions"); Directory.CreateDirectory(sessions);
        const string id = "0190f2c4-0000-7000-8000-000000000001";
        File.WriteAllText(Path.Combine(sessions, "s.jsonl"),
            JsonSerializer.Serialize(new { type = "session", version = 3, id, timestamp = "2026-11-01T00:00:00.000Z", cwd = other }) + "\n");
        Equal(windows, PiSessions.FindById(sandbox.Cwd, id, sessions, sandbox.AgentDir) is not null, "resume by id in a custom session directory");
        Equal(windows, PiSessions.ContinueRecent(sandbox.Cwd, sessions, sandbox.AgentDir) is not null, "--continue in a custom session directory");
        Equal(windows ? 1 : 0, PiSessions.List(sandbox.Cwd, sessions, sandbox.AgentDir).Length, "the session list of a custom directory");
    }

    private static async Task SavedTrustBeforeAutoTrust()
    {
        using var sandbox = new Sandbox("trust-saved", trusted: false);
        // AGENTS.md alone is not a trust-requiring project resource.
        sandbox.Write(Path.Combine(sandbox.Cwd, "AGENTS.md"), "# untrusted\n");
        // The user's own ~/.agents/skills may be an ancestor of the temporary directory; it is not a project resource, so detection
        // (an existence check only) treats the real profile as home then.
        var home = sandbox.Home;
        if (ProjectTrustStore.HasTrustRequiringProjectResources(sandbox.Cwd, home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (ProjectTrustStore.HasTrustRequiringProjectResources(sandbox.Cwd, home))
            throw new SkipException("An ancestor of the temporary directory holds .agents/skills, so the project requires trust here.");
        var store = new ProjectTrustStore(sandbox.AgentDir, sandbox.Home);
        Task<bool> Resolve(bool? forced = null) => PiProjectTrust.ResolveAsync(sandbox.Cwd, home, store, forced, "ask", null, CancellationToken.None);
        Equal(true, await Resolve(), "no decision: auto-trusted");
        store.Set(sandbox.Cwd, false);
        Equal(false, await Resolve(), "a saved Do not trust wins over auto-trust");
        Equal(true, await Resolve(true), "--approve still wins over the saved decision");
        store.Set(sandbox.Cwd, true);
        Equal(true, await Resolve(), "a saved Trust");
        store.Set(sandbox.Cwd, null);
        store.Set(sandbox.Root, false);
        Equal(false, await Resolve(), "a Do not trust saved for a parent folder wins too");
    }
}
