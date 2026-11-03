// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/source-info.ts (createSourceInfo,
// createSyntheticSourceInfo) and packages/coding-agent/src/core/resource-loader.ts (applyExtensionSourceInfo).
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>An extension entry point the package manager resolved (or a <c>-e</c> path), with its scope (<c>user</c>, <c>project</c>
/// or <c>temporary</c>), source (<c>local</c>, <c>auto</c>, <c>cli</c>, a package source), origin and base directory.</summary>
internal sealed record PiExtensionSource(string Path, string Scope, string Source, string Origin = "top-level", string? BaseDir = null)
{
    /// <summary>Source createSourceInfo: the extension's sourceInfo (also its commands' and tools').</summary>
    internal JsonObject SourceInfo()
    {
        var info = new JsonObject { ["path"] = Path, ["source"] = Source, ["scope"] = Scope, ["origin"] = Origin };
        if (BaseDir is not null) info["baseDir"] = BaseDir;
        return info;
    }
}

/// <summary>Source source-info.ts path keys.</summary>
internal static class PiSourceInfo
{
    /// <summary>Source isSyntheticPath: <c>builtin:&lt;name&gt;</c> and <c>&lt;inline:...&gt;</c> identifiers, not filesystem paths.</summary>
    internal static bool IsSyntheticPath(string path) => path.StartsWith("builtin:", StringComparison.Ordinal) || path.StartsWith('<');

    /// <summary>Source getResourcePathKey: the key that deduplicates resource paths; synthetic paths as they are (their lookup is
    /// case-sensitive), file paths through realpath and the platform's casing (<see cref="PiPaths.ComparisonKey"/>).</summary>
    internal static string ResourcePathKey(string path) => IsSyntheticPath(path) ? path : PiPaths.ComparisonKey(PiPaths.Canonicalize(path));
}
