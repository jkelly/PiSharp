using System.Collections.Immutable;

namespace PiSharp.CodingAgent.Resources;

/// <summary>A caller-authorized, resolved selection. Provenance is not authorization.</summary>
public sealed record PromptTemplatePathSelection(string Path, PromptTemplateSourceInfo SourceInfo,
    bool ReportMissingPath = false);
public enum PromptTemplateFileKind { Missing, File, Directory, SymbolicLink, Other }
public sealed record PromptTemplateDirectoryEntry(string Name, string Path, PromptTemplateFileKind Kind);

/// <summary>Borrowed file operations. Stat follows links; directory entries retain their link identity.
/// Reads return undecoded bytes so the parser owns exactly one leading BOM removal.</summary>
public interface IPromptTemplateFileSystem
{
    PromptTemplateFileKind Stat(string resolvedPath);
    ImmutableArray<PromptTemplateDirectoryEntry> ReadDirectory(string resolvedPath);
    ValueTask<byte[]> ReadFileAsync(string resolvedPath, CancellationToken cancellationToken);
}

/// <summary>Native completion data; CLI/RPC owners map their exact wire fields separately.</summary>
public sealed record PromptTemplateCommandEntry(string Name, string Description, string? ArgumentHint,
    PromptTemplateSourceInfo SourceInfo)
{
    public string Source => "prompt";
}
