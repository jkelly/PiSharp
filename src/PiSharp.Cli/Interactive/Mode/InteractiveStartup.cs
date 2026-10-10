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
    /// <summary>interactive-mode.ts bindCurrentSessionExtensions commandContextActions newSession/fork: what the mode does around an
    /// extension's ctx.newSession()/ctx.fork() (set by the mode, called by the session host).</summary>
    public InteractiveExtensionSessionActions? ExtensionSessionActions { get; set; }
    public void Publish(Func<PiSharp.CodingAgent.PersistentAgentSession?> session, object? profile)
    {
        currentSession = session; Profile = profile; ready.TrySetResult();
    }
}

/// <summary>The interactive mode's side of an extension's ctx.newSession()/ctx.fork(): <see cref="NewSessionStarting"/> clears the status
/// indicator before a new session, <see cref="Forked"/> receives the fork's selected text and the generation the fork made (the editor
/// takes the text once that session shows, with "Forked to new session"), and <see cref="Failed"/> is handleFatalRuntimeError with
/// "Failed to create session" or "Failed to fork session".</summary>
internal sealed record InteractiveExtensionSessionActions(Action NewSessionStarting, Action<string?, long> Forked, Action<string, Exception> Failed);
