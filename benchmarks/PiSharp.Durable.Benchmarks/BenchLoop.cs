// The sampling loop of Vitest's bench(), which upstream's storage.bench.ts runs on (tinybench 2: warm up for at least warmupTime ms and
// warmupIterations runs and discard those samples, then sample until at least time ms and iterations runs have passed; quantiles by linear
// interpolation over the sorted samples). Not a port of an upstream file: Vitest is upstream's runner, not part of pi-durable.
using System.Diagnostics;
using System.Globalization;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Vitest <c>bench()</c> options.</summary>
/// <param name="TimeMs">Minimum sampling time.</param>
/// <param name="Iterations">Minimum samples.</param>
/// <param name="WarmupTimeMs">Minimum warmup time.</param>
/// <param name="WarmupIterations">Minimum warmup runs.</param>
public sealed record BenchOptions(double TimeMs, int Iterations, double WarmupTimeMs, int WarmupIterations);

/// <summary>One benchmark's samples, in milliseconds.</summary>
public sealed record BenchResult(string Group, string Name, int Samples, double MeanMs, double MinMs, double MaxMs, double P75Ms, double P99Ms)
{
    /// <summary>Operations per second (tinybench <c>hz</c>).</summary>
    public double Hz => MeanMs > 0 ? 1000 / MeanMs : double.PositiveInfinity;

    /// <summary>One table row.</summary>
    public string Row() => string.Create(CultureInfo.InvariantCulture,
        $"| {Group} | {Name} | {Hz:N1} | {MeanMs:0.0000} | {MinMs:0.0000} | {P75Ms:0.0000} | {P99Ms:0.0000} | {MaxMs:0.0000} | {Samples} |");

    /// <summary>The table header of <see cref="Row"/>.</summary>
    public const string Header = "| group | backend | hz | mean ms | min ms | p75 ms | p99 ms | max ms | samples |\n|---|---|---:|---:|---:|---:|---:|---:|---:|";
}

/// <summary>The bench loop.</summary>
public static class BenchLoop
{
    /// <summary>Warms up, then samples <paramref name="run"/>.</summary>
    public static async Task<BenchResult> RunAsync(string group, string name, Func<Task> run, BenchOptions options)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);
        await SampleAsync(run, options.WarmupTimeMs, options.WarmupIterations);
        var samples = await SampleAsync(run, options.TimeMs, options.Iterations);
        samples.Sort();
        return new(group, name, samples.Count, samples.Count == 0 ? 0 : samples.Average(), samples.Count == 0 ? 0 : samples[0],
            samples.Count == 0 ? 0 : samples[^1], Quantile(samples, 0.75), Quantile(samples, 0.99));
    }

    private static async Task<List<double>> SampleAsync(Func<Task> run, double timeMs, int iterations)
    {
        var samples = new List<double>();
        var total = 0.0;
        while (total < timeMs || samples.Count < iterations)
        {
            var started = Stopwatch.GetTimestamp();
            await run();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            samples.Add(elapsed);
            total += elapsed;
        }
        return samples;
    }

    /// <summary>tinybench <c>quantileSorted</c>: linear interpolation between the neighbouring sorted samples.</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0) return 0;
        var position = (sorted.Count - 1) * q;
        var lower = (int)Math.Floor(position);
        return lower + 1 < sorted.Count ? sorted[lower] + (position - lower) * (sorted[lower + 1] - sorted[lower]) : sorted[lower];
    }
}
