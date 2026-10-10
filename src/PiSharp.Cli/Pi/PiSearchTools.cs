// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/grep.ts and core/tools/find.ts (ensureTool
// at execution: "ripgrep (rg) is not available and could not be downloaded", "fd is not available and could not be downloaded").
using PiSharp.Tools.Files;

namespace PiSharp.Cli.Pi;

/// <summary>The grep and find tools of the <c>pi</c> tool policy. Like upstream, the binary is resolved (and downloaded when missing)
/// on the first search, not at startup, and runs from wherever it was found with the process environment.</summary>
internal sealed class PiSearchTools(PiToolsManager tools, string workspace, string home, IReadOnlyDictionary<string, string> environment,
    Action<PiToolStatus>? status = null, Func<string, CancellationToken, ValueTask<bool>>? authorizeRead = null)
{
    private async ValueTask<string?> ResolveAsync(string tool, CancellationToken token)
    {
        var path = await tools.EnsureToolAsync(tool, status, token).ConfigureAwait(false);
        return path is null ? null : Path.GetFullPath(path);
    }

    internal PiGrepTool Grep(IDirectoryFileOperations files) =>
        new(workspace, home, token => ResolveAsync("rg", token), environment, files, authorizeRead);
    internal PiFindTool Find(IDirectoryFileOperations files) => new(workspace, home, token => ResolveAsync("fd", token), environment, files);
}
