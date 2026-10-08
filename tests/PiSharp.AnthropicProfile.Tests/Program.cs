using System.Text;
using System.Text.Json;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report fresh-path.");
var outcomes = new List<object>();
var failures = 0;
var originals = new List<OriginalTaskRecord>();
var reportingOriginals = new List<OriginalTaskRecord>();
try
{
await Check("profile-resolved.three-auth-main-two-fresh-summaries", ProfileTests.Channels);
await Check("profile-resolved.legacy-key-route-preserved", ProfileTests.Legacy);
await Check("profile-resolved.invalid-and-precanceled-zero-effects", ProfileTests.Admission);
await Check("profile-resolved.held-main-close-original-join", () => ProfileTests.Held(false));
await Check("profile-resolved.held-summary-close-original-join", () => ProfileTests.Held(true));
await Check("profile-resolved.active-refusal-inactive-inherited-close", ProfileTests.CallbackClose);
if (outcomes.Count != 6) throw new InvalidOperationException("Unexpected group inventory.");
var report = JsonSerializer.Serialize(new
{
    source = "d86654abb8862e201933517d6f1fce9f88dd117f", sourceVersion = "0.99.1",
    expectationKind = "SOURCE-DERIVED SYNTHETIC; NO SDK CAPTURE", groups = outcomes.Count,
    failures, outcomes,
    heldOriginalTasks = ProfileTests.Originals.Select(record => new
    {
        record.Name, status = record.Task?.Status.ToString(), completed = record.Task?.IsCompleted == true, originalTaskCaptured = record.Task is not null,
        originalFaultGraph = Graph(record.Task?.Exception), directFaultGraph = Graph(record.Direct)
    }).ToArray()
}, new JsonSerializerOptions { WriteIndented = true });
var bytes = new UTF8Encoding(false).GetBytes(report);
FileStream? output = null; Exception? writeFailure = null, disposalFailure = null;
try
{
    output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    var opened = output;
    await JoinReportingOriginal("report-write", () => opened.WriteAsync(bytes).AsTask());
    await JoinReportingOriginal("report-flush", () => opened.FlushAsync());
}
catch (Exception error) { writeFailure = error; }
finally
{
    if (output is { } opened)
        try { await JoinReportingOriginal("report-dispose", () => opened.DisposeAsync().AsTask()); }
        catch (Exception error) { disposalFailure = error; }
}
if (writeFailure is not null && disposalFailure is not null)
    throw new AggregateException("Report write/flush and disposal failed.", writeFailure, disposalFailure);
if (writeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writeFailure).Throw();
if (disposalFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalFailure).Throw();
Console.WriteLine(report);
Environment.ExitCode = failures == 0 ? 0 : 1;
}
catch (Exception reportOrGraphFailure)
{
    // Keep the actual Task and caught references even if graph traversal/serialization/output itself fails.
    throw new QualificationReportFailure(reportOrGraphFailure, originals.ToArray(),
        ProfileTests.Originals.Select(x => new OriginalTaskRecord(x.Name) { Task = x.Task, Direct = x.Direct }).ToArray(),
        reportingOriginals.ToArray());
}

async Task JoinReportingOriginal(string name, Func<Task> invoke)
{
    var record = new OriginalTaskRecord(name); reportingOriginals.Add(record);
    try { record.Task = invoke(); await record.Task; }
    catch (Exception error) { record.Direct = error; throw; }
}

async Task Check(string name, Func<Task> invoke)
{
    Task? original = null; Exception? direct = null;
    try { original = invoke(); await original; }
    catch (Exception error) { direct = error; failures++; }
    if (original is { IsCompleted: false }) throw new InvalidOperationException("Original group remained pending.");
    originals.Add(new OriginalTaskRecord(name) { Task = original, Direct = direct });
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
        var id = ids[current]; nodes.Add(new { id, type = current.GetType().FullName, current.HResult, current.Message, current.StackTrace });
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

internal sealed class OriginalTaskRecord(string name)
{
    public string Name { get; } = name;
    public Task? Task { get; set; }
    public Exception? Direct { get; set; }
}
internal sealed class QualificationReportFailure : Exception
{
    public Exception ReportingFailure { get; }
    public OriginalTaskRecord[] Groups { get; }
    public OriginalTaskRecord[] Held { get; }
    public OriginalTaskRecord[] ReportingOriginals { get; }
    public QualificationReportFailure(Exception reportingFailure, OriginalTaskRecord[] groups,
        OriginalTaskRecord[] held, OriginalTaskRecord[] reportingOriginals)
        : base("Qualification reporting failed; actual original task/fault references retained.",
            Faults(reportingFailure, groups, held, reportingOriginals))
    { ReportingFailure = reportingFailure; Groups = groups; Held = held; ReportingOriginals = reportingOriginals; }
    private static AggregateException Faults(Exception reportingFailure, params OriginalTaskRecord[][] vectors)
    {
        var errors = new List<Exception> { reportingFailure };
        foreach (var record in vectors.SelectMany(x => x))
        {
            if (record.Task?.Exception is { } aggregate) errors.Add(aggregate);
            if (record.Direct is { } direct) errors.Add(direct);
        }
        return new AggregateException("Exact original AggregateException and direct fault vectors.", errors);
    }
}