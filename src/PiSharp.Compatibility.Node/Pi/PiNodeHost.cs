using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PiSharp.Compatibility.Node.Pi;

/// <summary>How to start the Node extension host: the user's Node executable, the host script (<c>pi-host/host.mjs</c>), the working
/// directory extensions see as <c>process.cwd()</c>, and the environment (null inherits PiSharp's, as upstream extensions run in Pi's
/// own process).</summary>
public sealed record PiNodeHostLaunch(string NodeExecutable, string HostScript, string WorkingDirectory)
{
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    /// <summary>Lines the host and its extensions write to standard error (extension console output included).</summary>
    public Action<string>? StandardError { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The PiSharp side of the extension protocol: requests and notifications the Node host sends. Synchronous requests come
/// from extension code blocked in an upstream synchronous API (getters such as <c>ctx.sessionManager.getEntries()</c>); their handler
/// must answer without calling back into Node.</summary>
public interface IPiNodeHostPeer
{
    ValueTask<JsonNode?> RequestAsync(PiNodeHostRequest request, CancellationToken cancellationToken);
    ValueTask NotifyAsync(string method, JsonElement parameters);
}

/// <summary>One request from the Node host. <see cref="IsUndefined"/> results are sent without a <c>result</c> member.</summary>
public sealed record PiNodeHostRequest(string Method, JsonElement Parameters, bool Synchronous, Func<JsonNode?, ValueTask> ReportProgress);

/// <summary>A request the Node host answered with an error: the JavaScript error's message and stack.</summary>
public sealed class PiNodeHostException(string message, string? remoteStack) : Exception(message)
{
    public string? RemoteStack { get; } = remoteStack;
}

/// <summary>The Node extension host process. It is isolated from PiSharp: the only channel is the line protocol on its standard
/// input and output, every frame is size-bounded, and disposal (or a protocol fault) ends the process tree.</summary>
public sealed class PiNodeHost : IAsyncDisposable
{
    public const int MaximumFrameBytes = 256 * 1024 * 1024;
    /// <summary>A sentinel result: the JavaScript value <c>undefined</c> (the response has no <c>result</c> member).</summary>
    public static readonly JsonNode Undefined = JsonValue.Create("\u0000pisharp-undefined")!;

    private readonly Process _process;
    private readonly IPiNodeHostPeer _peer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _incoming = new();
    private readonly Channel<Func<Task>> _ordered = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closing = new();
    private long _nextId;
    private int _disposed;

    private sealed record Pending(TaskCompletionSource<JsonElement?> Result, Action<JsonElement>? Progress);

    private PiNodeHost(Process process, IPiNodeHostPeer peer) { _process = process; _peer = peer; }

    public int ProcessId => _process.Id;
    /// <summary>Completes when the Node process has exited.</summary>
    public Task Exited => _exited.Task;
    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>The Node executable: <c>PISHARP_NODE</c>, else <c>node</c> on PATH (with <c>.exe</c>/<c>.cmd</c> on Windows).</summary>
    public static string? FindNode(Func<string, string?> environment)
    {
        if (environment("PISHARP_NODE") is { Length: > 0 } configured) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node.cmd" } : ["node"];
        foreach (var directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), name); } catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>The bundled host script next to the PiSharp binaries, or <c>PISHARP_NODE_BRIDGE</c> (a directory holding host.mjs).</summary>
    public static string? FindHostScript(Func<string, string?> environment)
    {
        if (environment("PISHARP_NODE_BRIDGE") is { Length: > 0 } configured)
            return File.Exists(Path.Combine(configured, "host.mjs")) ? Path.GetFullPath(Path.Combine(configured, "host.mjs")) : null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "node-bridge", "pi-host", "host.mjs");
        return File.Exists(bundled) ? bundled : null;
    }

