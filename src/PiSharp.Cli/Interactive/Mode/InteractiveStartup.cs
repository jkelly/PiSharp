using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>What the Pi entry resolved that the interactive mode shows or uses (source InteractiveModeOptions plus the runtime
/// services interactive-mode.ts reads from the session: agent dir, cwd, session dir, resources and project trust).</summary>
internal sealed record InteractiveStartup(string AgentDir, string Home, string Cwd, string? SessionDir, bool UsesDefaultSessionDir,
    PiResources Resources, bool ProjectTrusted, string SessionMode)
{
    /// <summary>The live host objects the RPC host publishes once its session exists (in-process access for features the RPC
    /// protocol does not carry: share, JSONL export, labels, bug reports, cache statistics).</summary>
    public InteractiveHostLink Host { get; } = new();
}

/// <summary>Filled by the RPC host when its session is ready; read by the interactive mode.</summary>
internal sealed class InteractiveHostLink
{
    private Func<PiSharp.CodingAgent.PersistentAgentSession?>? currentSession;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    /// <summary>The current session object (it changes on new/fork/switch).</summary>
    public PiSharp.CodingAgent.PersistentAgentSession? CurrentSession => currentSession?.Invoke();
    public object? Profile { get; private set; }
    public void Publish(Func<PiSharp.CodingAgent.PersistentAgentSession?> session, object? profile)
    {
        currentSession = session; Profile = profile; ready.TrySetResult();
    }
}
