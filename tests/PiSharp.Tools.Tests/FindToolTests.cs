using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class FindToolTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("find admitted temp glob executor receives original pattern filters root and owned enumeration", TempGlob),
        ("find rejects unsupported initial and transformed glob syntax without executor effects", Unsupported),
        ("find retains executor order duplicates relative absolute and directory suffix results", PathOrder),
        ("find custom branch exact limit empty absent details and byte truncation notices", Limits),
        ("find final policy sees exact transformed target pattern limit and denial prevents execution", Policy),
        ("find root executor errors result bounds and escaping paths retain error results", Errors),
        ("find cancellation joins original executor enumeration and cleanup before settlement", Cancellation)
    ];
    private static async Task TempGlob()
    {
        using var temp = new Temp();
        Directory.CreateDirectory(Path.Combine(temp.Root, "nested")); Directory.CreateDirectory(Path.Combine(temp.Root, ".git"));
        Directory.CreateDirectory(Path.Combine(temp.Root, "node_modules"));
        foreach (var file in new[] { "a.txt", ".dot.txt", "other.md", "nested/b.txt", ".git/hidden.txt", "node_modules/vendor.txt" })
            await File.WriteAllTextAsync(Path.Combine(temp.Root, file), "fixture");
        var executor = new TempExecutor(temp.Root); var tool = new FindTool(temp.Root, temp.Root, executor);
        var result = await Invoke(tool, new { pattern = "*.txt", path = "" });
        Check(!result.IsError, "Supported fixture glob failed."); Equal("nested/b.txt\na.txt\n.dot.txt", Text(result));
        Check(!result.HasProperty("details"), "Ordinary find fabricated details.");
        Check(executor.Last is { Pattern: "*.txt", Limit: 1000 } && executor.Last.SearchPath == await Canonical(temp.Root), "Pattern/root/default limit changed.");
        Check(executor.Last!.Ignore.SequenceEqual(["**/node_modules/**", "**/.git/**"]), "Source custom ignore filters changed.");
        var parameters = tool.Declaration.Value.GetProperty("parameters");
        Equal("pattern", parameters.GetProperty("required")[0].GetString()!);
        Equal("number", parameters.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString()!);
        var invoker = tool.CreateInvoker(new Permit()); Check(ReferenceEquals(tool.CreateDefinition(invoker).Executor, invoker), "Definition bypassed mandatory policy.");
    }
    private static async Task Unsupported()
    {
        using var temp = new Temp(); var executor = new Fake(); var tool = new FindTool(temp.Root, temp.Root, executor);
        foreach (var pattern in new[] { "src/**/*.txt", "[ab].txt", "{a,b}.txt", "a?.txt", "", "*.txt\0" })
            Check((await Invoke(tool, new { pattern })).Failure?.Kind == ToolFailureKind.InvalidArguments, "Unsupported glob was approximated.");
        foreach (var limit in new[] { 0d, -1d, 1.5, 10001d })
            Check((await Invoke(tool, new { pattern = "*.txt", limit })).Failure?.Kind == ToolFailureKind.InvalidArguments, "Unsupported limit was coerced.");
        ToolActionTransform unsupported = (_, action, _) => ValueTask.FromResult(action with
        { Arguments = JsonData.Parse(JsonSerializer.Serialize(new { pattern = "**/*.txt", path = action.Target, limit = 1 })) });
        Check((await Invoke(tool, new { pattern = "*.txt" }, transforms: [unsupported])).Failure?.Kind == ToolFailureKind.InvalidArguments, "Transformed unsupported glob admitted.");
        Check(executor.Calls == 0, "Rejected arguments reached the executor.");
    }
    private static async Task PathOrder()
    {
        using var temp = new Temp(); var executor = new Fake { Paths = ["z.txt", Path.Combine(temp.Root, "nested", "a.txt"), "z.txt", "folder.txt" + Path.DirectorySeparatorChar] };
        var result = await Invoke(new(temp.Root, temp.Root, executor), new { pattern = "*.txt" });
        Equal("z.txt\nnested/a.txt\nz.txt\nfolder.txt/", Text(result));
        Check(!result.IsError && !result.HasProperty("details"), "Original executor paths were sorted, deduplicated or changed.");
    }
    private static async Task Limits()
    {
        // Output-format fixture domain: synthetic paths, not native filesystem names. The
        // 1,000-character components below exceed Windows filename admission; canonical
        // containment and real filesystem behavior have separate PathOrder/Errors/TempGlob coverage.
        using var temp = new Temp(); var executor = new Fake(); var files = new FakeFiles();
        var tool = new FindTool(temp.Root, temp.Root, executor, files);
        var empty = await Invoke(tool, new { pattern = "*.txt" }); Equal("No files found matching pattern", Text(empty));
        Check(!empty.HasProperty("details"), "Empty result fabricated details.");
        executor.Paths = ["one.txt", "two.txt"];
        var exact = await Invoke(tool, new { pattern = "*.txt", limit = 2 });
        Equal("one.txt\ntwo.txt\n\n[2 results limit reached]", Text(exact)); Equal(2, exact.Details.Value.GetProperty("resultLimitReached").GetInt32());
        executor.Paths = Enumerable.Range(0, 59).Select(i => i.ToString("D3") + new string('x', 993) + ".txt").ToImmutableArray();
        var truncated = await Invoke(tool, new { pattern = "*.txt", limit = 59 });
        Check(!truncated.IsError, "Synthetic byte-limited find failed: " + Text(truncated));
        var details = truncated.Details.Value.GetProperty("truncation"); Equal(59, details.GetProperty("totalLines").GetInt32());
        Equal(51, details.GetProperty("outputLines").GetInt32()); Equal(51050, details.GetProperty("outputBytes").GetInt32());
        Equal(9007199254740991L, details.GetProperty("maxLines").GetInt64());
        Check(!details.GetProperty("lastLinePartial").GetBoolean(), "Find emitted a partial line.");
        Check(Text(truncated).EndsWith("[59 results limit reached. 50.0KB limit reached]", StringComparison.Ordinal), "Custom branch used fd's longer notice or wrong notice order.");
        Equal(51200, details.GetProperty("maxBytes").GetInt32());
        Check(files.CanonicalPaths.Contains(Path.GetFullPath(executor.Paths[0], temp.Root)), "Synthetic paths bypassed canonical validation.");

        // UTF-8 bytes and escaped JSON characters have different budgets. Retain the
        // duplicated truncation.content plus ordered notices through the unchanged invoker.
        executor.Paths = Enumerable.Range(0, 59).Select(i => i.ToString("D3") + new string('\u00e9', 993) + ".txt").ToImmutableArray();
        var unicode = await Invoke(tool, new { pattern = "*.txt", limit = 59 });
        Check(!unicode.IsError, "Escaped UTF-8 find failed: " + Text(unicode));
        var unicodeDetails = unicode.Details.Value.GetProperty("truncation");
        Equal(59, unicodeDetails.GetProperty("totalLines").GetInt32());
        Equal(117645, unicodeDetails.GetProperty("totalBytes").GetInt32());
        Equal(25, unicodeDetails.GetProperty("outputLines").GetInt32());
        Equal(49849, unicodeDetails.GetProperty("outputBytes").GetInt32());
        Equal(string.Join('\n', executor.Paths.Take(25)), unicodeDetails.GetProperty("content").GetString()!);
        Check(!unicodeDetails.GetProperty("lastLinePartial").GetBoolean(), "UTF-8 find emitted a partial line.");
        Check(Text(unicode).EndsWith("[59 results limit reached. 50.0KB limit reached]", StringComparison.Ordinal), "UTF-8 find changed notice ordering.");
    }
    private static async Task Policy()
    {
        using var temp = new Temp(); var target = Path.Combine(temp.Root, "nested"); Directory.CreateDirectory(target);
        var executor = new Fake { Paths = ["one.md"] }; var tool = new FindTool(temp.Root, temp.Root, executor); PreparedToolAction? admitted = null;
        ToolActionTransform retarget = (_, action, _) => ValueTask.FromResult(action with { Target = target,
            Arguments = JsonData.Parse(JsonSerializer.Serialize(new { pattern = "*.md", path = target, limit = 1 })) });
        var denied = await Invoke(tool, new { pattern = "*.txt" }, new Permit(_ => false), [retarget]);
        Check(denied.Failure?.Kind == ToolFailureKind.Blocked && executor.Calls == 0, "Denied find executed.");
        var allowed = await Invoke(tool, new { pattern = "*.txt" }, new Permit(action => { admitted = action; return action.Target == target; }), [retarget]);
        Check(!allowed.IsError && executor.Last is { Pattern: "*.md", Limit: 1 } && executor.Last.SearchPath == target, "Final action did not reach the exact executor request.");
        Check(admitted is not null && admitted.Arguments.Value.GetProperty("pattern").GetString() == "*.md", "Policy lost transformed glob.");
        Check(!await tool.Adapter.ValidateAsync((admitted ?? throw new InvalidOperationException("Policy not called")) with { Target = temp.Root }, default), "Target/arguments mismatch admitted.");
    }
    private static async Task Errors()
    {
        using var temp = new Temp(); var executor = new Fake(); var files = new FakeFiles(); var tool = new FindTool(temp.Root, temp.Root, executor, files);
        files.Exists = false; Equal("Path not found: " + temp.Root, Text(await Invoke(tool, new { pattern = "*.txt" })));
        Check(executor.Calls == 0, "Missing root reached executor."); files.Exists = true;
        executor.Work = (_, _) => throw new IOException("admitted fixture executor failed");
        var failed = await Invoke(tool, new { pattern = "*.txt" }); Equal("admitted fixture executor failed", Text(failed));
        Check(failed.Failure?.Kind == ToolFailureKind.ExecutionError, "Executor error classification changed."); executor.Work = null;
        executor.Paths = ["../outside.txt"];
        Check((await Invoke(tool, new { pattern = "*.txt" })).IsError && files.CanonicalPaths.All(path => !path.EndsWith("outside.txt", StringComparison.Ordinal)), "Lexically escaping result was probed or displayed.");
        executor.Paths = ["link.txt"]; files.Canonical = path => path.EndsWith("link.txt", StringComparison.Ordinal) ? Path.Combine(Path.GetDirectoryName(temp.Root)!, "outside-fake.txt") : path;
        Check((await Invoke(tool, new { pattern = "*.txt" })).IsError, "Canonical escaping result displayed.");
        files.Canonical = null; executor.Paths = ["one", "two"];
        Check((await Invoke(tool, new { pattern = "*.txt", limit = 1 })).IsError, "Executor exceeded limit without rejection.");
        executor.Paths = default; Check((await Invoke(tool, new { pattern = "*.txt" })).IsError, "Uninitialized result admitted.");
    }
    private static async Task Cancellation()
    {
        using var temp = new Temp(); using var cancel = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var cleaned = false;
        var executor = new Fake(); executor.Work = async (_, token) =>
        { entered.TrySetResult(); try { await release.Task; token.ThrowIfCancellationRequested(); return ImmutableArray.Create("late.txt"); } finally { cleaned = true; } };
        Task<ToolResult>? original = null;
        try
        {
            original = Invoke(new(temp.Root, temp.Root, executor), new { pattern = "*.txt" }, token: cancel.Token);
            var first = await Task.WhenAny(entered.Task, original);
            Check(first == entered.Task && entered.Task.IsCompletedSuccessfully, "Invocation settled before executor entry.");
            cancel.Cancel(); Check(!original.IsCompleted && !cleaned, "Canceled find detached executor cleanup.");
            release.TrySetResult(); var result = await original;
            Check(cleaned && result.Failure?.Kind == ToolFailureKind.Canceled, "Find settled before original executor cleanup.");
        }
        finally { try { cancel.Cancel(); } finally { release.TrySetResult(); if (original is not null) await original; } }
    }
    private sealed class Fake : IFindExecutor
    {
        public ImmutableArray<string> Paths = []; public int Calls; public FindExecutionRequest? Last;
        public Func<FindExecutionRequest, CancellationToken, ValueTask<ImmutableArray<string>>>? Work;
        public bool SupportsPattern(string pattern) => pattern is "*.txt" or "*.md";
        public ValueTask<ImmutableArray<string>> FindAsync(FindExecutionRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Last = request; return Work?.Invoke(request, token) ?? ValueTask.FromResult(Paths); }
    }
    // Authored admitted executor for a finite clean fixture domain; not an fd or gitignore implementation.
    private sealed class TempExecutor(string root) : IFindExecutor
    {
        private readonly IDirectoryFileOperations _files = new LocalFileOperations(); public FindExecutionRequest? Last;
        public bool SupportsPattern(string pattern) => pattern is "*.txt" or "*.md";
        public async ValueTask<ImmutableArray<string>> FindAsync(FindExecutionRequest request, CancellationToken token)
        {
            Last = request;
            if (!SupportsPattern(request.Pattern) || !request.Ignore.SequenceEqual(["**/node_modules/**", "**/.git/**"])) throw new ArgumentException("Unsupported fixture executor request.");
            var expected = await _files.CanonicalizeAsync(root, token);
            if (request.SearchPath != expected) throw new ArgumentException("Fixture executor root changed.");
            var paths = ImmutableArray.CreateBuilder<string>(); await Visit(request.SearchPath, "", 0); return paths.ToImmutable();
            async Task Visit(string path, string prefix, int depth)
            {
                token.ThrowIfCancellationRequested(); if (depth > 8) throw new IOException("Fixture traversal bound exceeded.");
                var names = await _files.ReadDirectoryAsync(path, 100, 16_384, token);
                foreach (var name in names.OrderDescending(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); if (paths.Count >= request.Limit) return;
                    if (name is ".git" or "node_modules") continue;
                    if (name is ".gitignore" or ".ignore" || name.Contains(Path.DirectorySeparatorChar)) throw new IOException("Unsupported fixture tree.");
                    var full = Path.Combine(path, name); var relative = prefix + name;
                    var canonical = await _files.CanonicalizeAsync(full, token);
                    if (canonical != full) throw new IOException("Fixture links are unsupported.");
                    var directory = await _files.IsDirectoryAsync(full, token);
                    if (name.EndsWith(request.Pattern[1..], StringComparison.Ordinal)) paths.Add(relative + (directory ? "/" : ""));
                    if (directory) await Visit(full, relative + "/", depth + 1);
                }
            }
        }
    }
    private sealed class FakeFiles : IFileOperations
    {
        public bool Exists = true; public List<string> CanonicalPaths = []; public Func<string, string>? Canonical;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Exists); }
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); CanonicalPaths.Add(path); return ValueTask.FromResult(Canonical?.Invoke(path) ?? path); }
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maxBytes, CancellationToken token) => throw new InvalidOperationException("Unexpected file read");
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => throw new InvalidOperationException("Unexpected mutation");
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token) => throw new InvalidOperationException("Unexpected mutation");
    }
    private sealed class Permit(Func<PreparedToolAction, bool>? allow = null) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(allow?.Invoke(action) ?? true)); }
    }
    private sealed class Temp : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())); public string Root { get; }
        public Temp() { Root = Path.Combine(_parent, "pisharp-find-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        { if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-find-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixture root."); Directory.Delete(Root, recursive: true); }
    }
    private static async Task<ToolResult> Invoke(FindTool tool, object args, Permit? policy = null, IEnumerable<ToolActionTransform>? transforms = null, CancellationToken token = default)
    {
        var call = new ToolCallContent("find-fixture", "find", JsonData.Parse(JsonSerializer.Serialize(args)));
        return await tool.CreateInvoker(policy ?? new Permit(), transforms).ExecuteAsync(new(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), token);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(ToolResult result) => result.Content.Single().Text;
    private static async Task<string> Canonical(string path) => await new LocalFileOperations().CanonicalizeAsync(path, default);
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
