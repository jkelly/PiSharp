// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/package-manager.ts (NpmSource, LocalSource,
// ParsedSource, parseSource, parseNpmSpec, isExactNpmVersion, getNpmVersionRange, PathMetadata, ResolvedResource, ResolvedPaths,
// ProgressEvent, PackageUpdate, ConfiguredPackage, PackageFilter) and packages/coding-agent/src/core/pi-manifest.ts.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Packages;

/// <summary>Source ParsedSource: an npm, git or local package source.</summary>
internal abstract record PiPackageSource
{
    /// <summary>Source parseSource: <c>npm:</c> specs, then local paths (anything <c>isLocalPath</c> accepts), then git URLs, else a
    /// local path.</summary>
    internal static PiPackageSource Parse(string source)
    {
        if (source.StartsWith("npm:", StringComparison.Ordinal))
        {
            var spec = PiArgs.JsTrim(source["npm:".Length..]);
            var (name, version) = PiNpmSource.ParseSpec(spec);
            return new PiNpmSource(spec, name, version, version is null ? null : PiSemver.ValidRange(version), PiSemver.Valid(version ?? "") is not null);
        }
        if (PiPaths.IsLocalPath(source)) return new PiLocalSource(source);
        return (PiPackageSource?)PiGitUrl.Parse(source) ?? new PiLocalSource(source);
    }
}

/// <summary>Source NpmSource.</summary>
internal sealed partial record PiNpmSource(string Spec, string Name, string? Version, string? Range, bool Pinned) : PiPackageSource
{
    [GeneratedRegex(@"^(@?[^@]+(?:/[^@]+)?)(?:@(.+))?$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SpecRegex();

    /// <summary>Source parseNpmSpec.</summary>
    internal static (string Name, string? Version) ParseSpec(string spec)
    {
        var match = SpecRegex().Match(spec);
        if (!match.Success) return (spec, null);
        return (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null);
    }
}

/// <summary>Source LocalSource.</summary>
internal sealed record PiLocalSource(string Path) : PiPackageSource;

/// <summary>Source PathMetadata. Mutable like upstream's object: a package's resources share one instance whose base directory is set
/// once the package location is known.</summary>
internal sealed class PiPathMetadata
{
    internal required string Source { get; init; }
    internal required string Scope { get; init; }
    internal required string Origin { get; init; }
    internal string? BaseDir { get; set; }
    internal string? PackageRoot { get; set; }
    internal PiPathMetadata With(string? baseDir) => new() { Source = Source, Scope = Scope, Origin = Origin, BaseDir = baseDir, PackageRoot = PackageRoot };
}

/// <summary>Source ResolvedResource.</summary>
internal sealed record PiResolvedResource(string Path, bool Enabled, PiPathMetadata Metadata);

/// <summary>Source ResolvedPaths.</summary>
internal sealed record PiResolvedPaths(ImmutableArray<PiResolvedResource> Extensions, ImmutableArray<PiResolvedResource> Skills,
    ImmutableArray<PiResolvedResource> Prompts, ImmutableArray<PiResolvedResource> Themes)
{
    internal static PiResolvedPaths Empty { get; } = new([], [], [], []);
    internal ImmutableArray<PiResolvedResource> Get(string resourceType) => resourceType switch
    {
        "extensions" => Extensions, "skills" => Skills, "prompts" => Prompts, "themes" => Themes,
        _ => throw new ArgumentOutOfRangeException(nameof(resourceType))
    };
}

/// <summary>Source MissingSourceAction.</summary>
internal enum PiMissingSourceAction { Install, Skip, Error }

/// <summary>Source ProgressEvent: type start|progress|complete|error, action install|remove|update|clone|pull.</summary>
internal sealed record PiPackageProgressEvent(string Type, string Action, string Source, string? Message = null);

/// <summary>Source PackageUpdate.</summary>
internal sealed record PiPackageUpdate(string Source, string DisplayName, string Type, string Scope);

/// <summary>Source ConfiguredPackage.</summary>
internal sealed record PiConfiguredPackage(string Source, string Scope, bool Filtered, string? InstalledPath);

/// <summary>A settings <c>packages</c> entry (source PackageSource): a string, or an object with <c>source</c>, <c>autoload</c> and
/// per-type patterns. <see cref="Node"/> keeps the stored form so rewrites preserve other keys.</summary>
internal sealed record PiPackageEntry(string Source, JsonNode Node)
{
    internal bool IsFilter => Node is JsonObject;
    internal bool? Autoload => Node is JsonObject filter && filter["autoload"] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    /// <summary>The filter's patterns for a resource type, or null when the type is not given.</summary>
    internal ImmutableArray<string>? Patterns(string resourceType) => Node is JsonObject filter && filter[resourceType] is JsonArray array
        ? [.. array.OfType<JsonValue>().Select(item => item.TryGetValue<string>(out var text) ? text : null).OfType<string>()] : null;

    internal static PiPackageEntry? From(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => new(text, value),
        JsonObject filter when filter["source"] is JsonValue source && source.TryGetValue<string>(out var text) => new(text, filter),
        _ => null
    };

    internal static List<PiPackageEntry> List(JsonObject layer) =>
        layer["packages"] is JsonArray array ? [.. array.Select(From).OfType<PiPackageEntry>()] : [];
}

/// <summary>Source PiManifest / readPiManifest: the <c>pi</c> object of a package.json; a resource field counts only when it is an
/// array of strings, and an unreadable or malformed file has no manifest.</summary>
internal sealed record PiManifest(ImmutableArray<string>? Extensions, ImmutableArray<string>? Skills, ImmutableArray<string>? Prompts, ImmutableArray<string>? Themes)
{
    internal ImmutableArray<string>? Get(string resourceType) => resourceType switch
    {
        "extensions" => Extensions, "skills" => Skills, "prompts" => Prompts, "themes" => Themes, _ => null
    };

    internal static PiManifest? Read(string packageJsonPath)
    {
        try
        {
            if (JsonNode.Parse(PiPaths.ReadText(packageJsonPath)) is not JsonObject package || package["pi"] is not JsonObject pi) return null;
            ImmutableArray<string>? Field(string name) => pi[name] is JsonArray array && array.All(item => item is JsonValue value && value.TryGetValue<string>(out _))
                ? [.. array.Select(item => item!.GetValue<string>())] : null;
            return new(Field("extensions"), Field("skills"), Field("prompts"), Field("themes"));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return null; }
    }
}

/// <summary>A package manager failure (source <c>throw new Error(message)</c>); its message is what the CLI prints.</summary>
internal sealed class PiPackageException(string message, Exception? inner = null) : Exception(message, inner);
