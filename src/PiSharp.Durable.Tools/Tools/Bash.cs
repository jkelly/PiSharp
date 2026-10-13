// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/tools/bash.ts
// Timeouts stay in seconds as JavaScript numbers; messages print them as JavaScript does. Upstream's `prepare` may return a promise or
// nothing; here it returns a ValueTask.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>A command about to run, which <see cref="BashToolOptions.Prepare"/> may change: the script, its working directory and
/// environment (upstream <c>BashExecution</c>).</summary>
public sealed class BashExecution
{
    /// <summary>The script, with the tool's command prefix.</summary>
    public required string Command { get; set; }
    /// <summary>The working directory; starts as the environment's.</summary>
    public required string Cwd { get; set; }
    /// <summary>Variables set for the command; starts empty.</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Whether the command starts from the environment's variables; starts true.</summary>
    public bool InheritEnv { get; set; } = true;
}

/// <summary>Changes a command before it runs, with the call's api and context (upstream <c>BashPrepare</c>).</summary>
public delegate ValueTask BashPrepare(BashExecution execution, IToolExecutionApi api, DurableContext context);

/// <summary>Options of <see cref="CodingTools.CreateBashTool"/> (upstream <c>BashToolOptions</c>).</summary>
public sealed record BashToolOptions
{
    /// <summary>Lines run before each command.</summary>
    public string? CommandPrefix { get; init; }
    /// <summary>Changes each command before it runs.</summary>
    public BashPrepare? Prepare { get; init; }
}

/// <summary>Options of <see cref="CodingTools.CreatePowerShellTool"/> (upstream <c>PowerShellToolOptions</c>).</summary>
public sealed record PowerShellToolOptions
{
    /// <summary>Lines run before each command.</summary>
    public string? CommandPrefix { get; init; }
    /// <summary>Changes each command before it runs.</summary>
    public BashPrepare? Prepare { get; init; }
    /// <summary>PowerShell programs to try in order; default <c>pwsh</c>, then <c>powershell</c>. A program that cannot start is skipped.</summary>
    public IReadOnlyList<string>? Programs { get; init; }
}

/// <summary>Upstream <c>createBashTool</c> and <c>createPowerShellTool</c>.</summary>
internal static class BashTool
{
    private const double MaxTimeoutSeconds = 2_147_483_647 / 1000.0;

