using System.Collections.Immutable;

namespace PiSharp.CodingAgent.Resources.Skills;

/// <summary>Bounded local inert reads. Refuses links in the selected path and every existing ancestor.</summary>
public sealed class SystemSkillResourceFileSystem : ISkillResourceFileSystem
{
    private static void CheckPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Explicit absolute skill path required.");
        if (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Network/device skill path denied.");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Skill path link denied."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public PromptTemplateFileKind Stat(string path)
    {
        CheckPath(path);
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? PromptTemplateFileKind.Directory : PromptTemplateFileKind.File; }
        catch (FileNotFoundException) { return PromptTemplateFileKind.Missing; }
        catch (DirectoryNotFoundException) { return PromptTemplateFileKind.Missing; }
    }
    public ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string path, int maximumEntries)
    {
        CheckPath(path); if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        var result = ImmutableArray.CreateBuilder<PromptTemplateDirectoryEntry>();
        foreach (var entry in PiSharp.Contracts.Compatibility.NodeDirectoryOrder.Order(new DirectoryInfo(path).EnumerateFileSystemInfos()))
        {
            if (result.Count == maximumEntries) throw new IOException("Skill directory entry limit.");
            var attributes = entry.Attributes;
            result.Add(new(entry.Name, entry.FullName, (attributes & FileAttributes.ReparsePoint) != 0 ? PromptTemplateFileKind.SymbolicLink :
                (attributes & FileAttributes.Directory) != 0 ? PromptTemplateFileKind.Directory : PromptTemplateFileKind.File));
        }
        return result.ToImmutable();
    }
    public async ValueTask<byte[]> ReadFileAsync(string path, int maximumBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CheckPath(path);
        if (maximumBytes is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var buffer = new byte[maximumBytes + 1]; int count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false); if (read == 0) break; count += read;
        }
        if (count > maximumBytes) throw new IOException("Skill file byte limit.");
        return buffer[..count];
    }
}
