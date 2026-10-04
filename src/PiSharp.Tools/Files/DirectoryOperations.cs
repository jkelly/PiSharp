using System.Collections.Immutable;

namespace PiSharp.Tools.Files;

/// <summary>Directory extension to the existing trusted file seam. Every call joins owned I/O before settlement.</summary>
public interface IDirectoryFileOperations : IFileOperations
{
    ValueTask<bool> IsDirectoryAsync(string absolutePath, CancellationToken cancellationToken);
    ValueTask<ImmutableArray<string>> ReadDirectoryAsync(string absolutePath, int maximumEntries,
        int maximumNameCharacters, CancellationToken cancellationToken);
}
