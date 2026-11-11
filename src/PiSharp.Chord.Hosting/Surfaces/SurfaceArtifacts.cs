// PiSharp-only: Explicit native compiler/artifact adapter boundary; see sync-1.5.2-chord-publication-seam.md.
using System.Security.Cryptography;
using System.Text;
using PiSharp.Chord.Hosting.Plugins;

namespace PiSharp.Chord.Hosting.Surfaces;

/// <summary>An explicit compiler adapter. Native plugin assemblies are never treated as browser scripts.</summary>
public interface ISurfaceBuilder
{
    Task<SurfaceBuildArtifact> BuildAsync(SurfaceBuildRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Plugin">Null for a base entry; otherwise JavaScript must be a CommonJS factory function expression.</param>
public sealed record SurfaceBuildRequest(string Kind, string Entry, string Prefix, string? Plugin = null);

/// <summary>The input identities must describe the actual bytes read by the compiler, not a later directory snapshot.</summary>
public sealed record SurfaceBuildArtifact(string JavaScript, BaseInputs Inputs, string? Css = null,
    IReadOnlyList<SurfaceAsset>? Assets = null);

/// <summary>JavaScript and CSS refer to this asset through <see cref="SurfacePaths.Asset"/>.</summary>
public sealed record SurfaceAsset(string Name, ReadOnlyMemory<byte> Bytes, string? Mime = null);

/// <summary>A complete plugin result. A failed result retains the previously published artifact, if any.</summary>
public sealed record SurfacePluginBuild(string Name, SurfaceBuildArtifact? Artifact = null, Exception? Error = null);
public sealed record SurfacePluginFailure(string Plugin, Exception Error);

public static class SurfacePaths
{
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    internal static string ArtifactHash(SurfaceBuildArtifact artifact)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write(artifact.JavaScript); writer.Write(artifact.Css is not null);
        if (artifact.Css is { } css) writer.Write(css);
        foreach (var asset in (artifact.Assets ?? []).OrderBy(asset => asset.Name, StringComparer.Ordinal))
        { writer.Write(asset.Name); writer.Write(asset.Bytes.Length); writer.Write(asset.Bytes.Span); }
        writer.Flush();
        return Hash(bytes.ToArray());
    }
    public static string Prefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!prefix.StartsWith('/') || prefix.StartsWith("//", StringComparison.Ordinal) ||
            prefix.Split('/').Any(part => part is "." or "..") ||
            prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')))
            throw new ArgumentException("surface prefix must be a local URL path", nameof(prefix));
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }
    public static string Asset(string prefix, string name, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("asset name must be a simple filename", nameof(name));
        return $"{Prefix(prefix)}assets/{Hash(bytes)}/{name}";
    }
    internal static string Mime(string name) => (name.StartsWith('.') ? name : Path.GetExtension(name)).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif", ".webp" => "image/webp", ".woff" => "font/woff",
        ".woff2" => "font/woff2", ".ttf" => "font/ttf", ".wasm" => "application/wasm",
        ".json" => "application/json", _ => "application/octet-stream",
    };
}
