// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (runDurableTui).
namespace PiSharp.Cli.Durable;

/// <summary>The `pisharp durable` terminal UI (upstream <c>runDurableTui</c>): renders <see cref="IOpenDurable.View"/> and drives
/// <see cref="IOpenDurable.Controller"/> until the user exits.</summary>
internal static class DurableTui
{
    /// <summary>Runs the TUI until the user exits; returns the process exit code. The caller closes <paramref name="durable"/>.</summary>
    public static Task<int> RunAsync(IOpenDurable durable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(durable);
        // TODO(P12): the TUI port replaces this.
        throw new NotImplementedException("The durable TUI is not ported yet (P12).");
    }
}
