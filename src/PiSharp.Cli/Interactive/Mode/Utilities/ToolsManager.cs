// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/tools-manager.ts (getToolPath, ensureTool: the agent's bin
// directory first, then the system PATH; offline mode skips the download with a warning). Downloading fd/rg is the CLI's tools
// package (IMPL-F follow-up); without a downloader the missing-tool warning is reported and autocomplete works without fd.
namespace PiSharp.Cli.Interactive.Mode.Utilities;

internal static class ToolsManager
{
    private sealed record ToolConfig(string Name, string BinaryName, string[] SystemBinaryNames);

    private static readonly Dictionary<string, ToolConfig> Tools = new(StringComparer.Ordinal)
    {
        ["fd"] = new("fd", "fd", ["fd", "fdfind"]),
        ["rg"] = new("ripgrep", "rg", ["rg"])
    };

    /// <summary>getToolPath: the agent bin directory's binary, else a system command name on PATH, else null.</summary>
    public static string? GetToolPath(string tool, string agentDir, Func<string, string?> env)
    {
        if (!Tools.TryGetValue(tool, out var config)) return null;
        var localPath = Path.Join(agentDir, "bin", config.BinaryName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (File.Exists(localPath)) return localPath;
        foreach (var name in config.SystemBinaryNames)
            if (CommandExists(name, env)) return name;
        return null;
    }

    private static bool CommandExists(string command, Func<string, string?> env)
    {
        var path = env("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows() ? (env("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries) : [""];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                try { if (File.Exists(Path.Join(directory.Trim('"'), command + extension))) return true; }
                catch (ArgumentException) { }
            }
        return false;
    }

    /// <summary>ensureTool: the tool's path, or null after reporting why it is missing.</summary>
    public static Task<string?> EnsureTool(string tool, string agentDir, Func<string, string?> env, Action<string, string>? onStatus,
        Func<string, Task<string?>>? download = null)
    {
        var existing = GetToolPath(tool, agentDir, env);
        if (existing is not null) return Task.FromResult<string?>(existing);
        if (!Tools.TryGetValue(tool, out var config)) return Task.FromResult<string?>(null);
        if (!string.IsNullOrEmpty(env("PI_OFFLINE")))
        {
            onStatus?.Invoke("warning", $"{config.Name} not found. Offline mode enabled, skipping download.");
            return Task.FromResult<string?>(null);
        }
        if (download is null)
        {
            onStatus?.Invoke("warning", $"{config.Name} not found. Install it and make sure it is on PATH.");
            return Task.FromResult<string?>(null);
        }
        return DownloadAsync();
        async Task<string?> DownloadAsync()
        {
            onStatus?.Invoke("info", $"{config.Name} not found. Downloading...");
            try
            {
                var path = await download(tool).ConfigureAwait(false);
                if (path is not null) onStatus?.Invoke("info", $"{config.Name} installed to {path}");
                return path;
            }
            catch (Exception error)
            {
                onStatus?.Invoke("warning", $"Failed to download {config.Name}: {error.Message}");
                return null;
            }
        }
    }
}
