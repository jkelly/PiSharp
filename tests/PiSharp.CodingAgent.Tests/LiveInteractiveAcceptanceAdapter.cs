using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;

// Only translates fixture inputs to the production command. Agent, policy, HTTP, persistence and
// terminal owners are the actual application composition, not a replacement test runtime.
internal static class LiveInteractiveAcceptanceAdapter
{
    internal static Task<int> RunAsync(LiveInteractiveAcceptanceInput input)
    {
        var args = new List<string>
        {
            "session", "terminal", "--live", "--provider", LiveInteractiveAcceptanceInput.Model.Provider,
            "--model", LiveInteractiveAcceptanceInput.Model.Id, "--workspace", input.Workspace,
            "--session", input.Session, "--session-mode", input.CreateNew ? "new-lazy" : "open",
            "--max-output-tokens", "1024"
        };
        if (input.PermittedRead is not null) { args.Add("--allow-read"); args.Add(input.PermittedRead); }
        var runtime = new LiveSessionRuntime(
            variable => variable == "OPENROUTER_API_KEY" ? LiveInteractiveAcceptanceInput.InertKey : null,
            () => input.Handler);
        var configuration = TerminalKeybindingConfigurationLoader.Load(
            Path.Combine(input.Workspace, "fixture-agent"), "win32",
            new Dictionary<string, string?>(), readText: _ => null);
        return TerminalSessionCommand.RunObservedConfiguredAsync(args.ToArray(), input.Terminal, input.Viewport,
            input.Diagnostics, input.Observe, configuration, input.Cancellation, liveRuntime: runtime);
    }
}