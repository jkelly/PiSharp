using System.Text;
using System.Text.Json;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report fresh-path.");
var outcomes = new List<object>();
var failures = 0;
await AnthropicInjectedTransportTests.RunAsync(Check);
await AnthropicAuthenticatedProviderTests.RunAsync(Check);
await Check("resolved-main-summary.three-channels-normal-off-fresh-owners", ResolvedMainSummaryTests.Channels);
await Check("resolved-main-summary.held-original-send-joined-after-cancel", ResolvedMainSummaryTests.HeldOriginal);
if (outcomes.Count != 18) throw new InvalidOperationException("Unexpected group inventory.");
var report = JsonSerializer.Serialize(new
{
    source = "d86654abb8862e201933517d6f1fce9f88dd117f", sourceVersion = "0.99.1",
    expectationKind = "SOURCE-DERIVED SYNTHETIC; NO SDK CAPTURE", groups = outcomes.Count,
    failures, outcomes,
    heldOriginalTasks = ResolvedMainSummaryTests.OriginalTasks.Select(record => new
    {
        record.Name, status = record.Original.Status.ToString(), completed = record.Original.IsCompleted,
        originalFaultGraph = Graph(record.Original.Exception), directFaultGraph = Graph(record.Direct)
    }).ToArray()
}, new JsonSerializerOptions { WriteIndented = true });
var bytes = new UTF8Encoding(false).GetBytes(report);
await using (var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None))
    await output.WriteAsync(bytes);
Console.WriteLine(report);
Environment.ExitCode = failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> invoke)
{
    Task? original = null; Exception? direct = null;
    try { original = invoke(); await original; }
    catch (Exception error) { direct = error; failures++; }
    if (original is { IsCompleted: false }) throw new InvalidOperationException("Original group remained pending.");
    outcomes.Add(new
    {
        name, passed = direct is null && original is { IsCompletedSuccessfully: true },
        originalTaskCaptured = original is not null, originalDirectAwaitCompleted = original is not null,
        originalStatus = original?.Status.ToString(), originalFaultGraph = Graph(original?.Exception),
        directOrSynchronousFaultGraph = Graph(direct)
    });
}
static object? Graph(Exception? root)
{
    if (root is null) return null;
    var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
    var pending = new Queue<Exception>(); var nodes = new List<object>(); var edges = new List<object>();
    int Admit(Exception value)
    {
        if (ids.TryGetValue(value, out var known)) return known;
        if (ids.Count >= 1024) throw new InvalidOperationException("Fault graph node bound.");
        var id = ids.Count; ids.Add(value, id); pending.Enqueue(value); return id;
    }
    var rootId = Admit(root);
    while (pending.TryDequeue(out var current))
    {
        var id = ids[current]; nodes.Add(new { id, type = current.GetType().FullName, current.HResult });
        var children = current is AggregateException aggregate ? aggregate.InnerExceptions.AsEnumerable() :
            current.InnerException is { } inner ? new[] { inner } : Enumerable.Empty<Exception>();
        var ordinal = 0;
        foreach (var child in children)
        {
            if (edges.Count >= 4096) throw new InvalidOperationException("Fault graph edge bound.");
            edges.Add(new { from = id, to = Admit(child), ordinal = ordinal++ });
        }
    }
    return new { rootId, nodes, edges };
}
