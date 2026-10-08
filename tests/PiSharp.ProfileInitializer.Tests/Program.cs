using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using System.Text.Json;
using System.Threading.Tasks.Sources;

if (args.Length != 4 || args[0] != "--report" || args[2] != "--published-fixture-root")
    throw new ArgumentException("Expected --report <new-path> --published-fixture-root <explicit-output-root>");
string? reportPath = args[1];
ProfileInitializerTests.AdmitPublishedFixtureRoot(args[3]);
var cases = ProfileInitializerTests.Cases().Concat(McpRegisteredProfileDiscoveryTests.Cases()).ToArray();var failures = 0;
var evidence = new List<object>();
var callbackEvidence = new List<object>();
var capturedOriginals = new List<ContextOriginalRecord>();
var reportingOriginals = new List<ContextOriginalRecord>();
try
{
for (var ordinal = 0; ordinal < cases.Length; ordinal++)
{
    var test = cases[ordinal];
    Task? original = null;
    Exception? caught = null;
    var synchronousFailure = false;
    try { original = test.Run() ?? throw new InvalidOperationException("Test returned no original Task"); }
    catch (Exception error) { caught = error; synchronousFailure = true; }
    if (original is not null)
    {
        // Capture once and directly await the actual original, with no proxy or timeout wrapper.
        try { await original; }
        catch (Exception error) { caught = error; }
    }
    // Preserve actual Task/Exception references before console, graph or serialization work can fail.
    var originalAggregate = original?.Exception;
    capturedOriginals.Add(new(test.Name, original, caught) { Aggregate = originalAggregate });
    var passed = !synchronousFailure && original?.Status == TaskStatus.RanToCompletion && caught is null;
    if (passed) Console.WriteLine($"PASS {test.Name}");
    else { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {originalAggregate ?? caught}"); }
    evidence.Add(new
    {
        ordinal, name = test.Name, method = test.Run.Method.Name,
        status = passed ? "PASS" : "FAIL", originalInvoked = true,
        originalCaptured = original is not null, originalTaskJoined = original is not null,
        originalStatus = original?.Status.ToString(), actuallyCanceled = original?.IsCanceled ?? false,
        faulted = original?.IsFaulted ?? false, synchronousFailure,
        faults = CaptureFaults(originalAggregate, caught)
    });
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} profile initializer and discovery controls passed");
if (reportPath is not null)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        schemaVersion = 1, profile = "actual-profile-native-initializer-and-discovery-3-owned-originals",
        originalOracle = "d86654abb8862e201933517d6f1fce9f88dd117f",
        expected = cases.Length, executed = evidence.Count, passed = cases.Length - failures, failed = failures,
        tests = evidence, callbackSynchronousFailures = Array.Empty<object>(), callbackOriginals = callbackEvidence, directOriginalAwait = true, noNode = true
    }, new JsonSerializerOptions { WriteIndented = true }));
    FileStream? stream = null; Exception? writeFailure = null, disposalFailure = null;
    try
    {
        stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JoinReport("write", stream.WriteAsync(bytes).AsTask());
        await JoinReport("flush", stream.FlushAsync());
    }
    catch (Exception error) { writeFailure = error; }
    finally
    {
        if (stream is not null)
            try { await JoinReport("dispose", stream.DisposeAsync().AsTask()); }
            catch (Exception error) { disposalFailure = error; }
    }
    if (writeFailure is not null && disposalFailure is not null)
        throw new AggregateException("Context report write/flush and disposal failed.", writeFailure, disposalFailure);
    if (writeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writeFailure).Throw();
    if (disposalFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalFailure).Throw();
}
return failures == 0 ? 0 : 1;
}
catch (Exception reportingOrGraphFailure)
{
    var faults = new List<Exception> { reportingOrGraphFailure };
    foreach (var record in capturedOriginals.Concat(reportingOriginals))
    {
        if (record.Aggregate is { } aggregate) faults.Add(aggregate);
        if (record.Direct is { } direct) faults.Add(direct);
    }
    throw new ContextQualificationEvidenceException(capturedOriginals.ToArray(), reportingOriginals.ToArray(),
        new AggregateException("Exact original context Tasks, direct faults and reporting fault.", faults));
}

async Task JoinReport(string phase, Task original)
{
    var record = new ContextOriginalRecord(phase, original, null); reportingOriginals.Add(record);
    try { await original; }
    catch (Exception error) { record.Direct = error; record.Aggregate = original.Exception; throw; }
}

static object CaptureFaults(Exception? originalAggregate, Exception? awaitOrSynchronousFailure)
{
    const int maximumNodes = 1024, maximumEdges = 4096;
    var identities = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
    var queue = new List<Exception>(); var nodes = new List<object>(); var edges = 0;
    int? Admit(Exception? error)
    {
        if (error is null) return null;
        if (identities.TryGetValue(error, out var present)) return present;
        if (queue.Count == maximumNodes) throw new InvalidOperationException("Fault graph node bound exceeded.");
        var index = queue.Count; identities.Add(error, index); queue.Add(error); return index;
    }
    var originalAggregateRoot = Admit(originalAggregate); var awaitOrSynchronousRoot = Admit(awaitOrSynchronousFailure);
    for (var index = 0; index < queue.Count; index++)
    {
        var error = queue[index]; var children = new List<int>();
        IEnumerable<Exception> inner = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } single ? new[] { single } : Array.Empty<Exception>();
        foreach (var child in inner)
        {
            if (++edges > maximumEdges) throw new InvalidOperationException("Fault graph edge bound exceeded.");
            children.Add(Admit(child)!.Value);
        }
        nodes.Add(new { index, type = error.GetType().AssemblyQualifiedName, error.Message, error.HResult, error.StackTrace,
            aggregate = error is AggregateException, inner = children.ToArray(), cancellationException = error is OperationCanceledException,
            cancellationRequested = error is OperationCanceledException canceled && canceled.CancellationToken.IsCancellationRequested });
    }
    return new { originalAggregateRoot, awaitOrSynchronousRoot, nodes, maximumNodes, maximumEdges };
}

internal sealed class ContextOriginalRecord(string name, Task? original, Exception? direct)
{
    public string Name { get; } = name;
    public Task? Original { get; } = original;
    public AggregateException? Aggregate { get; set; }
    public Exception? Direct { get; set; } = direct;
}
internal sealed class ContextQualificationEvidenceException(ContextOriginalRecord[] groups,
    ContextOriginalRecord[] reporting, AggregateException faults) : IOException("Context qualification evidence failed.", faults)
{
    public ContextOriginalRecord[] Groups { get; } = groups;
    public ContextOriginalRecord[] Reporting { get; } = reporting;
}
