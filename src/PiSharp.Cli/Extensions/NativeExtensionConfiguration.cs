using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;

namespace PiSharp.Cli.Extensions;

internal enum NativeExtensionFailure
{
    InvalidConfiguration, ExecutionApprovalRequired, ApprovalMismatch, UnsupportedSchema, ActivationFailed, CleanupFailed
}

internal sealed class NativeExtensionException(NativeExtensionFailure failure, Exception? inner = null) : Exception(failure switch
{
    NativeExtensionFailure.ExecutionApprovalRequired => "Published native execution requires separate explicit approval.",
    NativeExtensionFailure.ApprovalMismatch => "Published native approval does not match this invocation.",
    NativeExtensionFailure.UnsupportedSchema => "Published native tool schema requires unfinished validator support.",
    NativeExtensionFailure.ActivationFailed => "Published native activation failed before session admission.",
    NativeExtensionFailure.CleanupFailed => "Published native cleanup failed; restart may be required.",
    _ => "Published native configuration is invalid."
}, inner)
{
    internal NativeExtensionFailure Failure { get; } = failure;
}

/// <summary>One explicit invocation, never package discovery or executable approval inferred from hashes.</summary>
internal sealed record NativeExtensionConfiguration(string Package, string ManifestPath, string ApprovalPath,
    string SnapshotRoot, ImmutableArray<string> EnabledTools)
{
    internal ImmutableArray<string> DeniedTools { get; init; } = [];
    internal ImmutableArray<string> EnabledCommands { get; init; } = [];
    internal const string Flags = "[--extension-package <published directory> --extension-manifest <JSON> " +
        "--extension-approval <separate JSON> --extension-snapshot-root <existing directory> " +
        "[--enable-extension-tool <exact name>] [--deny-extension-tool <enabled name>] [--enable-extension-command <exact name>]]";
    internal const string PolicyRevision = "experimental-policy-0";
    internal const string Scope = "cli-native-explicit";

    internal static NativeExtensionConfiguration? Optional(string? package, string? manifest, string? approval,
        string? snapshots, ImmutableArray<string> tools, ImmutableArray<string> deniedTools = default,
        ImmutableArray<string> commands = default)
    {
        if (deniedTools.IsDefault) deniedTools = [];
        if (commands.IsDefault) commands = [];
        if (package is null && manifest is null && approval is null && snapshots is null && tools.IsEmpty && deniedTools.IsEmpty && commands.IsEmpty) return null;
        if (approval is null) throw new NativeExtensionException(NativeExtensionFailure.ExecutionApprovalRequired);
        if (package is null || manifest is null || snapshots is null || tools.IsDefault || tools.Length > 16 ||
            tools.Distinct(StringComparer.Ordinal).Count() != tools.Length || tools.Any(name => !Identifier(name) ||
                name is "read" or "write" or "bash" or "powershell" or "edit" or "grep" or "find" or "ls") ||
            deniedTools.Length > 16 || deniedTools.Distinct(StringComparer.Ordinal).Count() != deniedTools.Length ||
            deniedTools.Any(name => !tools.Contains(name, StringComparer.Ordinal)) ||
            commands.Length > 16 || commands.Distinct(StringComparer.Ordinal).Count() != commands.Length ||
            commands.Any(name => !Identifier(name) || name is "help" or "quit" or "exit" or "reload" or "settings" or "trust" or
                "permissions" or "abort" or "state" or "clear-queue" or "steer" or "follow-up" or "edit" or "cancel" or "show" or "save" or "send"))
            throw Invalid();
        var result = new NativeExtensionConfiguration(SessionCommands.Absolute(package), SessionCommands.Absolute(manifest),
            SessionCommands.Absolute(approval), SessionCommands.Absolute(snapshots), tools) { DeniedTools = deniedTools, EnabledCommands = commands };
        if (result.ManifestPath == result.ApprovalPath || Within(result.Package, result.SnapshotRoot) ||
            Within(result.SnapshotRoot, result.Package)) throw Invalid();
        return result;
    }

    internal async Task<NativeExtensionPreflight> PreflightAsync(string session, string workspace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64 || !Directory.Exists(Package) || !Directory.Exists(SnapshotRoot)) throw Invalid();
        if (session == ManifestPath || session == ApprovalPath || Within(Package, session) || Within(SnapshotRoot, session)) throw Invalid();
        // Both documents are inert, separately supplied, strict-owned and bounded before any loader is constructed.
        var metadata = await ReadAsync(ManifestPath, token).ConfigureAwait(false);
        var approval = await ReadAsync(ApprovalPath, token).ConfigureAwait(false);
        var reader = new ExtensionManifestReader(new() { MaximumArtifactBytes = 8_388_608, MaximumTotalArtifactBytes = 16_777_216 });
        var parsed = reader.Read(metadata);
        if (!parsed.IsValid || !parsed.Manifest!.ExplicitOverrides.IsEmpty ||
            parsed.Manifest.DeclaredCapabilities.Contains(ExtensionDeclaredCapability.Commands) && EnabledCommands.IsEmpty) throw Invalid();
        ImmutableArray<PluginSystemImportApproval> systemImports = [];
        try
        {
            var value = approval.Value;
            string[] approvalFields = ["schemaVersion", "execution", "packageRoot", "manifestValueSha256", "artifactHashes", "sourceScope",
                "effectiveScopeId", "policyRevision", "hostGeneration", "sessionPath", "workspace", "snapshotRoot", "enabledTools"];
            var hasSystemImports = value.TryGetProperty("systemImports", out var imports);
            var hasCommands = value.TryGetProperty("enabledCommands", out var commandNames);
            Object(value, [.. approvalFields, .. (hasSystemImports ? new[] { "systemImports" } : []),
                .. (hasCommands ? new[] { "enabledCommands" } : [])]);
            if (value.GetProperty("schemaVersion").GetInt32() != 1 ||
                value.GetProperty("execution").GetString() != "ApprovePublishedFixtureExecution" ||
                Text(value, "packageRoot") != Package || Text(value, "sessionPath") != session || Text(value, "workspace") != workspace ||
                Text(value, "snapshotRoot") != SnapshotRoot ||
                Text(value, "manifestValueSha256") != parsed.ManifestValueSha256 ||
                Text(value, "sourceScope") != "Explicit" || Text(value, "effectiveScopeId") != Scope ||
                Text(value, "policyRevision") != PolicyRevision || value.GetProperty("hostGeneration").GetInt64() != 1)
                throw Mismatch();
            var names = value.GetProperty("enabledTools");
            if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() != EnabledTools.Length ||
                !names.EnumerateArray().Select(item => item.GetString()).SequenceEqual(EnabledTools)) throw Mismatch();
            if (!EnabledCommands.IsEmpty && !hasCommands || hasCommands && (commandNames.ValueKind != JsonValueKind.Array ||
                commandNames.GetArrayLength() != EnabledCommands.Length ||
                !commandNames.EnumerateArray().Select(item => item.GetString()).SequenceEqual(EnabledCommands))) throw Mismatch();
            var hashes = value.GetProperty("artifactHashes");
            if (hashes.ValueKind != JsonValueKind.Object || hashes.EnumerateObject().Count() != parsed.Manifest.ArtifactHashes.Length)
                throw Mismatch();
            foreach (var hash in parsed.Manifest.ArtifactHashes)
                if (!hashes.TryGetProperty(hash.RelativePath, out var actual) || actual.ValueKind != JsonValueKind.String ||
                    actual.GetString() != hash.Sha256) throw Mismatch();
            if (hasSystemImports)
            {
                if (imports.ValueKind != JsonValueKind.Array || imports.GetArrayLength() > 1) throw Mismatch();
                var builder = ImmutableArray.CreateBuilder<PluginSystemImportApproval>();
                foreach (var import in imports.EnumerateArray())
                {
                    Object(import, "profile", "artifact", "sha256");
                    var approved = new PluginSystemImportApproval(Text(import, "profile"), Text(import, "artifact"), Text(import, "sha256"));
                    if (approved.Profile != "windows-worker-directory-0" || approved.Artifact != "PiSharp.ExtensionHost.dll" ||
                        !parsed.Manifest.ArtifactHashes.Any(hash => hash.RelativePath == approved.Artifact && hash.Sha256 == approved.Sha256)) throw Mismatch();
                    builder.Add(approved);
                }
                systemImports = builder.ToImmutable();
            }
        }
        catch (NativeExtensionException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Mismatch(); }
        var inspection = new ExtensionTrustDecision(ExtensionTrustDisposition.ApproveMetadataInspection, Package,
            parsed.ManifestValueSha256!, ExtensionSourceScope.Explicit, Scope, PolicyRevision);
        var execution = new PluginExecutionDecision(PluginExecutionDisposition.ApprovePublishedFixtureExecution, Package,
            parsed.ManifestValueSha256!, parsed.Manifest.ArtifactHashes, ExtensionSourceScope.Explicit, Scope, PolicyRevision, 1)
            { SystemImportApprovals = systemImports };
        // Actual artifact integrity/path checks remain with the unchanged reader/loader, not this CLI document parser.
        var admitted = await reader.InspectAsync(metadata, Package, ExtensionSourceScope.Explicit, Scope, inspection, token).ConfigureAwait(false);
        if (!admitted.MetadataPreflightPassed) throw Invalid();
        return new(this, metadata, inspection, execution);
    }

    private static async Task<JsonData> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length is < 1 or > 262_144) throw Invalid();
            using var output = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > 262_144 - output.Length) throw Invalid();
                output.Write(buffer, 0, count);
            }
            var text = new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
            if (text.Length > 65_536) throw Invalid();
            using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            return JsonData.FromElement(parsed.RootElement);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (NativeExtensionException) { throw; }
        catch (Exception) { throw Invalid(); }
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw Mismatch();
    private static void Object(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
            value.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal))) throw Mismatch();
        foreach (var name in fields) if (!value.TryGetProperty(name, out _)) throw Mismatch();
    }
    private static bool Identifier(string name) => name.Length is > 0 and <= 128 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private static bool Within(string root, string target) => string.Equals(root, target, StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static NativeExtensionException Invalid() => new(NativeExtensionFailure.InvalidConfiguration);
    private static NativeExtensionException Mismatch() => new(NativeExtensionFailure.ApprovalMismatch);
}

internal sealed record NativeExtensionPreflight(NativeExtensionConfiguration Configuration, JsonData Metadata,
    ExtensionTrustDecision Inspection, PluginExecutionDecision Execution);
