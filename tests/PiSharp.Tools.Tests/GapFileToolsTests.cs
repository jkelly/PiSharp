using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class GapFileToolsTests
{
    public static (string Name, Func<Task> Run)[] Cases() => [("gap files streaming and byte preservation", ReadAndEdit), ("gap files rg context globs UTF16 and normalized limits", Search)];
    private static async Task ReadAndEdit()
    {
        using var temp = new Temp();
        var original = "\ufeffone\r\ntwo\nthree\rfour\r\nfive\n";
        Equal(original.Replace("two", "TWO").Replace("four", "FOUR"), EditPlan.Create(original, [new("two", "TWO"), new("four", "FOUR")], "file").Content);
        Equal("left \u201cquote\u201d  \uff21\r\nchanged\nlast\r", EditPlan.Create("left \u201cquote\u201d  \uff21\r\nsmart \u2018word\u2019\nlast\r", [new("smart 'word'", "changed")], "file").Content);
        var path = Path.Combine(temp.Root, "large");
        await using (var file = File.Create(path)) { await file.WriteAsync(Encoding.UTF8.GetBytes("first\nwanted\n")); file.SetLength(65L * 1024 * 1024); }
        var tools = new ReadWriteTools(temp.Root, temp.Root);
        Equal("wanted\n\n[1 more lines in file. Use offset=3 to continue.]", Text(await Invoke(tools.CreateInvoker(new Allow()), "read", new { path, offset = 2, limit = 1 })));
        var legacy = new ReadWriteTools(temp.Root, temp.Root, new Legacy());
        var rejected = await Invoke(legacy.CreateInvoker(new Allow()), "read", new { path, offset = 2, limit = 1 });
        Check(rejected.IsError && rejected.Details.ToString().Contains("ResourceLimit"), "Legacy adapter must retain bounded fallback.");
        var small = Path.Combine(temp.Root, "small"); await File.WriteAllTextAsync(small, "zero\r\none\n\ntwo\n");
        var streaming = new ReadWriteTools(temp.Root, temp.Root, options: new(MaximumReadBytes: 1));
        foreach (var offset in new[] { -2d, 0d, 1d, 1.5, 2d, 3d, 4d, 5d }) foreach (var limit in new[] { -2d, 0d, 1d, 1.5, 2d, 20d })
        { var args = new { path = small, offset, limit }; Equal(Text(await Invoke(tools.CreateInvoker(new Allow()), "read", args)), Text(await Invoke(streaming.CreateInvoker(new Allow()), "read", args))); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await new LocalFileOperations().OpenReadAsync(path, canceled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    }
    private static async Task Search()
    {
        using var temp = new Temp(); var directory = Directory.CreateDirectory(Path.Combine(temp.Root, "search")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(directory, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "utf16.txt"), "before\r\nneedle\r\nafter\r\n", Encoding.Unicode);
        var tool = new PiGrepTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>(Executable("rg")));
        async Task<ToolResult> Run(object args) => await Invoke(new ToolInvoker([tool], new Allow()), "grep", args);
        var result = await Run(new { pattern = "needle", path = directory, glob = "./src/*.txt", context = 1, limit = 1 });
        Equal("src/utf16.txt-1- before\nsrc/utf16.txt:2: needle\nsrc/utf16.txt-3- after", Text(result));
        Check(!result.HasProperty("details"), "Exact match limit incorrectly reported truncation.");
        await File.WriteAllTextAsync(Path.Combine(nested, "utf16.txt"), "needle\nneedle\n", Encoding.Unicode);
        foreach (var limit in new[] { 0d, -1d, 1.9 })
        { var limited = await Run(new { pattern = "needle", path = directory, glob = "src/*.txt", limit }); Check(limited.Details.ToString().Contains("matchLimitReached"), "Missing extra-match proof."); Check(!Text(limited).Contains(":2:"), "Normalized limit admitted a second match."); }
        var find = new PiFindTool(directory, directory, _ => ValueTask.FromResult<string?>(Executable("fd")));
        var exact = await Invoke(new ToolInvoker([find], new Allow()), "find", new { pattern = "*.txt", limit = 1 });
        Check(!exact.HasProperty("details"), "Exact find limit reported truncation.");
        await File.WriteAllTextAsync(Path.Combine(directory, "other.txt"), "needle");
        foreach (var limit in new[] { 0d, -2d, 1.9 })
        { var capped = await Invoke(new ToolInvoker([find], new Allow()), "find", new { pattern = "*.txt", limit }); Check(capped.Details.ToString().Contains("resultLimitReached"), "Find did not normalize limit."); Check(Text(capped).Split("\n\n")[0].Split('\n').Length == 1, "Find exceeded limit."); }
        var link = Path.Combine(temp.Root, "alias");
        try { Directory.CreateSymbolicLink(link, directory); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { Console.WriteLine("INFO symlink fixture unavailable: " + e.GetType().Name); return; }
        Check(Text(await Run(new { pattern = "needle", path = link, glob = "./src/*.txt" })).Contains("src/utf16.txt:1: needle"), "Symlink rooted glob failed.");
    }
    private static string Executable(string name)
    { foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { var path = Path.Combine(folder, name + (OperatingSystem.IsWindows() ? ".exe" : "")); if (File.Exists(path)) return path; } throw new Exception("Offline fixture requires installed " + name); }
    private sealed class Legacy : IFileOperations
    {
        private readonly LocalFileOperations inner = new();
        public ValueTask<bool> ExistsAsync(string p, CancellationToken t) => inner.ExistsAsync(p, t);
        public ValueTask<string> CanonicalizeAsync(string p, CancellationToken t) => inner.CanonicalizeAsync(p, t);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string p, int n, CancellationToken t) => inner.ReadAsync(p, n, t);
        public ValueTask CreateDirectoryAsync(string p, CancellationToken t) => inner.CreateDirectoryAsync(p, t);
        public ValueTask WriteAsync(string p, ReadOnlyMemory<byte> b, CancellationToken t) => inner.WriteAsync(p, b, t);
    }
    private static Task<ToolResult> Invoke(ToolInvoker invoker, string name, object args)
    { var call = new ToolCallContent("gap", name, JsonData.Parse(JsonSerializer.Serialize(args))); return invoker.ExecuteAsync(new(new("api", "provider", "model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), default).AsTask(); }
    private static string Text(ToolResult result) { Check(!result.IsError, result.Content.FirstOrDefault()?.Text ?? "error"); return string.Concat(result.Content.Select(c => c.Text)); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Equal(string expected, string actual) => Check(expected == actual, $"Expected <{expected}>, got <{actual}>");
    private sealed class Allow : IToolActionPolicy { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Temp : IDisposable { public string Root { get; } = Directory.CreateTempSubdirectory("pisharp-gap-files-").FullName; public void Dispose() { Directory.Delete(Root, true); } }
}
