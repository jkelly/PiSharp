using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Env.Daemon;
using PiSharp.Env.Protocol;

var root = Path.Combine(Path.GetTempPath(), "pisharp-env-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    await Run("framed filesystem and descriptor identity", FileOperations);
    await Run("malformed JSON stays synchronized", MalformedJson);
    await Run("hello validation and handle limits", Limits);
    await Run("cancel registered before worker starts", Cancel);
    await Run("disconnect releases resources and kept handles", Disconnect);
    await Run("partial frame terminates session", PartialFrame);
    await Run("startup token, heartbeat and silence shutdown", Liveness);
    await Run("pipelined chunks retain arrival order", ChunkOrder);
    await Run("bounded disconnect and late ownership cleanup", AbandonedRequest);
    await Run("raw FIFO daemon exits after EOF and SIGTERM", FifoShutdown);
    Console.WriteLine($"PASS: {passed} daemon integration tests");
    return 0;
}
finally { Directory.Delete(root, recursive: true); }

async Task Run(string name, Func<Task> test) { await test(); passed++; Console.WriteLine("PASS " + name); }
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static long Num(JsonObject value, string name) => value[name]!.GetValue<long>();

async Task FileOperations()
{
    await using var wire = await Wire.Open(new() { WorkingDirectory = root });
    var hello = await wire.Call("hello", new() { ["protocol"] = 1 });
    var daemonAssembly = typeof(EnvDaemonOptions).Assembly;
    var builtVersion = daemonAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0]
        ?? daemonAssembly.GetName().Version!.ToString();
    Check(hello.Json["version"]!.GetValue<string>() == builtVersion && !builtVersion.Contains('+'), "hello reports the built daemon product version without source metadata");
    Check(Num(hello.Json, "protocol") == 1, "product version does not change protocol 1");
    Check(hello.Json["features"]!.AsArray().Any(x => x!.GetValue<string>() == "listFiles"), "listFiles feature");
    var path = Path.Combine(root, "nested", "file.txt");
    var first = await wire.Call("write", new() { ["path"] = path, ["keep"] = true }, "alpha\n"u8.ToArray());
    var writeHandle = Num(first.Json, "handle");
    await wire.Call("writeChunk", new() { ["handle"] = writeHandle }, "beta\nlast"u8.ToArray());
    await wire.Call("close", new() { ["handle"] = writeHandle });
    var opened = await wire.Call("open", new() { ["path"] = path });
    var handle = Num(opened.Json, "handle");
    Check(opened.Json["info"]!["size"]!.GetValue<long>() == 15, "size");
    var read = await wire.Call("pread", new() { ["handle"] = handle, ["offset"] = 6, ["length"] = 4 });
    Check(Encoding.UTF8.GetString(read.Payload.Span) == "beta", "positional payload");
    var scan = await wire.Call("scanLines", new() { ["handle"] = handle, ["startLine"] = 1, ["endLine"] = 2 });
    Check(Num(scan.Json, "newlines") == 2 && Num(scan.Json, "start") == 6 && Num(scan.Json, "end") == 10, "scan ranges");
    var moved = path + ".moved";
    await wire.Call("rename", new() { ["path"] = path, ["to"] = moved });
    await wire.Call("write", new() { ["path"] = path }, "replacement"u8.ToArray());
    var stat = await wire.Call("fstat", new() { ["handle"] = handle });
    Check(Num(stat.Json, "size") == 15 && stat.Json["ino"]!.ToJsonString() == opened.Json["info"]!["ino"]!.ToJsonString(), "fstat follows descriptor");
    await wire.Call("close", new() { ["handle"] = handle });
    Check((await wire.Call("pread", new() { ["handle"] = handle, ["length"] = 1 }, allowError: true)).Json["code"]!.GetValue<string>() == "EBADF", "closed handle");
    var dir = await wire.Call("opendir", new() { ["path"] = Path.GetDirectoryName(path) });
    var page = await wire.Call("readdir", new() { ["handle"] = Num(dir.Json, "handle"), ["max"] = 1 });
    Check(page.Json["entries"]!.AsArray().Count == 1, "directory page");
    await wire.Call("close", new() { ["handle"] = Num(dir.Json, "handle") });
    var list = await wire.Call("listFiles", new() { ["path"] = root, ["gitignore"] = false, ["maxEntries"] = 1 });
    Check(Num(list.Json, "count") == 1 && list.Json["truncated"]!.GetValue<bool>(), "bounded listing");
    var real = await wire.Call("realpath", new() { ["path"] = path });
    Check(real.Json["path"]!.GetValue<string>() == path, "realpath");
    await wire.Call("truncate", new() { ["path"] = path, ["size"] = 2 });
    await wire.Call("fsync", new() { ["path"] = path });
    var tmp = await wire.Call("mkdtemp", new() { ["path"] = Path.Combine(root, "temp-") });
    Check(Directory.Exists(tmp.Json["path"]!.GetValue<string>()), "mkdtemp prefix");
    var rmError = await wire.Call("rm", new() { ["path"] = tmp.Json["path"]!.GetValue<string>() }, allowError: true);
    Check(rmError.Json["code"]!.GetValue<string>() == "ERR_FS_EISDIR", "rm directory error");
    await wire.Call("rm", new() { ["path"] = tmp.Json["path"]!.GetValue<string>(), ["recursive"] = true });
}

