// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/daemon/src/{exec,process}.rs.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Durable.Env;
using PiSharp.Env.Daemon.Native;
using PiSharp.Tools.Processes;

namespace PiSharp.Env.Daemon;

internal sealed record EnvProcessStart(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

/// <summary>Native processes owned by one daemon session. The process request remains pending until all output is delivered.</summary>
public static class EnvProcessOperations
{
    public static IReadOnlyList<IEnvDaemonOperation> Create() =>
        [new Operation("exec"), new Operation("process"), new Operation("processWrite"), new Operation("processCloseStdin")];

    private sealed class Operation(string name) : IEnvDaemonOperation
    {
        public string Name => name;
        public async ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request)
        {
            if (name is "processWrite" or "processCloseStdin")
            {
                if (request.Json["target"] is not JsonValue target || !target.TryGetValue<uint>(out var id) ||
                    !Processes(request).TryGetValue(id, out var process))
                    throw new EnvDaemonException("EBADF", "The program is no longer running");
                // ReadOnlyMemory<T> accepts a null array as an empty memory. Keep this conditional nullable so EOF
                // cannot silently become a zero-byte write through that implicit conversion.
                await process.InputAsync(name == "processWrite" ? request.Payload : (ReadOnlyMemory<byte>?)null, request.CancellationToken).ConfigureAwait(false);
                return EnvDaemonReply.Empty;
            }
            var input = Parse(request.Json, name == "process");
            if (!request.IsKillRequested || request.IsAbortRequested) request.CancellationToken.ThrowIfCancellationRequested();
            // A request cancellation during admission is observed immediately after the child is owned. A kill-mode
            // cancellation must still settle as the process's exit, rather than as an aborted admission.
            var child = await Child.StartAsync(input.Start, request.SessionCancellationToken).ConfigureAwait(false);
            request.Own(child);
            var run = new Run(request, child, name == "process", input);
            try { return await run.ExecuteAsync().ConfigureAwait(false); }
            finally { try { await child.DisposeAsync().ConfigureAwait(false); } finally { request.Release(child); } }
        }
    }

    private static ConcurrentDictionary<uint, Run> Processes(EnvDaemonRequest request) =>
        (ConcurrentDictionary<uint, Run>)request.State.GetOrAdd("env.processes", _ => new ConcurrentDictionary<uint, Run>());

    private sealed record Input(EnvProcessStart Start, byte[]? ShellInput, double? TimeoutMs, JsonObject? Spill, JsonObject? Window);
    private static Input Parse(JsonObject json, bool piped)
    {
        string Text(string key) => json[key]?.GetValue<string>() ?? throw new EnvDaemonException("EINVAL", $"{(piped ? "process" : "exec")} needs {key}");
        var cwd = Text("cwd");
        if (!Directory.Exists(cwd)) throw new EnvDaemonException("spawn_error", $"Working directory does not exist: {cwd}");
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (json["inheritEnv"]?.GetValue<bool>() != false)
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        if (json["env"] is JsonObject env)
            foreach (var (key, value) in env)
            {
                if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
                    throw new EnvDaemonException("EINVAL", "Environment values must be strings");
                environment[key] = text;
            }
        string program; IReadOnlyList<string> args; byte[]? shellInput = null;
        if (!piped && json["command"] is JsonValue script && script.TryGetValue<string>(out var command))
        {
            ShellConfiguration shell;
            try { shell = ShellDiscovery.Resolve(json["shellPath"]?.GetValue<string>()); }
            catch (ShellDiscoveryException error) { throw new EnvDaemonException("shell_unavailable", error.Message); }
            program = shell.Shell; args = shell.CommandArguments(command);
            if (shell.CommandTransport == ShellCommandTransport.Stdin) shellInput = Encoding.UTF8.GetBytes(command);
        }
        else
        {
            if (json["argv"] is not JsonArray argv || argv.Any(value => value is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
                throw new EnvDaemonException("EINVAL", piped ? "process needs argv" : "exec needs command or argv");
            if (argv.Count == 0) throw new EnvDaemonException("spawn_error", "Empty argv: no program to run");
            program = argv[0]!.GetValue<string>(); args = argv.Skip(1).Select(value => value!.GetValue<string>()).ToArray();
        }
        if (program.Length == 0 || program.Contains('\0') || args.Any(arg => arg.Contains('\0')) || cwd.Contains('\0') ||
            environment.Any(pair => pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0')))
            throw new EnvDaemonException("spawn_error", "Invalid process argument or environment");
        program = Resolve(program, cwd, environment);
        double? timeout = json["timeoutMs"]?.GetValue<double>();
        if (timeout is { } milliseconds && (!double.IsFinite(milliseconds) || milliseconds <= 0 || milliseconds > int.MaxValue))
            throw new EnvDaemonException("timeout", "Invalid timeout");
        return new(new(program, args, cwd, environment), shellInput, timeout, json["spill"] as JsonObject, json["window"] as JsonObject);
    }

    private static string Resolve(string program, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        // execvp search/permission behavior is handled by the Unix admission. Windows follows libuv:
        // cwd before PATH, existing extension as given, then .com and .exe (never a shell for batch files).
        if (!OperatingSystem.IsWindows()) return program;
        var extensions = Path.HasExtension(program) ? new[] { "", ".com", ".exe" } : [".com", ".exe"];
        var search = environment.TryGetValue("PATH", out var path) ? path : Environment.GetEnvironmentVariable("PATH") ?? "";
        var directories = program.IndexOfAny(['/', '\\', ':']) >= 0 ? new[] { cwd } : search.Split(';').Prepend(cwd);
        foreach (var directory in directories)
        {
            foreach (var suffix in extensions)
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(directory.Length == 0 ? cwd : directory.Trim('"'), program + suffix), cwd); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (File.Exists(candidate))
                {
                    if (candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || candidate.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                        throw new EnvDaemonException("spawn_error", $"spawn {program} EINVAL");
                    return candidate;
                }
            }
        }
        throw new EnvDaemonException("spawn_error", $"spawn {program} ENOENT");
    }

    private sealed class Child : IAsyncDisposable
    {
        private readonly IAsyncDisposable owner;
        private readonly Func<ValueTask> kill;
        private readonly object gate = new();
        private Task? disposal;
        public int ProcessId { get; }
        public Stream Stdin { get; }
        public Stream Stdout { get; }
        public Stream Stderr { get; }
        public Task<int> Exit { get; }
        private Child(IAsyncDisposable owner, int pid, Stream stdin, Stream stdout, Stream stderr, Task<int> exit, Func<ValueTask> kill)
        { this.owner = owner; ProcessId = pid; Stdin = stdin; Stdout = stdout; Stderr = stderr; Exit = exit; this.kill = kill; }
        public ValueTask KillAsync() => kill();
        public ValueTask DisposeAsync() { lock (gate) return new(disposal ??= owner.DisposeAsync().AsTask()); }
        public static async Task<Child> StartAsync(EnvProcessStart start, CancellationToken token)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var child = await WindowsPipedProcess.StartAsync(start, token).ConfigureAwait(false);
                    return new(child, child.ProcessId, child.StandardInput, child.StandardOutput, child.StandardError, child.Exit, child.KillAsync);
                }
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    var child = await UnixNativeProcess.SpawnPipedAsync([start.Executable, .. start.Arguments], start.WorkingDirectory, start.Environment, token).ConfigureAwait(false);
                    return new(child, child.ProcessId, child.StandardInput, child.StandardOutput, child.StandardError, child.Exit, child.KillAsync);
                }
                throw new EnvDaemonException("not_supported", "Processes are unavailable on this platform");
            }
            catch (Exception error) when (error is not EnvDaemonException and not OperationCanceledException)
            { throw new EnvDaemonException("spawn_error", error.Message); }
        }
    }

    private sealed record Chunk(int Stream, byte[]? Bytes);
    private sealed class Run(EnvDaemonRequest request, Child child, bool piped, Input options)
    {
        private readonly SemaphoreSlim inputGate = new(1, 1);
        private readonly object killGate = new();
        private Task? killTask;
        private bool inputClosed;
        private Task Stop() { lock (killGate) return killTask ??= Task.Run(async () => await child.KillAsync().ConfigureAwait(false)); }
        public async ValueTask InputAsync(ReadOnlyMemory<byte>? bytes, CancellationToken token)
        {
            await inputGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (inputClosed) throw new EnvDaemonException("EBADF", "The program's stdin is closed");
                if (bytes is { } data) { await child.Stdin.WriteAsync(data, token).ConfigureAwait(false); await child.Stdin.FlushAsync(token).ConfigureAwait(false); }
                else { inputClosed = true; await child.Stdin.DisposeAsync().ConfigureAwait(false); }
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ObjectDisposedException)
            { inputClosed = true; throw new EnvDaemonException("EBADF", error.Message); }
            finally { inputGate.Release(); }
        }

        public async Task<EnvDaemonReply> ExecuteAsync()
        {
            using var pumpsStop = new CancellationTokenSource();
            await using var spill = !piped && options.Spill is not null ? new ProcessSpill(options.Spill) : null;
            var window = !piped && options.Window is not null ? new ProcessOutputWindow(options.Window) : null;
            var chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
            async Task Pump(Stream source, int index)
            {
                try
                {
                    var buffer = new byte[16 * 1024];
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer, pumpsStop.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        await chunks.Writer.WriteAsync(new(index, buffer.AsSpan(0, count).ToArray()), pumpsStop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (pumpsStop.IsCancellationRequested) { }
                catch (Exception error) when ((error is IOException or System.ComponentModel.Win32Exception or ObjectDisposedException) &&
                    (child.Exit.IsCompleted || request.CancellationToken.IsCancellationRequested)) { }
                finally { await chunks.Writer.WriteAsync(new(index, null), request.SessionCancellationToken).ConfigureAwait(false); }
            }
            var pumps = Task.WhenAll(Task.Run(() => Pump(child.Stdout, 0)), Task.Run(() => Pump(child.Stderr, 1)));
            var decoders = new[] { new StreamDecoder(), new StreamDecoder() };
            Task delivery = Task.CompletedTask;
            Task? stop = null;
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = request.CancellationToken.Register(() => { canceled.TrySetResult(); _ = Stop(); });
            using var timeout = new CancellationTokenSource();
            if (options.TimeoutMs is { } milliseconds) timeout.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
            var timed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeoutRegistration = timeout.Token.Register(() => { timed.TrySetResult(); _ = Stop(); });
            var ended = 0;
            var observedExit = false;
            var timedOut = false;
            var aborted = false;
            var idleUntil = long.MaxValue;
            Task<Chunk>? read = null;
            Exception? failure = null;
            async Task Send(int stream, string text, JsonObject? skipped = null)
            {
                if (text.Length == 0) return;
                // Frame payloads are bounded even when a caller asks for a very large output window.
                for (var offset = 0; offset < text.Length;)
                {
                    var length = Math.Min(16 * 1024, text.Length - offset);
                    if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
                    var json = new JsonObject { ["kind"] = "output", ["stream"] = stream == 1 ? "stderr" : "stdout" };
                    if (offset == 0 && skipped is not null) json["skipped"] = skipped;
                    await request.EmitEventAsync(json, Encoding.UTF8.GetBytes(text.AsSpan(offset, length).ToString()), request.SessionCancellationToken).ConfigureAwait(false);
                    offset += length;
                }
            }
            async Task Deliver()
            {
                foreach (var item in window!.Take()) await Send(item.Stream, item.Text, item.Skipped).ConfigureAwait(false);
            }
            try
            {
                if (piped)
                {
                    Processes(request)[request.Id] = this;
                    await request.EmitEventAsync(new() { ["kind"] = "started", ["pid"] = child.ProcessId }, cancellationToken: request.SessionCancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Shell command transport ignores a broken input pipe, as Node's child.stdin does. The
                    // independently registered stop closes a stalled writer on abort or timeout.
                    try
                    {
                        if (options.ShellInput is { } data) await InputAsync(data, request.SessionCancellationToken).ConfigureAwait(false);
                        await InputAsync(null, request.SessionCancellationToken).ConfigureAwait(false);
                    }
                    catch (EnvDaemonException error) when (error.Code == "EBADF") { }
                }
                while (ended < 2 || !observedExit)
                {
                    var now = Environment.TickCount64;
                    if (canceled.Task.IsCompleted && stop is null)
                    { aborted = request.IsAbortRequested || !request.IsKillRequested; stop = Stop(); }
                    if (timed.Task.IsCompleted && !timedOut)
                    { timedOut = true; stop ??= Stop(); }
                    if (child.Exit.IsCompleted && !observedExit)
                    { observedExit = true; idleUntil = now + (piped ? 0 : 100); }
                    if (observedExit && now >= idleUntil && stop is null) stop = Stop();
                    if (stop?.IsCompleted == true && observedExit && now >= idleUntil + 100) pumpsStop.Cancel();
                    if (delivery.IsFaulted) await delivery.ConfigureAwait(false);
                    if (window is { IsEmpty: false } && delivery.IsCompleted && now >= window.NextSend)
                        delivery = Deliver();
                    if (ended == 2) { await Task.Delay(20, request.SessionCancellationToken).ConfigureAwait(false); continue; }
                    read ??= chunks.Reader.ReadAsync(request.SessionCancellationToken).AsTask();
                    var delay = Task.Delay(20, request.SessionCancellationToken);
                    var ready = await Task.WhenAny(read, delay).ConfigureAwait(false);
                    if (ready != read) { await delay.ConfigureAwait(false); continue; }
                    var chunk = await read.ConfigureAwait(false); read = null;
                    if (chunk.Bytes is null) { ended++; continue; }
                    if (observedExit) idleUntil = Environment.TickCount64 + (piped ? 0 : 100);
                    if (spill is not null) await spill.PushAsync(chunk.Bytes).ConfigureAwait(false);
                    if (piped) await request.EmitEventAsync(new() { ["kind"] = "output", ["stream"] = chunk.Stream == 1 ? "stderr" : "stdout" }, chunk.Bytes, request.SessionCancellationToken).ConfigureAwait(false);
                    else
                    {
                        var text = decoders[chunk.Stream].Decode(chunk.Bytes);
                        if (window is null) await Send(chunk.Stream, text).ConfigureAwait(false);
                        else window.Push(chunk.Stream, text);
                    }
                }
                if (!piped)
                    for (var stream = 0; stream < 2; stream++)
                    {
                        var text = decoders[stream].Finish();
                        if (window is null) await Send(stream, text).ConfigureAwait(false); else window.Push(stream, text);
                    }
                await delivery.ConfigureAwait(false);
                if (window is { IsEmpty: false }) await Deliver().ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                Processes(request).TryRemove(request.Id, out _);
                stop ??= Stop();
                pumpsStop.Cancel();
                // Drain the bounded queue while joining readers so a last EOF cannot deadlock cleanup.
                while (!pumps.IsCompleted) { while (chunks.Reader.TryRead(out _)) { } await Task.WhenAny(pumps, Task.Delay(10)).ConfigureAwait(false); }
                try { await pumps.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
                try { await stop.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
                try { await delivery.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
            }
            timedOut |= timed.Task.IsCompleted;
            if (timedOut || aborted || request.IsAbortRequested || request.CancellationToken.IsCancellationRequested && !request.IsKillRequested)
            {
                var error = new EnvDaemonException(timedOut ? "timeout" : "aborted", timedOut ? "timeout" : "aborted");
                if (spill?.Path is { } path) error.Details["spillPath"] = path;
                throw error;
            }
            if (failure is not null) throw failure;
            var result = new JsonObject { ["exitCode"] = await child.Exit.ConfigureAwait(false) };
            if (spill?.Path is { } spillPath) result["spillPath"] = spillPath;
            return new(result);
        }
    }
}
