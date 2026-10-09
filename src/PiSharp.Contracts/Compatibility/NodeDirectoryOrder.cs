using System.Text;

namespace PiSharp.Contracts.Compatibility;

/// <summary>
/// The entry order of Node's <c>fs.readdirSync</c>, which Pi's discovery walks directories in. On Unix libuv reads a directory
/// with <c>scandir(3)</c> sorted by <c>strcmp</c>, so entries come in byte order of their UTF-8 names whatever the file system
/// returns; on Windows libuv lists the directory as the file system enumerates it (NTFS: its name collation), which .NET's
/// enumeration also returns. Upstream code that sorts on its own still sorts after this.
/// </summary>
public static class NodeDirectoryOrder
{
    /// <summary>The entries in readdirSync order (see the type summary).</summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> entries) where T : FileSystemInfo =>
        OperatingSystem.IsWindows() ? entries : entries.OrderBy(entry => entry.Name, Utf8Paths.Instance);

    /// <summary>File system paths in readdirSync order of their last segment.</summary>
    public static IEnumerable<string> OrderPaths(IEnumerable<string> paths) =>
        OperatingSystem.IsWindows() ? paths : paths.Order(Utf8Paths.Instance);

    /// <summary>strcmp over UTF-8 bytes.</summary>
    public static int CompareNames(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left); var b = Encoding.UTF8.GetBytes(right);
        return a.AsSpan().SequenceCompareTo(b);
    }

    private sealed class Utf8Paths : IComparer<string>
    {
        internal static readonly Utf8Paths Instance = new();
        public int Compare(string? x, string? y) => CompareNames(Path.GetFileName(x ?? ""), Path.GetFileName(y ?? ""));
    }
}
