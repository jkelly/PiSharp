using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Runtime.Discovery;

/// <summary>Strict metadata and real byte preflight, without loading, constructing or activating an extension.</summary>
public sealed class ExtensionManifestReader
{
    private static readonly ImmutableArray<string> knownRids = ["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    private readonly ExtensionManifestReaderOptions options;
    private readonly ExtensionArtifactReadObserver? observer;
    private readonly ExtensionRegistryOptions jsonOptions;

    public ExtensionManifestReader(ExtensionManifestReaderOptions? options = null, ExtensionArtifactReadObserver? observer = null)
    {
        this.options = options ?? new();
        if (!VersionValue(this.options.HostApiVersion, out _) || !knownRids.Contains(this.options.HostRuntimeIdentifier) ||
            !RegistrationPolicy.Identifier(this.options.TrustPolicyRevision, 128) || this.options.MaximumManifestCharacters is < 1 or > 65_536 ||
            this.options.MaximumArtifacts is < 1 or > 128 || this.options.MaximumArtifactBytes < 0 || this.options.MaximumTotalArtifactBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.observer = observer;
        jsonOptions = new() { MaximumJsonCharacters = this.options.MaximumManifestCharacters, MaximumJsonDepth = 8 };
    }

    public ExtensionManifestReadResult Read(JsonData metadata)
    {
        if (metadata is null) return Invalid(ExtensionManifestFailure.InvalidJson, "manifest");
        var raw = metadata.ToString();
        if (raw.Length > options.MaximumManifestCharacters) return Invalid(ExtensionManifestFailure.ManifestTooLarge, "manifest");
        // Uses the registration boundary's strict retained-raw reparse, duplicate/finite/Unicode checks.
        if (!RegistrationPolicy.Json(metadata, jsonOptions)) return Invalid(ExtensionManifestFailure.InvalidJson, "manifest");
        try
        {
            var value = metadata.Value;
            Object(value, "manifest", "schemaVersion", "id", "packageVersion", "hostApiRange", "runtimeKind", "assembly", "entryType",
                "tfm", "rids", "requiredFeatures", "declaredCapabilities", "resourcePaths", "explicitOverrides", "artifactHashes");
            var schema = value.GetProperty("schemaVersion");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "0")
                throw Shape(ExtensionManifestFailure.UnsupportedSchema, "schemaVersion");
            var id = Text(value.GetProperty("id"), "id", 128);
            if (!RegistrationPolicy.Identifier(id, 128) || !char.IsAsciiLetterOrDigit(id[0]) || !char.IsAsciiLetterOrDigit(id[^1]) ||
                id.Any(char.IsAsciiLetterUpper)) throw Shape(ExtensionManifestFailure.InvalidManifest, "id");
            var packageVersion = Text(value.GetProperty("packageVersion"), "packageVersion", 64);
            if (!VersionValue(packageVersion, out _)) throw Shape(ExtensionManifestFailure.InvalidManifest, "packageVersion");
            var range = value.GetProperty("hostApiRange");
            Object(range, "hostApiRange", "minimum", "maximumExclusive");
            var minimum = Text(range.GetProperty("minimum"), "hostApiRange", 64);
            var maximum = Text(range.GetProperty("maximumExclusive"), "hostApiRange", 64);
            if (!VersionValue(minimum, out var lower) || !VersionValue(maximum, out var upper) || lower >= upper)
                throw Shape(ExtensionManifestFailure.InvalidManifest, "hostApiRange");
            var assembly = Relative(value.GetProperty("assembly"), "assembly");
            if (!assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw Shape(ExtensionManifestFailure.InvalidManifest, "assembly");
            var entryType = Text(value.GetProperty("entryType"), "entryType", 512);
            if (!entryType.Split(['.', '+']).All(part => part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_') &&
                part.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')))
                throw Shape(ExtensionManifestFailure.InvalidManifest, "entryType");
            var rids = Strings(value.GetProperty("rids"), "rids", 16);
            if (rids.IsEmpty) throw Shape(ExtensionManifestFailure.InvalidManifest, "rids");
            var features = Strings(value.GetProperty("requiredFeatures"), "requiredFeatures", 32);
            var capabilities = Strings(value.GetProperty("declaredCapabilities"), "declaredCapabilities", 3)
                .Select(name => name switch
                {
                    "tools" => ExtensionDeclaredCapability.Tools,
                    "commands" => ExtensionDeclaredCapability.Commands,
                    "observations" => ExtensionDeclaredCapability.Observations,
                    _ => throw Shape(ExtensionManifestFailure.UnsupportedCapability, "declaredCapabilities")
                }).ToImmutableArray();
            var resources = Strings(value.GetProperty("resourcePaths"), "resourcePaths", 64, paths: true);
            var overrideValue = value.GetProperty("explicitOverrides");
            if (overrideValue.ValueKind != JsonValueKind.Array || overrideValue.GetArrayLength() > 16)
                throw Shape(ExtensionManifestFailure.InvalidManifest, "explicitOverrides");
            var overrides = ImmutableArray.CreateBuilder<ExtensionExplicitOverride>();
            var overrideNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in overrideValue.EnumerateArray())
            {
                Object(item, "explicitOverrides", "kind", "target");
                var kind = Text(item.GetProperty("kind"), "explicitOverrides", 32) switch
                {
                    "tool" => ExtensionOverrideKind.Tool,
                    "command" => ExtensionOverrideKind.Command,
                    _ => throw Shape(ExtensionManifestFailure.UnsupportedOverride, "explicitOverrides")
                };
                var target = Text(item.GetProperty("target"), "explicitOverrides", 128);
                if (!RegistrationPolicy.Identifier(target, 128) || !overrideNames.Add(kind + ":" + target))
                    throw Shape(ExtensionManifestFailure.InvalidManifest, "explicitOverrides");
                overrides.Add(new(kind, target));
            }
            var artifactValue = value.GetProperty("artifactHashes");
            if (artifactValue.ValueKind != JsonValueKind.Object)
                throw Shape(ExtensionManifestFailure.InvalidManifest, "artifactHashes");
            var artifacts = ImmutableArray.CreateBuilder<ExtensionArtifactHash>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in artifactValue.EnumerateObject())
            {
                if (artifacts.Count >= options.MaximumArtifacts)
                    throw Shape(ExtensionManifestFailure.ArtifactCountExceeded, "artifactHashes");
                if (!ManifestPathPolicy.Relative(property.Name) || !paths.Add(property.Name))
                    throw Shape(ExtensionManifestFailure.InvalidArtifactPath, "artifactHashes");
                var hash = Text(property.Value, "artifactHashes", 64);
                if (!ManifestPathPolicy.Hash(hash)) throw Shape(ExtensionManifestFailure.InvalidManifest, "artifactHashes");
                artifacts.Add(new(property.Name, hash.ToLowerInvariant()));
            }
            if (!paths.Contains(assembly) || resources.Any(path => !paths.Contains(path)))
                throw Shape(ExtensionManifestFailure.InvalidManifest, "artifactHashes");
            // The path spelling used for every required artifact must match its hash declaration, even on Windows.
            if (!artifacts.Any(item => item.RelativePath == assembly) || resources.Any(path => !artifacts.Any(item => item.RelativePath == path)))
                throw Shape(ExtensionManifestFailure.InvalidArtifactPath, "artifactHashes");
            var manifest = new ExtensionManifest(0, id, packageVersion, new(minimum, maximum),
                Text(value.GetProperty("runtimeKind"), "runtimeKind", 32), assembly, entryType,
                Text(value.GetProperty("tfm"), "tfm", 32), rids, features, capabilities, resources,
                overrides.ToImmutable(), artifacts.ToImmutable(), metadata);
            var compatibility = Compatibility(manifest);
            if (compatibility is not null) return Invalid(compatibility.Failure, compatibility.Field);
            return new(manifest, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), []);
        }
        catch (ManifestShapeException error) { return Invalid(error.Failure, error.Field); }
        catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException)
        { return Invalid(ExtensionManifestFailure.InvalidManifest, "manifest"); }
    }

