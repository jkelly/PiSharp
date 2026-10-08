using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;

// Registration belongs to the coordinator's single test integrator. These groups must be directly
// awaited, like GrepToolTests: the held original grant/read cannot be abandoned by a timeout wrapper.
internal static class GrepContextHostAdapterTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("grep host adapter forwards exact canonical admission budget and borrows host lifetime", HostAdapterExactBorrowing),
        ("grep host adapter preserves read denial and original admission failure without reads", HostAdapterDenial),
        ("grep host adapter rejects lexical canonical post-admission escapes and invalid bounds", HostAdapterContainment),
        ("grep host adapter checks returned size and lets grep reject invalid UTF8", HostAdapterReturnedBounds),
        ("grep host adapter zero positive cache final policy and denial markers compose through invoker", HostAdapterToolComposition),
        ("grep host adapter cancellation directly joins held original admission", HostAdapterAdmissionCancellation),
        ("grep host adapter cancellation directly joins original read cleanup and preserves read failure", HostAdapterReadCancellation)
    ];

    private static async Task HostAdapterExactBorrowing()
    {
        var f = new Files(); var alias = Path.Combine(f.Root, "alias.txt");
        f.Canonicalize = path => path == alias ? f.Path : path;
        var grants = 0;
        using var cancel = new CancellationTokenSource();
        var reader = new AdmittedGrepContextReader(f.Root, f, (path, maximum, token) =>
        {
            Check(path == f.Path && maximum == 32 && token == cancel.Token, "Grant did not receive exact canonical path/bound/token.");
            grants++; return ValueTask.FromResult(true);
        });
        var result = await reader.ReadAsync(alias, 32, cancel.Token);
        Check(Encoding.UTF8.GetString(result.Span) == "before\nhit\nafter" && grants == 1 && f.Reads == 1 &&
            f.LastPath == f.Path && f.LastBound == 32 && f.LastToken == cancel.Token && f.Disposals == 0,
            "Borrowed operations/path/budget/lifetime changed.");
        await f.DisposeAsync(); Check(f.Disposals == 1, "Only owning host should dispose borrowed resources.");
    }

    private static async Task HostAdapterDenial()
    {
        var f = new Files();
        var reader = new AdmittedGrepContextReader(f.Root, f, (_, _, _) => ValueTask.FromResult(false));
        await Throws<UnauthorizedAccessException>(() => reader.ReadAsync(f.Path, 32, default).AsTask());
        Check(f.Reads == 0 && f.Disposals == 0, "Denied read reached acquisition or host disposal.");
        var original = new IOException("original grant failure");
        reader = new(f.Root, f, (_, _, _) => throw original);
        Check(ReferenceEquals(await Throws<IOException>(() => reader.ReadAsync(f.Path, 32, default).AsTask()), original) && f.Reads == 0,
            "Original grant failure was replaced or ignored.");
        await Throws<ArgumentNullException>(() => { _ = new AdmittedGrepContextReader(f.Root, null!, (_, _, _) => ValueTask.FromResult(true)); return Task.CompletedTask; });
        await Throws<ArgumentNullException>(() => { _ = new AdmittedGrepContextReader(f.Root, f, null!); return Task.CompletedTask; });
    }

    private static async Task HostAdapterContainment()
    {
        foreach (var mode in new[] { "lexical", "canonical", "post-grant", "root" })
        {
            var f = new Files(); var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "outside.txt");
            var grants = 0; var canonicalCalls = 0;
            f.Canonicalize = path => mode switch
            {
                "canonical" when path == f.Path => outside,
                "root" when path == f.Root => outside,
                "post-grant" when path == f.Path && ++canonicalCalls > 1 => outside,
                _ => path
            };
            var reader = new AdmittedGrepContextReader(f.Root, f, (_, _, _) => { grants++; return ValueTask.FromResult(true); });
            await Throws<UnauthorizedAccessException>(() => reader.ReadAsync(mode == "lexical" ? outside : f.Path, 32, default).AsTask());
            Check(f.Reads == 0 && grants == (mode == "post-grant" ? 1 : 0), "Path escape reached a grant or read acquisition.");
        }
        var bounded = new Files(); var calls = 0;
        var valid = new AdmittedGrepContextReader(bounded.Root, bounded, (_, _, _) => { calls++; return ValueTask.FromResult(true); });
        foreach (var bound in new[] { 0, -1, GrepTool.MaximumContextFileBytes + 1 })
            await Throws<ArgumentOutOfRangeException>(() => valid.ReadAsync(bounded.Path, bound, default).AsTask());
        await Throws<ArgumentException>(() => valid.ReadAsync("relative.txt", 32, default).AsTask());
        await Throws<ArgumentException>(() => valid.ReadAsync(bounded.Path + "\0", 32, default).AsTask());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => valid.ReadAsync(bounded.Path, 32, cancel.Token).AsTask());
        Check(calls == 0 && bounded.Reads == 0 && bounded.Canonicalizations == 0, "Invalid/pre-canceled input caused host effects.");
    }

    private static async Task HostAdapterReturnedBounds()
    {
        var f = new Files(); var joined = false;
        f.ReadWork = (_, _, _) => { joined = true; return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[33]); };
        var reader = new AdmittedGrepContextReader(f.Root, f, (_, _, _) => ValueTask.FromResult(true));
        var error = await Throws<FileToolException>(() => reader.ReadAsync(f.Path, 32, default).AsTask());
        Check(joined && error.Failure == FileToolFailure.ResourceLimit && f.Reads == 1, "Oversized returned receipt escaped defensive bound/join.");
        foreach (var bytes in new[] { new byte[] { 0xff }, new byte[] { 0 } })
        {
            f.ReadWork = (_, _, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(bytes);
            var executor = new Executor(f.Path);
            var tool = new GrepTool(f.Root, f.Root, executor, f, reader);
            Check((await Invoke(tool, context: 1)).Failure?.Kind == ToolFailureKind.ExecutionError && executor.Joined,
                "Malformed UTF8/NUL became a context marker or success.");
        }
    }

    private static async Task HostAdapterToolComposition()
    {
        var f = new Files(); var executor = new Executor(f.Path); var grants = 0; var allow = true;
        var reader = new AdmittedGrepContextReader(f.Root, f, (_, _, _) => { grants++; return ValueTask.FromResult(allow); });
        f.ReadWork = (_, _, _) =>
        {
            Check(executor.Joined, "Context read preceded original search join.");
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("before\nhit\nafter"));
        };
        var tool = new GrepTool(f.Root, f.Root, executor, f, reader);
        Check(Text(await Invoke(tool, 0)) == "a.txt:2: receipt\na.txt:2: receipt" && f.Reads == 0 && grants == 0, "Zero context acquired a read grant.");
        var block = "a.txt-1- before\na.txt:2: hit\na.txt-3- after";
        Check(Text(await Invoke(tool, 1)) == block + "\n" + block && f.Reads == 1 && grants == 1 && f.Disposals == 0,
            "Borrowed positive read did not cache once per file or disposed host.");
        allow = false;
        var denied = await Invoke(tool, 1);
        Check(!denied.IsError && Text(denied) == "a.txt:2: (unable to read file)\na.txt:2: (unable to read file)" && f.Reads == 1 && grants == 2,
            "Read denial was relaxed or retried for duplicate matches.");
        var before = executor.Calls;
        Check((await Invoke(tool, 1, policyAllowed: false)).Failure?.Kind == ToolFailureKind.Blocked && executor.Calls == before && grants == 2,
            "Read or search escaped final-action policy.");
    }

    private static async Task HostAdapterAdmissionCancellation()
    {
        var f = new Files(); using var cancel = new CancellationTokenSource();
        var entered = Gate(); var release = Gate(); var joined = false;
        var reader = new AdmittedGrepContextReader(f.Root, f, async (_, _, _) =>
        {
            entered.TrySetResult(); try { await release.Task; return true; } finally { joined = true; }
        });
        Task<ReadOnlyMemory<byte>>? original = null;
        try
        {
            original = reader.ReadAsync(f.Path, 32, cancel.Token).AsTask();
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Grant settled before held original entry.");
            cancel.Cancel(); Check(!original.IsCompleted && !joined && f.Reads == 0, "Cancellation abandoned original grant.");
            release.TrySetResult();
            var error = await Throws<OperationCanceledException>(() => original!);
            Check(joined && error.CancellationToken == cancel.Token && f.Reads == 0 && f.Disposals == 0, "Canceled grant was not joined or reached file acquisition.");
        }
        finally { cancel.Cancel(); release.TrySetResult(); if (original is not null) { try { await original; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } } }
    }

    private static async Task HostAdapterReadCancellation()
    {
        foreach (var fail in new[] { false, true })
        {
            var f = new Files(); using var cancel = new CancellationTokenSource();
            var entered = Gate(); var release = Gate(); var joined = false; var readError = new IOException("original read failure");
            f.ReadWork = async (_, _, _) =>
            {
                entered.TrySetResult();
                try { await release.Task; if (fail) throw readError; return Encoding.UTF8.GetBytes("hit"); }
                finally { joined = true; }
            };
            var reader = new AdmittedGrepContextReader(f.Root, f, (_, _, _) => ValueTask.FromResult(true));
            Task<ReadOnlyMemory<byte>>? original = null;
            try
            {
                original = reader.ReadAsync(f.Path, 32, cancel.Token).AsTask();
                Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Read settled before held original entry.");
                cancel.Cancel(); Check(!original.IsCompleted && !joined, "Cancellation abandoned original read cleanup.");
                release.TrySetResult();
                if (fail) Check(ReferenceEquals(await Throws<IOException>(() => original!), readError), "Cancellation replaced original read failure identity.");
                else Check((await Throws<OperationCanceledException>(() => original!)).CancellationToken == cancel.Token, "Caller token was replaced.");
                Check(joined && f.Disposals == 0, "Original read cleanup was not joined or borrowed host was disposed.");
            }
            finally
            {
                cancel.Cancel(); release.TrySetResult();
                if (original is not null) { try { await original; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } catch (IOException error) when (ReferenceEquals(error, readError)) { } }
            }
        }
    }

    private sealed class Files : IDirectoryFileOperations, IAsyncDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), "pisharp-grep-host-authored-only");
        public string Path => System.IO.Path.Combine(Root, "a.txt");
        public Func<string, string>? Canonicalize;
        public Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? ReadWork;
        public int Reads, Canonicalizations, Disposals;
        public string? LastPath; public int LastBound; public CancellationToken LastToken;
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Canonicalizations++; return ValueTask.FromResult(Canonicalize?.Invoke(path) ?? path); }
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximumBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++; LastPath = path; LastBound = maximumBytes; LastToken = token;
            return ReadWork is not null ? ReadWork(path, maximumBytes, token) : ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("before\nhit\nafter"));
        }
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }
        public ValueTask<bool> IsDirectoryAsync(string path, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(path == Root); }
        public ValueTask<ImmutableArray<string>> ReadDirectoryAsync(string path, int entries, int characters, CancellationToken token) => throw new InvalidOperationException("Unexpected enumeration");
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => throw new InvalidOperationException("Unexpected write capability use");
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token) => throw new InvalidOperationException("Unexpected write capability use");
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Executor(string path) : IGrepExecutor
    {
        public int Calls; public bool Joined;
        public ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Joined = true; return ValueTask.FromResult<ImmutableArray<GrepMatch>>([new(path, 2, "receipt\n"), new(path, 2, "receipt\n")]); }
    }
    private sealed class Permit(bool allowed) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(allowed)); }
    }
    private static async Task<ToolResult> Invoke(GrepTool tool, int context, bool policyAllowed = true)
    {
        var call = new ToolCallContent("grep-host-fixture", "grep", JsonData.Parse(JsonSerializer.Serialize(new { pattern = "hit", context })));
        return await tool.CreateInvoker(new Permit(policyAllowed)).ExecuteAsync(new(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), default);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(ToolResult result) => result.Content.Single().Text;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
