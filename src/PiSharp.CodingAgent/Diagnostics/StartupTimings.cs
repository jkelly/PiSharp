// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/timings.ts.
using System.Globalization;
using System.Text;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary>
/// Central timing instrumentation for startup profiling, enabled with <c>PI_TIMING=1</c> (read once, at first use of
/// <see cref="Default"/>). Namespaces are <c>"main"</c> (the default) and <c>"extensions"</c>; each records the milliseconds
/// since its previous mark. <see cref="PrintTimings"/> writes every namespace in creation order to standard error.
/// </summary>
public sealed class StartupTimings
{
    public const string Main = "main", Extensions = "extensions";
    private static readonly Lazy<StartupTimings> Shared = new(() => new(Environment.GetEnvironmentVariable("PI_TIMING") == "1"));

    private sealed class TimingNamespace(double lastTime)
    {
        internal readonly List<(string Label, double Ms)> Timings = [];
        internal double LastTime = lastTime;
    }

    private readonly object _gate = new();
    private readonly List<(string Name, TimingNamespace Timings)> _namespaces = [];
    private readonly TimeProvider _time;
    private readonly TextWriter? _error;

    public StartupTimings(bool enabled, TimeProvider? timeProvider = null, TextWriter? error = null)
    {
        Enabled = enabled; _time = timeProvider ?? TimeProvider.System; _error = error;
    }

    /// <summary>The process-wide instance, enabled by <c>PI_TIMING=1</c>.</summary>
    public static StartupTimings Default => Shared.Value;

    public bool Enabled { get; }

    private double Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public void ResetTimings(string timingNamespace = Main)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            var index = _namespaces.FindIndex(item => item.Name == timingNamespace);
            if (index >= 0) _namespaces[index] = (timingNamespace, new(Now)); else _namespaces.Add((timingNamespace, new(Now)));
        }
    }

    public void Time(string label, string timingNamespace = Main)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            var now = Now;
            if (!_namespaces.Exists(item => item.Name == timingNamespace)) ResetTimings(timingNamespace);
            var timings = _namespaces.First(item => item.Name == timingNamespace).Timings;
            timings.Timings.Add((label, now - timings.LastTime));
            timings.LastTime = now;
        }
    }

    public void PrintTimings()
    {
        if (!Enabled) return;
        var output = new StringBuilder();
        lock (_gate)
            foreach (var (name, timings) in _namespaces) PrintTimingGroup(output, $"Startup Timings: {name}", timings.Timings);
        if (output.Length == 0) return;
        var error = _error ?? Console.Error;
        error.Write(output.ToString()); error.Flush();
    }

    private static void PrintTimingGroup(StringBuilder output, string title, List<(string Label, double Ms)> timings)
    {
        var printable = timings.Where(timing => timing.Ms >= 0).ToList();
        if (printable.Count == 0) return;
        // Each console.error call ends its text with a line break.
        output.Append($"\n--- {title} ---\n");
        foreach (var (label, ms) in printable) output.Append($"  {label}: {Number(ms)}ms\n");
        output.Append($"  TOTAL: {Number(printable.Sum(timing => timing.Ms))}ms\n");
        output.Append(new string('-', title.Length + 8)).Append("\n\n");
    }

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
