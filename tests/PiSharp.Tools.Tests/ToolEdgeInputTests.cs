// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/read.ts, write.ts, edit.ts, ls.ts and bash.ts.
// Inputs upstream's validateToolArguments admits reach the tool's execute; the expected texts were captured by running the installed
// Pi 1.1.0 tools with node (validateToolArguments, then execute) on the same files.
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class ToolEdgeInputTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("edge read offset and limit follow the source number arithmetic", ReadNumbers),
        ("edge empty paths reach the file system as the source tools do", EmptyPaths),
        ("edge pi ls limits follow the source number arithmetic", LsNumbers),
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
