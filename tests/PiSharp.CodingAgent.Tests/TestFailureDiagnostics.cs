using System.Diagnostics;

// Structural diagnostics only: never reads Message, Data, argument values, or absolute paths.
internal static class TestFailureDiagnostics
{
    private static string? SourceName(string? path)
    {
        if (path is null) return null;
        // PDBs may originate on a different OS: remove both native separator forms.
        var start = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
        var name = path[start..];
        return name.Contains(':') ? null : name;
    }
    internal static object Capture(Exception original)
    {
        try
        {
            const int maximumNodes = 64, maximumEdges = 128, maximumFrames = 8;
            var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<Exception>();
            var nodes = new List<object>(); var edges = new List<object>(); var truncated = false;
            ids.Add(original, 0); pending.Enqueue(original);
            while (pending.TryDequeue(out var error))
            {
                var id = ids[error];
                var frames = (new StackTrace(error, true).GetFrames() ?? [])
                    .Take(maximumFrames).Select(frame =>
                    {
                        var method = frame.GetMethod();
                        return (object)new { declaringType = method?.DeclaringType?.FullName, method = method?.Name,
                            file = SourceName(frame.GetFileName()), line = frame.GetFileLineNumber() };
                    }).ToArray();
                nodes.Add(new { id, type = error.GetType().FullName, hresult = error.HResult,
                    aggregate = error is AggregateException, frames });
                IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions :
                    error.InnerException is { } inner ? [inner] : [];
                var position = 0;
                foreach (var child in children)
                {
                    if (edges.Count == maximumEdges) { truncated = true; break; }
                    if (!ids.TryGetValue(child, out var target))
                    {
                        if (ids.Count == maximumNodes) { truncated = true; position++; continue; }
                        target = ids.Count; ids.Add(child, target); pending.Enqueue(child);
                    }
                    // Every occurrence gets an edge: shared references and duplicate children remain visible.
                    edges.Add(new { parent = id, child = target, position = position++ });
                }
            }
            return new { version = 1, nodes, edges, truncated, messagesIncluded = false, absolutePathsIncluded = false };
        }
        catch
        {
            // Diagnostic collection must never replace the actual test failure.
            return new { version = 1, unavailable = true, messagesIncluded = false, absolutePathsIncluded = false };
        }
    }
}
