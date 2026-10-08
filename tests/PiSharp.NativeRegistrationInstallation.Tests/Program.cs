using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Expected --report fresh-path.");
var groups = InstallationTests.Cases().ToArray(); var failed = 0;
foreach (var group in groups)
{
    Task? original = null; Exception? caught = null;
    try { original = group.Run(); await original; } catch (Exception error) { caught = error; failed++; }
    Evidence.Record(group.Name, original, caught, caught is null);
}
try
{
    var json = JsonSerializer.Serialize(new Report("native-initializer-registration-installation", groups.Length, groups.Length - failed, failed, Evidence.Rows.ToArray()), Metadata.Default.Report);
    if (args.Length != 0)
    {
        FileStream? stream = null; var failures = new List<Exception>();
        try { stream = new(Path.GetFullPath(args[1]), FileMode.CreateNew, FileAccess.Write, FileShare.Read); var bytes = System.Text.Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
        catch (Exception error) { failures.Add(error); }
        finally { try { stream?.Dispose(); } catch (Exception error) { failures.Add(error); } }
        if (failures.Count != 0) throw new AggregateException("Report output failed.", failures);
    }
    Console.WriteLine(json);
}
catch (Exception error) { throw new MetadataFault(Evidence.Failures.Append(error), Evidence.Originals.ToArray()); }
return failed == 0 ? 0 : 1;

sealed record RawOriginal(string Name, Task? Task, AggregateException? Inventory, Exception? Observed);
sealed class MetadataFault(IEnumerable<Exception> errors, RawOriginal[] originals) : AggregateException("Original controls and report metadata failed.", errors)
{ public RawOriginal[] Originals { get; } = originals; }
sealed record FaultNode(int Index, string? Type, string Message, int[] Children);
sealed record FaultGraph(int? OriginalRoot, int? ObservedRoot, FaultNode[] Nodes, bool Truncated);
sealed record TaskRow(string Name, bool Captured, bool Joined, string? Status, bool? Passed, FaultGraph Faults);
sealed record Report(string Profile, int Expected, int Passed, int Failed, TaskRow[] Tasks);
static class Evidence
{
    internal static readonly List<TaskRow> Rows = [];
    internal static readonly List<RawOriginal> Originals = [];
    internal static readonly List<Exception> Failures = [];
    internal static void Record(string name, Task? original, Exception? observed, bool? passed = null)
    {
        var inventory = original?.Exception;
        Originals.Add(new(name, original, inventory, observed));
        if (inventory is not null) Failures.Add(inventory);
        if (observed is not null) Failures.Add(observed);
        try
        {
            var indices = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance); var pending = new List<Exception>(); var nodes = new List<FaultNode>(); var edges = 0; var truncated = false;
            int? Add(Exception? error)
            {
                if (error is null) return null;
                if (indices.TryGetValue(error, out var index)) return index;
                if (pending.Count == 1024) { truncated = true; return null; }
                index = pending.Count; indices.Add(error, index); pending.Add(error); return index;
            }
            var root = Add(inventory); var caught = Add(observed);
            for (var index = 0; index < pending.Count; index++)
            {
                var error = pending[index]; IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions : error.InnerException is { } inner ? [inner] : [];
                var links = new List<int>();
                foreach (var child in children) { if (edges == 4096) { truncated = true; break; } edges++; if (Add(child) is { } next) links.Add(next); }
                nodes.Add(new(index, error.GetType().AssemblyQualifiedName, error.Message, links.ToArray()));
            }
            Rows.Add(new(name, original is not null, original?.IsCompleted ?? false, original?.Status.ToString(), passed, new(root, caught, nodes.ToArray(), truncated)));
        }
        catch (Exception error) { throw new MetadataFault(Failures.Append(error), Originals.ToArray()); }
    }
}
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(Report))]
partial class Metadata : JsonSerializerContext { }
