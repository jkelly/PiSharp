using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace PiSharp.Qualification;
internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);

// Raw task and exception references survive graph, serializer and output failures.
internal sealed class QualificationFailure : Exception
{
    internal IReadOnlyList<Task> Originals { get; }
    internal QualificationFailure(string message, IEnumerable<Task> tasks, IEnumerable<Exception> faults)
        : base(message, new AggregateException(faults.Distinct<Exception>(ReferenceEqualityComparer.Instance)))
        => Originals = tasks.Distinct<Task>(ReferenceEqualityComparer.Instance).ToArray();
}
internal static class QualificationEvidence
{
    private sealed class Group(string name)
    {
        internal string Name = name;
        internal Task? Original;
        internal AggregateException? Aggregate;
        internal Exception? Direct, Synchronous;
    }
    internal static async Task<int> RunAsync(string[] args,
        Func<(string Name, Func<Task> Run)[]> captureCases, int expectedGroups,
        Func<CapturedOriginal[]> captureSubsidiaries)
    {
        var groups = new List<Group>();
        var subsidiary = Array.Empty<CapturedOriginal>();
        var outputOriginals = new List<CapturedOriginal>();
        var processing = new List<Exception>();
        string? path = null;
        try
        {
            if (args.Length != 2 || args[0] != "--report" || !Path.IsPathFullyQualified(args[1]))
                throw new ArgumentException("An explicit absolute fresh --report path is required.");
            path = Path.GetFullPath(args[1]);
            if (File.Exists(path)) throw new IOException("Qualification report must be fresh.");
            var cases = captureCases();
            if (cases.Length != expectedGroups || cases.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Run is null) ||
                cases.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != expectedGroups)
                throw new InvalidOperationException("Exact unique qualification roster differs.");
            foreach (var item in cases)
            {
                var group = new Group(item.Name); groups.Add(group);
                try
                {
                    group.Original = item.Run() ?? throw new InvalidOperationException("Original control returned no Task.");
                    await group.Original.ConfigureAwait(false);
                }
                catch (Exception direct)
                {
                    group.Direct = direct;
                    if (group.Original is null) group.Synchronous = direct;
                    if (group.Original is { IsFaulted: true } original) group.Aggregate = original.Exception;
                }
            }
            subsidiary = captureSubsidiaries(); // Each fixture already owns and directly joins these actual originals.
            if (subsidiary.Any(item => !item.Original.IsCompleted))
                throw new InvalidOperationException("An admitted subsidiary original is still pending after its owning control.");
        }
        catch (Exception error) { processing.Add(error); }

