// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/source-info.ts (createSourceInfo,
// createSyntheticSourceInfo) and packages/coding-agent/src/core/resource-loader.ts (applyExtensionSourceInfo).
using System.Text.Json.Nodes;

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
