using PiSharp.Cli.Mcp;

internal static class McpApplicationHostOriginals
{
    private sealed class Captured(Task original)
    {
        internal readonly Task Original = original;
        internal AggregateException? Aggregate;
        internal Exception? Direct;
        internal bool Joined;
    }
    private static readonly object Gate = new();
    private static readonly Dictionary<Task, Captured> Originals = new(ReferenceEqualityComparer.Instance);
    private static readonly List<(string Phase, Captured Capture)> Rows = [];
    private static readonly List<Task> Tracked = [];
    internal static int MarkTracked() { lock (Gate) return Tracked.Count; }
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Capture()
    { lock (Gate) return Rows.Select(row => (row.Phase, row.Capture.Original, row.Capture.Aggregate, row.Capture.Direct)).ToArray(); }
    private static Captured Register(Task original, string phase)
    {
        lock (Gate)
        {
            if (!Originals.TryGetValue(original, out var captured)) { captured = new(original); Originals.Add(original, captured); }
            Rows.Add((phase, captured)); return captured;
        }
    }
    internal static Task Track(Task original, string phase)
    { Register(original, phase); lock (Gate) if (!Tracked.Contains(original, ReferenceEqualityComparer.Instance)) Tracked.Add(original); return original; }
    internal static Task<T> Track<T>(Task<T> original, string phase)
    { Track((Task)original, phase); return original; }
    internal static McpApplicationGenerationAcquisition Generation(McpApplicationGenerationAcquisition source) =>
        (request, token) => new(Track(source(request, token).AsTask(), "actual-generation-supplier"));
    internal static (AggregateException? Aggregate, Exception? Direct) Evidence(Task original)
    {
        lock (Gate) { var captured = Originals[original]; if (!captured.Joined) throw new IOException("Original was not directly joined."); return (captured.Aggregate, captured.Direct); }
    }
    private static void Complete(Captured captured, Exception? direct)
    {
        lock (Gate)
        {
            if (captured.Joined) return;
            captured.Aggregate = captured.Original.IsFaulted ? captured.Original.Exception : null;
            captured.Direct = direct; captured.Joined = true;
        }
    }
    internal static async Task Observe(Task original, string phase)
    {
        var captured = Register(original, phase); Exception? direct = null;
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; }
        finally { Complete(captured, direct); }
        ThrowCaptured(captured);
    }
    internal static async Task<T> Observe<T>(Task<T> original, string phase)
    {
        var captured = Register(original, phase); Exception? direct = null; T result = default!;
        try { result = await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; }
        finally { Complete(captured, direct); }
        ThrowCaptured(captured); return result;
    }
    private static void ThrowCaptured(Captured captured)
    {
        if (captured.Aggregate is { } aggregate) throw new AggregateException(aggregate, captured.Direct!);
        if (captured.Direct is { } direct) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(direct).Throw();
    }
    internal static async Task JoinTrackedSince(int mark, List<Exception> failures, params Task?[] expectedFailed)
    {
        Task[] tasks; lock (Gate) tasks = Tracked.Skip(mark).ToArray();
        foreach (var original in tasks)
            try { await Observe(original, "owned-supplier-finally-join"); }
            catch (Exception error)
            { if (!expectedFailed.Any(expected => ReferenceEquals(expected, original))) failures.Add(error); }
    }
}
