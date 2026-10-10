// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/read.ts, write.ts, edit.ts, ls.ts and bash.ts.
// Inputs upstream's validateToolArguments admits reach the tool's execute; the expected texts were captured by running the installed
// Pi 1.1.0 tools with node (validateToolArguments, then execute) on the same files.
using System.Collections.Immutable;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class ToolEdgeInputTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("edge read offset and limit follow the source number arithmetic", ReadNumbers),
        ("edge empty paths reach the file system as the source tools do", EmptyPaths),
        ("edge pi ls limits follow the source number arithmetic", LsNumbers),
        ("edge NUL bytes give Node's argument errors", NulBytes),
        ("edge user shell failures give the source's errors", UserShellFailures),
        ("edge edit applies more than 1024 replacements as the source does", ManyEdits),
    ];

    private static async Task ReadNumbers()
    {
        using var temp = new Temp();
        await File.WriteAllTextAsync(temp.File("lines.txt"), "one\ntwo\nthree\nfour\nfive");
        var invoker = new ReadWriteTools(temp.Root, temp.Root).CreateInvoker(new Allow());
        foreach (var (input, expected, error) in new[]
        {
            ("""{"path":"lines.txt","offset":1.5}""", "one\ntwo\nthree\nfour\nfive", false),
            ("""{"path":"lines.txt","offset":2.5,"limit":1.5}""", "two\nthree\n\n[2 more lines in file. Use offset=4 to continue.]", false),
            ("""{"path":"lines.txt","limit":-1}""", "one\ntwo\nthree\nfour\n\n[6 more lines in file. Use offset=0 to continue.]", false),
            ("""{"path":"lines.txt","limit":0}""", "\n\n[5 more lines in file. Use offset=1 to continue.]", false),
            ("""{"path":"lines.txt","offset":-3,"limit":2}""", "one\ntwo\n\n[3 more lines in file. Use offset=3 to continue.]", false),
            ("""{"path":"lines.txt","offset":0}""", "one\ntwo\nthree\nfour\nfive", false),
            ("""{"path":"lines.txt","offset":9}""", "Offset 9 is beyond end of file (5 lines total)", true),
            ("""{"path":"lines.txt","offset":5.5}""", "five", false),
            ("""{"path":"lines.txt","offset":1e300}""", "Offset 1e+300 is beyond end of file (5 lines total)", true),
            ("""{"path":"lines.txt","limit":1e300}""", "one\ntwo\nthree\nfour\nfive", false),
            ("""{"path":"lines.txt","offset":3,"limit":-1}""", "\n\n[4 more lines in file. Use offset=2 to continue.]", false),
        })
        {
            var result = await invoker.ExecuteAsync(Invocation("read", input), default);
            Equal(expected, Text(result), input); Equal(error, result.IsError, input + " isError");
        }
    }

    private static async Task EmptyPaths()
    {
        using var temp = new Temp();
        var tools = new ReadWriteTools(temp.Root, temp.Root);
        var read = await tools.CreateInvoker(new Allow()).ExecuteAsync(Invocation("read", """{"path":""}"""), default);
        Equal("EISDIR: illegal operation on a directory, read", Text(read), "read empty path"); Equal(true, read.IsError, "read error");
        var write = await tools.CreateInvoker(new Allow()).ExecuteAsync(Invocation("write", """{"path":"","content":"x"}"""), default);
        Equal($"EISDIR: illegal operation on a directory, open '{temp.Root}'", Text(write), "write empty path"); Equal(true, write.IsError, "write error");
        var edit = new EditTool(temp.Root, temp.Root, new((path, _) => ValueTask.FromResult(path)));
        var edited = await edit.CreateInvoker(new Allow()).ExecuteAsync(Invocation("edit", """{"path":"","edits":[{"oldText":"a","newText":"b"}]}"""), default);
        Equal("EISDIR: illegal operation on a directory, read", Text(edited), "edit empty path"); Equal(true, edited.IsError, "edit error");
    }

    private static async Task LsNumbers()
    {
        using var temp = new Temp();
        Directory.CreateDirectory(temp.File("dir")); await File.WriteAllTextAsync(temp.File("e.txt"), "e"); await File.WriteAllTextAsync(temp.File("lines.txt"), "l");
        var invoker = new LsTool(temp.Root, temp.Root, pi: true).CreateInvoker(new Allow());
        foreach (var (input, expected) in new[]
        {
            ("""{"path":"","limit":0}""", "(empty directory)"),
            ("""{"path":".","limit":-2}""", "(empty directory)"),
            ("""{"path":".","limit":1.5}""", "dir/\ne.txt\n\n[1.5 entries limit reached. Use limit=3 for more]"),
            ("""{"path":".","limit":2.5}""", "dir/\ne.txt\nlines.txt"),
        })
            Equal(expected, Text(await invoker.ExecuteAsync(Invocation("ls", input), default)), input);
    }

    private static async Task NulBytes()
    {
        using var temp = new Temp();
        // util.inspect doubles each backslash of the absolute path.
        var cwd = temp.Root.Replace("\\", "\\\\"); var sep = Path.DirectorySeparatorChar == '\\' ? "\\\\" : "/";
        const string PathError = "The argument 'path' must be a string, Uint8Array, or URL without null bytes. Received ";
        var files = new ReadWriteTools(temp.Root, temp.Root).CreateInvoker(new Allow());
        foreach (var (tool, input, expected) in new[]
        {
            ("read", """{"path":"a\u0000b"}""", PathError + "'" + cwd + sep + "a\\x00b'"),
            ("read", """{"path":"x/y\u0000z'q\n\t\u0001\u00e9\u007f\u0085\u2028\ud83d\ude00\""}""",
                PathError + "`" + cwd + sep + "x" + sep + "y\\x00z'q\\n\\t\\x01\u00e9\\x7F\\x85\u2028\ud83d\ude00\"`"),
            ("read", """{"path":"it's\u0000"}""", PathError + "\"" + cwd + sep + "it's\\x00\""),
            ("write", """{"path":"a\u0000b","content":"x"}""", PathError + "'" + cwd + sep + "a\\x00b'"),
            ("read", "{\"path\":\"" + new string('p', 200) + "\\u0000\"}", (PathError + "'" + cwd + sep + new string('p', 200))[..(PathError.Length + 128)] + "..."),
        })
        {
            var result = await files.ExecuteAsync(Invocation(tool, input), default);
            Equal(expected, Text(result), tool + " " + input); Equal(true, result.IsError, input + " isError");
        }
        var edit = new EditTool(temp.Root, temp.Root, new((path, _) => ValueTask.FromResult(path)));
        Equal(PathError + "'" + cwd + sep + "a\\x00b'", Text(await edit.CreateInvoker(new Allow()).ExecuteAsync(
            Invocation("edit", """{"path":"a\u0000b","edits":[{"oldText":"a","newText":"b"}]}"""), default)), "edit");
        // existsSync is false for a NUL path: ls and grep report it as not found, with the raw NUL.
        Equal("Path not found: " + Path.Join(temp.Root, "a\0b"), Text(await new LsTool(temp.Root, temp.Root, pi: true).CreateInvoker(new Allow())
            .ExecuteAsync(Invocation("ls", """{"path":"a\u0000b"}"""), default)), "ls");
        var grep = new ToolInvoker([new PiGrepTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>("rg"))], new Allow());
        Equal("Path not found: " + Path.Join(temp.Root, "a\0b"), Text(await grep.ExecuteAsync(Invocation("grep", """{"pattern":"x","path":"a\u0000b"}"""), default)), "grep path");
        Equal("The argument 'args[5]' must be a string without null bytes. Received 'a\\x00b'",
            Text(await grep.ExecuteAsync(Invocation("grep", """{"pattern":"a\u0000b"}"""), default)), "grep pattern");
        var find = new ToolInvoker([new PiFindTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>("fd"))], new Allow());
        Equal("The argument 'args[7]' must be a string without null bytes. Received 'a\\x00b'",
            Text(await find.ExecuteAsync(Invocation("find", """{"pattern":"a\u0000b"}"""), default)), "find pattern");
        Equal("The argument 'args[8]' must be a string without null bytes. Received '" + cwd + sep + "a\\x00b'",
            Text(await find.ExecuteAsync(Invocation("find", """{"pattern":"*","path":"a\u0000b"}"""), default)), "find path");
        var bash = new ToolInvoker([new BashTool(new NoRunner(), new BashToolOptions(Environment.ProcessPath!, temp.Root,
            ImmutableDictionary<string, string>.Empty, temp.Root))], new Allow());
        Equal("The argument 'args[1]' must be a string without null bytes. Received 'echo a\\x00b'",
            Text(await bash.ExecuteAsync(Invocation("bash", """{"command":"echo a\u0000b"}"""), default)), "bash");
        Equal("The argument 'args[1]' must be a string without null bytes. Received \"echo 'q\\\\ \\x00\\n\\t\\x01\u00e9\"",
            Text(await bash.ExecuteAsync(Invocation("bash", """{"command":"echo 'q\\ \u0000\n\t\u0001\u00e9"}"""), default)), "bash escapes");
    }

    // edit.ts has no replacement-count limit (the native cap was 1,024).
    private static async Task ManyEdits()
    {
        using var temp = new Temp();
        await File.WriteAllTextAsync(temp.File("many.txt"), string.Join("\n", Enumerable.Range(0, 2000).Select(index => $"[{index}]")));
        var edits = string.Join(",", Enumerable.Range(0, 1500).Select(index => $$"""{"oldText":"[{{index}}]","newText":"<{{index}}>"}"""));
        var edit = new EditTool(temp.Root, temp.Root, new((path, _) => ValueTask.FromResult(path)));
        var result = await edit.CreateInvoker(new Allow()).ExecuteAsync(Invocation("edit", $$"""{"path":"many.txt","edits":[{{edits}}]}"""), default);
        Equal("Successfully replaced 1500 block(s) in many.txt.", Text(result), "1500 edits");
        Equal("<1499>\n[1500]", string.Join("\n", (await File.ReadAllTextAsync(temp.File("many.txt"))).Split('\n')[1499..1501]), "edited content");
    }

    // bash.ts createLocalShellOperations exec (user bash): the working-directory check, then spawn's missing-shell error.
    private static async Task UserShellFailures()
    {
        using var temp = new Temp();
        var missingShell = Path.Combine(temp.Root, "no-such-shell" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var shell = new NativeShellOperations(missingShell, ImmutableDictionary<string, string>.Empty, temp.Root);
        var missingDirectory = Path.Combine(temp.Root, "gone");
        Equal($"Working directory does not exist: {missingDirectory}\nCannot execute bash commands.",
            await Failure(() => shell.ExecuteAsync("echo hi", missingDirectory, _ => ValueTask.CompletedTask, default).AsTask()), "missing cwd");
        Equal($"spawn {missingShell} ENOENT", await Failure(() => shell.ExecuteAsync("echo hi", temp.Root, _ => ValueTask.CompletedTask, default).AsTask()), "missing shell");

        // Node's spawn errors for a shell libuv cannot start (Node 22.18 on Windows: a directory or a missing file is the async
        // "spawn <file> ENOENT", an empty .exe is ERROR_BAD_EXE_FORMAT thrown as "spawn EFTYPE", a text .exe ERROR_EXE_MACHINE_TYPE_MISMATCH
        // as "spawn UNKNOWN", a working directory that is a file ENOENT; on Linux and macOS libuv's chdir and execve errnos).
        var directoryShell = Directory.CreateDirectory(Path.Combine(temp.Root, "shell-dir" + (OperatingSystem.IsWindows() ? ".exe" : ""))).FullName;
        var emptyShell = temp.File("empty" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await File.WriteAllBytesAsync(emptyShell, []);
        var textShell = temp.File("text" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await File.WriteAllTextAsync(textShell, "hello");
        var fileCwd = temp.File("not-a-directory.txt");
        await File.WriteAllTextAsync(fileCwd, "x");
        async Task<string> Run(string shellPath, string? cwd = null) => await Failure(() => new NativeShellOperations(shellPath,
            ImmutableDictionary<string, string>.Empty, temp.Root).ExecuteAsync("echo hi", cwd ?? temp.Root, _ => ValueTask.CompletedTask, default).AsTask());
        if (OperatingSystem.IsWindows())
        {
            Equal($"spawn {directoryShell} ENOENT", await Run(directoryShell), "directory shell");
            Equal("spawn EFTYPE", await Run(emptyShell), "empty executable");
            Equal("spawn UNKNOWN", await Run(textShell), "text executable");
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Equal($"spawn {cmd} ENOENT", await Run(cmd, fileCwd), "file working directory");
            // libuv's search appends .com and .exe: an existing file without an extension is not run, the .exe beside it is.
            var bare = temp.File("noext"); await File.WriteAllTextAsync(bare, "x");
            Equal($"spawn {bare} ENOENT", await Run(bare), "extensionless file");
            Equal("<no error>", await Run(CopyCmd(temp.File("cmdcopy.exe"))[..^4]), "appended .exe runs");
        }
        else
        {
            Equal($"spawn {directoryShell} EACCES", await Run(directoryShell), "directory shell");
            Equal($"spawn {textShell} EACCES", await Run(textShell), "not executable");
            File.SetUnixFileMode(textShell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (OperatingSystem.IsMacOS()) Equal("spawn ENOEXEC", await Run(textShell), "neither a binary nor a script");
            else
            {
                // glibc execvp (Linux libuv) runs it as a /bin/sh script: "hello" is not found (Node 18 and 22: exit 127).
                var exit = await new PiSharp.Tools.Processes.Unix.PosixShellOperations(ShellDiscovery.ForBash(textShell),
                    ImmutableDictionary<string, string>.Empty.Add("PATH", "/usr/bin:/bin"), temp.Root)
                    .ExecuteAsync("echo hi", temp.Root, _ => ValueTask.CompletedTask, default);
                Equal(127, exit ?? -1, "text file runs as a script");
            }
            Equal("spawn ENOTDIR", await Run("/bin/sh", fileCwd), "file working directory");
            Equal("spawn ENOTDIR", await Run(Path.Combine(fileCwd, "sh")), "file in the shell path");
        }
        // libuv's Win32 translation and Node's split between emitted and thrown spawn errors.
        Equal("EPERM", PiSharp.Tools.NodeArgumentErrors.WindowsErrorCode(5), "ERROR_ACCESS_DENIED");
        Equal("EACCES", PiSharp.Tools.NodeArgumentErrors.WindowsErrorCode(740), "ERROR_ELEVATION_REQUIRED");
        Equal("ENOENT", PiSharp.Tools.NodeArgumentErrors.WindowsErrorCode(267), "ERROR_DIRECTORY");
        Equal("spawn /x EACCES", PiSharp.Tools.NodeArgumentErrors.SpawnFailure("/x", "EACCES"), "emitted");
        Equal("spawn EPERM", PiSharp.Tools.NodeArgumentErrors.SpawnFailure("/x", "EPERM"), "thrown");

        static string CopyCmd(string target) { File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), target); return target; }
        static async Task<string> Failure(Func<Task> run)
        {
            try { await run(); return "<no error>"; }
            catch (ToolSourceErrorException error) { return error.Message; }
        }
    }

    private sealed class NoRunner : IProcessRunner
    {
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The tool ran.");
    }

    private static ToolInvocation Invocation(string name, string arguments)
    {
        var call = new ToolCallContent(name + "-call", name, JsonData.Parse(arguments));
        return new(new("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private static string Text(ToolResult result) => string.Concat(result.Content.Select(content => content.Text));
    private sealed class Allow : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("pisharp-edge-").FullName;
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, got <{actual}>.");
    }
}
