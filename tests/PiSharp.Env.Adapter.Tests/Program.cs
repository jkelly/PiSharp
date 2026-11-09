using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Env;
using PiSharp.Env.Protocol;

if (args.Contains("--fixture")) { await Fixture.Run(args); return; }
var context = DurableContext.Background;
var linux = new RemoteInfo { Protocol = 1, Version = "1.5.0", Os = "linux", Arch = "x86_64", Home = "/home/test", Tmpdir = "/tmp", Cwd = "/daemon", Pid = 1, Separator = "/" };
var windows = linux with { Os = "windows", Cwd = @"C:\daemon", Home = @"C:\Users\test", Separator = "\\", DriveCwds = new Dictionary<string, string> { ["D:"] = @"D:\work" } };
Equal("/home/test/a", RemotePaths.Resolve(linux, "/workspace", "~/a"));
Equal("/a c", RemotePaths.Resolve(linux, "/workspace", "file:///a%20c"));
Equal("/x/b", RemotePaths.Resolve(linux, "/x/a", "../b"));
Equal("/x/a\\b", RemotePaths.Resolve(linux, "/x", "a\\b"));
Equal(@"D:\work\file", RemotePaths.Resolve(windows, @"C:\project", "D:file"));
Equal(@"C:\file", RemotePaths.Resolve(windows, @"C:\project", @"\file"));
Equal(@"\\host\share\file", RemotePaths.Resolve(windows, @"C:\project", "file://host/share/file"));
Equal(@"C:\a\b", RemotePaths.Resolve(windows, @"C:\project", "file:///C:/a/b"));
Equal(@"C:\a b\é", RemotePaths.Resolve(windows, @"C:\project", "file:///C:/a%20b/%C3%A9"));
Equal(@"C:\a\b", RemotePaths.Resolve(windows, @"C:\project", "file://localhost/C:/a/b"));
Equal("/C:/a/b", RemotePaths.Resolve(linux, "/workspace", "file:///C:/a/b"));
Equal("b", RemotePaths.Join(linux, ["a", "..", "b"]));
Equal("b/", RemotePaths.Join(linux, ["a", "..", "b/"]));
Equal(@"C:\project\file:\a", RemotePaths.Resolve(windows, @"C:\project", "file:///a"));

var starts = 0;
var executable = Environment.ProcessPath!;
var command = new List<string> { executable };
if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) command.Add(Assembly.GetExecutingAssembly().Location);
command.Add("--fixture");
await using var connection = new Connection(new ConnectionOptions
{
    CommandFactory = _ => { Interlocked.Increment(ref starts); return Task.FromResult<IReadOnlyList<string>>(command); },
    PingInterval = TimeSpan.FromSeconds(2), SilenceLimit = TimeSpan.FromSeconds(10), StartupTimeout = TimeSpan.FromSeconds(15),
});
var env = new RemoteExecutionEnv(new() { Connection = connection, Id = "test", Cwd = "/workspace" });
Equal("/workspace/a", (await env.AbsolutePathAsync("a", context)).GetOrThrow());
var content = Enumerable.Range(0, 1100000).Select(i => (byte)(i % 251)).ToArray();
Ok((await env.WriteFileAsync("large", content, context)).IsOk, "chunked write");
var wholeFile = (await env.ReadBinaryFileAsync("large", context)).GetOrThrow();
Ok(content.AsSpan().SequenceEqual(wholeFile), "whole-file short-read continuation");
var binary = (await env.OpenBinaryReaderAsync("large", null, context)).GetOrThrow();
var read = (await binary.ReadAsync(3, 800000, context)).GetOrThrow();
Ok(content.AsSpan(3, 800000).SequenceEqual(read), "short positional reads continued correctly");
Equal(1100000L, (await binary.InfoAsync(context)).GetOrThrow().Size);
var scan = (await binary.ScanLinesAsync(0, 1, context)).GetOrThrow();
Equal(7L, scan.End);
await binary.CloseAsync(context);
Equal(FileErrorCode.Invalid, (await binary.ReadAsync(0, 0, context)).Error!.Code);

