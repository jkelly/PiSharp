namespace PiSharp.Extensions.Runtime.Discovery;

public enum ExtensionTrustDisposition { Deny, ApproveMetadataInspection }

/// <summary>An explicit host/caller decision. Hash equality alone can never create this decision.</summary>
public sealed record ExtensionTrustDecision(
    ExtensionTrustDisposition Disposition, string CanonicalPackageRoot, string ManifestValueSha256,
    ExtensionSourceScope SourceScope, string EffectiveScopeId, string PolicyRevision);

public static class ExtensionTrustPolicy
{
    public static ExtensionManifestDiagnostic? Evaluate(ExtensionTrustDecision? decision, string root,
        string manifestHash, ExtensionSourceScope sourceScope, string effectiveScopeId, string policyRevision)
    {
        if (decision is null) return new(ExtensionManifestFailure.TrustRequired, "trust");
        if (decision.Disposition == ExtensionTrustDisposition.Deny)
            return new(ExtensionManifestFailure.TrustDenied, "trust");
        var canonical = ManifestPathPolicy.Root(root);
        if (decision.Disposition != ExtensionTrustDisposition.ApproveMetadataInspection ||
            !Enum.IsDefined(sourceScope) || decision.SourceScope != sourceScope ||
            !RegistrationPolicy.Identifier(effectiveScopeId, 128) || decision.EffectiveScopeId != effectiveScopeId ||
            !RegistrationPolicy.Identifier(policyRevision, 128) || !ManifestPathPolicy.Hash(manifestHash) ||
            canonical is null || !string.Equals(root, canonical, ManifestPathPolicy.PathComparison) ||
            decision.PolicyRevision != policyRevision || !ManifestPathPolicy.Hash(decision.ManifestValueSha256) ||
            !string.Equals(decision.ManifestValueSha256, manifestHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(decision.CanonicalPackageRoot, root, ManifestPathPolicy.PathComparison))
            return new(ExtensionManifestFailure.TrustBindingMismatch, "trust");
        return null;
    }
}

internal static class ManifestPathPolicy
{
    // Windows directories can be case-sensitive. Without physical identity proof, textual casing must match too.
    internal static StringComparison PathComparison => StringComparison.Ordinal;
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal static bool Relative(string? path)
    {
        if (path is not { Length: > 0 and <= 1_024 } || path.Contains('\\') || path.Contains(':') ||
            path.StartsWith('/') || path.Any(character => char.IsControl(character) || char.IsSurrogate(character))) return false;
        return path.Split('/').All(Segment);
    }

    private static bool Segment(string segment)
    {
        if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return false;
        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
        return !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    internal static string? Root(string? path)
    {
        if (path is not { Length: > 0 and <= 4_096 } || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Any(character => char.IsControl(character) || char.IsSurrogate(character))) return null;
        try
        {
            var prefix = Path.GetPathRoot(path)!;
            if (path[prefix.Length..].Contains(':') || !path[prefix.Length..]
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).All(Segment)) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    internal static string Resolve(string root, string relative)
    {
        if (!Relative(relative)) throw new PathAdmissionException(ExtensionManifestFailure.InvalidArtifactPath);
        var full = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
        var back = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(back) || back == ".." || back.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PathAdmissionException(ExtensionManifestFailure.InvalidArtifactPath);
        return full;
    }

    internal static void Inspect(string fullPath, bool requireDirectory)
    {
        var prefix = Path.GetPathRoot(fullPath)!;
        var current = prefix;
        Check(current, directory: true);
        var parts = fullPath[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            Check(current, directory: index != parts.Length - 1 || requireDirectory);
        }
    }

    private static void Check(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new PathAdmissionException(ExtensionManifestFailure.ReparsePoint);
        if (attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new PathAdmissionException(ExtensionManifestFailure.InvalidArtifactPath);
    }
}

internal sealed class PathAdmissionException(ExtensionManifestFailure failure) : Exception
{
    internal ExtensionManifestFailure Failure { get; } = failure;
}
