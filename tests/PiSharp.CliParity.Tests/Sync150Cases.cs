using PiSharp.Cli.Pi;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/project-trust.ts resolveProjectTrusted
// and main.ts createRuntime (31ade0e0d: for a project without trust-requiring resources, a saved decision wins over auto-trust), after
// test/project-trust-saved-decision.test.ts. Expectations are written from the sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.trust-saved-decision-wins-over-auto-trust", SavedTrustBeforeAutoTrust),
    ];

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