    public static async Task<PiNodeHost> StartAsync(PiNodeHostLaunch launch, IPiNodeHostPeer peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch); ArgumentNullException.ThrowIfNull(peer);
        var info = new ProcessStartInfo(launch.NodeExecutable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        info.ArgumentList.Add("--disable-warning=ExperimentalWarning");
        info.ArgumentList.Add(launch.HostScript);
        if (launch.Environment is { } environment)
        {
            info.Environment.Clear();
            foreach (var (name, value) in environment) if (value is not null) info.Environment[name] = value;
        }
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("The Node extension host did not start.");
        var host = new PiNodeHost(process, peer);
        process.Exited += (_, _) => host._exited.TrySetResult();
        if (process.HasExited) host._exited.TrySetResult();
        _ = host.ReadErrorsAsync(launch.StandardError);
        _ = host.ReadAsync();
        _ = host.RunOrderedAsync();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(launch.StartupTimeout);
        try { await host._ready.Task.WaitAsync(startup.Token).ConfigureAwait(false); }
        catch (Exception error)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException("The Node extension host did not report ready" + (host._exited.Task.IsCompleted ? $" (exit code {SafeExitCode(process)})." : "."), error);
        }
        return host;
    }

    private static int? SafeExitCode(Process process) { try { return process.ExitCode; } catch (InvalidOperationException) { return null; } }

    /// <summary>Sends a request; the result is null for <c>undefined</c>. A JavaScript error becomes <see cref="PiNodeHostException"/>.</summary>
    public async Task<JsonElement?> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default,
        Action<JsonElement>? onProgress = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), onProgress);
        _pending[id] = pending;
        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = WriteAsync(new JsonObject { ["type"] = "cancel", ["id"] = id }))
            : default;
        try
        {
            await WriteAsync(new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = method, ["params"] = ToNode(parameters) }).ConfigureAwait(false);
            return await pending.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>A notification for the host (no response).</summary>
    public Task NotifyAsync(string method, object? parameters) =>
        WriteAsync(new JsonObject { ["type"] = "notify", ["method"] = method, ["params"] = ToNode(parameters) });

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        PiSharp.Contracts.JsonData data => JsonNode.Parse(data.ToString()),
        _ => JsonSerializer.SerializeToNode(value)
    };

    private async Task WriteAsync(JsonObject frame)
    {
        var text = frame.ToJsonString() + "\n";
        if (Encoding.UTF8.GetByteCount(text) > MaximumFrameBytes) throw new InvalidOperationException("Node extension host frame limit exceeded.");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_exited.Task.IsCompleted) throw new InvalidOperationException("The Node extension host has exited.");
            await _process.StandardInput.WriteAsync(text.AsMemory()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException error) { throw new InvalidOperationException("The Node extension host connection is closed.", error); }
        finally { _writeGate.Release(); }
    }

    private async Task ReadErrorsAsync(Action<string>? sink)
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) sink?.Invoke(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task ReadAsync()
    {
        try
        {
            var reader = _process.StandardOutput;
            var builder = new StringBuilder();
            var buffer = new char[65536];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0) break;
                var start = 0;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != '\n') continue;
                    builder.Append(buffer, start, index - start);
                    start = index + 1;
                    var line = builder.ToString(); builder.Clear();
                    if (line.EndsWith('\r')) line = line[..^1];
                    if (line.Length > 0) Receive(line);
                }
                builder.Append(buffer, start, count - start);
                if (builder.Length > MaximumFrameBytes) throw new InvalidDataException("Node extension host frame limit exceeded.");
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or InvalidDataException or JsonException) { }
        finally
        {
            _exited.TrySetResult();
            foreach (var pending in _pending.Values) pending.Result.TrySetException(new PiNodeHostException("The Node extension host exited.", null));
            _ready.TrySetException(new PiNodeHostException("The Node extension host exited before it was ready.", null));
            _ordered.Writer.TryComplete();
        }
    }

    private void Receive(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 });
        var frame = document.RootElement.Clone();
        var type = frame.GetProperty("type").GetString();
        switch (type)
        {
            case "response":
            {
                if (!_pending.TryGetValue(frame.GetProperty("id").GetInt64(), out var pending)) return;
                if (frame.TryGetProperty("error", out var error))
                    pending.Result.TrySetException(new PiNodeHostException(error.TryGetProperty("message", out var text) ? text.GetString() ?? "Error" : "Error",
                        error.TryGetProperty("stack", out var stack) ? stack.GetString() : null));
                else pending.Result.TrySetResult(frame.TryGetProperty("result", out var result) ? result : null);
                return;
            }
            case "progress":
                if (_pending.TryGetValue(frame.GetProperty("id").GetInt64(), out var target) && frame.TryGetProperty("value", out var value)) target.Progress?.Invoke(value);
                return;
            case "cancel":
                if (_incoming.TryGetValue(frame.GetProperty("id").GetInt64(), out var source)) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
                return;
            case "notify":
            {
                var method = frame.GetProperty("method").GetString()!;
                var parameters = frame.TryGetProperty("params", out var p) ? p : default;
                if (method == "ready") { _ready.TrySetResult(parameters); return; }
                _ordered.Writer.TryWrite(async () =>
                {
                    try { await _peer.NotifyAsync(method, parameters).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning("Node host notification {0} failed: {1}", method, error.Message); }
                });
                return;
            }
            case "request":
            {
                var id = frame.GetProperty("id").GetInt64();
                var method = frame.GetProperty("method").GetString()!;
                var parameters = frame.TryGetProperty("params", out var p) ? p : default;
                var synchronous = frame.TryGetProperty("sync", out var s) && s.ValueKind == JsonValueKind.True;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
                _incoming[id] = cancellation;
                async Task Run()
                {
                    JsonObject response;
                    try
                    {
                        var request = new PiNodeHostRequest(method, parameters, synchronous,
                            progress => new ValueTask(WriteAsync(new JsonObject { ["type"] = "progress", ["id"] = id, ["value"] = progress?.DeepClone() })));
                        var result = await _peer.RequestAsync(request, cancellation.Token).ConfigureAwait(false);
                        response = new JsonObject { ["type"] = "response", ["id"] = id };
                        if (!ReferenceEquals(result, Undefined)) response["result"] = result;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        response = new JsonObject { ["type"] = "response", ["id"] = id, ["error"] = new JsonObject { ["message"] = error.Message } };
                    }
                    finally { _incoming.TryRemove(id, out _); cancellation.Dispose(); }
                    try { await WriteAsync(response).ConfigureAwait(false); } catch (InvalidOperationException) { }
                }
                // Synchronous requests (the extension thread is blocked) and actions keep their order; asynchronous requests such as
                // dialogs start in order and complete on their own.
                if (synchronous) _ordered.Writer.TryWrite(Run);
                else _ordered.Writer.TryWrite(() => { _ = Run(); return Task.CompletedTask; });
                return;
            }
        }
    }

    private async Task RunOrderedAsync()
    {
        await foreach (var work in _ordered.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning("Node host work failed: {0}", error.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _closing.Cancel(); } catch (ObjectDisposedException) { }
        if (!_exited.Task.IsCompleted)
        {
            try
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await RequestAsync("shutdown", new { }, grace.Token).ConfigureAwait(false);
                await _exited.Task.WaitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or PiNodeHostException or TimeoutException) { }
        }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        try { _process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        _process.Dispose();
        _closing.Dispose();
    }
}
