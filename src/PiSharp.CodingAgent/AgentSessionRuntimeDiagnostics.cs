// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/agent-session-runtime.ts
// (AgentSessionRuntime.diagnostics, takeUnreportedDiagnostics) and packages/coding-agent/src/core/agent-session-services.ts
// (AgentSessionRuntimeDiagnostic).
using System.Collections.Immutable;

namespace PiSharp.CodingAgent;

/// <summary>A problem a session runtime found while it was set up: <c>error</c>, <c>warning</c> or <c>info</c>.</summary>
public sealed record AgentSessionRuntimeDiagnostic(string Type, string Message);

/// <summary>
/// The diagnostics of the current session runtime, and which of them a host already reported. A host sets the runtime's
/// diagnostics at startup and again after each session replacement (/new, /resume, /fork, import), then calls
/// <see cref="TakeUnreported"/> to get each diagnostic once. Safe for concurrent use.
/// </summary>
public sealed class AgentSessionRuntimeDiagnostics
{
    private readonly object gate = new();
    private ImmutableArray<AgentSessionRuntimeDiagnostic> current = [];
    /// <summary>Type and message of every diagnostic <see cref="TakeUnreported"/> returned so far.</summary>
    private readonly HashSet<(string Type, string Message)> reported = [];

    /// <summary>The current runtime's diagnostics, reported or not.</summary>
    public ImmutableArray<AgentSessionRuntimeDiagnostic> Current { get { lock (gate) return current; } }

    /// <summary>Replaces the diagnostics with those of a new runtime (startup, or a session replacement that collected them again).</summary>
    public void Replace(IEnumerable<AgentSessionRuntimeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var next = diagnostics.ToImmutableArray();
        if (next.Any(diagnostic => diagnostic is null)) throw new ArgumentException("A diagnostic is null.", nameof(diagnostics));
        lock (gate) current = next;
    }

    /// <summary>Adds a diagnostic the current runtime found after it was set up.</summary>
    public void Add(AgentSessionRuntimeDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        lock (gate) current = current.Add(diagnostic);
    }

    /// <summary>
    /// Returns the current runtime's diagnostics that were not reported yet, and marks them reported. A diagnostic with the same
    /// type and message as one returned before is left out, so an unchanged settings warning is not repeated on every replacement.
    /// </summary>
    public ImmutableArray<AgentSessionRuntimeDiagnostic> TakeUnreported()
    {
        lock (gate) return [.. current.Where(diagnostic => reported.Add((diagnostic.Type, diagnostic.Message)))];
    }
}
