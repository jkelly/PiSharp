// Support for the tools.* cases (upstream test/tools.test.ts and test/tools-read-differential.test.ts): a minimal execution api, the
// test environments upstream derives from NodeExecutionEnv (here LocalExecutionEnv), and a unified-patch applier standing in for jsdiff's
// applyPatch.
using System.Reflection;
using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools.Env;

/// <summary>Every member of the api the tools do not use throws.</summary>
public class ToolApiStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException($"{targetMethod?.Name} is not available to the tools tests");
}

/// <summary>
/// Upstream's <c>fakeApi</c>: the environment, collected output and diagnostics, and nothing durable. <see cref="OnOutput"/> replaces the
/// output collection, as upstream's spread api does.
/// </summary>
internal sealed class FakeToolApi(IExecutionEnv? env) : DelegatingToolExecutionApi(DispatchProxy.Create<IToolExecutionApi, ToolApiStub>())
{
    public List<string> Collected { get; } = [];
    public List<ToolDiagnostic> Reported { get; } = [];
    public ShellOutputWindow? Window { get; init; }
    public Action<string, ShellOutputSkip?>? OnOutput { get; init; }
    public override TaskId TaskId => new(1);
    public override ConversationId ConversationId => new(1);
    public override string CallId => "call";
    public override IExecutionEnv? Env => env;
    public override ShellOutputWindow? OutputWindow => Window;
    public override void Output(string chunk, ShellOutputSkip? skipped = null)
    {
        if (OnOutput is not null) OnOutput(chunk, skipped);
        else Collected.Add(chunk);
    }
    public override void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null) => Output(Encoding.UTF8.GetString(chunk), skipped);
    public override void Diagnostic(ToolDiagnostic diagnostic) => Reported.Add(diagnostic);
    public override Task DetailsAsync(JsonValue value, DurableContext context) => Task.CompletedTask;
}

/// <summary>What one call returned, streamed and reported.</summary>
internal sealed record ToolRun(ToolExecutionResult Result, List<string> Output, List<ToolDiagnostic> Reported)
{
    public string Text => string.Join("\n", (Result.Content ?? JsonArrayValue.Empty).Cast<JsonObjectValue>()
        .Where(part => part["type"] is JsonStringValue { Value: "text" }).Select(part => ((JsonStringValue)part["text"]).Value));
    public string DiagnosticText => string.Join("\n", (Result.Diagnostics ?? []).Select(diagnostic => diagnostic.Message));
}

/// <summary>A call that threw, with what it streamed and reported first.</summary>
internal sealed record ToolFailure(Exception Error, List<string> Output, List<ToolDiagnostic> Reported);

internal static class ToolRunner
{
    public static async Task<ToolRun> Run(ToolRegistration tool, JsonValue args, IExecutionEnv? env, DurableContext? context = null)
    {
        var api = new FakeToolApi(env);
        var result = await tool.Execute(args, api, context ?? DurableContext.Background);
        return new ToolRun(result, api.Collected, api.Reported);
    }

    public static async Task<ToolFailure> RunFailing(ToolRegistration tool, JsonValue args, IExecutionEnv env)
    {
        var api = new FakeToolApi(env);
        try
        {
            await tool.Execute(args, api, DurableContext.Background);
        }
        catch (Exception error)
        {
            return new ToolFailure(error, api.Collected, api.Reported);
        }
        throw new InvalidOperationException("Expected the tool to throw");
    }

    /// <summary>jsdiff's <c>applyPatch</c> for the well-formed single-file patches the edit tool writes: applies each hunk at its stated
    /// position, checking context and removed lines.</summary>
    public static string ApplyPatch(string original, string patch)
    {
        var source = new List<string>();
        for (var start = 0; start < original.Length;)
        {
            var newline = original.IndexOf('\n', start);
            var end = newline == -1 ? original.Length : newline + 1;
            source.Add(original[start..end]);
            start = end;
        }
        var lines = patch.Split('\n');
        var output = new StringBuilder();
        var consumed = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.StartsWith("@@ ", StringComparison.Ordinal)) continue;
            var header = line.Split(' ');
            var oldStart = int.Parse(header[1][1..].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
            var oldLines = int.Parse(header[1].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
            var target = oldLines == 0 ? oldStart : oldStart - 1;
            while (consumed < target) output.Append(source[consumed++]);
            for (index++; index < lines.Length && !lines[index].StartsWith("@@ ", StringComparison.Ordinal); index++)
            {
                var body = lines[index];
                if (body.Length == 0 || body[0] == '\\') continue;
                var noNewline = index + 1 < lines.Length && lines[index + 1].StartsWith('\\');
                var text = body[1..] + (noNewline ? "" : "\n");
                switch (body[0])
                {
                    case ' ':
                        if (source[consumed] != text) throw new InvalidOperationException($"context mismatch at {consumed}: {source[consumed]}");
                        output.Append(source[consumed++]);
                        break;
                    case '-':
                        if (source[consumed] != text) throw new InvalidOperationException($"removal mismatch at {consumed}: {source[consumed]}");
                        consumed++;
                        break;
                    case '+':
                        output.Append(text);
                        break;
                }
            }
            index--;
        }
        while (consumed < source.Count) output.Append(source[consumed++]);
        return output.ToString();
    }
}

