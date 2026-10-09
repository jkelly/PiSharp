using System.Text.RegularExpressions;

namespace PiSharp.Tools.Files;

/// <summary>Explicit cwd/home path profile. Read-only host probes choose source-ordered read filename variants.</summary>
public sealed class PathResolver
{
    private readonly IFileOperations _operations;
    private readonly int _maximumCharacters;
    public string WorkingDirectory { get; }
    public string HomeDirectory { get; }

    public PathResolver(string workingDirectory, string homeDirectory, IFileOperations operations, int maximumCharacters = 4096)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        _operations = operations; _maximumCharacters = maximumCharacters;
        WorkingDirectory = Absolute(workingDirectory);
        HomeDirectory = Absolute(homeDirectory);
    }

    public string Resolve(string path)
    {
        // Source resolveToCwd: path.resolve(cwd, "") is the working directory.
        if (path is { Length: 0 }) return WorkingDirectory;
        CheckText(path);
        var normalized = Regex.Replace(path, "[\u00a0\u2000-\u200a\u202f\u205f\u3000]", " ");
        if (normalized.StartsWith('@')) normalized = normalized[1..];
        if (OperatingSystem.IsWindows() && normalized.StartsWith('/') && !normalized.StartsWith("//", StringComparison.Ordinal) && !normalized.Contains('\\'))
        {
            var drive = Regex.Match(normalized, "^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (drive.Success) normalized = char.ToUpperInvariant(drive.Groups[1].Value[0]) + ":\\" + drive.Groups[2].Value.Replace('/', '\\');
        }
        if (normalized == "~") normalized = HomeDirectory;
        else if (normalized.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && normalized.StartsWith("~\\", StringComparison.Ordinal)))
            normalized = Path.Join(HomeDirectory, normalized[2..]);
        else if (normalized.StartsWith("file://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Unsupported file URL.", nameof(path));
            normalized = uri.LocalPath;
        }
        if (OperatingSystem.IsWindows() && Regex.IsMatch(normalized, "^[a-z]:($|[^\\\\/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Drive-relative paths require ambient drive state and are unsupported.", nameof(path));
        return Absolute(Path.GetFullPath(normalized, WorkingDirectory));
    }

    public async ValueTask<string> ResolveReadAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        var nfd = resolved.Normalize(System.Text.NormalizationForm.FormD);
        var variants = new[] { resolved,
            Regex.Replace(resolved, " (AM|PM)\\.", "\u202f$1.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            nfd, resolved.Replace('\'', '\u2019'), nfd.Replace('\'', '\u2019') };
        foreach (var variant in variants.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckText(variant);
            if (await _operations.ExistsAsync(variant, cancellationToken).ConfigureAwait(false)) return variant;
        }
        return resolved;
    }

    public string Absolute(string path)
    {
        CheckText(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute path is required.", nameof(path));
        var full = Path.GetFullPath(path); CheckText(full); return full;
    }

    private void CheckText(string path)
    {
        if (path is null || path.Length == 0 || path.Length > _maximumCharacters || path.Contains('\0'))
            throw new ArgumentException("Unsupported or oversized path.", nameof(path));
        for (var index = 0; index < path.Length; index++)
        {
            if (char.IsHighSurrogate(path[index]))
            {
                if (index + 1 >= path.Length || !char.IsLowSurrogate(path[++index])) throw new ArgumentException("Invalid path UTF-16.", nameof(path));
            }
            else if (char.IsLowSurrogate(path[index])) throw new ArgumentException("Invalid path UTF-16.", nameof(path));
        }
    }
}
