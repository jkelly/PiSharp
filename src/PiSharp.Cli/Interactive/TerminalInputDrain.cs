using PiSharp.Tui;

namespace PiSharp.Cli.Interactive;

// The terminal caller invokes this only after its original input owner has joined.
// Late bytes are discarded, never decoded/admitted as editor or RPC actions.
internal static class TerminalInputDrain
{
    internal static async Task RunAsync(IConsoleTerminal terminal, CancellationToken token, TimeProvider? clock = null)
    {
        var provider = clock ?? TimeProvider.System;
        token.ThrowIfCancellationRequested();
        var started = provider.GetTimestamp();
        using var maximum = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000), provider);
        var buffer = new char[4096];
        while (provider.GetElapsedTime(started) < TimeSpan.FromMilliseconds(1000))
        {
            token.ThrowIfCancellationRequested();
            using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(50), provider);
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token, maximum.Token, idle.Token);
            try
            {
                // A deadline requests cancellation; it never abandons this physical read.
                var count = await terminal.ReadAsync(buffer.AsMemory(), read.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (count < 0 || count > buffer.Length) throw new IOException("Terminal drain returned an invalid character count.");
                if (count == 0 || maximum.IsCancellationRequested || idle.IsCancellationRequested) return;
            }
            catch (OperationCanceledException canceled) when (canceled.CancellationToken == read.Token && read.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                return; // Only this drain's idle/maximum deadline is a successful early exit.
            }
        }
    }
}
