// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/main.ts (createSessionManager, resolveSessionPath,
// findLocalSessionByExactId, promptConfirm, openSessionOrExit, forkSessionOrExit) and packages/coding-agent/src/core/session-manager.ts
// (SessionManager.open/_setSessionFile, create, continueRecent, inMemory).
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Pi;

/// <summary>The session a run uses. <see cref="Mode"/> is the session host's storage mode: <c>open</c> (an existing file),
/// <c>new-lazy</c> (a new file written once the first assistant message exists) or <c>new-memory</c> (<c>--no-session</c>).</summary>
internal sealed record PiSessionPlan(string Mode, string SessionPath, string Cwd, string? SessionDirectory, string? HeaderId,
    string? HeaderTimestamp, bool HasMessages)
{
    /// <summary>The session file for the missing-cwd check (source getSessionFile), null for an in-memory session.</summary>
    internal string? SessionFile => Mode == "new-memory" ? null : SessionPath;
}

internal static class PiSessionPlanner
{
    private abstract record Resolved;
    private sealed record ByPath(string Path) : Resolved;
    private sealed record Local(string Path) : Resolved;
    private sealed record Global(string Path, string Cwd) : Resolved;
    private sealed record NotFound(string Arg) : Resolved;

    /// <summary>Source resolveSessionPath: a path-like argument is a file; otherwise an exact id, then an id prefix in the cwd's
    /// sessions, then across all projects.</summary>
    private static Resolved ResolveSessionPath(string arg, string cwd, string? sessionDir, string agentDir, string home)
    {
        if (arg.Contains('/') || arg.Contains('\\') || arg.EndsWith(".jsonl", StringComparison.Ordinal)) return new ByPath(PiPaths.ResolvePath(arg, cwd, home));
        if (PiSessions.FindById(cwd, arg, sessionDir, agentDir) is { } exact) return new Local(exact);
        if (PiSessions.List(cwd, sessionDir, agentDir).FirstOrDefault(session => session.Id.StartsWith(arg, StringComparison.Ordinal)) is { } local) return new Local(local.Path);
        var all = PiSessions.ListAll(sessionDir, agentDir);
        var global = all.FirstOrDefault(session => session.Id == arg) ?? all.FirstOrDefault(session => session.Id.StartsWith(arg, StringComparison.Ordinal));
        return global is not null ? new Global(global.Path, global.Cwd) : new NotFound(arg);
    }

    /// <summary>Source createSessionManager.</summary>
    internal static async Task<PiSessionPlan> PlanAsync(PiArgs parsed, string cwd, string? sessionDir, string agentDir, string home, PiAppMode mode,
        PiHost host, CancellationToken token)
    {
        if (parsed.NoSession)
        {
            var (memoryPath, memoryId, memoryTime) = PiSessions.NewSessionFile(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), parsed.SessionId, host.Now());
            return new("new-memory", memoryPath, cwd, null, memoryId, memoryTime, false);
        }
        if (parsed.Fork is { } fork)
        {
            if (parsed.SessionId is { } targetId && PiSessions.FindById(cwd, targetId, sessionDir, agentDir) is not null)
                throw PiCommand.Fail($"Session already exists with id '{targetId}'");
            return ResolveSessionPath(fork, cwd, sessionDir, agentDir, home) switch
            {
                ByPath path => Open(Fork(path.Path), sessionDir, cwd, host),
                Local local => Open(Fork(local.Path), sessionDir, cwd, host),
                Global global => Open(Fork(global.Path), sessionDir, cwd, host),
                NotFound missing => throw PiCommand.Fail($"No session found matching '{missing.Arg}'"),
                _ => throw new InvalidOperationException()
            };
            string Fork(string source)
            {
                try { return PiSessions.ForkFrom(source, cwd, sessionDir, agentDir, parsed.SessionId, host.Now()); }
                catch (Exception error) when (error is PiSessionException or IOException or UnauthorizedAccessException) { throw PiCommand.Fail($"Error: {error.Message}"); }
            }
        }
        if (parsed.Session is { } sessionArg)
        {
            switch (ResolveSessionPath(sessionArg, cwd, sessionDir, agentDir, home))
            {
                case ByPath path: return Open(path.Path, sessionDir, cwd, host);
                case Local local: return Open(local.Path, sessionDir, cwd, host);
                case Global global:
                {
                    await PiCommand.Line(host.Stdout, host.Color ? PiCommand.Yellow + $"Session found in different project: {global.Cwd}\u001b[39m" : $"Session found in different project: {global.Cwd}").ConfigureAwait(false);
                    if (!await ConfirmAsync("Fork this session into current directory?").ConfigureAwait(false))
                    {
                        await PiCommand.Line(host.Stdout, host.Color ? PiCommand.Dim + "Aborted.\u001b[22m" : "Aborted.").ConfigureAwait(false);
                        throw new PiExit(0);
                    }
                    try { return Open(PiSessions.ForkFrom(global.Path, cwd, sessionDir, agentDir, null, host.Now()), sessionDir, cwd, host); }
                    catch (Exception error) when (error is PiSessionException or IOException or UnauthorizedAccessException) { throw PiCommand.Fail($"Error: {error.Message}"); }
                }
                case NotFound missing: throw PiCommand.Fail($"No session found matching '{missing.Arg}'");
            }
        }
        if (parsed.Resume)
        {
            var current = PiSessions.List(cwd, sessionDir, agentDir);
            var selector = host.SelectSession ?? DefaultSelector(host);
            var selected = await selector(current, () => PiSessions.ListAll(sessionDir, agentDir), token).ConfigureAwait(false);
            if (selected is null)
            {
                await PiCommand.Line(host.Stdout, host.Color ? PiCommand.Dim + "No session selected\u001b[22m" : "No session selected").ConfigureAwait(false);
                throw new PiExit(0);
            }
            return Open(selected, sessionDir, cwd, host);
        }
        if (parsed.Continue)
            return PiSessions.ContinueRecent(cwd, sessionDir, agentDir) is { } recent ? Open(recent, sessionDir, cwd, host) : Create(null);
        if (parsed.SessionId is { } sessionId)
        {
            if (PiSessions.FindById(cwd, sessionId, sessionDir, agentDir) is { } existing) return Open(existing, sessionDir, cwd, host);
            await PiCommand.Line(host.Stderr, host.Color
                ? PiCommand.Yellow + $"Warning: No project session found with id '{sessionId}'; creating a new session with that id.\u001b[39m"
                : $"Warning: No project session found with id '{sessionId}'; creating a new session with that id.").ConfigureAwait(false);
            return Create(sessionId);
        }
        return Create(null);