var lineReader = (await env.OpenTextLineReaderAsync("lines", context)).GetOrThrow();
Equal(new TextLine("α", true), (await lineReader.ReadLineAsync(context)).GetOrThrow());
Equal(new TextLine("last", false), (await lineReader.ReadLineAsync(context)).GetOrThrow());
Ok((await lineReader.ReadLineAsync(context)).GetOrThrow() is null, "line EOF");
await lineReader.CloseAsync(context);
var slowLines = (await env.OpenTextLineReaderAsync("slow-lines", context)).GetOrThrow();
using (var cancellation = new CancellationTokenSource(30))
    Equal(FileErrorCode.Aborted, (await slowLines.ReadLineAsync(context.WithCancellation(cancellation.Token))).Error!.Code);
Equal(new TextLine("α", true), (await slowLines.ReadLineAsync(context)).GetOrThrow());
await slowLines.CloseAsync(context);
var streamLines = (await env.OpenTextLineReaderAsync("slow-stream-lines", context)).GetOrThrow();
using (var cancellation = new CancellationTokenSource(30))
    Equal(FileErrorCode.Aborted, (await streamLines.ReadLineAsync(context.WithCancellation(cancellation.Token))).Error!.Code);
Equal(new TextLine("one", true), (await streamLines.ReadLineAsync(context)).GetOrThrow());
Equal(new TextLine("two", false), (await streamLines.ReadLineAsync(context)).GetOrThrow());
await streamLines.CloseAsync(context);

var listing = (await env.ListFilesAsync(".", null, context)).GetOrThrow();
Equal(new FileListEntry("a.txt", FileKind.File), listing.Entries[0]);
Equal(FileErrorCode.Invalid, (await env.ListFilesAsync(".", new() { MaxEntries = 0.5 }, context)).Error!.Code);
await using (var legacy = new Connection(new() { Command = [.. command, "--legacy"] }))
{
    var legacyEnv = new RemoteExecutionEnv(new() { Connection = legacy, Id = "legacy", Cwd = "/workspace" });
    Equal(FileErrorCode.NotSupported, (await legacyEnv.ListFilesAsync(".", null, context)).Error!.Code);
}
Equal(FileErrorCode.NotFound, (await env.FileInfoAsync("missing", context)).Error!.Code);
Equal(FileErrorCode.NotFound, (await env.ListDirAsync(".", context)).Error!.Code);
Ok(!(await env.ExistsAsync("missing", context)).GetOrThrow(), "missing does not exist");
var directory = (await env.OpenDirReaderAsync(".", context)).GetOrThrow();
var page = (await directory.NextAsync(2, context)).GetOrThrow();
Equal(1, page.Entries.Length); Ok(page.Done, "directory skipped missing/other");
await directory.CloseAsync(context);

ShellOutputInfo? outputInfo = null;
var executed = await env.ExecAsync("ok", new() { OnOutput = (text, _, info) => { Equal("hello", text); outputInfo = info; } }, context);
Equal(7, executed.GetOrThrow().ExitCode); Equal(12L, outputInfo!.Skipped!.Bytes);
var timedOut = await env.ExecAsync("timeout", new() { Timeout = 1 }, context);
Equal(ExecutionErrorCode.Timeout, timedOut.Error!.Code); Equal("/spill", timedOut.Error!.SpillPath);
var callback = await env.ExecAsync("callback", new() { OnOutput = (_, _, _) => throw new Exception("callback failure") }, context);
Equal(ExecutionErrorCode.CallbackError, callback.Error!.Code);
var localTimeout = await env.ExecAsync("hang", new() { Timeout = 0.03 }, context);
Equal(ExecutionErrorCode.Timeout, localTimeout.Error!.Code);
await using (var slowConnection = new Connection(new ConnectionOptions
{
    CommandFactory = async ct => { await Task.Delay(10000, ct); return command; },
}))
{
    var slowEnv = new RemoteExecutionEnv(new() { Connection = slowConnection, Id = "slow", Cwd = "/workspace" });
    var startupTimeout = await slowEnv.ExecAsync("ok", new() { Timeout = 0.03, Cwd = "child" }, context);
    Equal(ExecutionErrorCode.Timeout, startupTimeout.Error!.Code);
}

