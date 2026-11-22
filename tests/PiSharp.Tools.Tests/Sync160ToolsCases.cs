// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/suite/regressions/12066-find-ancestor-glob.test.ts,
// packages/coding-agent/test/tools.test.ts (the 1.6.0 find "./", read text-only-model and bash over-long-last-line cases) and
// packages/coding-agent/test/read-streaming.test.ts (abort while opening, abort mid-read).
// Ported cases over the native tools; the find cases run the installed fd. Not an upstream execution capture.
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class Sync160ToolsCases
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync160.find-path-pattern-does-not-match-above-search-path", FindAncestor),
        ("sync160.find-path-patterns-anchor-at-search-path", FindAnchored),
        ("sync160.find-search-path-glob-characters-smart-case-and-link", FindSearchPath),
        ("sync160.find-leading-dot-slash", FindDotSlash),
        ("sync160.find-fd-arguments-anchor-and-escape-search-path", FindArguments),
        ("sync160.find-and-grep-pattern-descriptions", Descriptions),
        ("sync160.read-image-keeps-text-only-model-warning-out-of-content", ReadImageDetails),
        ("sync160.read-abort-while-opening-is-a-clean-cancelled-result", ReadAbortOnOpen),
        ("sync160.read-abort-mid-read-stops-reading", ReadAbortMidRead),
        ("sync160.bash-over-long-last-line-shows-its-end", BashLongLastLine)
    ];

    // ---------------------------------------------------------------- find (#12066, #12020)

    private static void CreateProject(string root)
    {
        foreach (var directory in new[] { "src", "lib", "test", "pkg/src" }) Directory.CreateDirectory(Path.Combine(root, directory));
        File.WriteAllText(Path.Combine(root, "src", "a.ts"), "");
        File.WriteAllText(Path.Combine(root, "lib", "b.ts"), "");
        File.WriteAllText(Path.Combine(root, "test", "c.ts"), "");
        File.WriteAllText(Path.Combine(root, "pkg", "src", "d.ts"), "");
    }

    private static async Task<string[]> RunFind(string fd, string searchPath, string pattern)
    {
        var find = new PiFindTool(searchPath, searchPath, _ => ValueTask.FromResult<string?>(fd));
        var result = await Invoke(new ToolInvoker([find], new Policy()), "find", new { pattern });
        Check(!result.IsError, "find failed for " + pattern + ": " + Text(result));
        var text = Text(result);
        return text == "No files found matching pattern" ? [] : [.. text.Split('\n').Order(StringComparer.Ordinal)];
    }

    // fd matches --full-path globs against the absolute path, and find prefixed path patterns with "**/". With the project in
    // ".../src/app", "src/**/*.ts" therefore returned every .ts file of the project.
    private static async Task FindAncestor()
    {
        var fd = Executable("fd"); using var temp = new Temp();
        var project = Path.Combine(temp.Root, "src", "app"); CreateProject(project);
        Same(["src/a.ts"], await RunFind(fd, project, "src/**/*.ts"), "src/**/*.ts");
        Same([], await RunFind(fd, project, "app/**/*.ts"), "app/**/*.ts");
    }

    private static async Task FindAnchored()
    {
        var fd = Executable("fd"); using var temp = new Temp();
        var project = Path.Combine(temp.Root, "src", "app"); CreateProject(project);
        Same(["src/a.ts"], await RunFind(fd, project, "src/*.ts"), "src/*.ts");
        Same(["src/a.ts"], await RunFind(fd, project, "./src/*.ts"), "./src/*.ts");
        Same(["pkg/src/d.ts"], await RunFind(fd, project, "pkg/src/*.ts"), "pkg/src/*.ts");
        Same(["pkg/src/d.ts", "src/a.ts"], await RunFind(fd, project, "**/src/*.ts"), "**/src/*.ts");
        Same(["pkg/src/d.ts", "src/a.ts"], await RunFind(fd, project, "**/src/**/*.ts"), "**/src/**/*.ts");
        // A trailing "/**" lists what is inside the folder, not the folder.
        Same(["src/a.ts"], await RunFind(fd, project, "src/**"), "src/**");
        // Basename and absolute patterns are unchanged.
        Same(["lib/b.ts", "pkg/src/d.ts", "src/a.ts", "test/c.ts"], await RunFind(fd, project, "*.ts"), "*.ts");
        Same(["lib/b.ts"], await RunFind(fd, project, project.Replace('\\', '/') + "/lib/*.ts"), "absolute pattern");
        if (OperatingSystem.IsWindows())
            Same(["pkg/src/d.ts"], await RunFind(fd, project, @"pkg\src\*.ts"), "native separators");
    }

    private static async Task FindSearchPath()
    {
        var fd = Executable("fd"); using var temp = new Temp();
        // "*" and "?" are not allowed in Windows file names.
        var literal = Path.Combine(temp.Root, OperatingSystem.IsWindows() ? "w[1]{x,y}" : "w*?[1]{x,y}", "src"); CreateProject(literal);
        Same(["src/a.ts"], await RunFind(fd, literal, "src/*.ts"), "glob characters in the search path are literal");

        // Smart case follows the pattern, not the upper-case letters of the search path.
        var upper = Path.Combine(temp.Root, "Upper"); Directory.CreateDirectory(Path.Combine(upper, "src"));
        File.WriteAllText(Path.Combine(upper, "src", "A.ts"), ""); File.WriteAllText(Path.Combine(upper, "src", "b.ts"), "");
        Same(["src/A.ts"], await RunFind(fd, upper, "src/a.ts"), "all-lowercase pattern ignores case");
        Same([], await RunFind(fd, upper, "src/B.ts"), "a pattern with an upper-case letter is case-sensitive");

        var project = Path.Combine(temp.Root, "src", "app"); CreateProject(project);
        var link = Path.Combine(temp.Root, "src", "link");
        if (!TryLink(link, project)) { Console.WriteLine("INFO sync160 find link fixture unavailable; the linked search path was not checked"); return; }
        Same(["src/a.ts"], await RunFind(fd, link, "src/**/*.ts"), "linked search path");
    }

    // fd matches nothing for a leading "./", so the tool drops it.
    private static async Task FindDotSlash()
    {
        var fd = Executable("fd"); using var temp = new Temp();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src"));
        File.WriteAllText(Path.Combine(temp.Root, "src", "a.ts"), "a"); File.WriteAllText(Path.Combine(temp.Root, "package.json"), "{}");
        var find = new PiFindTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>(fd));
        foreach (var pattern in new[] { "./src/*.ts", "./src/**/*.ts", "././src/*.ts" })
            Equal("src/a.ts", Text(await Invoke(new ToolInvoker([find], new Policy()), "find", new { pattern, path = temp.Root })));
        // Without a "/" left, "./*.json" matches basenames at any depth, like "*.json".
        File.WriteAllText(Path.Combine(temp.Root, "src", "b.json"), "{}");
        Same(["package.json", "src/b.json"], await RunFind(fd, temp.Root, "./*.json"), "./*.json");
    }

    // The fd invocation of each platform, checked without running fd: POSIX hands fd the pattern behind the glob-escaped search path
    // and sets smart case from the user's pattern; Windows lists everything and matches the path relative to the search path itself.
    private static Task FindArguments()
    {
        const string root = "/home/u/w*?[1]{x,y}/Upper";
        Same(["--glob", "--max-results", "1001", "--full-path", "--ignore-case", "--", @"/home/u/w\*\?\[1\]\{x,y\}/Upper/src/*.ts", root],
            [.. PiFindTool.PatternArguments("src/*.ts", root, 1000, windows: false)], "anchored lower-case pattern");
        Same(["--glob", "--max-results", "1001", "--full-path", "--", @"/home/u/w\*\?\[1\]\{x,y\}/Upper/src/B.ts", root],
            [.. PiFindTool.PatternArguments("src/B.ts", root, 1000, windows: false)], "an upper-case letter keeps fd case-sensitive");
        Same(["--glob", "--max-results", "3", "--full-path", "--ignore-case", "--", "/work/**/src/*.ts", "/work/"],
            [.. PiFindTool.PatternArguments("././**/src/*.ts", "/work/", 2, windows: false)], "leading ./ and trailing search path slashes");
        Same(["--glob", "--max-results", "1001", "--full-path", "--", "/abs/lib/*.ts", root],
            [.. PiFindTool.PatternArguments("/abs/lib/*.ts", root, 1000, windows: false)], "an absolute pattern is matched as given");
        Same(["--glob", "--max-results", "1001", "--", "*.ts", root], [.. PiFindTool.PatternArguments("./*.ts", root, 1000, windows: false)], "basename pattern");
        // On POSIX "\" is a glob escape, not a separator: the pattern has no "/" and stays a basename pattern.
        Same(["--glob", "--max-results", "1001", "--", @"src\*.ts", root], [.. PiFindTool.PatternArguments(@"src\*.ts", root, 1000, windows: false)], "POSIX backslash");
        Same(["--", "", @"C:\work"], [.. PiFindTool.PatternArguments(@".\src\*.ts", @"C:\work", 1000, windows: true)], "Windows path pattern lists everything");
        Same(["--glob", "--max-results", "1001", "--", "*.ts", @"C:\work"], [.. PiFindTool.PatternArguments("*.ts", @"C:\work", 1000, windows: true)], "Windows basename pattern");
        Equal("src/*.ts", PiFindTool.EffectivePattern(@".\.\src\*.ts", windows: true)); Equal(@".\src\*.ts", PiFindTool.EffectivePattern(@".\src\*.ts", windows: false));
        Check(PiFindTool.IsAbsolutePattern("/a/*.ts", false) && !PiFindTool.IsAbsolutePattern("a/*.ts", false) && !PiFindTool.IsAbsolutePattern("C:/a/*.ts", false), "POSIX isAbsolute");
        Check(PiFindTool.IsAbsolutePattern("C:/a/*.ts", true) && PiFindTool.IsAbsolutePattern("/a/*.ts", true) && !PiFindTool.IsAbsolutePattern("C:a/*.ts", true) &&
            !PiFindTool.IsAbsolutePattern("**/a/*.ts", true), "Windows isAbsolute");
        // The Windows matcher sees the path relative to the search path, so an ancestor named like the pattern's first folder cannot match.
        Check(WindowsFindGlob.Match("src/a.ts", "src/**/*.ts", true) && !WindowsFindGlob.Match("lib/b.ts", "src/**/*.ts", true) &&
            !WindowsFindGlob.Match("pkg/src/d.ts", "src/*.ts", true) && WindowsFindGlob.Match("pkg/src/d.ts", "**/src/*.ts", true), "relative matching");
        Check(!WindowsFindGlob.Match("src/", "src/**/*", true) && WindowsFindGlob.Match("src/sub/", "src/**/*", true), "a folder is not inside itself");
        return Task.CompletedTask;
    }

    private static Task Descriptions()
    {
        var find = new PiFindTool(Root, Root, _ => ValueTask.FromResult<string?>(null)).Declaration.Value;
        Equal("Glob pattern relative to the search directory, e.g. 'src/**/*.ts'. Without '/', e.g. '*.ts', it matches file names at any depth",
            find.GetProperty("parameters").GetProperty("properties").GetProperty("pattern").GetProperty("description").GetString());
        var grep = new PiGrepTool(Root, Root, _ => ValueTask.FromResult<string?>(null)).Declaration.Value;
        Equal("Filter files by glob pattern relative to path, e.g. 'src/**/*.ts'. Without '/', e.g. '*.ts', it matches file names at any depth",
            grep.GetProperty("parameters").GetProperty("properties").GetProperty("glob").GetProperty("description").GetString());
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- read (#12131, #11956)

    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";

    // The result is replayed after a switch to a vision model, so it keeps the image and no note saying the model cannot see it; the
    // provider replaces the image per request instead. Renderers get the model's id in details.imageUnsupportedByModel.
    private static async Task ReadImageDetails()
    {
        using var temp = new Temp();
        await File.WriteAllBytesAsync(Path.Combine(temp.Root, "text-model.png"), Convert.FromBase64String(Png1x1));
        async Task<ToolResult> Read(ReadWriteToolOptions options, string path = "text-model.png") =>
            await Invoke(new ReadWriteTools(temp.Root, temp.Root, options: options).CreateInvoker(new Policy()), "read", new { path });

        var result = await Read(new() { CurrentModelSupportsImages = () => false, CurrentModelId = () => "text-only" });
        Check(!result.IsError, "read failed: " + result.ContentValue);
        var content = result.ContentValue.Value;
        Equal(2, content.GetArrayLength());
        Equal("Read image file [image/png]", content[0].GetProperty("text").GetString());
        Equal("image", content[1].GetProperty("type").GetString()); Equal("image/png", content[1].GetProperty("mimeType").GetString());
        Equal("""{"imageUnsupportedByModel":"text-only"}""", result.Details.ToString());
        Equal("Read image file [image/png]", result.StructuredContent!.Value.GetProperty("note").GetString());

        // A host that reports only "no image input" still marks the result.
        result = await Read(new() { CurrentModelSupportsImages = () => false });
        Equal("Read image file [image/png]", result.ContentValue.Value[0].GetProperty("text").GetString());
        Equal(ReadWriteTools.UnnamedModel, result.Details.Value.GetProperty(ReadWriteTools.ImageUnsupportedByModelProperty).GetString());

        // A vision model, or no model: details stay absent.
        foreach (var supports in new bool?[] { true, null })
        {
            result = await Read(new() { CurrentModelSupportsImages = () => supports, CurrentModelId = () => "some-model" });
            Equal("Read image file [image/png]", result.ContentValue.Value[0].GetProperty("text").GetString());
            Check(!result.HasProperty("details"), "details must be absent for " + (supports?.ToString() ?? "no model"));
        }

        // An image that could not be attached has nothing a placeholder replaces: no note and no details either.
        var rle = Bmp1x1Red(); BinaryPrimitives.WriteUInt32LittleEndian(rle.AsSpan(30), 1); BinaryPrimitives.WriteUInt16LittleEndian(rle.AsSpan(28), 8);
        await File.WriteAllBytesAsync(Path.Combine(temp.Root, "broken.bmp"), rle);
        result = await Read(new() { CurrentModelSupportsImages = () => false, CurrentModelId = () => "text-only" }, "broken.bmp");
        Equal(1, result.ContentValue.Value.GetArrayLength());
        Equal("Read image file [image/bmp]\n[Image omitted: could not be converted to a supported inline image format.]",
            result.ContentValue.Value[0].GetProperty("text").GetString());
        Check(!result.HasProperty("details"), "an omitted image carries no details");
    }

    // Upstream crashed on an unhandled stream error when the signal aborted while the file was being opened. Here the abort is a
    // cancelled result with the source text, the opened stream is closed and nothing is read from it.
    private static async Task ReadAbortOnOpen()
    {
        using var temp = new Temp();
        var path = Path.Combine(temp.Root, "abort-on-open.txt"); await File.WriteAllTextAsync(path, "line\n");

        // A real local file: the open succeeds after the abort.
        using (var canceled = new CancellationTokenSource())
        {
            var local = new HookedLocal { OnOpen = canceled.Cancel };
            var result = await Direct(new ReadWriteTools(temp.Root, temp.Root, local), new { path }, canceled.Token);
            Aborted(result, "abort while opening a local file");
            Equal(1, local.Opens);
            // The handle is closed: the file can be replaced and removed at once.
            File.Delete(path); await File.WriteAllTextAsync(path, "line\n");
        }

        // A stream that would hand out data: none is read after the abort.
        using (var canceled = new CancellationTokenSource())
        {
            var files = new StreamFiles(Encoding.UTF8.GetBytes("line\n")) { OnOpen = canceled.Cancel };
            Aborted(await Direct(new ReadWriteTools(Root, Root, files), new { path = "file" }, canceled.Token), "abort while opening a stream");
            Equal(0, files.Last!.Reads); Check(files.Last.Disposed, "the opened stream must be closed");
        }

        // An open that fails because of the abort is the same result, not a file-system error.
        foreach (var failure in new Exception[] { new OperationCanceledException(), new IOException("aborted"), new UnauthorizedAccessException("aborted") })
        {
            using var canceled = new CancellationTokenSource();
            var files = new StreamFiles([]) { OnOpen = canceled.Cancel, OpenFailure = failure };
            Aborted(await Direct(new ReadWriteTools(Root, Root, files), new { path = "file" }, canceled.Token), "open failing with " + failure.GetType().Name);
        }

        // Through the invoker the call is cancelled as well.
        using (var canceled = new CancellationTokenSource())
        {
            var files = new StreamFiles(Encoding.UTF8.GetBytes("line\n")) { OnOpen = canceled.Cancel };
            var result = await Invoke(new ReadWriteTools(Root, Root, files).CreateInvoker(new Policy()), "read", new { path = "file" }, canceled.Token);
            Check(result.IsError && result.Failure?.Kind == ToolFailureKind.Canceled, "invoker result must be cancelled");
            Equal("Operation aborted", result.Content[0].Text);
        }

        // Without an abort the same adapters read the file.
        Equal("line\n", Text(await Direct(new ReadWriteTools(Root, Root, new StreamFiles(Encoding.UTF8.GetBytes("line\n"))), new { path = "file" }, default)));
    }

    private static async Task ReadAbortMidRead()
    {
        // Many stream chunks, so the abort lands with most of the file unread.
        using var canceled = new CancellationTokenSource();
        var files = new StreamFiles(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024))) { ChunkBytes = 64 * 1024, OnRead = canceled.Cancel };
        Aborted(await Direct(new ReadWriteTools(Root, Root, files), new { path = "file" }, canceled.Token), "abort mid-read");
        Equal(1, files.Last!.Reads); Check(files.Last.Disposed, "the stream must be closed after the abort");
    }

    private static void Aborted(ToolResult result, string what)
    {
        Check(result.IsError && result.Failure?.Kind == ToolFailureKind.Canceled, what + ": expected a cancelled result, got " + Text(result));
        Equal("Operation aborted", Text(result));
    }

    /// <summary>Runs the read adapter itself, so the result is the tool's own and not the invoker's.</summary>
    private static async Task<ToolResult> Direct(ReadWriteTools tools, object input, CancellationToken token)
    {
        var adapter = tools.Adapters[0];
        var call = new ToolCallContent("sync160", "read", JsonData.Parse(JsonSerializer.Serialize(input)));
        var action = await adapter.PrepareAsync(new(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), default);
        return await adapter.ExecuteAsync(action, token);
    }

    // ---------------------------------------------------------------- bash (#12062)

    // A short line followed by one line far past the limit returned no output at all, with a footer like "[Showing lines 3-2 of 2]".
    private static async Task BashLongLastLine()
    {
        using var temp = new Temp();
        var data = Encoding.UTF8.GetBytes("first line\n" + new string('y', 219_000) + "END\n");
        var runner = new AccumulatingRunner(data, 8192);
        var tool = new BashTool(runner, new BashToolOptions(Environment.ProcessPath!, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Root), () => "sync160.log");
        var result = await Invoke(tool.CreateInvoker(new Policy()), "bash", new { command = "grep -n x bundle.min.js" });
        Check(!result.IsError, "bash failed: " + Text(result));
        var output = Text(result);
        var truncation = result.Details.Value.GetProperty("truncation");
        Check(truncation.GetProperty("lastLinePartial").GetBoolean(), "the last line must be reported partial");
        Equal(2, truncation.GetProperty("totalLines").GetInt32()); Equal(data.Length, truncation.GetProperty("totalBytes").GetInt32());
        Check(output.Contains("yyyyEND", StringComparison.Ordinal), "the end of the long line is missing");
        Check(output.Contains("\n\n[Showing last 50.0KB of line 2 (line is ", StringComparison.Ordinal), "footer: " + output[^160..]);
        Check(!output.Contains("first line", StringComparison.Ordinal) && !output.Contains("[Showing lines", StringComparison.Ordinal), "unexpected line footer");
        Equal(data.Length, (int)new FileInfo(result.Details.Value.GetProperty("fullOutputPath").GetString()!).Length);

        // The accumulator on its own: the snapshot is the last 50KB of the over-long line, with and without its newline.
        foreach (var ending in new[] { "\n", "" })
        {
            await using var accumulator = new ShellOutputAccumulator(Path.Combine(temp.Root, "direct" + ending.Length + ".log"), new ProcessRunnerOptions(), new LocalProcessOutputStorage());
            var bytes = Encoding.UTF8.GetBytes("first line\n" + new string('y', 219_000) + "END" + ending);
            for (var index = 0; index < bytes.Length; index += 8192) await accumulator.AppendAsync(bytes.AsMemory(index, Math.Min(8192, bytes.Length - index)));
            await accumulator.FinishAsync();
            var snapshot = accumulator.Snapshot();
            Check(snapshot.Truncation.Truncated && snapshot.Truncation.LastLinePartial, "direct snapshot must be a partial last line");
            Equal(50 * 1024, snapshot.Truncation.OutputBytes); Check(snapshot.Content.EndsWith("yyyyEND", StringComparison.Ordinal), "direct snapshot end");
            Equal(2, snapshot.Truncation.TotalLines);
        }
    }

    private sealed class AccumulatingRunner(byte[] data, int chunk) : IProcessRunner
    {
        public async ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default)
        {
            await using var accumulator = new ShellOutputAccumulator(request.SpillPath, new ProcessRunnerOptions(), new LocalProcessOutputStorage());
            for (var index = 0; index < data.Length; index += chunk) await accumulator.AppendAsync(data.AsMemory(index, Math.Min(chunk, data.Length - index)));
            await accumulator.FinishAsync();
            return new(ProcessRunStatus.Exited, 0, 123, true, true, true, accumulator.Snapshot(), accumulator.Structured(), .2, []);
        }
    }

    // ---------------------------------------------------------------- support

    private static string Root => Path.GetFullPath(Path.GetTempPath());
    private static string Text(ToolResult result) => string.Join("", result.Content.Select(c => c.Text));
    private static async Task<ToolResult> Invoke(ToolInvoker invoker, string name, object input, CancellationToken token = default)
    {
        var call = new ToolCallContent("sync160", name, JsonData.Parse(JsonSerializer.Serialize(input)));
        return await invoker.ExecuteAsync(new(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), token);
    }
    private static string Executable(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length == 0) continue;
            var path = Path.Combine(folder, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("Offline fixture requires installed " + name);
    }
    /// <summary>A directory link: a junction on Windows (no extra privileges), a symbolic link elsewhere.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return true; }
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit(15_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static byte[] Bmp1x1Red()
    {
        var buffer = new byte[58];
        "BM"u8.CopyTo(buffer); BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2), 58); BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(14), 40); BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(22), 1); BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(28), 24); BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(34), 4);
        buffer[56] = 0xff; return buffer;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}");
    private static void Same(string[] expected, string[] actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(", ", expected)}]; actual [{string.Join(", ", actual)}]");

    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("pisharp-sync160-tools-").FullName;
        public void Dispose()
        {
            // A junction is removed as a link, never followed.
            foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToArray())
                if (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null) Directory.Delete(directory);
            Directory.Delete(Root, true);
        }
    }

    /// <summary>The local file operations with a hook that runs while the file is being opened.</summary>
    private sealed class HookedLocal : IDirectoryFileOperations, IStreamingFileOperations
    {
        private readonly LocalFileOperations _inner = new();
        public Action? OnOpen; public int Opens;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => _inner.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => _inner.CanonicalizeAsync(path, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token) => _inner.ReadAsync(path, maximum, token);
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => _inner.CreateDirectoryAsync(path, token);
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token) => _inner.WriteAsync(path, content, token);
        public ValueTask<bool> IsDirectoryAsync(string path, CancellationToken token) => _inner.IsDirectoryAsync(path, token);
        public ValueTask<ImmutableArray<string>> ReadDirectoryAsync(string path, int maximumEntries, int maximumNameCharacters, CancellationToken token) =>
            _inner.ReadDirectoryAsync(path, maximumEntries, maximumNameCharacters, token);
        public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            Opens++;
            var stream = await _inner.OpenReadAsync(path, token);
            OnOpen?.Invoke();
            return stream;
        }
    }

    private sealed class StreamFiles(byte[] bytes) : IFileOperations, IStreamingFileOperations
    {
        public Action? OnOpen, OnRead; public Exception? OpenFailure; public int ChunkBytes = 4096; public CountingStream? Last;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => ValueTask.FromResult(path);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token) => throw new InvalidOperationException("buffered read");
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            OnOpen?.Invoke();
            if (OpenFailure is not null) throw OpenFailure;
            Last = new(bytes, ChunkBytes, OnRead);
            return ValueTask.FromResult<Stream>(Last);
        }
    }

    /// <summary>A seekless stream that hands out fixed chunks and, like a file stream opened without the signal, never looks at the token.</summary>
    private sealed class CountingStream(byte[] bytes, int chunk, Action? onRead) : Stream
    {
        private int _position; public int Reads; public bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Reads++;
            var count = Math.Min(chunk, Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; onRead?.Invoke();
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
