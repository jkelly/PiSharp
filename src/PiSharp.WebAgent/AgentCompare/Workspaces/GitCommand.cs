// Decision 0011 (run W4, unit R4): runs git for the workspace manager. Always with an argument list (ProcessStartInfo.ArgumentList), never
// a shell string, so a path with spaces, quotes, '&' or '$' is one argument and nothing is interpreted. Input (a patch, a commit message,
// a path list) goes through stdin. Every command gets core.quotepath=false (UTF-8 paths) and, on Windows, core.longpaths=true; the
// inherited GIT_DIR / GIT_WORK_TREE / GIT_INDEX_FILE family is cleared so the user's shell cannot redirect a command, prompts are off
// (GIT_TERMINAL_PROMPT=0) and optional locks are skipped (GIT_OPTIONAL_LOCKS=0, so `git status` never takes the user's index lock).
// Looking at a folder never runs a command its repository names (W4 fix, agent-ui): every command also gets core.fsmonitor=false (`git
// status` would run a repo-local fsmonitor command) and core.hooksPath pointed at a folder this app never creates (NoHooksFolder under the
// workspaces root), so none of the project's hooks runs: not post-checkout on `worktree add`, not reference-transaction on Keep the
// branch's `update-ref`. The user's hooks still run when they commit themselves. An inspection's `git status` also switches off the filter
// drivers the repository's own config defines (WorkspaceManager.NeutralFiltersAsync: a clean filter runs during `git status`).
// No secret ever reaches a git command line: the arguments are paths, commit ids and fixed options.
// Command-line config wins over every config file (W4 fix, agent-runner). A worktree slot's commands also name its repository explicitly
// (GIT_DIR, GIT_WORK_TREE and GIT_INDEX_FILE, WorkspaceManager.Diff.cs), so the slot's own .git file is never read.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>The result of one git command.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">Standard output, as bytes (diffs and paths are not always UTF-8).</param>
/// <param name="Error">Standard error.</param>
internal sealed record GitResult(int ExitCode, byte[] Output, string Error)
{
    /// <summary>Standard output as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Output);

    /// <summary>Standard output's first line, trimmed.</summary>
    public string Line => Text.Split('\n', 2)[0].Trim();

    /// <summary>Exit code 0.</summary>
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs git commands.</summary>
internal sealed class GitCommand(WorkspaceManagerOptions options)
{
    // Variables that would point a command at another repository, index or object store.
    private static readonly string[] Cleared =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_PREFIX", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_LITERAL_PATHSPECS", "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS",
        "GIT_ICASE_PATHSPECS", "GIT_EXTERNAL_DIFF", "GIT_DIFF_OPTS",
    ];

    /// <summary>The hooks folder every command names: under the workspaces root, never created, so git finds no hook to run.</summary>
    public const string NoHooksFolder = ".no-hooks";

    /// <summary>The options every command starts with: UTF-8 paths, long paths on Windows, no fsmonitor and no hooks.</summary>
    public IReadOnlyList<string> GlobalOptions()
    {
        List<string> list = ["-c", "core.quotepath=false"];
        if (OperatingSystem.IsWindows()) list.AddRange(["-c", "core.longpaths=true"]);
        list.AddRange(["-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + Path.Join(Path.GetFullPath(options.Root), NoHooksFolder)]);
        return list;
    }

    /// <summary>Runs <c>git</c> with <paramref name="arguments"/> in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">Where git runs.</param>
    /// <param name="arguments">The arguments after the global options.</param>
    /// <param name="input">Bytes for standard input, or null.</param>
    /// <param name="environment">Extra variables for this command (null removes one).</param>
    /// <param name="timeout">The time limit; default <see cref="WorkspaceManagerOptions.GitTimeout"/>.</param>
    /// <param name="cancellationToken">Stops (and kills) the command.</param>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.GitMissing"/> or <see cref="WorkspaceException.GitTimeout"/>.</exception>
    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, byte[]? input = null,
        IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(options.GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var option in GlobalOptions()) info.ArgumentList.Add(option);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var name in Cleared) info.Environment.Remove(name);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["LC_ALL"] = "C";
        foreach (var (name, value) in options.GitEnvironment) Set(info, name, value);
        if (environment is not null)
            foreach (var (name, value) in environment) Set(info, name, value);

        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception error)
        {
            throw new WorkspaceException(WorkspaceException.GitMissing, "Git isn't installed, or isn't on PATH, so workspaces can't be made or compared.", error);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? options.GitTimeout);
        var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output, limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            if (input is { Length: > 0 })
            {
                try { await process.StandardInput.BaseStream.WriteAsync(input, limit.Token).ConfigureAwait(false); }
                catch (IOException) { } // the command exited before reading everything; its exit code says why
            }
            try { process.StandardInput.Close(); } catch (IOException) { }
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await reading.ConfigureAwait(false);
            var error = await errors.ConfigureAwait(false);
            return new GitResult(process.ExitCode, output.ToArray(), error);
        }
        catch (OperationCanceledException) when (!process.HasExited || limit.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new WorkspaceException(WorkspaceException.GitTimeout, $"git {Describe(arguments)} took longer than {(timeout ?? options.GitTimeout).TotalSeconds:0} s and was stopped.");
        }
    }

    /// <summary>Runs a command that must succeed.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.GitFailed"/> with git's message.</exception>
    public async Task<GitResult> RequireAsync(string workingDirectory, IReadOnlyList<string> arguments, byte[]? input = null,
        IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingDirectory, arguments, input, environment, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok) throw Failed(arguments, result);
        return result;
    }

    /// <summary>A <see cref="WorkspaceException.GitFailed"/> naming the command (never its paths) and git's first error line.</summary>
    public static WorkspaceException Failed(IReadOnlyList<string> arguments, GitResult result) =>
        new(WorkspaceException.GitFailed, $"git {Describe(arguments)} failed (exit {result.ExitCode})" +
            (FirstLine(result.Error) is { Length: > 0 } line ? ": " + line : "."));

    /// <summary>The first non-empty line of git's error output that is not a warning.</summary>
    public static string FirstLine(string error) =>
        error.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !line.StartsWith("warning:", StringComparison.Ordinal)) ?? "";

    // The subcommand: the first argument that is neither an option nor the value of a "-c" option.
    private static string Describe(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == "-c") index++;
            else if (!arguments[index].StartsWith('-')) return arguments[index];
        }
        return "";
    }

    private static void Set(ProcessStartInfo info, string name, string? value)
    {
        if (value is null) info.Environment.Remove(name);
        else info.Environment[name] = value;
    }
}