    /// <summary><c>Type.Object({ command, timeout? })</c> as TypeBox prints it, for <paramref name="shell"/>.</summary>
    internal static JsonObjectValue ParametersFor(string shell) => (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["command"],"properties":{"command":{"type":"string","description":""" + "\"" + shell +
        """ command to execute"},"timeout":{"type":"number","description":"Timeout in seconds (optional, no default timeout)"}}}""");

    private static string Description(string shell) =>
        $"Execute a {shell} command in the current working directory. Returns combined stdout and stderr. Output is truncated to last {Truncation.DefaultMaxLines} lines or {Truncation.DefaultMaxBytes / 1024}KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.";

    private static void ValidateTimeout(double? timeout)
    {
        if (timeout is not { } seconds) return;
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentException("Invalid timeout: must be a finite number of seconds");
        if (seconds > MaxTimeoutSeconds) throw new ArgumentException($"Invalid timeout: maximum is {Js.NumberToString(MaxTimeoutSeconds)} seconds");
    }

    private static (string Command, double? Timeout) Arguments(JsonValue arguments)
    {
        var args = (JsonObjectValue)arguments;
        return (((JsonStringValue)args["command"]).Value,
            args.TryGetValue("timeout", out var timeout) && timeout is JsonNumberValue seconds ? seconds.Value : null);
    }

    /// <summary>The execution of one call: the command with its prefix, in the environment's working directory, then <c>prepare</c>.</summary>
    private static async Task<BashExecution> PrepareExecutionAsync(string command, string? commandPrefix, BashPrepare? prepare, IToolExecutionApi api,
        DurableContext context)
    {
        var execution = new BashExecution
        {
            Command = string.IsNullOrEmpty(commandPrefix) ? command : $"{commandPrefix}\n{command}",
            Cwd = ToolEnv.Require(api).Cwd,
        };
        if (prepare is not null) await prepare(execution, api, context).ConfigureAwait(false);
        return execution;
    }

    /// <summary>
    /// Runs each command form in turn until one starts, streaming output to <c>api.Output</c> within the retained window, and turns the result
    /// into the tool's outcome: a spill diagnostic, and a thrown error for a failure or a nonzero exit.
    /// </summary>
    private static async Task RunCommandAsync(IReadOnlyList<ShellCommand> commands, BashExecution execution, double? timeout, IToolExecutionApi api,
        DurableContext context)
    {
        var env = ToolEnv.Require(api);
        Result<ShellExecResult, ExecutionError>? last = null;
        foreach (var command in commands)
        {
            var result = await env.ExecAsync(command, new ShellExecOptions
            {
                Cwd = execution.Cwd,
                Env = execution.Env,
                InheritEnv = execution.InheritEnv,
                Timeout = timeout,
                OnOutput = (text, _, info) => api.Output(text, info.Skipped),
                Spill = new ShellSpillOptions(Truncation.DefaultMaxBytes, Truncation.DefaultMaxLines),
                // An environment may then omit output outside the retained tail and report the omission.
                Window = api.OutputWindow,
            }, context).ConfigureAwait(false);
            last = result;
            // A program that could not start produced no output; the next form may.
            if (result.IsOk || result.Error.Code != ExecutionErrorCode.SpawnError) break;
        }
        if (last is not { } outcome) throw new InvalidOperationException("No command to run");
        var spillPath = outcome.IsOk ? outcome.Value.SpillPath : outcome.Error.SpillPath;
        if (spillPath is not null) api.Diagnostic(new ToolDiagnostic(ToolDiagnosticSeverity.Info, $"Full output: {spillPath}", "full_output"));
        if (!outcome.IsOk)
        {
            var error = outcome.Error;
            if (error.Code == ExecutionErrorCode.Aborted && context.IsCancellationRequested) throw error;
            if (error.Code == ExecutionErrorCode.Timeout) throw new TimeoutException($"Command timed out after {Js.NumberToString(timeout ?? double.NaN)} seconds");
            if (error.Code == ExecutionErrorCode.Aborted) throw new OperationCanceledException("Command aborted");
            throw error;
        }
        if (outcome.Value.ExitCode != 0) throw new InvalidOperationException($"Command exited with code {outcome.Value.ExitCode}");
    }

    /// <summary>
    /// Runs a command through the environment's shell. Its output streams to <c>api.Output</c>, where the Harness keeps the tail within the
    /// default limits; the result content is that retained output. The retained window goes to the environment, which may omit output outside
    /// it and report how much it omitted, so dropped counts stay exact. Output beyond the limits is spilled to a file whose path is reported as
    /// a diagnostic. A nonzero exit or timeout throws, which makes an error result that still carries the output and diagnostics.
    /// </summary>
    public static ToolRegistration CreateBash(BashToolOptions? options) => Define.Tool(new ToolRegistration
    {
        Name = "bash",
        Description = Description("bash"),
        Parameters = ParametersFor("Bash"),
        OutputLimits = new ToolOutputLimits { Retain = ToolOutputRetention.Tail },
        Execute = async (arguments, api, context) =>
        {
            var (command, timeout) = Arguments(arguments);
            ValidateTimeout(timeout);
            var execution = await PrepareExecutionAsync(command, options?.CommandPrefix, options?.Prepare, api, context).ConfigureAwait(false);
            await RunCommandAsync([execution.Command], execution, timeout, api, context).ConfigureAwait(false);
            return new ToolExecutionResult();
        },
    });

    /// <summary>Output in UTF-8 whatever the console's code page, as the coding agent's <c>powershell</c> tool does.</summary>
    private const string Utf8Output = "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}";
    private static readonly string[] PowerShellArgs = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"];

    /// <summary>
    /// Runs a PowerShell command, like <c>bash</c> but through PowerShell instead of the environment's shell: <c>pwsh</c> (PowerShell 7),
    /// else Windows PowerShell, started directly with the command as an argument, so no other shell parses it.
    /// </summary>
    public static ToolRegistration CreatePowerShell(PowerShellToolOptions? options)
    {
        var programs = options?.Programs?.ToArray() ?? ["pwsh", "powershell"];
        return Define.Tool(new ToolRegistration
        {
            Name = "powershell",
            Description = Description("PowerShell"),
            Parameters = ParametersFor("PowerShell"),
            OutputLimits = new ToolOutputLimits { Retain = ToolOutputRetention.Tail },
            Execute = async (arguments, api, context) =>
            {
                var (command, timeout) = Arguments(arguments);
                ValidateTimeout(timeout);
                var execution = await PrepareExecutionAsync(command, options?.CommandPrefix, options?.Prepare, api, context).ConfigureAwait(false);
                var script = $"{Utf8Output}\n{execution.Command}";
                var commands = programs.Select(program => ShellCommand.FromArgv([program, .. PowerShellArgs, script])).ToList();
                await RunCommandAsync(commands, execution, timeout, api, context).ConfigureAwait(false);
                return new ToolExecutionResult();
            },
        });
    }
}
