// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/main.ts (the interactive branch: InteractiveMode construction
// with InteractiveModeOptions, run, and the resume hint printed after an interactive quit) and coding-agent/src/modes/interactive/
// interactive-mode.ts (shutdown's terminal handling). PiSharp runs the session in the in-process RPC host and drives it from the
// interactive mode on a dedicated UI loop, on Windows, Linux and macOS.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Pi;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

internal static class InteractiveModeHost
{
    /// <summary>Runs interactive mode over <c>session terminal</c> arguments (the RPC host runs the same session arguments).</summary>
    public static async Task<int> RunAsync(string[] terminalArgs, PiEntryOptions options, PiSharp.Cli.Mcp.McpSessionHost? mcpHost,
        McpBinding? mcp, TextWriter stdout, TextWriter stderr, CancellationToken token, Func<UiLoop, ITerminal>? createTerminal = null,
        Action<InteractiveMode>? observeMode = null, Func<InteractiveModeContext, InteractiveModeContext>? configureContext = null)
    {
        var startup = options.Interactive ?? throw new InvalidOperationException("Interactive startup options are missing.");
        var rpcArgs = terminalArgs.Where(value => value != "--terminal-preview").ToArray();
        rpcArgs[1] = "rpc";
        var loop = new UiLoop("pi-interactive");
        var terminal = createTerminal?.Invoke(loop) ?? new ProcessTerminal(loop);
        var settings = new InteractiveSettings(startup.Cwd, startup.AgentDir, startup.ProjectTrusted);
        Themes.AgentDirectory = startup.AgentDir;
        var client = new RpcSessionClient();
        var connection = new BoundedRpcConnection(client.ObserveAsync);
        client.Bind(connection.SendAsync);
        var hostDiagnostics = new StringWriter();
        var consoleLines = new List<string>();
        using var hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var exitRequested = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var userQuit = 0;
        var trustStore = new ProjectTrustStore(startup.AgentDir, startup.Home);
        var crashLogPath = PiSharp.Cli.Diagnostics.CrashReporting.DefaultCrashLogPath();
        var login = TryCreateLogin();
        var context = new InteractiveModeContext
        {
            Rpc = client, Settings = settings, Startup = startup, Terminal = terminal, Loop = loop,
            ConsoleLog = line => { lock (consoleLines) consoleLines.Add(line); },
            TakeUnnotifiedCrash = () => PiSharp.CodingAgent.Diagnostics.CrashLog.TakeUnnotifiedCrash(crashLogPath) is { } crash
                ? new CrashNoticeInfo(DateTimeOffset.TryParse(crash.Timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture) : crash.Timestamp, crash.Message)
                : null,
            RecordCrashImpl = (kind, error, sessionFile, cwd) => PiSharp.Cli.Diagnostics.CrashReporting.RecordCrash(kind, error, sessionFile, cwd),
            HasTrustRequiringProjectResources = cwd => ProjectTrustStore.HasTrustRequiringProjectResources(cwd, startup.Home),
            GetTrustEntry = trustStore.GetEntry,
            SaveTrustDecisions = updates => trustStore.SetMany(updates),
            RequestExitImpl = (_, code) => { Interlocked.Exchange(ref userQuit, 1); exitRequested.TrySetResult(code); },
            Suspend = Suspend,
            Login = login,
            Mcp = mcp,
            ExportToJsonl = (path, cwd) => Task.Run(() => PiSharp.CodingAgent.Export.AgentSessionExport.ExportToJsonl(
                startup.Host.CurrentSession ?? throw new InvalidOperationException("The session is not ready."), path, cwd)),
            ExportToHtml = (rpc, path, themeName) => Task.Run(() => PiSharp.CodingAgent.Export.AgentSessionExport.ExportToHtmlAsync(
                startup.Host.CurrentSession ?? throw new InvalidOperationException("The session is not ready."),
                new PiSharp.CodingAgent.Export.SessionHtmlExportHost(new PiSharp.CodingAgent.Export.PiThemeHost { AgentDirectory = startup.AgentDir },
                    settings.ThemeSetting, startup.Cwd), path, themeName)),
            ShareSession = (ui, themeName, abort) =>
            {
                var session = startup.Host.CurrentSession ?? throw new InvalidOperationException("The session is not ready.");
                var share = PiSharp.CodingAgent.Export.AgentSessionExport.ShareSession(session,
                    new PiSharp.CodingAgent.Export.SessionHtmlExportHost(new PiSharp.CodingAgent.Export.PiThemeHost { AgentDirectory = startup.AgentDir },
                        settings.ThemeSetting, startup.Cwd), themeName);
                return new PiSharp.CodingAgent.Export.SessionShare().ShareSessionAsync(share, ui, abort);
            },
            ImportSessionFile = path => ImportSessionFile(path, startup),
            RenameSessionFile = async (path, name, rpc) =>
            {
                if (rpc is not null) { await rpc.RequestAsync(new JsonObject { ["type"] = "set_session_name", ["name"] = name }); return; }
                await AppendSessionInfoAsync(path, name);
            },
            OnCredentialsChanged = _ => Task.CompletedTask,
            IsRetryableAssistantError = message => IsRetryableAssistantError(message)
        };
        InteractiveMode? mode = null;
        Task<int>? host = null;
        var exitCode = 0;
        string? resumeCommand = null;
        try
        {
            context = InteractiveHostServices.Configure(context, options);
            if (configureContext is not null) context = configureContext(context);
            mode = await loop.InvokeAsync(() => new InteractiveMode(context, new InteractiveModeOptions
            {
                MigratedProviders = [.. options.MigratedAuthProviders],
                StartupDiagnostics = [.. options.StartupDiagnostics.Select(diagnostic => (diagnostic.Type, diagnostic.Message))],
                DeprecationWarnings = [.. options.DeprecationWarnings],
                InitialMessage = options.InitialMessage,
                InitialImages = options.InitialImages.IsDefaultOrEmpty ? null : new JsonArray([.. options.InitialImages.Select(image => JsonNode.Parse(image.ToString()))]),
                InitialMessages = [.. options.InitialMessages],
                Verbose = options.Verbose,
                TuiMode = options.TuiMode,
                InitialThemeSetting = options.Theme
            }));
            observeMode?.Invoke(mode);
            host = Task.Run(() => RpcSessionCommand.RunWithPresentationAsync(rpcArgs, connection.Input, connection.Output, hostDiagnostics, mode,
                hostCancellation.Token, userShutdown: () => Volatile.Read(ref userQuit) != 0 && !token.IsCancellationRequested, mcpHost: mcpHost));
            var ready = await Task.WhenAny(startup.Host.Ready, host);
            if (ready == host)
            {
                exitCode = await host;
                return exitCode == 0 ? 1 : exitCode;
            }
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var run = loop.InvokeAsync(() => mode.RunAsync(runCancellation.Token));
            var completed = await Task.WhenAny(exitRequested.Task, host, run, Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default));
            if (completed == run && run.IsFaulted) throw run.Exception!.InnerException ?? run.Exception;
            if (exitRequested.Task.IsCompleted) exitCode = await exitRequested.Task;
            runCancellation.Cancel();
            resumeCommand = await loop.InvokeAsync(mode.FormatResumeCommand);
        }
        catch (Exception error)
        {
            if (mode is not null)
                try { await loop.InvokeAsync(() => mode.Stop("transcript")); } catch { }
            PiSharp.Cli.Diagnostics.CrashReporting.ReportUncaughtException(error, stderr, [], startup.Host.CurrentSession?.Path, startup.Cwd);
            exitCode = 1;
        }
        finally
        {
            if (mode is not null)
                try { await loop.InvokeAsync(() => mode.Stop()); } catch { }
            Interlocked.Exchange(ref userQuit, 1);
            connection.CompleteInput();
            client.Close();
            if (host is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var hostCode = await host.WaitAsync(timeout.Token);
                    if (exitCode == 0 && hostCode != 0 && !token.IsCancellationRequested) exitCode = hostCode;
                }
                catch (OperationCanceledException) { hostCancellation.Cancel(); }
                catch (Exception) { exitCode = exitCode == 0 ? 1 : exitCode; }
            }
            try { await connection.DisposeAsync(); } catch { }
            loop.Stop();
        }
        foreach (var line in consoleLines) await stdout.WriteLineAsync(line);
        var diagnostics = hostDiagnostics.ToString();
        if (diagnostics.Length > 0 && exitCode != 0) await stderr.WriteAsync(diagnostics);
        if (resumeCommand is not null && exitCode == 0) await stdout.WriteAsync($"\u001b[2mTo resume this session:\u001b[22m {resumeCommand}\n");
        await stdout.FlushAsync(CancellationToken.None);
        return exitCode;
    }

    /// <summary>SessionManager.open(path).appendSessionInfo(name) for a session that is not open: a session_info entry under the last entry.</summary>
    internal static async Task AppendSessionInfoAsync(string path, string name)
    {
        string? parentId = null;
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (line.Trim().Length == 0) continue;
            try { if (JsonNode.Parse(line) is JsonObject entry && SessionEntries.Type(entry) != "session" && SessionEntries.Id(entry) is { } id) parentId = id; }
            catch (System.Text.Json.JsonException) { }
        }
        var record = new JsonObject
        {
            ["type"] = "session_info", ["id"] = Guid.NewGuid().ToString("N")[..8], ["parentId"] = parentId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), ["name"] = name
        };
        await File.AppendAllTextAsync(path, record.ToJsonString() + "\n");
    }

    private static PiSharp.Cli.Authentication.ProviderLoginHost? TryCreateLogin()
    {
        try { return PiSharp.Cli.Authentication.ProviderLoginHost.CreateDefault(); }
        catch { return null; }
    }

    /// <summary>importFromJsonl: copy the file into the session directory under a new name, then switch to it.</summary>
    private static string ImportSessionFile(string path, InteractiveStartup startup)
    {
        var directory = startup.Host.CurrentSession?.Path is { } current ? Path.GetDirectoryName(current)! : startup.SessionDir ?? Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var target = Path.Join(directory, Path.GetFileName(path));
        if (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            target = Path.Join(directory, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.jsonl");
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.Ordinal)) File.Copy(path, target);
        return target;
    }

    /// <summary>Ctrl+Z: stop the TUI, stop the process group (SIGTSTP) and restore the TUI on SIGCONT.</summary>
    private static void Suspend(Action stop, Action resume)
    {
        if (OperatingSystem.IsWindows()) return;
        PosixSuspend.Run(stop, resume);
    }

    /// <summary>pi-ai isRetryableAssistantError: transient provider failures the retry loop handles.</summary>
    internal static bool IsRetryableAssistantError(JsonObject message)
    {
        if (SessionEntries.Str(message["stopReason"]) != "error") return false;
        var error = SessionEntries.Str(message["errorMessage"]) ?? "";
        return System.Text.RegularExpressions.Regex.IsMatch(error,
            @"overloaded|rate.?limit|too many requests|429|500|502|503|504|service.?unavailable|server error|internal error|connection.?error|connection.?refused|other side closed|fetch failed|upstream.?connect|reset before headers|terminated|retry delay",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class PosixSuspend
{
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    public static void Run(Action stop, Action resume)
    {
        System.Runtime.InteropServices.PosixSignalRegistration? registration = null;
        registration = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGCONT, context =>
        {
            registration?.Dispose();
            resume();
        });
        stop();
        // SIGTSTP is 20 on Linux and 18 on macOS; pid 0 signals the process group.
        kill(0, OperatingSystem.IsMacOS() ? 18 : 20);
    }
}
