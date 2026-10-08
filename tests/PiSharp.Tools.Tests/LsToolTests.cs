using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

internal static class LsToolTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("ls real owned directory includes dotfiles suffixes and case-insensitive stable ordering", RealDirectory),
        ("ls source numeric limits empty output and absent details", Limits),
        ("ls skips failed child stats and distinguishes missing file and directory read failures", Errors),
        ("ls complete-line byte truncation and combined notices preserve source metadata", Truncation),
        ("ls mandatory policy sees exact transformed canonical target and denied listing has no directory effects", Policy),
        ("ls rejects malformed names and canonical children outside admitted listing root", Boundaries),
        ("ls cancellation joins original blocked directory call and cleanup before settlement", Cancellation)
    ];

    private static async Task RealDirectory()
    {
        using var temp = new Temp();
        Directory.CreateDirectory(Path.Combine(temp.Root, "Beta"));
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "alpha"), "a");
        await File.WriteAllTextAsync(Path.Combine(temp.Root, ".hidden"), "h");
        var tool = new LsTool(temp.Root, temp.Root);
        Check(tool.Declaration.Value.GetProperty("parameters").GetProperty("properties").GetProperty("limit").GetProperty("type").GetString() == "number", "Limit schema narrowed to integer.");
        var invoker = tool.CreateInvoker(new Permit());
        Check(ReferenceEquals(tool.CreateDefinition(invoker).Executor, invoker), "Definition bypassed policy.");
        var result = await Invoke(tool, new { path = "" });
        Equal(".hidden\nalpha\nBeta/", Text(result)); Check(!result.HasProperty("details"), "Ordinary listing fabricated details.");
        var stable = new Fake(temp.Root) { Names = ["a", "A", "b"] };
        Equal("a\nA\nb", Text(await Invoke(new(temp.Root, temp.Root, stable), new { })));
    }

    private static async Task Limits()
    {
        using var temp = new Temp(); var files = new Fake(temp.Root) { Names = ["c", "a", "b"] };
        var tool = new LsTool(temp.Root, temp.Root, files);
        var fractional = await Invoke(tool, new { limit = 1.5 });
        Equal("a\nb\n\n[1.5 entries limit reached. Use limit=3 for more]", Text(fractional));
        Equal(1.5, fractional.Details.Value.GetProperty("entryLimitReached").GetDouble());
        var exact = await Invoke(tool, new { limit = 3 }); Equal("a\nb\nc", Text(exact)); Check(!exact.HasProperty("details"), "Exact count incorrectly marked reached.");
        foreach (var limit in new[] { 0d, -1d })
        { var empty = await Invoke(tool, new { limit }); Equal("(empty directory)", Text(empty)); Check(!empty.HasProperty("details"), "Zero/negative output has limit details."); }
        files.Names = []; Equal("(empty directory)", Text(await Invoke(tool, new { })));
        files.Names = Enumerable.Range(0, 501).Select(i => i.ToString("D4")).ToImmutableArray();
        var capped = await Invoke(tool, new { }); Equal(500d, capped.Details.Value.GetProperty("entryLimitReached").GetDouble());
        Check(Text(capped).EndsWith("[500 entries limit reached. Use limit=1000 for more]", StringComparison.Ordinal), "Default limit notice changed.");
        Check((await Invoke(tool, new { limit = 100001 })).IsError, "Unbounded requested limit admitted.");
    }

    private static async Task Errors()
    {
        using var temp = new Temp(); var files = new Fake(temp.Root) { Names = ["bad", "good"] };
        files.Stat = (path, _) => path == Path.Combine(temp.Root, "bad") ? throw new IOException("gone") : ValueTask.FromResult(path == temp.Root);
        var tool = new LsTool(temp.Root, temp.Root, files);
        Equal("good", Text(await Invoke(tool, new { limit = 1 })));
        files.Exists = false; var missing = await Invoke(tool, new { });
        Equal("Path not found: " + temp.Root, Text(missing)); Check(missing.Failure?.Kind == ToolFailureKind.ExecutionError, "Missing-path classification changed.");
        files.Exists = true; files.Stat = (_, _) => ValueTask.FromResult(false);
        Equal("Not a directory: " + temp.Root, Text(await Invoke(tool, new { })));
        files.Stat = (path, _) => ValueTask.FromResult(path == temp.Root); files.Read = _ => throw new IOException("fixture denied");
        Equal("Cannot read directory: fixture denied", Text(await Invoke(tool, new { })));
    }

    private static async Task Truncation()
    {
        using var temp = new Temp(); var files = new Fake(temp.Root)
        { Names = Enumerable.Range(0, 60).Select(i => i.ToString("D3") + new string('x', 997)).ToImmutableArray() };
        var result = await Invoke(new(temp.Root, temp.Root, files), new { limit = 59 });
        Check(!result.IsError, "Truncated result rejected by native result budget.");
        var truncation = result.Details.Value.GetProperty("truncation");
        Equal(59, truncation.GetProperty("totalLines").GetInt32()); Equal(51, truncation.GetProperty("outputLines").GetInt32());
        Equal(51050, truncation.GetProperty("outputBytes").GetInt32()); Equal(9007199254740991L, truncation.GetProperty("maxLines").GetInt64());
        Check(!truncation.GetProperty("lastLinePartial").GetBoolean() && !truncation.GetProperty("firstLineExceedsLimit").GetBoolean(), "Partial line admitted.");
        Equal("bytes", truncation.GetProperty("truncatedBy").GetString()!);
        Check(Text(result).EndsWith("[59 entries limit reached. Use limit=118 for more. 50.0KB limit reached]", StringComparison.Ordinal), "Combined notice ordering changed.");
    }

    private static async Task Policy()
    {
        using var temp = new Temp(); var other = Path.Combine(temp.Root, "other");
        var files = new Fake(temp.Root) { Names = ["one"] }; files.Stat = (path, _) => ValueTask.FromResult(path == other || path == temp.Root);
        var tool = new LsTool(temp.Root, temp.Root, files); PreparedToolAction? authorized = null;
        ToolActionTransform retarget = (_, action, _) => ValueTask.FromResult(action with
        { Target = other, Arguments = JsonData.Parse(JsonSerializer.Serialize(new { path = other, limit = 1 })) });
        var deny = new Permit(action => { authorized = action; return false; });
        var denied = await Invoke(tool, new { }, deny, [retarget]);
        Check(denied.Failure?.Kind == ToolFailureKind.Blocked && files.ReadCalls == 0 && files.StatCalls == 0, "Denied listing reached directory operations.");
        Equal(other, authorized!.Target);
        var allowed = await Invoke(tool, new { }, new Permit(action => { authorized = action; return action.Target == other; }), [retarget]);
        Equal("one", Text(allowed)); Equal(other, files.LastRead!); Check(authorized!.Arguments.Value.GetProperty("limit").GetDouble() == 1, "Final policy lost transformed limit.");
        var malformed = await tool.Adapter.ValidateAsync((authorized ?? throw new InvalidOperationException("Policy was not called.")) with { Target = temp.Root }, default);
        Check(!malformed, "Target/argument mismatch admitted.");
    }

    private static async Task Boundaries()
    {
        using var temp = new Temp(); var files = new Fake(temp.Root) { Names = ["../escape"] };
        var tool = new LsTool(temp.Root, temp.Root, files);
        Check((await Invoke(tool, new { })).IsError && files.StatCalls == 1, "Malformed name caused child traversal.");
        files.Names = ["link", "safe"];
        // This is a fake identity only: no filesystem operation accesses the sibling path.
        files.Canonical = path => path == Path.Combine(temp.Root, "link") ? Path.Combine(Path.GetDirectoryName(temp.Root)!, "outside-fake") : path;
        Equal("safe", Text(await Invoke(tool, new { })));
        Check(files.StatPaths.All(path => !path.Contains("outside-fake", StringComparison.Ordinal)), "Escaping link target was statted.");
        files.Names = Enumerable.Repeat("x", LsTool.MaximumEntries + 1).ToImmutableArray();
        Check((await Invoke(tool, new { })).IsError, "Host exceeded entry bound.");
    }

    private static async Task Cancellation()
    {
        using var temp = new Temp(); using var cancellation = new CancellationTokenSource();
        var entered = Gate(); var release = Gate(); var cleaned = false;
        var files = new Fake(temp.Root);
        files.Read = async token =>
        {
            entered.TrySetResult();
            try { await release.Task; token.ThrowIfCancellationRequested(); return ImmutableArray.Create("late"); }
            finally { cleaned = true; }
        };
        Task<ToolResult>? original = null;
        try
        {
            original = Invoke(new(temp.Root, temp.Root, files), new { }, token: cancellation.Token);
            var first = await Task.WhenAny(entered.Task, original);
            Check(first == entered.Task && entered.Task.IsCompletedSuccessfully,
                "Original invocation settled before entering the held directory call.");
            cancellation.Cancel();
            Check(!original.IsCompleted && !cleaned, "Cancellation detached the original filesystem call.");
            release.TrySetResult(); var result = await original;
            Check(cleaned && result.Failure?.Kind == ToolFailureKind.Canceled, "Canceled settlement overtook cleanup.");
        }
        finally
        {
            try { cancellation.Cancel(); }
            finally
            {
                release.TrySetResult();
                if (original is not null) await original;
            }
        }
    }

    private sealed class Fake(string root) : IDirectoryFileOperations
    {
        public ImmutableArray<string> Names = []; public bool Exists = true;
        public Func<string, string>? Canonical;
        public Func<string, CancellationToken, ValueTask<bool>>? Stat;
        public Func<CancellationToken, ValueTask<ImmutableArray<string>>>? Read;
        public int ReadCalls, StatCalls; public string? LastRead; public List<string> StatPaths = [];
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Exists); }
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Canonical?.Invoke(path) ?? path); }
        public ValueTask<bool> IsDirectoryAsync(string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); StatCalls++; StatPaths.Add(path); return Stat?.Invoke(path, token) ?? ValueTask.FromResult(path == root); }
        public ValueTask<ImmutableArray<string>> ReadDirectoryAsync(string path, int maxEntries, int maxCharacters, CancellationToken token)
        { token.ThrowIfCancellationRequested(); ReadCalls++; LastRead = path; return Read?.Invoke(token) ?? ValueTask.FromResult(Names); }
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maxBytes, CancellationToken token) => throw new InvalidOperationException("ls read file contents");
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => throw new InvalidOperationException("ls mutated directory");
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token) => throw new InvalidOperationException("ls wrote file");
    }
    private sealed class Permit(Func<PreparedToolAction, bool>? allow = null) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(allow?.Invoke(action) ?? true)); }
    }
    private sealed class Temp : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public Temp() { Root = Path.Combine(_parent, "pisharp-ls-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-ls-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned fixture root.");
            Directory.Delete(Root, recursive: true);
        }
    }
    private static async Task<ToolResult> Invoke(LsTool tool, object args, Permit? policy = null,
        IEnumerable<ToolActionTransform>? transforms = null, CancellationToken token = default)
    {
        var call = new ToolCallContent("ls-fixture", "ls", JsonData.Parse(JsonSerializer.Serialize(args)));
        var invocation = new ToolInvocation(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        return await tool.CreateInvoker(policy ?? new Permit(), transforms).ExecuteAsync(invocation, token);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(ToolResult value) => value.Content.Single().Text;
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