/// <summary>Upstream's <c>deferred()</c>.</summary>
internal sealed class ToolGate
{
    private readonly TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Task => source.Task;
    public void Resolve() => source.TrySetResult();
}

/// <summary>Reads text slowly, so concurrent edits overlap.</summary>
internal sealed class SlowReadExecutionEnv(string cwd) : LocalExecutionEnv(cwd)
{
    public override async Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context)
    {
        await Task.Delay(20);
        return await base.ReadTextFileAsync(path, context);
    }
}

/// <summary>Holds the write of "first\n" until released and records whether "second\n" started.</summary>
internal class BlockingWriteExecutionEnv(string cwd) : LocalExecutionEnv(cwd)
{
    public ToolGate FirstWriteStarted { get; } = new();
    public ToolGate FinishFirstWrite { get; } = new();
    public volatile bool SecondWriteStarted;

    public override async Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context)
    {
        var text = Encoding.UTF8.GetString(content.Span);
        if (text == "first\n")
        {
            FirstWriteStarted.Resolve();
            await FinishFirstWrite.Task;
        }
        else if (text == "second\n") SecondWriteStarted = true;
        return await base.WriteFileAsync(path, content, context);
    }
}

/// <summary>Holds the first edit's write until released, then writes it whatever its context, as upstream's background write does.</summary>
internal sealed class BlockingEditExecutionEnv(string cwd) : LocalExecutionEnv(cwd)
{
    public ToolGate FirstEditWriteStarted { get; } = new();
    public ToolGate FinishFirstEditWrite { get; } = new();
    public volatile bool FirstEditWriteSettled;
    public volatile bool SecondEditWriteStarted;

    public override async Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context)
    {
        var text = Encoding.UTF8.GetString(content.Span);
        if (text == "ALPHA\nbeta\n")
        {
            FirstEditWriteStarted.Resolve();
            await FinishFirstEditWrite.Task;
            var result = await base.WriteFileAsync(path, content, DurableContext.Background);
            FirstEditWriteSettled = true;
            return result;
        }
        if (text is "ALPHA\nBETA\n" or "alpha\nBETA\n") SecondEditWriteStarted = true;
        return await base.WriteFileAsync(path, content, context);
    }
}

/// <summary>A blocking environment with another file system id.</summary>
internal sealed class OtherFileSystemEnv(string cwd) : BlockingWriteExecutionEnv(cwd)
{
    public override string Id => "other";
}

/// <summary>Emits more than the default line limit, spills it, then times out.</summary>
internal sealed class TimeoutOutputExecutionEnv(string cwd) : LocalExecutionEnv(cwd)
{
    public const int TruncatedOutputLines = Truncation.DefaultMaxLines + 1;

    public override async Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context)
    {
        var output = string.Join("\n", Enumerable.Range(1, TruncatedOutputLines).Select(index => $"line-{index}")) + "\n";
        var spillPath = (await CreateTempFileAsync(new TempFileOptions { Prefix = "timeout-", Suffix = ".log" }, context)).GetOrThrow();
        (await this.WriteFileAsync(spillPath, output, context)).GetOrThrow();
        options?.OnOutput?.Invoke(output, context, new ShellOutputInfo(ShellStream.Stdout));
        return new ExecutionError(ExecutionErrorCode.Timeout, $"timeout:{options?.Timeout}") { SpillPath = spillPath };
    }
}

/// <summary>An environment where only the listed programs exist; it records each command and prints <c>output</c>.</summary>
internal sealed class ProgramsEnv(string cwd, IReadOnlyList<string> installed, string output, int exitCode) : LocalExecutionEnv(cwd)
{
    public List<ShellCommand> Commands { get; } = [];

    public override Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context)
    {
        Commands.Add(command);
        if (command.IsScript || !installed.Contains(command.Argv[0]))
            return Task.FromResult<Result<ShellExecResult, ExecutionError>>(
                new ExecutionError(ExecutionErrorCode.SpawnError, $"spawn {(command.IsScript ? command.Script : command.Argv[0])} ENOENT"));
        options?.OnOutput?.Invoke(output, context, new ShellOutputInfo(ShellStream.Stdout));
        return Task.FromResult<Result<ShellExecResult, ExecutionError>>(new ShellExecResult(exitCode));
    }
}