async Task MalformedJson()
{
    await using var wire = await Wire.Open(new() { WorkingDirectory = root });
    await wire.Call("hello", new() { ["protocol"] = 1 });
    var raw = new byte[14]; BinaryPrimitives.WriteUInt32BigEndian(raw, 10); raw[4] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(5), 123); BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(9), 1); raw[13] = (byte)'{';
    await wire.Client.WriteAsync(raw); await wire.Client.FlushAsync();
    var error = await wire.Read();
    Check(error.Id == 123 && error.Json["code"]!.GetValue<string>() == "EINVAL", "JSON error response");
    await wire.Call("lstat", new() { ["path"] = root });
}

async Task Limits()
{
    await using var wire = await Wire.Open(new() { WorkingDirectory = root, MaxHandles = 1, Version = "explicit-test-version" });
    var error = await wire.Call("lstat", new() { ["path"] = root }, allowError: true);
    Check(error.Type == FrameType.Error, "hello required");
    var hello = await wire.Call("hello", new() { ["protocol"] = 1 });
    Check(hello.Json["version"]!.GetValue<string>() == "explicit-test-version", "explicit daemon version override is retained");
    var handle = await wire.Call("opendir", new() { ["path"] = root });
    error = await wire.Call("opendir", new() { ["path"] = root }, allowError: true);
    Check(error.Json["code"]!.GetValue<string>() == "EMFILE", "handle limit");
    await wire.Call("close", new() { ["handle"] = Num(handle.Json, "handle") });
    await wire.Call("opendir", new() { ["path"] = root });
    error = await wire.Call("pread", new() { ["handle"] = -1, ["length"] = 2 }, allowError: true);
    Check(error.Json["code"]!.GetValue<string>() == "EINVAL", "negative handle");
}

async Task Cancel()
{
    var operation = new BlockingOperation();
    await using var wire = await Wire.Open(new() { Operations = [operation] });
    await wire.Call("hello", new() { ["protocol"] = 1 });
    var id = wire.NextId();
    await FrameCodec.WriteAsync(wire.Client, new(FrameType.Request, id, new() { ["op"] = "block" }));
    await FrameCodec.WriteAsync(wire.Client, new(FrameType.Cancel, id, new()));
    var result = await wire.Read();
    Check(result.Id == id && result.Type == FrameType.Error && result.Json["code"]!.GetValue<string>() == "aborted", "immediate cancellation");
}

async Task Disconnect()
{
    var operation = new BlockingOperation();
    var wire = await Wire.Open(new() { Operations = [operation], WorkingDirectory = root });
    await wire.Call("hello", new() { ["protocol"] = 1 });
    var path = Path.Combine(root, "kept");
    await wire.Call("write", new() { ["path"] = path, ["keep"] = true }, "kept"u8.ToArray());
    await FrameCodec.WriteAsync(wire.Client, new(FrameType.Request, wire.NextId(), new() { ["op"] = "block" }));
    await operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await wire.DisposeAsync();
    Check(operation.Resource.Disposed, "owned resource disposed");
    using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
}

