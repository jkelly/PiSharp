// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/daemon/src/pty.rs.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Env.Daemon.Native;

namespace PiSharp.Env.Daemon;

/// <summary>Raw Unix pseudo-terminal operations. Windows deliberately reports ENOTSUP.</summary>
public static class TerminalOperations
{
    private const string StateKey = "PiSharp.Env.Terminals";
    public static IReadOnlyList<IEnvDaemonOperation> Create() =>
        [new Operation("pty"), new Operation("ptyWrite"), new Operation("ptyResize"), new Operation("ptyForeground")];

    private sealed class Operation(string name) : IEnvDaemonOperation
    {
        public string Name => name;
        public ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request) => name == "pty"
            ? RunAsync(request) : ValueTask.FromResult(Control(request, name));
    }

    private static ConcurrentDictionary<uint, TerminalState> Terminals(EnvDaemonRequest request) =>
        (ConcurrentDictionary<uint, TerminalState>)request.State.GetOrAdd(StateKey, _ => new ConcurrentDictionary<uint, TerminalState>());

    private static int Size(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<int>(out var size) && size is >= 1 and <= 4096
            ? size : throw new EnvDaemonException("EINVAL", $"{key} must be between 1 and 4096");

    private static EnvDaemonReply Control(EnvDaemonRequest request, string operation)
    {
        if (request.Json["target"] is not JsonValue target || !target.TryGetValue<uint>(out var id) ||
            !Terminals(request).TryGetValue(id, out var terminal) || terminal.Ended)
            throw new EnvDaemonException("EBADF", "the terminal has ended");
        try
        {
            if (operation == "ptyWrite") terminal.Write(request.Payload);
            else if (operation == "ptyResize") terminal.Child.Resize(Size(request.Json, "cols"), Size(request.Json, "rows"));
            else
            {
                var group = terminal.Child.Foreground();
                return new(new JsonObject { ["foreground"] = group <= 0 || group == terminal.Child.ProcessId ? null :
                    new JsonObject { ["pid"] = group, ["name"] = UnixNativeProcess.ProcessName(group) } });
            }
            return EnvDaemonReply.Empty;
        }
        catch (ObjectDisposedException) { throw new EnvDaemonException("EBADF", "the terminal has ended"); }
        catch (Win32Exception error) { throw new EnvDaemonException("EBADF", error.Message, "pty", operation == "ptyResize" ? "ioctl" : "tcgetpgrp"); }
    }

    private static async ValueTask<EnvDaemonReply> RunAsync(EnvDaemonRequest request)
    {
        var cols = Size(request.Json, "cols"); var rows = Size(request.Json, "rows");
        var cwd = Text(request.Json["cwd"]) ?? throw new EnvDaemonException("EINVAL", "pty needs cwd");
        var argv = Arguments(request.Json);
        if (OperatingSystem.IsWindows()) throw new EnvDaemonException("ENOTSUP", "Terminals are not supported on this system yet");
        if (request.IsAbortRequested || request.SessionCancellationToken.IsCancellationRequested) throw new EnvDaemonException("aborted", "aborted");
        if (!Directory.Exists(cwd)) throw new EnvDaemonException("spawn_error", $"Working directory does not exist: {cwd}");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Json["inheritEnv"] is not JsonValue inherit || !inherit.TryGetValue<bool>(out var inherits) || inherits)
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) env[key] = value;
        if (request.Json["env"] is JsonObject overrides)
            foreach (var pair in overrides) if (Text(pair.Value) is { } value) env[pair.Key] = value;

        string? executable = null;
        if (argv is null)
        {
            var shell = request.Json["env"] is JsonObject supplied ? Text(supplied["SHELL"]) : null;
            shell ??= Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "/bin/sh";
            executable = shell;
            argv = ["-" + Path.GetFileName(shell)];
        }
        UnixNativeProcess child;
        try { child = UnixNativeProcess.SpawnTerminal(argv, cwd, env, cols, rows, request.SessionCancellationToken, executable); }
        catch (OperationCanceledException) { throw new EnvDaemonException("aborted", "aborted"); }
        catch (Exception error) when (error is Win32Exception or ArgumentException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        { throw new EnvDaemonException("spawn_error", error.Message); }
        var terminal = new TerminalState(child, request.SessionCancellationToken);
        request.Own(terminal);
        var terminals = Terminals(request);
        terminals[request.Id] = terminal;
        using var cancel = request.CancellationToken.Register(terminal.Kill);
        Task output = Task.CompletedTask;
        try
        {
            await request.EmitEventAsync(new() { ["kind"] = "started", ["pid"] = child.ProcessId }, cancellationToken: terminal.Token).ConfigureAwait(false);
            output = ReadOutputAsync(request, terminal);
            terminal.Output = output;
            var exitCode = await child.Exit.ConfigureAwait(false);
            // A background job may retain the slave. Drain only until 100 ms of silence after exit.
            terminal.LastOutput = Environment.TickCount64;
            while (!output.IsCompleted)
            {
                var remaining = 100 - (Environment.TickCount64 - Interlocked.Read(ref terminal.LastOutput));
                if (remaining <= 0 && Volatile.Read(ref terminal.OutputPending) == 0) break;
                await Task.WhenAny(output, Task.Delay(TimeSpan.FromMilliseconds(Math.Max(10, remaining)))).ConfigureAwait(false);
            }
            if (output.IsCompleted) await output.ConfigureAwait(false);
            if (request.IsAbortRequested)
                throw new EnvDaemonException("aborted", "aborted");
            return new(new JsonObject { ["exitCode"] = exitCode });
        }
        finally
        {
            terminals.TryRemove(request.Id, out _);
            try { await terminal.DisposeAsync().ConfigureAwait(false); await output.ConfigureAwait(false); }
            finally { request.Release(terminal); }
        }
    }

    private static async Task ReadOutputAsync(EnvDaemonRequest request, TerminalState terminal)
    {
        var bytes = new byte[64 * 1024];
        try
        {
            while (await terminal.Child.StandardOutput.ReadAsync(bytes, terminal.Token).ConfigureAwait(false) is var count && count > 0)
            {
                Volatile.Write(ref terminal.OutputPending, 1);
                try { await request.EmitEventAsync(new() { ["kind"] = "output" }, bytes.AsMemory(0, count), terminal.Token).ConfigureAwait(false); }
                finally
                {
                    Interlocked.Exchange(ref terminal.LastOutput, Environment.TickCount64);
                    Volatile.Write(ref terminal.OutputPending, 0);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException or Win32Exception) { }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string[]? Arguments(JsonObject json)
    {
        if (!json.ContainsKey("argv")) return null;
        if (json["argv"] is not JsonArray array || array.Count == 0) throw new EnvDaemonException("EINVAL", "argv must be a non-empty array of strings");
        var argv = array.Select(node => Text(node) ?? throw new EnvDaemonException("EINVAL", "argv must be an array of strings")).ToArray();
        if (argv[0].Length == 0 || argv.Any(value => value.Contains('\0'))) throw new EnvDaemonException("EINVAL", "argv contains an invalid argument");
        return argv;
    }

    private sealed class TerminalState : IAsyncDisposable
    {
        private readonly Channel<byte[]> input = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        private readonly CancellationTokenSource stop;
        private readonly Task writer;
        private readonly object disposeGate = new();
        private Task? disposal;
        private int ended;
        public readonly UnixNativeProcess Child;
        public Task Output = Task.CompletedTask;
        public long LastOutput;
        public int OutputPending;
        public CancellationToken Token => stop.Token;
        public bool Ended => Volatile.Read(ref ended) != 0;
        public TerminalState(UnixNativeProcess child, CancellationToken session)
        { Child = child; stop = CancellationTokenSource.CreateLinkedTokenSource(session); writer = WriteInputAsync(); }
        public void Write(ReadOnlyMemory<byte> bytes)
        {
            if (Ended || !input.Writer.TryWrite(bytes.ToArray())) throw new EnvDaemonException("EBADF", "the terminal has ended");
        }
        public void Kill() { try { Child.KillAsync().GetAwaiter().GetResult(); } catch (Win32Exception) { } }
        private async Task WriteInputAsync()
        {
            try
            {
                await foreach (var bytes in input.Reader.ReadAllAsync(Token).ConfigureAwait(false))
                    await Child.StandardInput.WriteAsync(bytes, Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException or Win32Exception) { input.Writer.TryComplete(); }
        }
        public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= DisposeCoreAsync()); }
        private async Task DisposeCoreAsync()
        {
            Interlocked.Exchange(ref ended, 1); input.Writer.TryComplete(); stop.Cancel();
            await Child.DisposeAsync().ConfigureAwait(false);
            await writer.ConfigureAwait(false);
            await Output.ConfigureAwait(false);
            while (input.Reader.TryRead(out _)) { }
            stop.Dispose();
        }
    }
}