    public async ValueTask<ExtensionManifestAdmission> InspectAsync(JsonData metadata, string packageRoot,
        ExtensionSourceScope sourceScope, string effectiveScopeId, ExtensionTrustDecision? trustDecision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(sourceScope) || !RegistrationPolicy.Identifier(effectiveScopeId, 128))
            return new(null, null, null, sourceScope, "invalid-scope", [], [new(ExtensionManifestFailure.TrustBindingMismatch, "effectiveScope")]);
        var read = Read(metadata);
        ExtensionManifestAdmission Reject(ExtensionManifestDiagnostic diagnostic, string? root = null) =>
            new(read.Manifest, read.ManifestValueSha256, root, sourceScope, effectiveScopeId, [], [diagnostic]);
        if (!read.IsValid) return new(null, null, null, sourceScope, effectiveScopeId, [], read.Diagnostics);
        var manifest = read.Manifest!;
        var root = ManifestPathPolicy.Root(packageRoot);
        if (root is null) return Reject(new(ExtensionManifestFailure.InvalidPackageRoot, "packageRoot"));
        var trust = ExtensionTrustPolicy.Evaluate(trustDecision, root, read.ManifestValueSha256!, sourceScope, effectiveScopeId, options.TrustPolicyRevision);
        if (trust is not null) return Reject(trust, root);
        // GetAttributes alone does not identify Unix FIFOs/devices. Do not open them in this experimental profile.
        if (!OperatingSystem.IsWindows()) return Reject(new(ExtensionManifestFailure.UnsupportedFilesystemPlatform, "packageRoot"), root);
        var currentField = "packageRoot";
        try
        {
            if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
                return Reject(new(ExtensionManifestFailure.InvalidPackageRoot, "packageRoot"), root);
            ManifestPathPolicy.Inspect(root, requireDirectory: true);
            var verified = ImmutableArray.CreateBuilder<VerifiedExtensionArtifact>();
            long total = 0;
            foreach (var artifact in manifest.ArtifactHashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentField = artifact.RelativePath;
                var full = ManifestPathPolicy.Resolve(root, artifact.RelativePath);
                ManifestPathPolicy.Inspect(full, requireDirectory: false);
                var result = await VerifyAsync(artifact, full, options.MaximumTotalArtifactBytes - total, cancellationToken).ConfigureAwait(false);
                // Detect persistent component replacement. This is not atomic no-follow/open-handle identity proof.
                ManifestPathPolicy.Inspect(full, requireDirectory: false);
                verified.Add(result);
                total += result.Bytes;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(manifest, read.ManifestValueSha256, root, sourceScope, effectiveScopeId, verified.ToImmutable(), []);
        }
        catch (PathAdmissionException error) { return Reject(new(error.Failure, currentField), root); }
        catch (FileNotFoundException) { return Reject(new(ExtensionManifestFailure.MissingArtifact, currentField), root); }
        catch (DirectoryNotFoundException) { return Reject(new(ExtensionManifestFailure.MissingArtifact, currentField), root); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return Reject(new(ExtensionManifestFailure.ArtifactReadFailed, currentField), root); }
    }

