using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

// Test-only cause identities, following TerminalShutdownDiagnostics' bounded graph shape.
// Capture is synchronous and never exports exception messages, stacks, names or transcript data.
internal static class StartupCauseDiagnostics
{
    internal static JsonElement Capture(ImmutableArray<Exception> failures)
    {
        var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
        var nodes = new List<object>(); var truncated = failures.Length > 16;
        int Identity(Exception error)
        {
            if (ids.TryGetValue(error, out var existing)) return existing;
            if (ids.Count == 16) { truncated = true; return 0; }
            var id = ids.Count + 1; ids.Add(error, id);
            var children = error is AggregateException aggregate ? aggregate.InnerExceptions.AsEnumerable() :
                error.InnerException is { } inner ? new[] { inner } : Enumerable.Empty<Exception>();
            var edges = children.Take(8).Select(Identity).ToArray();
            if (children.Skip(8).Any()) truncated = true;
            var type = error.GetType().FullName ?? error.GetType().Name;
            if (type.Length > 120) truncated = true;
            nodes.Add(new { id, type = type[..Math.Min(type.Length, 120)], code = error.HResult,
                children = edges, cancellation = error is OperationCanceledException,
                tokenCanceled = error is OperationCanceledException canceled && canceled.CancellationToken.IsCancellationRequested });
            return id;
        }
        var roots = failures.Take(16).Select(Identity).ToArray();
        var receipt = JsonSerializer.SerializeToElement(new { schemaVersion = 1, failureCount = failures.Length,
            roots, nodes, truncated });
        if (Encoding.UTF8.GetByteCount(receipt.GetRawText()) <= 8192) return receipt;
        // Oversize graphs have a content-free bounded fallback; zero references mark omitted nodes.
        return JsonSerializer.SerializeToElement(new { schemaVersion = 1, failureCount = failures.Length,
            nodeCount = ids.Count, truncated = true });
    }
}
