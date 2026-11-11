// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/tools/read.ts, packages/coding-agent/src/core/tools/edit.ts.
// Authored native regression; not an upstream execution capture.
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class Sync152ToolsCases
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync152.read-seekless-single-pass-ranges-and-decoding", StreamRanges),
        ("sync152.read-bounded-huge-line-and-snapshot", StreamBounds),
        ("sync152.read-stream-error-and-cancellation-dispose", StreamErrors),
        ("sync152.edit-mixed-eol-local-style-and-seams", MixedEndings),
        ("sync152.edit-large-file-window-and-before-write-failure", EditWindows),
        ("sync152.find-windows-glob-dot-smartcase-noext", FindGlob),
        ("sync152.find-cancellation-before-spawn", FindCanceled),
        ("sync152.powershell51-exit-suffix-and-pwsh-exclusion", PowerShellExit),
        ("sync152.ls-zero-negative-and-exact-limit", ListLimits)
    ];
    private static async Task StreamRanges()
    {
        var operations = new MemoryFiles(Encoding.UTF8.GetBytes("\ufeffone\r\n\U0001f642\rsolo\nlast\n").Concat(new byte[] { 0xff }).ToArray());
        var tools = new ReadWriteTools(Root, Root, operations);
        var read = await Invoke(tools.CreateInvoker(new Policy()), "read", new { path = "file", offset = 2, limit = 1 });
        Equal("\U0001f642\rsolo\n\n[2 more lines in file. Use offset=3 to continue.]", Text(read));
        Equal(1, operations.Opens); Equal(0, operations.BufferReads); Check(operations.Last!.Disposed, "stream disposal");
        read = await Invoke(tools.CreateInvoker(new Policy()), "read", new { path = "file" });
        Equal("\ufeffone\r\n\U0001f642\rsolo\nlast\n\ufffd", Text(read));
        read = await Invoke(tools.CreateInvoker(new Policy()), "read", new { path = "file", offset = 5 });
        Check(read.IsError && Text(read).Contains("(4 lines total)"), "beyond EOF line count");
        read = await Invoke(tools.CreateInvoker(new Policy()), "read", new { path = "file", offset = 1.5, limit = -1 });
        Equal(1, operations.BufferReads); Check(!read.IsError, "buffer fallback failed");
    }
    private static async Task StreamBounds()
    {
        var operations = new MemoryFiles(Encoding.UTF8.GetBytes(new string('x', 200_000) + "\nlast\n"));
        var read = await Invoke(new ReadWriteTools(Root, Root, operations, new(MaximumReadBytes: 16)).CreateInvoker(new Policy()), "read", new { path = "file" });
        Check(!read.IsError && Text(read).Contains("195.3KB"), "huge first line counted");
        var truncation = read.Details.Value.GetProperty("truncation");
        Equal(200006, truncation.GetProperty("totalBytes").GetInt32()); Equal(2, truncation.GetProperty("totalLines").GetInt32()); Equal(0, operations.BufferReads);
        operations = new MemoryFiles(Encoding.UTF8.GetBytes("before\n")) { SnapshotLength = 7, Appended = Encoding.UTF8.GetBytes("after\n") };
        read = await Invoke(new ReadWriteTools(Root, Root, operations).CreateInvoker(new Policy()), "read", new { path = "file" });
        Equal("before\n", Text(read));
    }
    private static async Task StreamErrors()
    {
        var operations = new MemoryFiles(Encoding.UTF8.GetBytes("first\n")) { FailRead = true };
        var read = await Invoke(new ReadWriteTools(Root, Root, operations).CreateInvoker(new Policy()), "read", new { path = "file" });
        Check(read.IsError && operations.Last!.Disposed, "stream read failure must dispose");
        using var canceled = new CancellationTokenSource();
        operations = new MemoryFiles(Encoding.UTF8.GetBytes("first\n")) { OnRead = canceled.Cancel };
        read = await Invoke(new ReadWriteTools(Root, Root, operations).CreateInvoker(new Policy()), "read", new { path = "file" }, canceled.Token);
        Check(read.IsError && operations.Last!.Disposed, "stream cancellation must dispose");
    }
    private static Task MixedEndings()
    {
        var plan = EditPlan.Create("a\r\nb\nc\rd\r\ne", [new("b", "B\nBB"), new("e", "E\nEE")], "file");
        Equal("a\r\nB\nBB\nc\rd\r\nE\r\nEE", plan.Content);
        plan = EditPlan.Create("a\rb\rc", [new("b", "B\nBB")], "file"); Equal("a\rB\rBB\rc", plan.Content);
        plan = EditPlan.Create("a\rbX\nc", [new("bX", "\nB")], "file");
        Equal(EditMatcher.NormalizeToLF("a\rbX\nc").Replace("bX", "\nB"), EditMatcher.NormalizeToLF(plan.Content));
        plan = EditPlan.Create("keep\r\nsmart \u201cquote\u201d\nend", [new("smart \"quote\"", "new\ntext")], "file");
        Equal("keep\r\nnew\ntext\nend", plan.Content); Check(DiffFormatter.FormatEdit("file", plan).Diff.Contains("new"), "fuzzy edit diff");
        return Task.CompletedTask;
    }
    private static async Task EditWindows()
    {
        var before = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => "line " + i));
        var plan = EditPlan.Create(before, [new("line 10000\n", "updated\nextra\n"), new("line 19990\n", "changed\n")], "file");
        var diff = DiffFormatter.FormatEdit("file", plan, new(MaximumLines: 100));
        Equal(10001, diff.FirstChangedLine); Check(diff.Patch.Contains("@@ -") && diff.Patch.Contains("+updated"), "bounded diff");
        var operations = new MemoryFiles(Encoding.UTF8.GetBytes(before));
        var queue = new FileMutationQueue((path, _) => ValueTask.FromResult(path));
        var tool = new EditTool(Root, Root, queue, operations, new(DiffOptions: new(MaximumOutputCharacters: 1)), new Probe());
        var result = await Invoke(tool.CreateInvoker(new Policy()), "edit", new { path = "file", edits = new[] { new { oldText = "line 10000\n", newText = "changed\n" } } });
        Check(result.IsError, "failed diff must fail the edit"); Equal(0, operations.Writes);
    }
    private static Task FindGlob()
    {
        Check(WindowsFindGlob.Match("C:/Root/SRC/.hidden/Test.TS", "**/src/**/*.ts", true), "dot and smart case");
        Check(!WindowsFindGlob.Match("C:/Root/SRC/Test.ts", "**/src/**/*.ts", false), "case-sensitive glob");
        Check(WindowsFindGlob.Match("C:/Root/src/+(a|b).ts", "**/src/+(a|b).ts", true), "literal extglob");
        Check(!WindowsFindGlob.Match("C:/Root/src/a.ts", "**/src/+(a|b).ts", true), "no extglob");
        Check(WindowsFindGlob.Match("C:/Root/src/a.cs", "**/src/*.{ts,cs}", true), "brace glob");
        Check(!WindowsFindGlob.Match("C:/Root/src", "**/src/**/*", true), "trailing globstar excludes directory itself");
        Equal(1, PiFindTool.NormalizeLimit(0, 1000)); Equal(1, PiFindTool.NormalizeLimit(-4, 1000)); return Task.CompletedTask;
    }
    private static async Task FindCanceled()
    {
        using var canceled = new CancellationTokenSource();
        var find = new PiFindTool(Root, Root, _ =>
        {
            canceled.Cancel(); return ValueTask.FromResult<string?>(Path.Combine(Root, "missing-fd-sync152.exe"));
        });
        var result = await Invoke(new ToolInvoker([find], new Policy()), "find", new { pattern = "**/*.cs" }, canceled.Token);
        Check(result.Failure?.Kind == ToolFailureKind.Canceled, "cancellation must win before spawning fd");
    }
    private static Task PowerShellExit()
    {
        var shell = new ShellConfiguration(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ["-Command"]);
        var command = shell.CommandArguments("git push 2>&1 | Select-Object -Last 3")[^1];
        Check(command.Contains("\n;\nif ($?) { exit 0 }") && command.Contains("NativeCommandError*"), "PS51 correction");
        Equal(command, shell.PrepareCommand(command)); Equal("Write-Output `", shell.CommandArguments("Write-Output `")[^1]);
        Equal("git status", new ShellConfiguration("pwsh.exe", ["-Command"]).CommandArguments("git status")[^1]); return Task.CompletedTask;
    }
    private static async Task ListLimits()
    {
        var root = Path.Combine(Root, "pisharp-sync152-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one"), ""); var tool = new LsTool(root, root, pi: true);
            Equal("one", Text(await Invoke(tool.CreateInvoker(new Policy()), "ls", new { limit = 0 })));
            await File.WriteAllTextAsync(Path.Combine(root, "two"), "");
            var result = await Invoke(tool.CreateInvoker(new Policy()), "ls", new { limit = -5 });
            Check(Text(result).StartsWith("one\n\n[1 entries limit reached"), "one extra result detection");
        }
        finally { Directory.Delete(root, true); }
    }
    private static string Root => Path.GetFullPath(Path.GetTempPath());
    private static string Text(ToolResult result) => string.Join("", result.Content.Select(c => c.Text));
    private static async Task<ToolResult> Invoke(ToolInvoker invoker, string name, object input, CancellationToken token = default)
    {
        var call = new ToolCallContent("sync152", name, JsonData.Parse(JsonSerializer.Serialize(input)));
        return await invoker.ExecuteAsync(new(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}");
    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private sealed class Probe : IFileAccessProbe
    {
        public ValueTask CheckAsync(string path, FileAccessModes modes, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class MemoryFiles(byte[] bytes) : IFileOperations, IStreamingFileOperations
    {
        public int Opens, BufferReads, Writes; public ChunkStream? Last; public bool FailRead; public Action? OnRead;
        public long? SnapshotLength; public byte[] Appended = [];
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => ValueTask.FromResult(path);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token) { BufferReads++; return ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token) { Writes++; return ValueTask.CompletedTask; }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        { Opens++; Last = new(bytes.Concat(Appended).ToArray(), SnapshotLength, FailRead, OnRead); return ValueTask.FromResult<Stream>(Last); }
    }
    private sealed class ChunkStream(byte[] bytes, long? snapshot, bool fail, Action? onRead) : Stream
    {
        private int _position; public bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => snapshot is not null;
        public override bool CanWrite => false;
        public override long Length => snapshot ?? throw new NotSupportedException();
        public override long Position { get => _position; set => throw new InvalidOperationException("second pass"); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); if (fail) throw new IOException("fixture");
            var count = Math.Min(3, Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; onRead?.Invoke(); return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException("second pass");
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