    private async ValueTask<VerifiedExtensionArtifact> VerifyAsync(ExtensionArtifactHash artifact, string full,
        long remainingBudget, CancellationToken cancellationToken)
    {
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8_192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (observer is not null) await observer(ExtensionArtifactReadStage.Opened, artifact.RelativePath, cancellationToken).ConfigureAwait(false);
            var length = stream.Length;
            if (length > options.MaximumArtifactBytes) throw new PathAdmissionException(ExtensionManifestFailure.ArtifactTooLarge);
            if (length > remainingBudget) throw new PathAdmissionException(ExtensionManifestFailure.ArtifactBudgetExceeded);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[8_192];
            long bytes = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (count > options.MaximumArtifactBytes - bytes) throw new PathAdmissionException(ExtensionManifestFailure.ArtifactTooLarge);
                if (count > remainingBudget - bytes) throw new PathAdmissionException(ExtensionManifestFailure.ArtifactBudgetExceeded);
                hash.AppendData(buffer, 0, count);
                bytes += count;
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (bytes != length || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(artifact.Sha256)))
                throw new PathAdmissionException(ExtensionManifestFailure.IntegrityMismatch);
            return new(artifact.RelativePath, full, actual, bytes);
        }
        finally
        {
            try
            {
                try
                {
                    if (observer is not null) await observer(ExtensionArtifactReadStage.Closing, artifact.RelativePath, CancellationToken.None).ConfigureAwait(false);
                }
                finally { await stream.DisposeAsync().ConfigureAwait(false); }
            }
            // Closing/disposal must settle before actual caller cancellation takes precedence over their faults.
            finally { cancellationToken.ThrowIfCancellationRequested(); }
        }
    }

    private ExtensionManifestDiagnostic? Compatibility(ExtensionManifest manifest)
    {
        if (manifest.RuntimeKind != "native") return new(ExtensionManifestFailure.UnsupportedRuntimeKind, "runtimeKind");
        VersionValue(options.HostApiVersion, out var host);
        VersionValue(manifest.HostApiRange.Minimum, out var lower);
        VersionValue(manifest.HostApiRange.MaximumExclusive, out var upper);
        if (host < lower || host >= upper) return new(ExtensionManifestFailure.UnsupportedHostApi, "hostApiRange");
        if (manifest.Tfm != "net10.0") return new(ExtensionManifestFailure.UnsupportedTfm, "tfm");
        if (manifest.Rids.Any(rid => !knownRids.Contains(rid)) || !manifest.Rids.Contains(options.HostRuntimeIdentifier))
            return new(ExtensionManifestFailure.UnsupportedRid, "rids");
        if (manifest.RequiredFeatures.Any(feature => !ExperimentalExtensionContract.Features.Contains(feature)))
            return new(ExtensionManifestFailure.UnsupportedFeature, "requiredFeatures");
        if (!manifest.ExplicitOverrides.IsEmpty) return new(ExtensionManifestFailure.UnsupportedOverride, "explicitOverrides");
        return null;
    }

    private static ExtensionManifestReadResult Invalid(ExtensionManifestFailure failure, string field) => new(null, null, [new(failure, field)]);
    private static ManifestShapeException Shape(ExtensionManifestFailure failure, string field) => new(failure, field);
    private static void Object(JsonElement value, string field, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw Shape(ExtensionManifestFailure.InvalidManifest, field);
    }
    private static string Text(JsonElement value, string field, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String) throw Shape(ExtensionManifestFailure.InvalidManifest, field);
        var text = value.GetString()!;
        if (text.Length is 0 || text.Length > maximum || text.Any(char.IsControl)) throw Shape(ExtensionManifestFailure.InvalidManifest, field);
        return text;
    }
    private static string Relative(JsonElement value, string field)
    {
        var text = Text(value, field, 1_024);
        if (!ManifestPathPolicy.Relative(text)) throw Shape(ExtensionManifestFailure.InvalidArtifactPath, field);
        return text;
    }
    private static ImmutableArray<string> Strings(JsonElement value, string field, int maximum, bool paths = false)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
            throw Shape(ExtensionManifestFailure.InvalidManifest, field);
        var builder = ImmutableArray.CreateBuilder<string>();
        var unique = new HashSet<string>(paths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var text = paths ? Relative(item, field) : Text(item, field, 128);
            if ((!paths && !RegistrationPolicy.Identifier(text, 128)) || !unique.Add(text))
                throw Shape(ExtensionManifestFailure.InvalidManifest, field);
            builder.Add(text);
        }
        return builder.ToImmutable();
    }
    private static bool VersionValue(string? value, out Version version)
    {
        version = new(0, 0, 0);
        if (value is null || value.Length > 64) return false;
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || (part.Length > 1 && part[0] == '0') ||
            !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        return Version.TryParse(value, out version!);
    }
    private sealed class ManifestShapeException(ExtensionManifestFailure failure, string field) : Exception
    {
        internal ExtensionManifestFailure Failure { get; } = failure;
        internal string Field { get; } = field;
    }
}