async Task PartialFrame()
{
    var wire = await Wire.Open(new());
    await wire.Client.WriteAsync(new byte[] { 0, 0, 0 });
    await wire.Client.DisposeAsync();
    try { await wire.SessionTask.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("partial frame accepted"); }
    catch (EndOfStreamException) { }
    await wire.DisposeAsync();
}

async Task Liveness()
{
    const string token = "00112233445566778899aabbccddeeff";
    await using var wire = await Wire.Open(new() { Token = token, PingInterval = TimeSpan.FromMilliseconds(50), IdleTimeout = TimeSpan.FromMilliseconds(500), ShutdownDrainTimeout = TimeSpan.FromMilliseconds(200) }, ignoreReadCancellation: true);
    var line = new byte[40]; await wire.Client.ReadExactlyAsync(line);
    Check(Encoding.ASCII.GetString(line) == "PI-ENV " + token + "\n", "startup sync token");
    await wire.Call("hello", new() { ["protocol"] = 1 });
    var ping = await FrameCodec.ReadAsync(wire.Client).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    Check(ping?.Type == FrameType.Ping && ping.Id == 0, "heartbeat");
    await wire.SessionTask.WaitAsync(TimeSpan.FromSeconds(3));
}

async Task ChunkOrder()
{
    await using var wire = await Wire.Open(new() { WorkingDirectory = root });
    await wire.Call("hello", new() { ["protocol"] = 1 });
    var path = Path.Combine(root, "ordered");
    var opened = await wire.Call("write", new() { ["path"] = path, ["keep"] = true });
    var handle = Num(opened.Json, "handle");
    var ids = new List<uint>();
    for (var i = 0; i < 20; i++)
    {
        var id = wire.NextId(); ids.Add(id);
        await FrameCodec.WriteAsync(wire.Client, new(FrameType.Request, id, new() { ["op"] = "writeChunk", ["handle"] = handle }, Encoding.ASCII.GetBytes(i.ToString("D2"))));
    }
    foreach (var id in ids) { var frame = await wire.Read(); Check(frame.Id == id && frame.Type == FrameType.Result, "ordered chunk reply"); }
    await wire.Call("close", new() { ["handle"] = handle });
    Check(await File.ReadAllTextAsync(path) == string.Concat(Enumerable.Range(0, 20).Select(i => i.ToString("D2"))), "ordered chunk bytes");
}

async Task AbandonedRequest()
{
    var operation = new UninterruptibleOperation();
    var wire = await Wire.Open(new() { Operations = [operation], ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100) });
    await wire.Call("hello", new() { ["protocol"] = 1 });
    await FrameCodec.WriteAsync(wire.Client, new(FrameType.Request, wire.NextId(), new() { ["op"] = operation.Name }));
    await operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await wire.DisposeAsync();
    Check(watch.Elapsed < TimeSpan.FromSeconds(2), "blocked worker prevented disconnect");
    Check(!operation.Gate.Task.IsCompleted, "worker was released prematurely");
    operation.Gate.SetResult();
    await operation.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(operation.Resource.Disposed, "late owned resource was leaked");
}

async Task FifoShutdown()
{
    if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Unix raw FIFO EOF/SIGTERM regression on Windows"); return; }
    var fifo = Path.Combine(root, "raw-fifo");
    Check(UnixFiles.MakeFifo(fifo, 384) == 0, "mkfifo failed");
    foreach (var signal in new[] { false, true })
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
        var start = new System.Diagnostics.ProcessStartInfo(dotnet)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(EnvDaemonSession).Assembly.Location);
        start.ArgumentList.Add("serve"); start.ArgumentList.Add("--token"); start.ArgumentList.Add("00112233445566778899aabbccddeeff");
        using var child = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start daemon");
        try
        {
            var header = new byte[40]; await child.StandardOutput.BaseStream.ReadExactlyAsync(header);
            await FrameCodec.WriteAsync(child.StandardInput.BaseStream, new(FrameType.Request, 1, new() { ["op"] = "hello", ["protocol"] = 1 }));
            Check((await FrameCodec.ReadAsync(child.StandardOutput.BaseStream))?.Type == FrameType.Result, "FIFO child handshake");
            await FrameCodec.WriteAsync(child.StandardInput.BaseStream, new(FrameType.Request, 2, new() { ["op"] = "open", ["path"] = fifo, ["mode"] = "read" }));
            await FrameCodec.WriteAsync(child.StandardInput.BaseStream, new(FrameType.Request, 3, new() { ["op"] = "lstat", ["path"] = fifo }));
            ProtocolFrame? response;
            do { response = await FrameCodec.ReadAsync(child.StandardOutput.BaseStream); } while (response?.Type == FrameType.Ping);
            Check(response?.Id == 3, "raw FIFO open should remain blocked without a writer");
            if (signal) Check(UnixFiles.Kill(child.Id, 15) == 0, "SIGTERM failed");
            else child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));
            Check(child.ExitCode == 0, "FIFO daemon shutdown failed");
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }
}