        PiSessionPlan Create(string? id)
        {
            var directory = sessionDir ?? PiSessions.DefaultSessionDirectory(cwd, agentDir);
            Directory.CreateDirectory(directory);
            var (path, headerId, timestamp) = PiSessions.NewSessionFile(directory, id, host.Now());
            return new("new-lazy", path, Path.GetFullPath(cwd), directory, headerId, timestamp, false);
        }
        async Task<bool> ConfirmAsync(string message)
        {
            if (host.Confirm is not null) return await host.Confirm(message, token).ConfigureAwait(false);
            await host.Stdout.WriteAsync($"{message} [y/N] ".AsMemory(), token).ConfigureAwait(false);
            await host.Stdout.FlushAsync(token).ConfigureAwait(false);
            var answer = (await host.Stdin.ReadLineAsync(token).ConfigureAwait(false) ?? "").ToLowerInvariant();
            return answer is "y" or "yes";
        }
    }

    /// <summary>Source SessionManager.open/_setSessionFile: a missing file becomes a new session at that path, an empty file gets a
    /// header, a non-empty file that is not a session fails. The session runs in its header's cwd.</summary>
    private static PiSessionPlan Open(string path, string? sessionDir, string cwd, PiHost host)
    {
        var resolved = Path.GetFullPath(path);
        var directory = sessionDir ?? Path.GetDirectoryName(resolved)!;
        if (!File.Exists(resolved))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            var (_, id, timestamp) = PiSessions.NewSessionFile(Path.GetDirectoryName(resolved)!, null, host.Now());
            return new("new-lazy", resolved, Path.GetFullPath(cwd), Path.GetDirectoryName(resolved), id, timestamp, false);
        }
        var header = PiSessions.ReadHeader(resolved);
        if (header is null)
        {
            if (new FileInfo(resolved).Length > 0) throw PiCommand.Fail($"Error: Session file is not a valid {PiConfig.AppName} session: {resolved}");
            var (_, id, timestamp) = PiSessions.NewSessionFile(Path.GetDirectoryName(resolved)!, null, host.Now());
            header = new JsonObject { ["type"] = "session", ["version"] = PiSessions.CurrentSessionVersion, ["id"] = id, ["timestamp"] = timestamp, ["cwd"] = Path.GetFullPath(cwd) };
            File.WriteAllText(resolved, PiJson.Stringify(header) + "\n", new System.Text.UTF8Encoding(false));
        }
        var headerCwd = PiSessions.Text(header, "cwd");
        var sessionCwd = string.IsNullOrEmpty(headerCwd) ? Path.GetFullPath(cwd) : headerCwd;
        return new("open", resolved, sessionCwd, directory, null, null, HasMessages(resolved));
    }

    private static bool HasMessages(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
                if (line.Contains("\"message\"", StringComparison.Ordinal) && JsonNode.Parse(line) is JsonObject entry && PiSessions.Text(entry, "type") == "message") return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return false;
    }

    /// <summary>The non-interactive <c>--resume</c> choice until IMPL-I's picker: on a terminal, a numbered list of the cwd's sessions
    /// on standard error and a number read from standard input; otherwise none.</summary>
    private static PiSessionSelector DefaultSelector(PiHost host) => async (current, _, token) =>
    {
        if (!host.StdinIsTty || current.IsEmpty) return null;
        for (var index = 0; index < current.Length; index++)
        {
            var session = current[index];
            var title = session.Name ?? session.FirstMessage;
            if (title.Length > 80) title = title[..77] + "...";
            await PiCommand.Line(host.Stderr, $"{index + 1,3}. {title} ({session.MessageCount} messages, {session.Modified.LocalDateTime:yyyy-MM-dd HH:mm})").ConfigureAwait(false);
        }
        await host.Stderr.WriteAsync("Select a session (number, empty to cancel): ".AsMemory(), token).ConfigureAwait(false);
        await host.Stderr.FlushAsync(token).ConfigureAwait(false);
        var answer = await host.Stdin.ReadLineAsync(token).ConfigureAwait(false);
        return int.TryParse(answer, out var number) && number >= 1 && number <= current.Length ? current[number - 1].Path : null;
    };
}
