using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;
using PiSharp.SdkPluginEcho.Tests;

namespace PiSharp.Qualification;

internal sealed record EchoArtifactPin(string Path, long Bytes, string Sha256);
internal sealed record EchoAdmissionInput(string MetadataJson, string Root, ExtensionSourceScope SourceScope,
    string EffectiveScopeId, ExtensionTrustDecision Inspection, PluginExecutionDecision Execution,
    EchoArtifactPin[] ArtifactPins);
internal sealed record EchoRunConfiguration(PluginAssemblyLoaderOptions Options,
    EchoAdmissionInput Sender, EchoAdmissionInput Receiver);

// Host supplies already-approved exact fresh publications. No decision is synthesized here.
internal static class SdkPluginEchoEntry
{
    private static readonly string[] Names =
    [
        "sdk-plugin-echo.actual-two-loader-contexts-shared-object-mutation-and-retirement",
        "sdk-plugin-echo.actual-held-listener-original-joins-owner-retirement"
    ];
    private static readonly List<EchoOriginal> records = [];
    private static readonly List<FileStream> leases = [];

    internal static async Task<int> Main(string[] args)
    {
        Task<int>? reportOriginal = null; AggregateException? reportAggregate = null;
        Exception? reportDirect = null; var cleanup = new List<Exception>(); int result = -1;
        try
        {
            if (args.Length != 6 || args[0] != "--config" || args[2] != "--config-sha256" || args[4] != "--report" ||
                !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[5]))
                throw new ArgumentException("Explicit root-approved absolute config/hash/fresh report required.");
            (string Name, Func<Task> Run)[] Capture()
            {
                var configuration = ReadConfiguration(args[1], args[3]);
                var sender = Admit(configuration.Sender, configuration.Options);
                var receiver = Admit(configuration.Receiver, configuration.Options);
                if (Path.GetFullPath(sender.Root) == Path.GetFullPath(receiver.Root))
                    throw new InvalidOperationException("Two distinct genuine publication roots required.");
                var cases = PluginEchoControls.Cases(configuration.Options, sender, receiver,
                    original => { lock (records) records.Add(original); }).ToArray();
                if (!cases.Select(item => item.Name).SequenceEqual(Names, StringComparer.Ordinal))
                    throw new InvalidOperationException("Exact two existing control delegates required.");
                return cases; // Same original delegates; no per-case adopting/wrapper Task.
            }
            reportOriginal = QualificationEvidence.RunAsync(["--report", args[5]], Capture, 2, CaptureOriginals);
            result = await reportOriginal.ConfigureAwait(false);
        }
        catch (Exception direct)
        {
            reportDirect = direct;
            if (reportOriginal is { IsFaulted: true }) reportAggregate = reportOriginal.Exception;
        }
        finally
        {
            // Every control closes its actual loader/registry before returning; then source leases retire.
            foreach (var lease in leases)
                try { lease.Dispose(); } catch (Exception error) { cleanup.Add(error); }
        }
        if (reportDirect is not null || cleanup.Count != 0)
        {
            var faults = new List<Exception>(cleanup);
            if (reportAggregate is not null) faults.Add(reportAggregate);
            if (reportDirect is not null) faults.Add(reportDirect);
            var raw = Snapshot();
            faults.AddRange(raw.SelectMany(item => new Exception?[] { item.Aggregate, item.Direct }).OfType<Exception>());
            throw new EchoEntryFailure(raw, reportOriginal, faults);
        }
        return result;
    }

    private static EchoRunConfiguration ReadConfiguration(string path, string expectedHash)
    {
        if (expectedHash.Length != 64 || expectedHash.Any(unit => unit is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Exact lowercase external configuration SHA256 required.");
        var stream = Hold(path, 1_048_576, null);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); stream.Position = 0;
        if (actual != expectedHash) throw new IOException("External immutable admission configuration changed.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, true);
        var text = reader.ReadToEnd();
        using var document = JsonDocument.Parse(text);
        // Options must not silently select an ambient temporary snapshot root or default authority.
        var root = document.RootElement;
        var option = root.GetProperty("options");
        foreach (var name in new[] { "snapshotParentDirectory", "hostGeneration", "manifestOptions" })
            if (!option.TryGetProperty(name, out _)) throw new InvalidOperationException("Explicit loader options required.");
        foreach (var name in new[] { "hostApiVersion", "hostRuntimeIdentifier", "trustPolicyRevision" })
            if (!option.GetProperty("manifestOptions").TryGetProperty(name, out _))
                throw new InvalidOperationException("Explicit host API/platform/policy required.");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter());
        var configuration = JsonSerializer.Deserialize<EchoRunConfiguration>(text, options)
            ?? throw new InvalidOperationException("Missing explicit configuration.");
        if (!Path.IsPathFullyQualified(configuration.Options.SnapshotParentDirectory) ||
            !Directory.Exists(configuration.Options.SnapshotParentDirectory) || configuration.Options.HostGeneration < 1)
            throw new InvalidOperationException("An explicitly supplied existing private snapshot parent required.");
        return configuration;
    }

    private static EchoPackageAdmission Admit(EchoAdmissionInput input, PluginAssemblyLoaderOptions options)
    {
        if (input.MetadataJson.Length > 65_536 || !Path.IsPathFullyQualified(input.Root) ||
            input.ArtifactPins.Length is < 1 or > 128 ||
            input.Inspection.Disposition != ExtensionTrustDisposition.ApproveMetadataInspection ||
            input.Execution.Disposition != PluginExecutionDisposition.ApprovePublishedFixtureExecution ||
            input.Execution.HostGeneration != options.HostGeneration)
            throw new InvalidOperationException("Independent actual root artifact/execution admission required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Root));
        var pins = new Dictionary<string, EchoArtifactPin>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in input.ArtifactPins)
        {
            if (!Path.IsPathFullyQualified(pin.Path) || !Path.GetFullPath(pin.Path).StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !pins.TryAdd(Path.GetFullPath(pin.Path), pin))
                throw new InvalidOperationException("Exact distinct publication-contained artifact pins required.");
            var stream = Hold(pin.Path, 8_388_608, pin.Bytes);
            var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); stream.Position = 0;
            if (sha != pin.Sha256) throw new IOException("Admitted published artifact changed.");
        }
        // This verifies shared foundation bytes against the assemblies actually used by this host,
        // rather than claiming identity from a manifest name or a manufactured assembly object.
        foreach (var assembly in new[] { typeof(JsonData).Assembly, typeof(IPiSharpExtension).Assembly })
        {
            if (!ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), AssemblyLoadContext.Default))
                throw new InvalidOperationException("Actual host shared foundation identity required.");
            var copy = Path.Combine(root, Path.GetFileName(assembly.Location));
            if (!pins.TryGetValue(copy, out var pin) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() != pin.Sha256)
                throw new InvalidOperationException("Actual publication must carry byte-identical host Contracts/Abstractions.");
        }
        return new(JsonData.Parse(input.MetadataJson), root, input.SourceScope, input.EffectiveScopeId,
            input.Inspection, input.Execution); // Exact caller decisions, unchanged; loader verifies all bindings.
    }

    private static FileStream Hold(string path, long maximumBytes, long? exactBytes)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        leases.Add(stream);
        if (stream.Length > maximumBytes || exactBytes is { } expected && stream.Length != expected ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Exact bounded regular admitted input required.");
        return stream;
    }
    private static EchoOriginal[] Snapshot() { lock (records) return records.ToArray(); }
    private static CapturedOriginal[] CaptureOriginals()
    {
        var raw = Snapshot();
        // The unchanged controls put every no-Task synchronous acquisition error in their
        // failed CaseTask graph. Preserve all real subsidiary rows even on that failure path.

        return raw.Where(item => item.Original is not null).Select(item =>
            new CapturedOriginal(item.Phase, item.Original!, item.Aggregate, item.Direct)).ToArray();
    }
    private sealed class EchoEntryFailure(EchoOriginal[] originals, Task? reportOriginal, IEnumerable<Exception> faults)
        : Exception("Actual echo qualification/report/source-retirement originals failed.",
            new AggregateException(faults.Distinct<Exception>(ReferenceEqualityComparer.Instance)))
    {
        internal IReadOnlyList<EchoOriginal> Originals { get; } = originals;
        internal Task? ReportOriginal { get; } = reportOriginal;
    }
}