// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/files-entry.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Hosting.App;

public sealed record FilesCaps(long MaxFile = 256L * 1024 * 1024, long MaxTotal = 4L * 1024 * 1024 * 1024);
public sealed record FileWrite(string Space, string Path, string Mime, string Source, string? Ref = null, string? Key = null,
    JsonNode? Meta = null, long? Created = null);
public sealed record FileInfo(string Id, string Space, string Path, long Version, long Size, string Mime, string Source,
    long Created, string? Ref = null, string? Key = null, JsonNode? Meta = null);
public sealed record FolderInfo(string Space, string Path, long Created);
public sealed record FolderEntry(string Kind, FolderInfo? Folder = null, FileInfo? File = null);
public sealed record FilesPage<T>(IReadOnlyList<T> Items, string? Next = null);
public sealed record SpaceSummary(long Files, long Versions, long Bytes, IReadOnlyList<FileInfo> Newest);
public sealed record FolderListing(IReadOnlyList<FolderEntry> Entries, bool More);
public sealed record BeginUpload(string Kind, string? Upload = null, FileInfo? File = null);
public sealed record FilesBlob(string Id, string Path, long Size);

internal static class FilesValues
{
    internal const int MaxPiece = 4 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    internal static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json);
    internal static T Decode<T>(JsonNode? node) => node.Deserialize<T>(Json) ?? throw Invalid("Missing value");
    internal static string Token() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
    internal static ChordError Invalid(string message, JsonNode? details = null) => new("invalid", "files: " + message, "args", details);
    internal static ChordError TooLarge(string message) => new("too_large", "files: " + message);
    internal static void Space(string space)
    {
        if (space is null || !Regex.IsMatch(space, "\\A[a-z0-9][a-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant))
            throw Invalid("Invalid space name");
    }
    internal static void Path(string path, bool root = false)
    {
        if (root && path == "") return;
        if (path is null || path.Length > 1024 || path.Contains('\0') ||
            path.Split('/').Any(s => s.Length == 0 || s is "." or "..")) throw Invalid("Invalid path");
    }
    internal static void Write(FileWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        Space(write.Space); Path(write.Path);
        if (write.Mime is null || write.Source is null || write.Key == "" || write.Ref == "" || write.Created < 0)
            throw Invalid("Invalid file metadata");
        if (write.Created > 9_007_199_254_740_991L || !ChordJson.IsJsonValue(write.Meta)) throw Invalid("File metadata must be JSON data");
    }
    internal static string Parent(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";
    internal static bool Under(string path, string parent) => path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);
    internal static void Conflict(string space, string path) => throw Invalid($"{space}:{path} conflicts with an existing entry", new JsonObject { ["conflict"] = path });
    internal static FileInfo Clone(FileInfo file) => file with { Meta = file.Meta?.DeepClone() };
    internal static FileWrite Clone(FileWrite write) => write with { Meta = write.Meta?.DeepClone() };
}
