using System.Collections.Immutable;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Commands;

/// <summary>Compact facts from the actual runner result, which already joins termination, exit and redirected tasks.
/// This observer neither launches independently nor discovers/kills process IDs.</summary>
internal sealed record OwnedProcessCleanupReceipt(int Ordinal, int? ProcessId, int? ExitCode, bool ProcessStarted,
    bool CleanupConfirmed, bool CapturedOutputComplete, ImmutableArray<ProcessDiagnostic> Diagnostics);
internal sealed class OwnedProcessCleanupException(OwnedProcessCleanupReceipt receipt)
    : IOException("An owned process returned uncertain cleanup or failed output settlement.")
{ internal OwnedProcessCleanupReceipt Receipt { get; } = receipt; }

internal sealed class OwnedProcessCleanup(IProcessRunner runner) : IProcessRunner
{
    private readonly object gate = new();
    private readonly List<OwnedProcessCleanupReceipt> receipts = [];
    private readonly List<Exception> failures = [];
    private int admitted, active;
    internal ImmutableArray<OwnedProcessCleanupReceipt> Capture() { lock (gate) return receipts.OrderBy(value => value.Ordinal).ToImmutableArray(); }
    internal ImmutableArray<Exception> CaptureFailures()
    {
        lock (gate)
        {
            var result = failures.ToImmutableArray();
            if (active != 0) result = result.Add(new IOException("Owned process originals have not settled."));
            return result;
        }
    }
    public async ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        int ordinal;
        lock (gate)
        {
            if (admitted >= 1024) throw new InvalidOperationException("Owned process receipt limit reached before process admission.");
            ordinal = ++admitted; active++;
        }
        try
        {
            // Directly await the actual runner. Its existing owner determines termination and original stream joins.
            var result = await runner.RunAsync(request, onUpdate, cancellationToken).ConfigureAwait(false);
            var receipt = new OwnedProcessCleanupReceipt(ordinal, result.ProcessId, result.ExitCode, result.ProcessStarted,
                result.CleanupConfirmed, result.CapturedOutputComplete, result.Diagnostics);
            lock (gate)
            {
                receipts.Add(receipt);
                if (!receipt.CleanupConfirmed || !receipt.CapturedOutputComplete || receipt.Diagnostics.Any(value => value is
                    ProcessDiagnostic.CleanupFailed or ProcessDiagnostic.OutputIoFailed or ProcessDiagnostic.ProgressCallbackFailed or ProcessDiagnostic.LifecycleCallbackFailed))
                    failures.Add(new OwnedProcessCleanupException(receipt));
            }
            return result;
        }
        catch (Exception error) { lock (gate) failures.Add(error); throw; }
        finally { lock (gate) active--; }
    }
}
