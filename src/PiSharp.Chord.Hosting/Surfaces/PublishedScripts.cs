// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/surfaces/published.ts, packages/chord/src/node/surfaces/served.ts.
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Surfaces;

namespace PiSharp.Chord.Hosting.Surfaces;

/// <summary>An owned immutable copy of a published file. Readers cannot mutate its cached bytes.</summary>
public sealed class SurfaceFile
{
    private readonly byte[] bytes;
    public string Mime { get; }
    public int Length => bytes.Length;
    public string Hash { get; }
    public SurfaceFile(ReadOnlySpan<byte> bytes, string mime)
    { this.bytes = bytes.ToArray(); Mime = mime; Hash = SurfacePaths.Hash(bytes); }
    public byte[] ToArray() => bytes.ToArray();
    public HttpResult Response() => new HttpResult.Bytes((offset, length, _) =>
        Task.FromResult<ReadOnlyMemory<byte>>(bytes.AsSpan(checked((int)offset), length).ToArray()), bytes.Length, Mime)
    {
        Headers = new Dictionary<string, string[]>
        {
            ["Cache-Control"] = ["private, max-age=31536000, immutable"],
            ["X-Content-Type-Options"] = ["nosniff"],
        },
    };
}

public sealed record SurfacePublication(ScriptSet Set, IReadOnlyList<SurfacePluginFailure> Failed);

/// <summary>Publishes complete plugin sets atomically and retains the current set and three predecessors.</summary>
public sealed partial class PublishedScripts
{
    private sealed record PluginFiles(ScriptEntry Entry, string Script, IReadOnlyDictionary<string, SurfaceFile> Files);
    private readonly object gate = new();
    private readonly string prefix;
    private readonly string baseScript;
    private Dictionary<string, PluginFiles> current = new(StringComparer.Ordinal);
    private readonly Queue<IReadOnlyDictionary<string, SurfaceFile>> history = new();
    private ScriptSet set;
    public PublishedScripts(string prefix, string baseScript)
    { this.prefix = SurfacePaths.Prefix(prefix); this.baseScript = baseScript; set = new(baseScript, "", []); }
    public ScriptSet Set { get { lock (gate) return set; } }
    public int RetainedSets { get { lock (gate) return history.Count; } }
    public SurfaceFile? File(string path)
    {
        lock (gate)
            foreach (var files in history.Reverse()) if (files.TryGetValue(path, out var file)) return file;
        return null;
    }
    public SurfacePublication Publish(IEnumerable<SurfacePluginBuild> plugins)
    {
        lock (gate)
        {
            var next = new Dictionary<string, PluginFiles>(StringComparer.Ordinal);
            var failed = new List<SurfacePluginFailure>();
            foreach (var plugin in plugins)
            {
                if (!PluginScanner.IsPluginName(plugin.Name) || next.ContainsKey(plugin.Name) || failed.Any(x => x.Plugin == plugin.Name))
                    throw new ArgumentException("invalid or duplicate surface plugin name", nameof(plugins));
                if ((plugin.Artifact is null) == (plugin.Error is null))
                    throw new ArgumentException("surface plugin must contain exactly one artifact or error", nameof(plugins));
                if (plugin.Artifact is { } artifact) next.Add(plugin.Name, MakePlugin(plugin.Name, artifact));
                else
                {
                    failed.Add(new(plugin.Name, plugin.Error!));
                    if (current.TryGetValue(plugin.Name, out var good)) next.Add(plugin.Name, good);
                }
            }
            var files = new Dictionary<string, SurfaceFile>(StringComparer.Ordinal);
            var ordered = next.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray();
            foreach (var plugin in ordered) foreach (var file in plugin.Files) AddFile(files, file.Key, file.Value);
            var combined = string.Concat(ordered.Select(plugin => SourceMap().Replace(plugin.Script, "")));
            var combinedPath = $"{prefix}plugins.{SurfacePaths.Hash(combined)}.js";
            AddFile(files, combinedPath, new(Encoding.UTF8.GetBytes(combined), SurfacePaths.Mime(".js")));
            var published = new ScriptSet(baseScript, combinedPath, Array.AsReadOnly(ordered.Select(plugin => plugin.Entry).ToArray()));
            // Validate against every retained generation before committing. Paths never change content.
            foreach (var previous in history) foreach (var file in files)
                if (previous.TryGetValue(file.Key, out var old) && (old.Hash != file.Value.Hash || old.Mime != file.Value.Mime))
                    throw new InvalidDataException("surface path changed content");
            current = next;
            history.Enqueue(new ReadOnlyDictionary<string, SurfaceFile>(files));
            if (history.Count > 4) history.Dequeue();
            set = published;
            return new(published, failed.AsReadOnly());
        }
    }
    private PluginFiles MakePlugin(string name, SurfaceBuildArtifact artifact)
    {
        var digest = SurfacePaths.Hash(artifact.JavaScript);
        var script = $"chordFacet({JsonSerializer.Serialize(name)},{JsonSerializer.Serialize(digest)},{artifact.JavaScript}\n);\n";
        // Hash the served bytes as well as exposing the factory digest expected by the client registry.
        var stem = $"{prefix}plugin/{name.Replace('/', '~')}.{SurfacePaths.Hash(script)}";
        var files = new Dictionary<string, SurfaceFile>(StringComparer.Ordinal);
        AddFile(files, stem + ".js", new(Encoding.UTF8.GetBytes(script), SurfacePaths.Mime(".js")));
        string? css = null;
        if (artifact.Css is { } style)
        {
            css = $"{prefix}plugin/{name.Replace('/', '~')}.{SurfacePaths.Hash(style)}.css";
            AddFile(files, css, new(Encoding.UTF8.GetBytes(style), SurfacePaths.Mime(".css")));
        }
        foreach (var asset in artifact.Assets ?? [])
            AddFile(files, SurfacePaths.Asset(prefix, asset.Name, asset.Bytes.Span), new(asset.Bytes.Span, asset.Mime ?? SurfacePaths.Mime(asset.Name)));
        return new(new(name, digest, stem + ".js", css), script, new ReadOnlyDictionary<string, SurfaceFile>(files));
    }
    internal static void AddFile(IDictionary<string, SurfaceFile> files, string path, SurfaceFile file)
    {
        if (files.TryGetValue(path, out var existing) && (existing.Hash != file.Hash || existing.Mime != file.Mime))
            throw new InvalidDataException("conflicting surface assets");
        files[path] = file;
    }
    [GeneratedRegex(@"\n//# sourceMappingURL=data:[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex SourceMap();
}
