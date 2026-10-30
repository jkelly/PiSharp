// Decision 0011 (run W3, S2): SpendRecovery, the Spend Guard's startup recovery. A row left reserved in the ledger belongs to a request
// whose process is gone (the app was stopped or killed mid-stream): its stream cannot report usage any more, and the provider may already
// have charged it, so it becomes uncertain with the reason "stream dropped, no usage reported" and keeps counting toward the caps until
// the user resolves it in History › Spend. It is never settled automatically. The ledger runs this once, before its first other call, so
// the order in which the app's hosted services start does not matter; this service makes it happen at start even when nothing else
// touches the ledger.
using Microsoft.Extensions.Hosting;

namespace PiSharp.WebAgent.Spend;

/// <summary>Runs the ledger's startup recovery when the app starts.</summary>
/// <param name="ledger">The app's ledger.</param>
public sealed class SpendRecovery(SpendLedger ledger) : IHostedService
{
    /// <summary>How many reservations the last start made uncertain (0 until it ran).</summary>
    public int Recovered { get; private set; }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken) =>
        Recovered = await ledger.RecoverAtStartAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
