using System.Text.RegularExpressions;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Interactive;

/// <summary>Owns actual native raw subscriptions for one terminal connection and its session generations.</summary>
internal sealed class TerminalExtensionInputAdmission
{
    private readonly object gate = new();
    private readonly long connectionGeneration;
    internal long ConnectionGeneration => connectionGeneration;
    private readonly Func<long> captureSessionGeneration;
    private readonly CancellationToken sessionToken;
    private readonly Dictionary<long, RegisteredTerminalInputSession> sessions = [];
    private readonly List<Task> closes = [];
    private RegisteredTerminalInputSession? current;
    private bool closed;
    private Task? close;
    private readonly Func<string, bool>? consumePendingColorResponse;
    private readonly Action<string>? reportColorScheme;
    private readonly Action<string>? reportCellSize;
    private static readonly Regex Scheme = new("^(?:\\x1b\\[\\?997;(1|2)n)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Cell = new("^\\x1b\\[6;([0-9]+);([0-9]+)t$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal TerminalExtensionInputAdmission(long connectionGeneration, Func<long> captureSessionGeneration,
        CancellationToken sessionToken = default, Func<string, bool>? consumePendingColorResponse = null,
        Action<string>? reportColorScheme = null, Action<string>? reportCellSize = null)
    {
        ArgumentNullException.ThrowIfNull(captureSessionGeneration);
        if (connectionGeneration < 0) throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        this.connectionGeneration = connectionGeneration; this.captureSessionGeneration = captureSessionGeneration;
        this.sessionToken = sessionToken; this.consumePendingColorResponse = consumePendingColorResponse;
        this.reportColorScheme = reportColorScheme; this.reportCellSize = reportCellSize;
    }
    private RegisteredTerminalInputSession SelectCurrent(long generation)
    {
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(TerminalExtensionInputAdmission));
            if (generation != captureSessionGeneration()) throw new StaleTerminalGenerationException();
            if (current?.SessionGeneration == generation) return current;
            if (sessions.Count >= 256) throw new InvalidOperationException("Terminal session generation limit reached.");
            if (sessions.ContainsKey(generation)) throw new InvalidOperationException("Retired terminal session generation cannot be reused.");
            if (current is not null)
            {
                current.RetireAdmission();
                var retired = current;
                // The actual worker close is retained immediately and joined by final host cleanup.
                using (ExecutionContext.SuppressFlow()) closes.Add(Task.Run(async () =>
                {
                    var original = retired.DisposeAsync(); await original.ConfigureAwait(false);
                }));
            }
            current = new(connectionGeneration, generation, sessionToken);
            sessions.Add(generation, current);
            return current;
        }
    }
    internal async Task<ExtensionTerminalInputOutcome> DispatchAsync(string data, long generation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (generation != captureSessionGeneration()) return Stale();
        // The original consumes only a pending color-query response, then a scheme report, before raw listeners.
        if (consumePendingColorResponse?.Invoke(data) == true) return ExtensionTerminalInputOutcome.Consumed();
        if (Scheme.IsMatch(data)) { reportColorScheme?.Invoke(data); return ExtensionTerminalInputOutcome.Consumed(); }
        RegisteredTerminalInputSession owner;
        try { owner = SelectCurrent(generation); }
        catch (StaleTerminalGenerationException) { return Stale(); }
        var original = owner.DispatchAsync(data, connectionGeneration, generation, token);
        ExtensionTerminalInputOutcome outcome;
        try { outcome = await original.ConfigureAwait(false); }
        catch when (original.IsFaulted)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original.Exception!).Throw(); throw; }
        if (generation != captureSessionGeneration()) { owner.RetireAdmission(); return Stale(); }
        // Cell-size recognition sees listener transformations and consumption, exactly after raw listeners.
        if (outcome.Disposition == ExtensionTerminalInputDisposition.Forward && Cell.IsMatch(outcome.Data))
        { reportCellSize?.Invoke(outcome.Data); return ExtensionTerminalInputOutcome.Consumed(); }
        return outcome;
    }
    internal IExtensionUiProvider Decorate(IExtensionUiProvider inner) =>
        new ExtensionTerminalInputUiProvider(inner,
            context => SelectCurrent(captureSessionGeneration()).SelectOwnerScope(context),
            context => SelectCurrent(captureSessionGeneration()).SelectRegistrationSink(context));
    internal IExtensionTerminalInput RegistrationSink(IExtensionContext context) =>
        SelectCurrent(captureSessionGeneration()).SelectRegistrationSink(context);
    internal ValueTask StopAdmissionAndJoinAsync()
    {
        lock (gate)
        {
            if (close is not null) return new(close);
            foreach (var owner in sessions.Values) owner.AssertCanClose();
            current?.RetireAdmission(); closed = true;
            var admitted = sessions.Values.ToArray(); var pending = closes.ToArray();
            using (ExecutionContext.SuppressFlow()) close = Task.Run(() => CloseAsync(admitted, pending));
            return new(close);
        }
    }
    private static async Task CloseAsync(RegisteredTerminalInputSession[] admitted, Task[] pending)
    {
        var faults = new List<Exception>(); var originals = new List<Task>(pending);
        foreach (var owner in admitted)
            try { originals.Add(owner.DisposeAsync().AsTask()); } catch (Exception error) { faults.Add(error); }
        foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
        if (faults.Count != 0) throw new AggregateException("Terminal input ownership originals failed.", faults);
    }
    private static ExtensionTerminalInputOutcome Stale() => ExtensionTerminalInputOutcome.Unavailable(ExtensionUiUnavailableReason.StaleContext);
    private sealed class StaleTerminalGenerationException : Exception { }
}
