// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/plugins/base-inputs.ts.
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace PiSharp.Chord.Hosting.Plugins;

/// <summary>Immutable content identities for the bytes read by a build, plus optional directory membership.</summary>
public sealed class BaseInputs
{
    internal static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public IReadOnlyDictionary<string, string?> Files { get; }
    public IReadOnlyList<string> Directories { get; }
    public int MaxFiles { get; }
    [System.Text.Json.Serialization.JsonConstructor]
    public BaseInputs(IReadOnlyDictionary<string, string?> files, IReadOnlyList<string> directories, int maxFiles)
    {
        if (maxFiles < 1 || files.Count > maxFiles) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        Files = new ReadOnlyDictionary<string, string?>(files.ToDictionary(pair => Path.GetFullPath(pair.Key), pair => pair.Value, Paths));
        Directories = Array.AsReadOnly(directories.Select(Path.GetFullPath).ToArray()); MaxFiles = maxFiles;
    }
    private BaseInputs(Dictionary<string, string?> files, string[] directories, int maxFiles)
    { Files = new ReadOnlyDictionary<string, string?>(files); Directories = Array.AsReadOnly(directories); MaxFiles = maxFiles; }
    public static string ContentHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static BaseInputs FromHashes(IEnumerable<KeyValuePair<string, string?>> files, IEnumerable<string>? directories = null, int maxFiles = 10000)
    {
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        var hashes = new Dictionary<string, string?>(Paths);
        foreach (var file in files) { hashes.Add(Path.GetFullPath(file.Key), file.Value); if (hashes.Count > maxFiles) throw new InvalidDataException("base input scan limit exceeded"); }
        return new(hashes, (directories ?? []).Select(Path.GetFullPath).Distinct(Paths).ToArray(), maxFiles);
    }
    public static BaseInputs Capture(IEnumerable<string> files, IEnumerable<string>? directories = null, int maxFiles = 10000)
    {
        var roots = (directories ?? []).Select(Path.GetFullPath).Distinct(Paths).ToArray();
        var all = files.Select(Path.GetFullPath).Concat(roots.SelectMany(root => Enumerate(root, maxFiles))).Distinct(Paths).ToArray();
        if (all.Length > maxFiles) throw new InvalidDataException("base input scan limit exceeded");
        return FromHashes(all.Select(file => new KeyValuePair<string, string?>(file, HashFile(file))), roots, maxFiles);
    }
    public bool HasChanged()
    {
        try
        {
            foreach (var file in Files) if (!string.Equals(file.Value, HashFile(file.Key), StringComparison.Ordinal)) return true;
            foreach (var directory in Directories)
                foreach (var file in Enumerate(directory, MaxFiles)) if (!Files.ContainsKey(file)) return true;
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return true; }
    }
    internal static string? HashFile(string path) => File.Exists(path) ? ContentHash(File.ReadAllBytes(path)) : null;
    internal static bool Excluded(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part is "bin" or "obj" or "node_modules" or ".git" or ".svn" or ".hg");
    internal static IEnumerable<string> Enumerate(string root, int maxFiles)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (Excluded(Path.GetRelativePath(root, entry))) continue;
                if (++count > maxFiles) throw new InvalidDataException("base input scan limit exceeded");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"base input link is unsupported: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else { yield return Path.GetFullPath(entry); }
            }
        }
    }
}
