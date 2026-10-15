// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/main.ts
// Upstream runs main.ts from source (node --import source-resolver.ts .../durable/main.ts [--continue]); PiSharp runs it as the hidden
// subcommand `pisharp durable [--continue|-c]` (decision 0005, intentional difference "pisharp durable entry point").
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Durable;

/// <summary><c>pisharp durable</c>: arguments, open, run, close (upstream <c>main.ts</c>).</summary>
internal static class DurableMain
{
    /// <summary>The one-line notice printed to stderr before the session opens.</summary>
    internal const string ExperimentalNotice =
        "pisharp durable is experimental (a port of pi's experimental durable agent): its sessions, commands and behavior may change.";

    /// <summary>Upstream <c>parseArgs</c>: only <c>--continue</c> and <c>-c</c>.</summary>
    /// <exception cref="ArgumentException">"Unknown argument: X".</exception>
    internal static OpenDurableOptions ParseArgs(IEnumerable<string> argv)
    {
        var continueSession = false;
        foreach (var arg in argv)
        {
            if (arg is "--continue" or "-c") continueSession = true;
            else throw new ArgumentException($"Unknown argument: {arg}");
        }
        return new OpenDurableOptions { ContinueSession = continueSession };
    }

    /// <summary>Runs <c>pisharp durable</c> with the arguments after <c>durable</c>; returns the exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        OpenDurableOptions options;
        try { options = ParseArgs(args); }
        catch (ArgumentException error)
        {
            await host.Stderr.WriteLineAsync(error.Message).ConfigureAwait(false);
            await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        await host.Stderr.WriteLineAsync(ExperimentalNotice).ConfigureAwait(false);
        await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        IOpenDurable durable;
        try { durable = await DurableRuntime.OpenAsync(options, DurableRuntimeHost.FromPiHost(host), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Upstream lets the error end the process (exit 1) with its message.
            await host.Stderr.WriteLineAsync(error.Message).ConfigureAwait(false);
            await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        int code;
        string? failure;
        try
        {
            code = await DurableTui.RunAsync(durable, cancellationToken).ConfigureAwait(false);
            failure = durable.View.Current.Failure;
        }
        finally { await durable.DisposeAsync().ConfigureAwait(false); }
        // The TUI showed it on its screen, which is gone now (PiSharp-only, pi-durable 1.2.0's SessionFailed).
        if (failure is not null)
        {
            await host.Stderr.WriteLineAsync(DurableFailure.Message(failure)).ConfigureAwait(false);
            await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return code;
    }
}
