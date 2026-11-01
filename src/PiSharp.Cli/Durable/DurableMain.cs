// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/main.ts
// Upstream runs main.ts from source (node --import source-resolver.ts .../durable/main.ts [--continue]); PiSharp runs it as the
// subcommand `pisharp durable [--continue|-c]`, listed in --help as experimental (decision 0005, intentional difference "pisharp durable
// entry point").
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Durable;

/// <summary><c>pisharp durable</c>: arguments, open, run, close (upstream <c>main.ts</c>).</summary>
internal static class DurableMain
{
    /// <summary>The one-line notice printed to stderr before the session opens.</summary>
    internal const string ExperimentalNotice =
        "pisharp durable is experimental (a port of pi's experimental durable agent): its sessions, commands and behavior may change.";

    /// <summary><c>pisharp durable --help</c> (PiSharp-only: upstream ran main.ts from source and had no help).</summary>
    internal static string Help => string.Join('\n',
        "Usage:",
        $"  {PiConfig.DisplayName} durable [options]",
        "",
        "EXPERIMENTAL: PiSharp's crash-safe durable agent (a port of pi's experimental durable agent), whose sessions survive",
        "a crash. Its sessions, commands and behavior may change.",
        "",
        "Options:",
        "  --continue, -c                 Continue the newest durable session",
        "  --tier, --service-tier <tier>  Service tier for a new session: " + string.Join(", ", PiSharp.AI.ServiceTiers.All),
        "  --help, -h                     Show this help",
        "");

    /// <summary>Upstream <c>parseArgs</c>: <c>--continue</c> and <c>-c</c>; PiSharp-only <c>--tier &lt;tier&gt;</c> (decision 0007).</summary>
    /// <exception cref="ArgumentException">"Unknown argument: X".</exception>
    internal static OpenDurableOptions ParseArgs(IEnumerable<string> argv)
    {
        var continueSession = false;
        string? tier = null;
        using var args = argv.GetEnumerator();
        while (args.MoveNext())
        {
            var arg = args.Current;
            if (arg is "--continue" or "-c") continueSession = true;
            else if (arg is "--tier" or "--service-tier")
            {
                var value = args.MoveNext() ? args.Current : throw new ArgumentException($"{arg} needs a tier");
                tier = PiSharp.AI.ServiceTiers.Normalize(value)
                    ?? throw new ArgumentException($"Invalid service tier \"{value}\". Valid values: {string.Join(", ", PiSharp.AI.ServiceTiers.All)}");
            }
            else throw new ArgumentException($"Unknown argument: {arg}");
        }
        return new OpenDurableOptions { ContinueSession = continueSession, ServiceTier = tier };
    }

    /// <summary>Runs <c>pisharp durable</c> with the arguments after <c>durable</c>; returns the exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        // Decision 0008 (PiSharp-only): `serve` runs this machine as a durable device, `token` prints or rotates its token.
        if (args is ["serve", .. var serve])
        {
            await host.Stderr.WriteLineAsync(ExperimentalNotice).ConfigureAwait(false);
            // A service manager stops the device with SIGTERM (SIGHUP off Windows): it shuts down as on Ctrl+C (decision 0010, M6).
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, host.Signals?.Invoke().Token ?? CancellationToken.None);
            return await DurableServe.ServeAsync(serve, host, stopping.Token).ConfigureAwait(false);
        }
        if (args is ["token", .. var token]) return await DurableServe.TokenAsync(token, host).ConfigureAwait(false);
        // Decision 0010, M6 (PiSharp-only): the device's API as commands. Their first word is a group the TUI's arguments never use; --help
        // and -h print Help below (main), which lists the TUI's options only.
        if (args is ["help", .. var topic])
        {
            var text = topic is [var group, ..] && DurableRemote.Groups.Contains(group) ? DurableRemote.Help(group)
                : topic is [var name, ..] && DurableOps.Names.Contains(name) ? DurableOps.Help(name)
                : DurableRemote.Help() + "\nOn the device's data directory:\n" + DurableOps.Help();
            await host.Stdout.WriteLineAsync(text.TrimEnd()).ConfigureAwait(false);
            return 0;
        }
        if (args is [var first, ..] && DurableRemote.Groups.Contains(first)) return await DurableRemote.RunAsync(args, host, cancellationToken).ConfigureAwait(false);
        if (args is [var op, ..] && DurableOps.Names.Contains(op)) return await DurableOps.RunAsync(args, host, cancellationToken).ConfigureAwait(false);
        if (args.Contains("--help") || args.Contains("-h"))
        {
            await host.Stdout.WriteAsync(Help.AsMemory(), cancellationToken).ConfigureAwait(false);
            await host.Stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
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