        // Capture raw inventory before performing any fallible projection/serialization/output.
        var rawTasks = groups.Where(item => item.Original is not null).Select(item => item.Original!)
            .Concat(subsidiary.Select(item => item.Original)).ToList();
        var rawFaults = groups.SelectMany(item => new Exception?[] { item.Aggregate, item.Direct, item.Synchronous })
            .Concat(subsidiary.SelectMany(item => new Exception?[] { item.Aggregate, item.Direct }))
            .Where(item => item is not null).Select(item => item!).ToList();
        rawFaults.AddRange(processing);
        bool passed = processing.Count == 0 && groups.Count == expectedGroups &&
            groups.All(item => item.Original is { IsCompletedSuccessfully: true } && item.Direct is null && item.Synchronous is null);
        try
        {
            var graph = new FaultDag();
            var tasks = new Dictionary<Task, int>(ReferenceEqualityComparer.Instance);
            int TaskId(Task original)
            {
                if (!tasks.TryGetValue(original, out var id)) { id = tasks.Count + 1; tasks.Add(original, id); }
                return id;
            }
            var groupRows = groups.Select(item => new
            {
                name = item.Name, taskId = item.Original is null ? (int?)null : TaskId(item.Original),
                status = item.Original?.Status.ToString(), completed = item.Original?.IsCompleted ?? false,
                succeeded = item.Original?.IsCompletedSuccessfully ?? false,
                faulted = item.Original?.IsFaulted ?? false, canceled = item.Original?.IsCanceled ?? false,
                originalException = graph.Root(item.Aggregate), directException = graph.Root(item.Direct), synchronousException = graph.Root(item.Synchronous)
            }).ToArray();
            var subsidiaryRows = subsidiary.Select(item => new
            {
                phase = item.Phase, taskId = TaskId(item.Original), status = item.Original.Status.ToString(),
                completed = item.Original.IsCompleted, succeeded = item.Original.IsCompletedSuccessfully,
                faulted = item.Original.IsFaulted, canceled = item.Original.IsCanceled,
                originalException = graph.Root(item.Aggregate), directException = graph.Root(item.Direct)
            }).ToArray();
            var processingRows = processing.Select(error => graph.Root(error)).ToArray();
            var dag = graph.Complete();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "actual-original-qualification-v1", expectedCases = expectedGroups, executedCases = groups.Count,
                passed = groups.Count(item => item.Original is { IsCompletedSuccessfully: true } && item.Direct is null && item.Synchronous is null),
                allPassed = passed, groups = groupRows, subsidiaryOriginals = subsidiaryRows, processingExceptions = processingRows,
                uniqueOriginalTasks = tasks.Count, exceptionDag = dag,
                nodeLimit = 1024, edgeLimit = 4096, truncated = false,
                sourceOnlyClaims = new { fullProviderParity = false, fullMcpOAuthParity = false, wholePluginOrSupervisor = false }
            });
            if (path is null) throw new IOException("No admitted report destination.");
            await WriteAsync(path, bytes, outputOriginals).ConfigureAwait(false);
            Console.WriteLine($"Qualification groups: {groups.Count}/{expectedGroups}; passed: {passed}.");
        }
        catch (Exception error)
        {
            rawFaults.Add(error);
            AddOutputInventory();
            throw new QualificationFailure("Qualification reporting failed; all captured raw originals are retained.", rawTasks, rawFaults);
        }
        AddOutputInventory();
        if (!passed) throw new QualificationFailure("Original qualification control failed; full captured fault inventory is retained.", rawTasks, rawFaults);
        return 0;

        void AddOutputInventory()
        {
            rawTasks.AddRange(outputOriginals.Select(item => item.Original));
            rawFaults.AddRange(outputOriginals.SelectMany(item => new Exception?[] { item.Aggregate, item.Direct })
                .Where(item => item is not null).Select(item => item!));
        }
    }
    private static async Task WriteAsync(string path, byte[] bytes, List<CapturedOriginal> inventory)
    {
        FileStream? stream = null; var failures = new List<Exception>();
        try
        {
            var opened = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            stream = opened;
            await Join("report-write", () => opened.WriteAsync(bytes, 0, bytes.Length)).ConfigureAwait(false);
            if (failures.Count == 0) await Join("report-flush", () => opened.FlushAsync()).ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (stream is { } retiring) await Join("report-dispose", () => retiring.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Actual report write/flush/dispose originals failed.", failures);
        async Task Join(string phase, Func<Task> acquire)
        {
            Task? original = null; Exception? direct = null; AggregateException? aggregate = null; var index = -1;
            try
            {
                original = acquire(); index = inventory.Count; inventory.Add(new(phase, original, null, null));
                await original.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                direct = error;
                if (original is { IsFaulted: true }) aggregate = original.Exception;
                if (aggregate is not null) failures.Add(aggregate);
                failures.Add(error);
            }
            finally { if (original is not null && index >= 0) inventory[index] = new(phase, original, aggregate, direct); }
        }
    }
    private sealed class FaultDag
    {
        private readonly Dictionary<Exception, int> ids = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<Exception> pending = new();
        private readonly List<object> nodes = []; private readonly List<object> edges = [];
        internal int? Root(Exception? error)
        {
            if (error is null) return null;
            if (!ids.TryGetValue(error, out var id))
            {
                if (ids.Count == 1024) throw new InvalidOperationException("Original exception DAG node bound exceeded.");
                id = ids.Count + 1; ids.Add(error, id); pending.Enqueue(error);
            }
            return id;
        }
        internal object Complete()
        {
            while (pending.TryDequeue(out var error))
            {
                var id = ids[error];
                nodes.Add(new { id, type = error.GetType().FullName, message = error.Message, stack = error.StackTrace });
                IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions :
                    error.InnerException is { } inner ? [inner] : [];
                var position = 0;
                foreach (var child in children)
                {
                    if (edges.Count == 4096) throw new InvalidOperationException("Original exception DAG edge bound exceeded.");
                    edges.Add(new { from = id, to = Root(child), ordinal = position++ });
                }
            }
            return new { nodes, edges, nodeCount = ids.Count, edgeCount = edges.Count };
        }
    }
}