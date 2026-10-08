using System.Text.Json;
using PiSharp.Cli.Output;

namespace PiSharp.Cli;

internal static class Program
{
    private const string Usage = "Usage: PiSharp.Cli --offline-demo --workspace <new absolute directory> --session <new absolute JSONL file in workspace>; " + Commands.SessionCommands.Usage + "; " + Commands.RpcSessionCommand.Usage + "; " + Commands.InteractiveSessionCommand.Usage + "; " + Commands.TerminalSessionCommand.Usage + "; " + Commands.SessionCopyCommand.Usage + "; " + Commands.SessionCatalogCommand.Usage + "; " + Commands.SessionContextEditCommand.Usage + "; " + Commands.McpCommand.Usage + "; " + Models.ModelListing.Usage;

    private static async Task<int> Main(string[] args)
    {
        // Codemode scripts run in a child process of this executable (see PiSharp.Codemode.CodemodeWorker).
        if (args is [PiSharp.Codemode.CodemodeWorker.Argument])
            return await PiSharp.Codemode.CodemodeWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()).ConfigureAwait(false);
        PiSharp.Codemode.CodemodeWorker.Default ??= PiSharp.Codemode.CodemodeWorkerLauncher.ForCurrentProcess();
        if (args.Length > 0 && args[0] == "session") return await RunSessionAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0] == "mcp") return await RunMcpAsync(args[1..]).ConfigureAwait(false);
        // Pi's own command line (plain pisharp, -p, --mode json|rpc, --help, --list-models, ...); the offline demo keeps its form.
        if (args is not ["--offline-demo", ..]) return await RunPiAsync(args).ConfigureAwait(false);
        Stream standardOutput;
        try { standardOutput = StandardOutputStream.Open(); }
        catch (Exception) { return Fail("StandardOutputUnavailable", "Standard output could not be opened.", 1); }
        await using var ownedOutput = standardOutput;
        await using var output = new Utf8StreamTextWriter(standardOutput);
        if (args.Length != 5 || args[0] != "--offline-demo") return Fail("InvalidArguments", Usage, 2);
        string? workspace = null; string? session = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] == "--workspace" && workspace is null) workspace = args[index + 1];
            else if (args[index] == "--session" && session is null) session = args[index + 1];
            else return Fail("InvalidArguments", Usage, 2);
        }
        if (workspace is null || session is null) return Fail("InvalidArguments", Usage, 2);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, observation) => { observation.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = await OfflineDemo.RunAsync(workspace, session, cancellation.Token).ConfigureAwait(false);
            await output.WriteLineAsync(result.Report.ToString()).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OfflineDemoException error) { return Fail(error.Failure.ToString(), error.Message, 2); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { return Fail("Canceled", "Offline demo canceled after settling owned work; inspect the new workspace for completed effects.", 1); }
        catch (Exception)
        { return Fail("OfflineDemoFailed", "Offline demo failed; inspect the newly created workspace and session before retrying with new paths.", 1); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>The Pi-compatible entry over the real console. Standard output carries only the mode's own output: other console
    /// writes go to standard error (source output-guard.ts takeOverStdout).</summary>
    private static async Task<int> RunPiAsync(string[] args)
    {
        Stream standardOutput;
        try { standardOutput = StandardOutputStream.Open(); }
        catch (Exception) { return Fail("StandardOutputUnavailable", "Standard output could not be opened.", 1); }
        await using var ownedOutput = standardOutput;
        await using var output = new Utf8StreamTextWriter(standardOutput);
        var originalOut = Console.Out;
        Console.SetOut(Console.Error);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, observation) => { observation.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var stdinRedirected = Console.IsInputRedirected;
            var host = new Pi.PiHost
            {
                Cwd = Directory.GetCurrentDirectory(), Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                GetEnvironment = Environment.GetEnvironmentVariable, SetEnvironment = Environment.SetEnvironmentVariable,
                Stdout = output, Stderr = Console.Error,
                Stdin = stdinRedirected ? new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false), false) : Console.In,
                StdinIsTty = !stdinRedirected, StdoutIsTty = !Console.IsOutputRedirected,
                Color = !Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
                LiveRuntime = Commands.LiveSessionRuntime.Default,
                CreateMcpHost = _ => Mcp.McpSessionHost.CreateDefault(),
                OpenRpcInput = CancellableStandardInput.Open, OpenRpcOutput = () => standardOutput,
                RunInteractive = OperatingSystem.IsWindows()
                    ? (terminalArgs, options, token) => RunTerminalHostAsync(terminalArgs, token, liveRuntime: options.LiveRuntime, mcpHost: Mcp.McpSessionHost.CreateDefault())
                    : null
            };
            return await Pi.PiCommand.RunAsync(args, host, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Console.SetOut(originalOut);
        }
    }

    private static async Task<int> RunMcpAsync(string[] args)
    {
        Stream standardOutput;
        try { standardOutput = StandardOutputStream.Open(); }
        catch (Exception) { return Fail("StandardOutputUnavailable", "Standard output could not be opened.", 1); }
        await using var ownedOutput = standardOutput;
        await using var output = new Utf8StreamTextWriter(standardOutput);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, observation) => { observation.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = await Commands.McpCommand.RunAsync(args, output, Console.Error, Commands.McpCommand.DefaultOptions(), cancellation.Token).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return result;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int Fail(string code, string message, int exitCode)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message }));
        return exitCode;
    }

    private static async Task<int> RunSessionAsync(string[] args)
    {
        if (args is ["session", "terminal", ..])
        {
            if (args.Count(value => value == "--terminal-preview") != 1 && args.Count(value => value == "--live") != 1)
                return Fail("InvalidArguments", Commands.TerminalSessionCommand.Usage, 2);
            var startup = await Commands.TerminalSessionCommand.ValidateStartupAsync(args, Console.Error).ConfigureAwait(false);
            if (startup.Arguments is null) return startup.Result;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, observation) => { observation.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args is ["session", "terminal", ..])
            {
                return await RunTerminalHostAsync(args, cancellation.Token, mcpHost: Mcp.McpSessionHost.CreateDefault()).ConfigureAwait(false);
            }
            Stream standardOutput;
            try { standardOutput = StandardOutputStream.Open(); }
            catch (Exception) { return Fail("StandardOutputUnavailable", "Standard output could not be opened.", 1); }
            await using var ownedOutput = standardOutput;
            if (args is ["session", "rpc", ..])
            {
                Stream input;
                try { input = CancellableStandardInput.Open(); }
                catch (Exception) { return Fail("StandardInputUnavailable", "RPC standard input could not be opened.", 1); }
                try
                {
                    await using (input.ConfigureAwait(false))
                        return await Commands.RpcSessionCommand.RunHostedAsync(args, input, standardOutput, Console.Error, Mcp.McpSessionHost.CreateDefault(), cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception) { return Fail("RpcHostFailed", "RPC host failed after owned input cleanup; inspect durable state.", 1); }
            }
            await using var output = new Utf8StreamTextWriter(standardOutput);
            if (args is ["session", "chat", ..])
            {
                Stream input;
                try { input = CancellableStandardInput.Open(); }
                catch (Exception) { return Fail("StandardInputUnavailable", "Interactive standard input could not be opened.", 1); }
                try
                {
                    await using (input.ConfigureAwait(false))
                    {
                        using var reader = new StreamReader(input, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                        return await Commands.InteractiveSessionCommand.RunHostedAsync(args, reader, output, Console.Error, Mcp.McpSessionHost.CreateDefault(), cancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) { return Fail("InteractiveHostFailed", "Interactive host failed after owned input cleanup; inspect durable state.", 1); }
            }
            if (args is ["session", "copy-inspect" or "copy" or "migrate", ..])
                return await Commands.SessionCopyCommand.RunAsync(args, output, Console.Error, cancellation.Token).ConfigureAwait(false);
            if (args is ["session", "list", ..])
                return await Commands.SessionCatalogCommand.RunAsync(args, output, Console.Error, cancellation.Token).ConfigureAwait(false);
            if (args is ["session", "context-edit", ..])
                return await Commands.SessionContextEditCommand.RunAsync(args, output, Console.Error, cancellation.Token).ConfigureAwait(false);
            if (args is ["session", "compact" or "branch-summary", ..])
                return await Commands.SessionSummaryCommand.RunAsync(args, output, Console.Error, cancellation.Token).ConfigureAwait(false);
            return await Commands.SessionCommands.RunAsync(args, output, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunTerminalHostAsync(string[] args, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<PiSharp.Tui.WindowsConsoleTerminal>>? openConsole = null, TextWriter? errorOutput = null,
        Func<CancellationToken, ValueTask<PiSharp.Tui.IConsoleTerminal>>? openOwnedTestTerminal = null,
        PiSharp.Cli.Interactive.TerminalKeybindingConfiguration? ownedTestConfiguration = null,
        Commands.LiveSessionRuntime? liveRuntime = null, Mcp.McpSessionHost? mcpHost = null)
    {
        var diagnostics = errorOutput ?? Console.Error;
        var startup = await Commands.TerminalSessionCommand.ValidateStartupAsync(args, diagnostics).ConfigureAwait(false);
        if (startup.Arguments is null) return startup.Result;
        PiSharp.Tui.IConsoleTerminal? terminal = null;
        Task? terminalRestore = null;
        PiSharp.Tui.WindowsConsoleCloseScope? close = null;
        Exception? failure = null;
        var exitCode = 1;
        try
        {
            terminal = openOwnedTestTerminal is not null ? await openOwnedTestTerminal(cancellationToken).ConfigureAwait(false) :
                openConsole is not null ? await openConsole(cancellationToken).ConfigureAwait(false) :
                await PiSharp.Tui.WindowsConsoleTerminal.OpenAsync(
                    new() { FollowActiveScreenBuffer = true }, cancellationToken).ConfigureAwait(false);
            if (terminal is PiSharp.Tui.WindowsConsoleTerminal windowsTerminal)
                close = PiSharp.Tui.WindowsConsoleCloseScope.Open(windowsTerminal);
            if (terminal is not PiSharp.Tui.ITerminalViewportSource viewport)
                throw new InvalidOperationException("Owned terminal must provide its actual viewport.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, close?.CancellationToken ?? default);
            exitCode = await Commands.TerminalSessionCommand.RunWithTerminalRestoreAsync(args, terminal, viewport,
                diagnostics, RestoreTerminalAndJoin, linked.Token, ownedTestConfiguration, liveRuntime, mcpHost).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        // The process-local close handler remains registered while application work,
        // the original console lease, and required output physically settle. Windows
        // may terminate the process as soon as the genuine close handler returns.
        if (terminal is not null)
            try { await RestoreTerminalAndJoin().ConfigureAwait(false); }
            catch (Exception error) { failure = Combine(failure, error); }
        if (close is not null)
            try { await close.JoinCancellationAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = Combine(failure, error); }
        try
        {
            if (failure is PiSharp.Tui.TerminalException terminalError)
                exitCode = HostFailure(terminalError.Failure.ToString(), terminalError.Message, 1);
            else if (failure is OperationCanceledException)
                exitCode = HostFailure("Canceled", "Terminal session canceled after owned console and session cleanup.", 1);
            else if (failure is not null)
                exitCode = HostFailure("TerminalHostFailed", "Terminal host failed after owned console cleanup; inspect durable state before retrying.", 1);
            await diagnostics.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            if (close is not null)
            {
                close.SignalCleanupComplete();
                await close.DisposeAsync().ConfigureAwait(false);
            }
        }
        return exitCode;
        ValueTask RestoreTerminalAndJoin() => new(terminalRestore ??= RestoreTerminalCoreAsync());
        async Task RestoreTerminalCoreAsync() { await terminal!.DisposeAsync().ConfigureAwait(false); }
        int HostFailure(string code, string message, int result)
        {
            diagnostics.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message }));
            return result;
        }
    }

    private static Exception Combine(Exception? first, Exception second) => first is null ? second : new AggregateException(first, second);
}