var stale = (await env.OpenBinaryReaderAsync("lines", null, context)).GetOrThrow();
try { await connection.RequestAsync("drop", new()); } catch (RemoteError) { }
var startsBefore = starts;
Ok(!(await stale.InfoAsync(context)).IsOk, "stale reader fails");
Equal(startsBefore, starts); // It must never start a daemon to reuse the numeric handle.
Ok((await env.FileInfoAsync("lines", context)).IsOk, "new path request reconnects");
Equal(startsBefore + 1, starts);
var beforeMutation = starts;
Ok(!(await env.WriteFileAsync("lost-write", new byte[] { 1 }, context)).IsOk, "lost mutation fails");
Equal(beforeMutation, starts); // Lost mutation is never replayed.
await env.CleanupAsync(context);
Console.WriteLine("PASS remote adapters: paths, chunked transfers, lines, listing, errors, exec, cancellation and session fencing");
await RealDaemonTests.RunAsync();

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}");
}
static void Ok(bool value, string message) { if (!value) throw new Exception(message); }

static class Fixture
{
    public static async Task Run(string[] args)
    {
        var token = args[Array.IndexOf(args, "--token") + 1];
        var output = Console.OpenStandardOutput(); var input = Console.OpenStandardInput();
        await output.WriteAsync(Encoding.UTF8.GetBytes($"PI-ENV {token}\n")); await output.FlushAsync();
        var files = new Dictionary<string, byte[]> { ["/workspace/lines"] = Encoding.UTF8.GetBytes("\uFEFFα\nlast"), ["/workspace/slow-lines"] = Encoding.UTF8.GetBytes("\uFEFFα\nlast"), ["/workspace/slow-stream-lines"] = Encoding.UTF8.GetBytes("one\ntwo") };
        var handles = new Dictionary<long, (string Path, long Position)>(); long next = 0; uint? pending = null;
        async Task Send(ProtocolFrame frame) => await FrameCodec.WriteAsync(output, frame);
        while (await FrameCodec.ReadAsync(input) is { } frame)
        {
            if (frame.Type == FrameType.Ping) continue;
            if (frame.Type == FrameType.Cancel)
            {
                if (pending == frame.Id) { await Send(new(FrameType.Error, frame.Id, new() { ["code"] = "aborted", ["message"] = "aborted" })); pending = null; }
                continue;
            }
            var json = frame.Json; var op = json["op"]!.GetValue<string>();
            var path = json["path"]?.GetValue<string>() ?? "";
            var reply = new JsonObject(); byte[] payload = [];
            if (op == "drop" || op == "write" && path.EndsWith("lost-write", StringComparison.Ordinal)) return;
            switch (op)
            {
                case "hello": reply = new() { ["protocol"] = 1, ["version"] = "1.5.0", ["os"] = "linux", ["arch"] = "x86_64", ["home"] = "/home/test", ["tmpdir"] = "/tmp", ["cwd"] = "/daemon", ["separator"] = "/", ["pid"] = Environment.ProcessId, ["features"] = args.Contains("--legacy") ? new JsonArray() : new JsonArray("listFiles") }; break;
                case "write":
                    files[path] = json["append"]?.GetValue<bool>() == true && files.TryGetValue(path, out var existing) ? existing.Concat(frame.Payload.ToArray()).ToArray() : frame.Payload.ToArray();
                    if (json["keep"]?.GetValue<bool>() == true) { handles[++next] = (path, 0); reply["handle"] = next; }
                    break;
                case "writeChunk":
                    path = handles[json["handle"]!.GetValue<long>()].Path;
                    files[path] = files[path].Concat(frame.Payload.ToArray()).ToArray(); break;
                case "open":
                    handles[++next] = (path, 0); reply["handle"] = next;
                    reply[json["mode"] is null ? "info" : "stat"] = Info(path, files[path].Length, path.EndsWith("stream-lines", StringComparison.Ordinal) ? "other" : "file"); break;
                case "fstat": path = handles[json["handle"]!.GetValue<long>()].Path; reply = Info(path, files[path].Length); break;
                case "pread":
                    var handle = json["handle"]!.GetValue<long>(); var entry = handles[handle]; path = entry.Path;
                    if (path.Contains("slow-", StringComparison.Ordinal)) await Task.Delay(120);
                    if (path.EndsWith("stream-lines", StringComparison.Ordinal) && json["offset"] is not null) throw new InvalidOperationException("Pipe read must be sequential");
                    var offset = json["offset"]?.GetValue<long>() ?? entry.Position;
                    var length = (int)Math.Min(json["length"]!.GetValue<long>(), path.EndsWith("lines", StringComparison.Ordinal) ? 3 : 17000);
                    payload = files[path].AsSpan((int)Math.Min(offset, files[path].Length), (int)Math.Min(length, Math.Max(0, files[path].Length - offset))).ToArray();
                    if (json["offset"] is null) handles[handle] = (path, offset + payload.Length); break;
                case "scanLines": reply = new() { ["newlines"] = 1, ["start"] = 0, ["end"] = 7, ["firstLineEnd"] = 7, ["lastLineStart"] = 0, ["selectedBytes"] = 7, ["firstLineBytes"] = 7 }; break;
                case "close": handles.Remove(json["handle"]!.GetValue<long>()); break;
                case "lstat":
                    if (path.EndsWith("missing", StringComparison.Ordinal)) { await Send(new(FrameType.Error, frame.Id, new() { ["code"] = "ENOENT", ["path"] = path, ["message"] = "missing" })); continue; }
                    reply = Info(path, files.TryGetValue(path, out var data) ? data.Length : 0); break;
                case "opendir": handles[++next] = (path, 0); reply["handle"] = next; break;
                case "readdir": reply = new() { ["done"] = true, ["entries"] = new JsonArray(new JsonObject { ["name"] = "gone", ["error"] = new JsonObject { ["code"] = "ENOENT", ["message"] = "gone" } }, new JsonObject { ["name"] = "pipe", ["info"] = Info("pipe", 0, "other") }, new JsonObject { ["name"] = "file", ["info"] = Info("file", 3) }) }; break;
                case "listFiles":
                    if (args.Contains("--legacy")) throw new InvalidOperationException("Legacy daemon must not receive listFiles");
                    reply = new() { ["count"] = 1, ["truncated"] = false }; payload = Encoding.UTF8.GetBytes("fa.txt\0"); break;
                case "exec":
                    var command = json["command"]!.GetValue<string>();
                    if (command == "timeout") { await Send(new(FrameType.Error, frame.Id, new() { ["code"] = "timeout", ["message"] = "timed out", ["spillPath"] = "/spill" })); continue; }
                    await Send(new(FrameType.Event, frame.Id, new() { ["kind"] = "output", ["stream"] = "stderr", ["skipped"] = new JsonObject { ["bytes"] = 12, ["newlines"] = 1, ["endsWithNewline"] = false } }, Encoding.UTF8.GetBytes("hello")));
                    if (command is "callback" or "hang") { pending = frame.Id; continue; }
                    reply["exitCode"] = 7; break;
                default: throw new Exception("Unexpected fixture operation " + op);
            }
            await Send(new(FrameType.Result, frame.Id, reply, payload));
        }
    }
    private static JsonObject Info(string path, long size, string kind = "file") => new() { ["name"] = path.Split('/').Last(), ["kind"] = kind, ["size"] = size, ["mtimeSec"] = 100, ["mtimeNsec"] = 123 };
}
