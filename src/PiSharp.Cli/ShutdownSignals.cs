// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts and modes/print-mode.ts
// (registerSignalHandlers: SIGTERM everywhere, SIGHUP except on Windows; dispose the runtime, then exit 143 or 129).
using System.Runtime.InteropServices;

namespace PiSharp.Cli;

/// <summary>Termination signals of the non-interactive hosts (RPC, print and JSON). The first signal cancels the host's work so it
/// joins session shutdown, owned processes and credential writes before exiting 143 for SIGTERM or 129 for SIGHUP.
/// A repeated signal explicitly forces exit, bypassing unfinished cleanup.</summary>
internal sealed class ShutdownSignals : IDisposable
{
    private static readonly Lazy<ShutdownSignals> ProcessSignals = new(() => new(register: true));
    /// <summary>The process-wide registration, created on first use.</summary>
    internal static ShutdownSignals Process => ProcessSignals.Value;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<PosixSignalRegistration> _registrations = [];
    private int _received;
    private int _signalCount;
    private readonly Action<int> _forceExit;

    internal ShutdownSignals(bool register, Action<int>? forceExit = null)
    {
        _forceExit = forceExit ?? (register ? Environment.Exit : _ => { });
        if (!register) return;
        Add(PosixSignal.SIGTERM);
        if (!OperatingSystem.IsWindows()) Add(PosixSignal.SIGHUP);
    }

    /// <summary>Cancelled when a registered signal arrives.</summary>
    internal CancellationToken Token => _shutdown.Token;
    /// <summary>The first signal received, if any.</summary>
    internal PosixSignal? Received => Volatile.Read(ref _received) switch { 0 => null, var value => (PosixSignal)value };
    /// <summary>Pi's exit code for the received signal: 129 for SIGHUP, 143 for SIGTERM; null without a signal.</summary>
    internal int? ExitCode => Received switch { PosixSignal.SIGHUP => 129, PosixSignal.SIGTERM => 143, _ => null };
    /// <summary>The host's own exit code, unless a signal ended it.</summary>
    internal int Exit(int result) => ExitCode ?? result;

    /// <summary>The first signal records graceful cancellation; a repeated signal invokes the force-exit policy.</summary>
    internal void Signal(PosixSignal signal)
    {
        if (Interlocked.Increment(ref _signalCount) > 1)
        {
            // The first signal is graceful even if EOF/ctx.shutdown already started cleanup.
            // A subsequent signal is the user's explicit escape from stuck cleanup.
            _forceExit(signal == PosixSignal.SIGHUP ? 129 : 143);
            return;
        }
        Interlocked.CompareExchange(ref _received, (int)signal, 0);
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }

    private void Add(PosixSignal signal)
    {
        try
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                // The host exits after its graceful shutdown, with the signal's exit code.
                context.Cancel = true; Signal(context.Signal);
            }));
        }
        catch (Exception error) when (error is PlatformNotSupportedException or IOException) { }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear(); _shutdown.Dispose();
    }
}
