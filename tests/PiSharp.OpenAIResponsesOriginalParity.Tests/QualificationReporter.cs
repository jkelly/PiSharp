internal sealed class OriginalTaskRecord(string name, Task? original = null)
{
    internal string Name { get; } = name;
    internal Task? Original { get; set; } = original;
    internal AggregateException? FullException { get; private set; }
    internal Exception? Direct { get; set; }
    internal void Capture() { if (Original?.IsFaulted == true) FullException ??= Original.Exception; }
    internal IEnumerable<Exception> Faults()
    {
        Capture();
        if (FullException is not null) yield return FullException;
        if (Direct is not null) yield return Direct;
    }
}

internal static class QualificationReporter
{
    internal static object Project(OriginalTaskRecord record)
    {
        record.Capture();
        return new { record.Name, passed = record.Original?.IsCompletedSuccessfully == true && record.Direct is null,
            originalCaptured = record.Original is not null, directAwaitCompleted = record.Original?.IsCompleted == true,
            originalStatus = record.Original?.Status.ToString(), originalCanceled = record.Original?.IsCanceled,
            originalGraph = Graph(record.FullException), directGraph = Graph(record.Direct) };
    }
    private static object? Graph(Exception? root)
    {
        if (root is null) return null;
        var seen = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
        var pending = new List<Exception>(); var nodes = new List<object>();
        var edges = 0; long omittedEdges = 0, omittedNodeReferences = 0;
        int? Add(Exception value)
        {
            if (seen.TryGetValue(value, out var id)) return id;
            if (pending.Count >= 1024) { omittedNodeReferences++; return null; }
            seen.Add(value, pending.Count); pending.Add(value); return pending.Count - 1;
        }
        var rootId = Add(root);
        for (var i = 0; i < pending.Count; i++)
        {
            var value = pending[i];
            IReadOnlyList<Exception> children = value is AggregateException aggregate ? aggregate.InnerExceptions :
                value.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
            var references = new List<int>();
            for (var childIndex = 0; childIndex < children.Count; childIndex++)
            {
                if (edges >= 4096) { omittedEdges += children.Count - childIndex; break; }
                edges++;
                if (Add(children[childIndex]) is { } childId) references.Add(childId); else omittedEdges++;
            }
            nodes.Add(new { id = i, type = value.GetType().FullName, value.Message, value.StackTrace, inner = references });
        }
        return new { rootId, nodes, traversedEdges = edges,
            truncated = omittedEdges != 0 || omittedNodeReferences != 0, omittedEdges, omittedNodeReferences,
            maximumNodes = 1024, maximumEdges = 4096 };
    }
}

internal sealed class QualificationReportingFailure : Exception
{
    internal Exception ReportingFailure { get; }
    internal OriginalTaskRecord[] GroupOriginals { get; }
    internal OriginalTaskRecord[] HeldOriginals { get; }
    internal QualificationReportingFailure(Exception reportingFailure, OriginalTaskRecord[] groups, OriginalTaskRecord[] held)
        : base("Qualification reporting failed; full original and direct fault inventories retained.",
            new AggregateException(new[] { reportingFailure }.Concat(groups.Concat(held).SelectMany(record => record.Faults()))))
    { ReportingFailure = reportingFailure; GroupOriginals = groups; HeldOriginals = held; }
}
