using System.Collections.Immutable;

namespace PiSharp.CodingAgent.Resources;

/// <summary>Dependency-free .NET file operations for explicitly selected prompt resources.</summary>
public sealed class SystemPromptTemplateFileSystem : IPromptTemplateFileSystem
{
    public PromptTemplateFileKind Stat(string resolvedPath)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(resolvedPath); }
        catch (FileNotFoundException) { return PromptTemplateFileKind.Missing; }
        catch (DirectoryNotFoundException) { return PromptTemplateFileKind.Missing; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = (attributes & FileAttributes.Directory) != 0
                ? Directory.ResolveLinkTarget(resolvedPath, returnFinalTarget: true)
                : File.ResolveLinkTarget(resolvedPath, returnFinalTarget: true);
            if (target is not null)
            {
                if (!target.Exists) return PromptTemplateFileKind.Missing;
                attributes = target.Attributes;
            }
        }
        return (attributes & FileAttributes.Directory) != 0 ? PromptTemplateFileKind.Directory : PromptTemplateFileKind.File;
    }

    public ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string resolvedPath)
    {
        var entries = ImmutableArray.CreateBuilder<PromptTemplateDirectoryEntry>();
        foreach (var entry in new DirectoryInfo(resolvedPath).GetFileSystemInfos())
        {
            var attributes = entry.Attributes;
            var kind = (attributes & FileAttributes.ReparsePoint) != 0 ? PromptTemplateFileKind.SymbolicLink
                : (attributes & FileAttributes.Directory) != 0 ? PromptTemplateFileKind.Directory : PromptTemplateFileKind.File;
            entries.Add(new(entry.Name, entry.FullName, kind));
        }
        return entries.ToImmutable();
    }

    public async ValueTask<byte[]> ReadFileAsync(string resolvedPath, CancellationToken cancellationToken) =>
        await File.ReadAllBytesAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
}
