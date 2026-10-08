using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;

namespace PublishedNodeProtectedPathsFixture;

/// <summary>Explicitly approved, hook-only native package around the unchanged protected-paths worker profile.</summary>
public sealed class Entry : IPiSharpExtension
{
    public const string OptionsVariable = "PISHARP_NATIVE_NODE_OPTIONS";
    private readonly object gate = new();
    private NodeToolCallExtension? extension;
    private Options? options;
    private Task? disposal;
    private bool initialized;
    private string initializationStage = "constructed";
    private object? initializationFailure;

    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        lock (gate)
        {
            if (initialized || disposal is not null) throw new InvalidOperationException("Package already initialized or closed.");
            initialized = true;
        }
        try
        {
            initializationStage = "read-explicit-options";
            options = Options.Read(Environment.GetEnvironmentVariable(OptionsVariable));
            initializationStage = "admit-pinned-launch";
            var launch = NodeExtensionWorkerLaunch.ForProtectedPaths(options.Node, options.Repository, options.RunRoot,
                options.Oracle, options.Jiti, options.Reference, options.WorkerGeneration, options.SessionGeneration);
            initializationStage = "construct-source-proxy";
            extension = new(launch);
            initializationStage = "initialize-source-hook";
            // Pass the real shared ABI registry directly. There is no replacement hook, synthetic tool, or source replay.
            await extension.InitializeAsync(registry, cancellationToken).ConfigureAwait(false);
            initializationStage = "initialized";
        }
        catch (Exception error) { initializationFailure = Failure(error); throw; }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        var failures = new List<Exception>();
        if (extension is not null)
            try { await extension.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        if (options is not null)
        {
            try
            {
                NodeWorkerTermination? termination = null;
                if (extension?.Termination is { } terminated) termination = await terminated.ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, profile = "native-package-protected-paths-0", hookOnly = true,
                    initializationStage, initializationFailure,
                    sourceLoad = extension?.SourceLoadReport?.Value,
                    sourceLastObservation = extension?.LastSourceObservation?.Value,
                    sourceLastSettlement = extension?.LastSourceSettlement?.Value,
                    sourceFinalization = extension?.SourceFinalizationReport?.Value,
                    activeNativeContexts = extension?.ActiveBrokerContexts ?? 0,
                    protocol = extension?.WorkerSnapshot, termination,
                    assemblies = new[] { AssemblyOwner(typeof(Entry)), AssemblyOwner(typeof(NodeToolCallExtension)),
                        AssemblyOwner(typeof(NodeWorkerSupervisor)), AssemblyOwner(typeof(IPiSharpExtension)), AssemblyOwner(typeof(JsonData)) },
                    cleanupFailures = failures.Select(error => error.GetType().FullName).ToArray(),
                    upstreamDifferential = false, phaseAcceptanceClaimed = false
                }) + "\n");
                if (bytes.Length > 2_097_152) throw new IOException("Package cleanup receipt limit.");
                NoLinks(options.Receipt);
                await using var target = new FileStream(options.Receipt, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 8192, FileOptions.Asynchronous);
                await target.WriteAsync(bytes).ConfigureAwait(false);
                await target.FlushAsync().ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Published Node package cleanup failed.", failures);
    }

    private static object Failure(Exception error)
    {
        var rows = new List<object>();
        for (Exception? current = error; current is not null && rows.Count < 8; current = current.InnerException)
            rows.Add(new { type = current.GetType().FullName, message = ErrorText(current.Message), stack = ErrorText(current.StackTrace ?? "") });
        return new { exceptions = rows, chainLimit = 8 };
    }
    private static object ErrorText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new { bytes = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            prefix = value.Length <= 2048 ? value : value[..2048], truncated = value.Length > 2048 };
    }

    private static object AssemblyOwner(Type type)
    {
        var context = AssemblyLoadContext.GetLoadContext(type.Assembly);
        return new { name = type.Assembly.GetName().Name, context = context?.Name, collectible = context?.IsCollectible };
    }

    private sealed record Options(string Node, string Repository, string Oracle, string Jiti, string Reference,
        string RunRoot, string Receipt, long WorkerGeneration, long SessionGeneration)
    {
        internal static Options Read(string? file)
        {
            var path = Absolute(file); NoLinks(path);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length is < 1 or > 8192) throw new IOException("Explicit package options limit.");
            using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            string[] fields = ["schemaVersion", "node", "repository", "oracle", "jiti", "reference", "runRoot",
                "receipt", "workerGeneration", "sessionGeneration"];
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
                !value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(fields.Order(StringComparer.Ordinal)) || value.GetProperty("schemaVersion").GetInt32() != 1)
                throw new IOException("Explicit package options differ.");
            var options = new Options(Text(value, "node"), Text(value, "repository"), Text(value, "oracle"),
                Text(value, "jiti"), Text(value, "reference"), Text(value, "runRoot"), Text(value, "receipt"),
                value.GetProperty("workerGeneration").GetInt64(), value.GetProperty("sessionGeneration").GetInt64());
            var parent = Path.GetDirectoryName(path)!;
            if (Path.GetDirectoryName(options.RunRoot) != parent || Path.GetDirectoryName(options.Receipt) != parent ||
                !Directory.Exists(parent) || Path.Exists(options.RunRoot) || Path.Exists(options.Receipt) || options.Receipt == path)
                throw new IOException("Fresh task-local worker and receipt paths required.");
            foreach (var item in new[] { options.RunRoot, options.Receipt }) NoLinks(item);
            return options;
        }
        private static string Text(JsonElement value, string name) => Absolute(value.GetProperty(name).GetString());
    }

    private static string Absolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("Explicit absolute package paths required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static void NoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked package harness path.");
    }
}
