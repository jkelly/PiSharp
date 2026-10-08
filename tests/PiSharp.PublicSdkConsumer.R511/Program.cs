using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != "--report" || !Path.IsPathFullyQualified(args[1]) || File.Exists(args[1]))
                throw new ArgumentException("Invalid report destination.");
            var rows = new List<object>();
            var passed = 0;
            foreach (var (id, run) in ConsumerCases.All())
            {
                Task? original = null;
                Exception? observed = null;
                try { original = run(); await original; }
                catch (Exception error) { observed = error; }
                // Capture Task.Exception once; its aggregate may be a distinct object on subsequent reads.
                var fullFault = original?.Exception;
                var graph = SafeGraph(observed, fullFault);
                var success = observed is null && original?.IsCompletedSuccessfully == true;
                rows.Add(new { id, status = success ? "PASS" : "FAIL", originalStatus = original?.Status.ToString(),
                    synchronousFailure = original is null, actuallyCanceled = original?.IsCanceled == true, faultGraph = graph });
                if (!success) break;
                passed++;
            }
            await using var report = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(report, new { profile = "public-native-sdk-consumer-r511", expected = 4,
                executed = rows.Count, passed, tests = rows, noNode = true, inertSessionProvider = true });
            await report.FlushAsync();
            return passed == 4 ? 0 : 1;
        }
        catch (Exception error)
        {
            // No messages, payloads, Data, stack traces, arguments or private paths leave this route.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { code = "CONSUMER_REPORT_OR_ARGUMENT_FAILURE",
                type = error.GetType().FullName, hresult = error.HResult }));
            return 1;
        }
    }

    private static object SafeGraph(Exception? observed, AggregateException? fullFault)
    {
        try
        {
            var identities = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
            var nodes = new List<object>();
            var truncated = false;
            int? Visit(Exception? value, int depth)
            {
                if (value is null) return null;
                if (identities.TryGetValue(value, out var known)) return known;
                if (depth > 8 || identities.Count >= 128) { truncated = true; return null; }
                var id = identities.Count;
                identities.Add(value, id);
                var children = value is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() :
                    value.InnerException is { } child ? new[] { child } : Array.Empty<Exception>();
                if (children.Length > 64) truncated = true;
                var edges = children.Take(64).Select(exceptionChild => Visit(exceptionChild, depth + 1)).ToArray();
                nodes.Add(new { id, type = value.GetType().FullName, hresult = value.HResult,
                    childCount = children.Length, children = edges });
                return id;
            }
            var observedId = Visit(observed, 0);
            var fullFaultId = Visit(fullFault, 0);
            return new { observedId, fullFaultId, truncated, collectionFailed = false, nodes };
        }
        catch { return new { collectionFailed = true, truncated = true, nodes = Array.Empty<object>() }; }
    }
}
