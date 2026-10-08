namespace PiSharp.Cli.Extensions.Execution;

public sealed record NativeExtensionExecOriginal(string Phase, Task Original,
    AggregateException? Aggregate, Exception? Direct);

public sealed class NativeExtensionExecFault(string phase, Task? original, Exception evidence)
    : IOException("Native exec " + phase + " failed.", evidence)
{
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}

/// <summary>One terminal fault inventory per actual task, shared by all phase aliases.</summary>
public sealed class NativeExtensionExecOriginals
{
    private readonly object gate = new();
    private readonly Dictionary<Task, NativeExtensionExecOriginal> cached = new(ReferenceEqualityComparer.Instance);
    private readonly List<NativeExtensionExecOriginal> rows = [];
    public NativeExtensionExecOriginal[] Capture() { lock (gate) return rows.ToArray(); }
    internal Exception? Record(string phase, Task original, Exception? direct)
    {
        if (!original.IsCompleted) throw new InvalidOperationException("Only joined actual originals can be recorded.");
        lock (gate)
        {
            if (!cached.TryGetValue(original, out var evidence))
            {
                evidence = new(phase, original, original.IsFaulted ? original.Exception : null, direct);
                cached.Add(original, evidence);
            }
            rows.Add(evidence with { Phase = phase });
            if (evidence.Aggregate is { } aggregate) return new NativeExtensionExecFault(phase, original, aggregate);
            if (evidence.Direct is { } observed) return new NativeExtensionExecFault(phase, original, observed);
            return null;
        }
    }
    internal async Task Join(string phase, Task original, List<Exception> failures)
    {
        Exception? direct = null;
        try { await original.ConfigureAwait(false); } catch (Exception error) { direct = error; }
        if (Record(phase, original, direct) is { } failure) failures.Add(failure);
    }
    internal static void Throw(List<Exception> failures)
    {
        if (failures.Count > 0) throw new AggregateException("Native exec originals failed.", failures);
    }
}
