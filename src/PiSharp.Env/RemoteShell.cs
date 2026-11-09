using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Env;

public sealed partial class RemoteExecutionEnv
{
    public async Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context)
    {
        if (context.IsCancellationRequested) return new ExecutionError(ExecutionErrorCode.Aborted, "aborted");
        if (options?.Timeout is { } seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue))
            return new ExecutionError(ExecutionErrorCode.Timeout, "Invalid timeout: must be positive, finite and at most 2147483.647 seconds");
        using var cancellation = new CancellationTokenSource();
        var stopReason = 0; // Linearizes caller cancellation, timeout and callback failure.
        using var abortRegistration = context.CancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref stopReason, 1, 0) == 0) cancellation.Cancel();
        });
        await using var timer = options?.Timeout is { } timeoutSeconds ? new Timer(_ =>
        {
            if (Interlocked.CompareExchange(ref stopReason, 2, 0) == 0) cancellation.Cancel();
        }, null, TimeSpan.FromMilliseconds(timeoutSeconds * 1000), Timeout.InfiniteTimeSpan) : null;
        ExecutionError? callbackError = null;
        uint? requestId = null;
        var settled = false;
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (options?.InheritEnv != false && this.options.ShellEnv is { } defaults)
                foreach (var pair in defaults) environment[pair.Key] = pair.Value;
            if (options?.Env is { } overrides) foreach (var pair in overrides) environment[pair.Key] = pair.Value;
            var json = new JsonObject
            {
                ["cwd"] = string.IsNullOrEmpty(options?.Cwd) ? Cwd : await ResolveAsync(options.Cwd, cancellation.Token).ConfigureAwait(false),
                ["env"] = Json(environment), ["inheritEnv"] = options?.InheritEnv ?? true,
            };
            if (command.IsScript) json["command"] = command.Script;
            else json["argv"] = Json(command.Argv.IsDefault ? [] : command.Argv.ToArray());
            if (this.options.ShellPath is { } shellPath) json["shellPath"] = shellPath;
            if (options?.Timeout is { } timeout) json["timeoutMs"] = timeout * 1000;
            if (options?.Spill is { } spill) json["spill"] = Json(spill);
            if (options?.Window is { } window) json["window"] = Json(window);
            var reply = await Connection.RequestAsync("exec", json, new()
            {
                OnStart = (id, session) => { requestId = id; running[id] = session; },
                OnEvent = (evt, payload) =>
                {
                    if (settled || callbackError is not null || evt["kind"]?.GetValue<string>() != "output" || options?.OnOutput is null || payload.IsEmpty) return;
                    try
                    {
                        ShellOutputSkip? skipped = evt["skipped"] is JsonObject skip
                            ? new(skip["bytes"]!.GetValue<long>(), skip["newlines"]!.GetValue<long>(), skip["endsWithNewline"]!.GetValue<bool>()) : null;
                        options.OnOutput(Encoding.UTF8.GetString(payload.Span), context,
                            new(evt["stream"]?.GetValue<string>() == "stderr" ? ShellStream.Stderr : ShellStream.Stdout, skipped));
                    }
                    catch (Exception error)
                    {
                        callbackError = new ExecutionError(ExecutionErrorCode.CallbackError, error.Message, error);
                        if (Interlocked.CompareExchange(ref stopReason, 3, 0) == 0) cancellation.Cancel();
                    }
                },
            }, cancellation.Token).ConfigureAwait(false);
            if (callbackError is not null) return callbackError;
            return new ShellExecResult(reply.Json["exitCode"]!.GetValue<int>(), reply.Json["spillPath"]?.GetValue<string>());
        }
        catch (Exception error)
        {
            if (callbackError is not null) return callbackError;
            var remote = error as RemoteError;
            var code = remote?.Code switch
            {
                "timeout" => ExecutionErrorCode.Timeout, "aborted" => Volatile.Read(ref stopReason) == 2 ? ExecutionErrorCode.Timeout : ExecutionErrorCode.Aborted,
                "shell_unavailable" => ExecutionErrorCode.ShellUnavailable, "spawn_error" => ExecutionErrorCode.SpawnError,
                _ => error is OperationCanceledException ? Volatile.Read(ref stopReason) == 2 ? ExecutionErrorCode.Timeout : ExecutionErrorCode.Aborted : ExecutionErrorCode.Unknown,
            };
            return new ExecutionError(code, code == ExecutionErrorCode.Timeout ? "timeout:" + options?.Timeout?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : error.Message, error)
                { SpillPath = remote?.Fields["spillPath"]?.GetValue<string>() };
        }
        finally { settled = true; if (requestId is { } id) running.TryRemove(id, out _); }
    }
}