sealed class UninterruptibleOperation : IEnvDaemonOperation
{
    public string Name => "uninterruptible";
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TestResource Resource { get; } = new();
    public async ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request)
    {
        Started.TrySetResult(); await Gate.Task;
        request.Own(Resource); Finished.TrySetResult(); return EnvDaemonReply.Empty;
    }
}
static class UnixFiles
{
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    internal static extern int MakeFifo([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static extern int Kill(int pid, int signal);
}

sealed class BlockingOperation : IEnvDaemonOperation
{
    public string Name => "block";
    public TestResource Resource { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request)
    {
        request.Own(Resource); Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, request.CancellationToken);
        return EnvDaemonReply.Empty;
    }
}
sealed class TestResource : IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
sealed class Wire : IAsyncDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly EnvDaemonSession session;
    private readonly string? pipeDirectory;
    private uint id;
    public NamedPipeClientStream Client { get; }
    public Task SessionTask { get; }
    private Wire(NamedPipeServerStream server, NamedPipeClientStream client, EnvDaemonSession session, string? pipeDirectory)
    { this.server = server; Client = client; this.session = session; this.pipeDirectory = pipeDirectory; SessionTask = Task.Run(() => session.RunAsync()); }
    public static async Task<Wire> Open(EnvDaemonOptions options, bool ignoreReadCancellation = false)
    {
        var leaf = "pse-" + Guid.NewGuid().ToString("N");
        // .NET supports absolute Unix pipe paths; avoid a long macOS TMPDIR/CoreFxPipe_ prefix.
        var directory = OperatingSystem.IsWindows() ? null : Path.Combine("/tmp", leaf);
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(directory!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var name = directory is null ? leaf : Path.Combine(directory, "wire");
        NamedPipeServerStream? server = null; NamedPipeClientStream? client = null;
        try
        {
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var connect = server.WaitForConnectionAsync();
            client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(); await connect;
            return new(server, client, new EnvDaemonSession(ignoreReadCancellation ? new IgnoreReadCancellation(server) : server, server, options), directory);
        }
        catch
        {
            client?.Dispose(); server?.Dispose();
            if (directory is not null) Directory.Delete(directory);
            throw;
        }
    }
    public uint NextId() => ++id;
    public async Task<ProtocolFrame> Read()
    {
        while (true)
        {
            var frame = await FrameCodec.ReadAsync(Client).AsTask().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new Exception("Unexpected EOF");
            if (frame.Type != FrameType.Ping) return frame;
        }
    }
    public async Task<ProtocolFrame> Call(string op, JsonObject json, byte[]? payload = null, bool allowError = false)
    {
        var requestId = NextId(); json["op"] = op;
        await FrameCodec.WriteAsync(Client, new(FrameType.Request, requestId, json, payload ?? []));
        var frame = await Read();
        if (frame.Id != requestId || frame.Type == FrameType.Error && !allowError) throw new Exception($"{op}: {frame.Json}");
        return frame;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
            try { await SessionTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch (IOException) { }
        }
        finally
        {
            try { await session.DisposeAsync(); }
            finally
            {
                await server.DisposeAsync();
                if (pipeDirectory is not null) Directory.Delete(pipeDirectory);
            }
        }
    }
}
sealed class IgnoreReadCancellation(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, CancellationToken.None);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
