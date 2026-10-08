using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PublishedNodeHelloFixture;

/// <summary>Explicit approved private-assembly package; actual Hello and validation functions execute in Node.</summary>
public sealed class Entry : IPiSharpExtension
{
    private readonly object gate = new(); private NodeHelloExtension? extension; private Options? options;
    private Task? disposal; private bool initialized; private string stage = "constructed"; private object? failure;
    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        lock (gate) { if (initialized || disposal is not null) throw new InvalidOperationException("Hello package already initialized/closed."); initialized = true; }
        try
        {
            stage = "read-explicit-options"; options = Options.Read(Environment.GetEnvironmentVariable("PISHARP_NATIVE_NODE_HELLO_OPTIONS"));
            stage = "admit-immutable-hello-launch";
            var launch = NodeHelloWorkerLaunch.ForHello(options.Node, options.Repository, options.RunRoot, options.Oracle, options.Jiti,
                options.Reference, options.HelloReference, options.WorkerGeneration, options.SessionGeneration);
            stage = "initialize-original-source"; extension = new(launch, options.Workspace);
            await extension.InitializeAsync(registry, cancellationToken).ConfigureAwait(false);
            if (options.AuthoredFinalReplacement)
                registry.RegisterToolCallHandler(new("authored-invalid-hello-replacement", (call, context, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<ExtensionToolCallPatch?>(call.ToolName == "hello" &&
                        call.Arguments.Value.GetProperty("name").GetString() == "fixture-invalid-replacement"
                        ? new(JsonData.Parse("{\"name\":7}")) : null);
                }));
            stage = "initialized";
        }
        catch (Exception error) { failure = Failure(error); throw; }
    }
    public ValueTask DisposeAsync() { lock (gate) return new(disposal ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        var failures = new List<Exception>();
        if (extension is not null) try { await extension.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (options is not null)
        {
            try
            {
                NodeWorkerTermination? termination = null;
                if (extension?.Termination is { } closed) termination = await closed.ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, profile = "native-package-real-hello-0", initializationStage = stage, initializationFailure = failure,
                    authoredFinalReplacement = options.AuthoredFinalReplacement, sourceLoad = extension?.SourceLoadReport?.Value,
                    sourceOperations = extension?.SourceOperations.Select(value => value.Value).ToArray(), sourceFinalization = extension?.SourceFinalizationReport?.Value,
                    activeNativeContexts = extension?.ActiveContexts ?? 0, protocol = extension?.WorkerSnapshot, termination,
                    assemblies = new[] { AssemblyOwner(typeof(Entry)), AssemblyOwner(typeof(NodeHelloExtension)), AssemblyOwner(typeof(NodeWorkerSupervisor)), AssemblyOwner(typeof(IPiSharpExtension)), AssemblyOwner(typeof(JsonData)) },
                    cleanupFailures = failures.Select(error => error.GetType().FullName).ToArray(), phaseAcceptanceClaimed = false, executedCorpusCountCredit = 0
                }) + "\n");
                if (bytes.Length > 2_097_152) throw new IOException("Hello package receipt byte limit.");
                NoLinks(options.Receipt); await using var target = new FileStream(options.Receipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
                await target.WriteAsync(bytes).ConfigureAwait(false); await target.FlushAsync().ConfigureAwait(false); target.Flush(flushToDisk: true);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Original Hello package cleanup failed.", failures);
    }
    private static object AssemblyOwner(Type type)
    { var owner = AssemblyLoadContext.GetLoadContext(type.Assembly); return new { name = type.Assembly.GetName().Name, context = owner?.Name, collectible = owner?.IsCollectible }; }
    private static object Failure(Exception error)
    {
        var rows = new List<object>();
        for (Exception? current = error; current is not null && rows.Count < 8; current = current.InnerException)
            rows.Add(new { type = current.GetType().FullName, message = ErrorText(current.Message), stack = ErrorText(current.StackTrace ?? "") });
        return new { exceptions = rows, chainLimit = 8 };
    }
    private static object ErrorText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); var end = Math.Min(4096, value.Length);
        if (end < value.Length && end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        return new { utf8Bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), prefix = value[..end], complete = end == value.Length };
    }
    private sealed record Options(string Node, string Repository, string Oracle, string Jiti, string Reference, string HelloReference,
        string Workspace, string RunRoot, string Receipt, long WorkerGeneration, long SessionGeneration, bool AuthoredFinalReplacement)
    {
        internal static Options Read(string? file)
        {
            var path = Absolute(file); NoLinks(path); using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length is < 1 or > 8192) throw new IOException("Explicit Hello options byte limit.");
            using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 4 }); var value = document.RootElement;
            string[] fields = ["schemaVersion", "node", "repository", "oracle", "jiti", "reference", "helloReference", "workspace", "runRoot", "receipt", "workerGeneration", "sessionGeneration", "authoredFinalReplacement"];
            if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)) || value.GetProperty("schemaVersion").GetInt32() != 1)
                throw new IOException("Explicit Hello options fields differ.");
            var options = new Options(Text(value, "node"), Text(value, "repository"), Text(value, "oracle"), Text(value, "jiti"), Text(value, "reference"), Text(value, "helloReference"),
                Text(value, "workspace"), Text(value, "runRoot"), Text(value, "receipt"), value.GetProperty("workerGeneration").GetInt64(), value.GetProperty("sessionGeneration").GetInt64(), value.GetProperty("authoredFinalReplacement").GetBoolean());
            var parent = Path.GetDirectoryName(path)!;
            if (Path.GetDirectoryName(parent) != options.Workspace || !Directory.Exists(options.Workspace) || Path.GetDirectoryName(options.RunRoot) != parent ||
                Path.GetDirectoryName(options.Receipt) != parent || Path.Exists(options.RunRoot) || Path.Exists(options.Receipt) || options.Receipt == path)
                throw new IOException("Fresh owned Hello invocation/receipt/workspace paths required.");
            foreach (var item in new[] { options.Workspace, options.RunRoot, options.Receipt }) NoLinks(item); return options;
        }
        private static string Text(JsonElement value, string name) => Absolute(value.GetProperty(name).GetString());
    }
    private static string Absolute(string? path)
    { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Any(char.IsControl)) throw new IOException("Explicit local absolute Hello path required."); return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
    private static void NoLinks(string path)
    { for (var current = path; current is not null; current = Path.GetDirectoryName(current)) if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked Hello input path."); }
}
