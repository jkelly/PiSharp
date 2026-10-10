using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcSessionCreationTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("rpc.lifecycle-staged-response-budget-rolls-back-known-file-before-source-retirement", ResponseBudget),
        ("rpc.lifecycle-tree-chronological-labels-opaque-shapes-and-depth-budget-have-no-effects", TreeAndBounds),
        ("rpc.lifecycle-postcommit-notification-error-retains-fresh-file-and-current-identity", NotificationFailure),
        ("rpc.lifecycle-queue-publication-orders-authority-switch-and-joins-held-writes", QueuePublication),
        ("rpc.lifecycle-fork-of-missing-or-non-user-entry-answers-upstream-text", ForkInvalidEntry)
    ];
    private static readonly ModelDescriptor Model = new("lifecycle-rpc", "openai-responses", "fixture");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"lifecycle-rpc","api":"openai-responses","provider":"fixture","name":"Offline lifecycle","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    private static async Task ResponseBudget()
    {
        await using var fixture = await Fixture.Create([Entry("u", null, "message", new { message = new { role = "user", timestamp = 0, content = new string('x', 6000) } })], new(MaximumOutputBytes: 512));
        var before = await Bytes(fixture.Source); var attached = fixture.Owner.Current;
        await fixture.Send(new { id = "oversized", type = "fork", entryId = "u" });
        var error = fixture.Response("oversized").Value;
        Check(!error.GetProperty("success").GetBoolean() && error.GetProperty("error").GetString()!.Contains("limits", StringComparison.Ordinal), "Oversized fork response was accepted.");
        Check(ReferenceEquals(attached, fixture.Owner.Current) && !attached.Session.Snapshot.IsRetired && Directory.GetFiles(fixture.Root, "*.jsonl").Length == 1 &&
            Directory.GetFiles(fixture.Root, ".pisharp-branch-*.tmp").Length == 0 && !fixture.Records.Any(record => Type(record) == "session_switched"),
            "Response budgeting crossed source retirement or leaked staged file/writer.");
        Check((await Bytes(fixture.Source)).SequenceEqual(before), "Rejected response changed physical source bytes.");
        await fixture.Owner.AppendExtensionEntryAsync(attached, new("fixture", "after-rejection", 1, JsonData.Parse("{}")));
    }
    // agent-session-runtime.ts fork(): a missing entry, and a "before" fork of anything but a user message, both throw
    // "Invalid entry ID for forking", which rpc-mode answers as the command's error.
    private static async Task ForkInvalidEntry()
    {
        await using var fixture = await Fixture.Create([Entry("u", null, "message", new { message = new { role = "user", timestamp = 0, content = "hello" } }),
            Entry("c", "u", "custom", new { customType = "fixture", data = new { } })]);
        var before = await Bytes(fixture.Source); var attached = fixture.Owner.Current;
        foreach (var (id, entry) in new[] { ("missing", "absent"), ("custom", "c") })
        {
            await fixture.Send(new { id, type = "fork", entryId = entry });
            var error = fixture.Response(id).Value;
            Check(!error.GetProperty("success").GetBoolean() && error.GetProperty("error").GetString() == "Invalid entry ID for forking",
                "Fork of " + entry + " answered " + error.GetRawText());
        }
        Check(ReferenceEquals(attached, fixture.Owner.Current) && Directory.GetFiles(fixture.Root, "*.jsonl").Length == 1 &&
            (await Bytes(fixture.Source)).SequenceEqual(before), "A refused fork created a session or changed the source.");
    }
    private static async Task TreeAndBounds()
    {
        var entries = new[] { Entry("root", null, "future", new { opaque = (object?)null }),
            Entry("late", "root", "future", new { marker = "late" }, "2026-10-02T00:00:02.000Z"),
            Entry("early", "root", "future", new { marker = "early" }, "2026-10-02T00:00:01.000Z"),
            Entry("label", "early", "label", new { targetId = "late", label = "named" }) };
        await using (var fixture = await Fixture.Create(entries))
        {
            var before = await Bytes(fixture.Source);
            await fixture.Send(new { id = "tree", type = "get_tree" }); var data = fixture.Response("tree").Value.GetProperty("data");
            var roots = data.GetProperty("tree"); Check(roots.GetArrayLength() == 1, "Tree roots changed.");
            var children = roots[0].GetProperty("children");
            Check(children[0].GetProperty("entry").GetProperty("id").GetString() == "early" &&
                children[1].GetProperty("entry").GetProperty("id").GetString() == "late" &&
                children[1].GetProperty("label").GetString() == "named" && children[1].TryGetProperty("labelTimestamp", out _),
                "Chronological child order or global resolved label changed.");
            Check(roots[0].GetProperty("entry").GetProperty("opaque").ValueKind == JsonValueKind.Null && data.GetProperty("leafId").GetString() == "label", "Tree lost raw null or selected leaf.");
            await fixture.Send(new { id = "messages", type = "get_fork_messages" });
            Check(fixture.Response("messages").Value.GetProperty("data").GetProperty("messages").GetArrayLength() == 0, "Nonmessages entered fork choices.");
            Check((await Bytes(fixture.Source)).SequenceEqual(before), "Tree read changed bytes.");
        }
        await using (var bounded = await Fixture.Create(entries, new(MaximumJsonDepth: 8)))
        {
            var before = await Bytes(bounded.Source);
            await bounded.Send(new { id = "deep", type = "get_tree" });
            Check(!bounded.Response("deep").Value.GetProperty("success").GetBoolean() &&
                (await Bytes(bounded.Source)).SequenceEqual(before), "Tree depth budget was silently bypassed.");
        }
    }
    private static async Task NotificationFailure()
    {
        await using var fixture = await Fixture.Create([]); var previous = fixture.Owner.Current;
        fixture.Owner.AfterReplacement = _ => throw new IOException("authored lifecycle notification fault");
        await fixture.Send(new { id = "new", type = "new_session" }); var error = fixture.Response("new").Value;
        Check(!error.GetProperty("success").GetBoolean() && error.GetProperty("error").GetString()!.Contains("committed", StringComparison.Ordinal) &&
            fixture.Owner.Current.Generation == 2 && fixture.Records.Count(record => Type(record) == "session_switched") == 1 &&
            File.Exists(fixture.Owner.Current.Session.Path), "Postcommit failure pretended the new attachment was rolled back.");
        await fixture.Send(new { id = "state", type = "get_state" });
        Check(fixture.Response("state").Value.GetProperty("data").GetProperty("sessionFile").GetString() == fixture.Owner.Current.Session.Path, "RPC still referenced its retired session.");
        await fixture.Owner.AppendExtensionEntryAsync(fixture.Owner.Current, new("fixture", "after-failure", 1, JsonData.Parse("{}")));
        try { await fixture.Owner.AppendExtensionEntryAsync(previous, new("fixture", "stale", 1, JsonData.Parse("{}"))); throw new Exception("Stale context was accepted."); }
        catch (InvalidOperationException) { }
    }

    // Authored regression; execution requires the separately authorized native test window.
    private static async Task QueuePublication()
    {
        var deadline = TimeSpan.FromSeconds(10);
        using var output = new HeldQueueOutput();
        await using var fixture = await Fixture.Create([], output: output);
        var previous = fixture.Owner.Current;
        var attachmentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attachmentChanged = fixture.Owner.AttachmentChanged!;
        fixture.Owner.AttachmentChanged = async change =>
        {
            var publication = attachmentChanged(change).AsTask();
            attachmentEntered.TrySetResult();
            await publication;
        };
        using var caller = new CancellationTokenSource();
        // An empty clear emits an old-generation queue record while preserving the
        // lifecycle requirement that replacement begins with empty pending queues.
        var oldQueue = fixture.Dispatcher.SubmitAsync(JsonData.Parse("""{"id":"old","type":"clear_queue"}"""), caller.Token);
        Task? replacement = null, newQueue = null;
        try
        {
            await output.OldEntered.Task.WaitAsync(deadline);
            caller.Cancel();
            replacement = fixture.Send(new { id = "replace", type = "new_session" });
            await attachmentEntered.Task.WaitAsync(deadline);
            // This inspection is a deterministic authority-boundary witness, not a timing delay.
            var sessionField = typeof(RpcSessionDispatcher).GetField("_session",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Check(ReferenceEquals(sessionField.GetValue(fixture.Dispatcher), previous.Session),
                "Replacement exposed new RPC authority while an old queue publication was still held.");
            Check(!oldQueue.IsCompleted && !replacement.IsCompleted, "Cancellation detached an entered physical write.");
            output.ReleaseOld.TrySetResult();
            await output.SwitchEntered.Task.WaitAsync(deadline);
            newQueue = fixture.Send(new { id = "fresh", type = "follow_up", message = "fresh queue" });
            Check(fixture.Owner.Current.Session.GetPendingInputQueueSnapshot().FollowUpMessages.Length == 1,
                "Switch output held the admission transition or admitted to the old session.");
            Check(!newQueue.IsCompleted && !replacement.IsCompleted, "New queue escaped the held switch publication.");
        }
        finally
        {
            output.ReleaseOld.TrySetResult(); output.ReleaseSwitch.TrySetResult();
            await Task.WhenAll(new[] { oldQueue, replacement, newQueue }.OfType<Task>());
        }
        var events = fixture.Records.Where(record => Type(record) is "queue_update" or "session_switched").ToArray();
        Check(events.Select(Type).SequenceEqual(new[] { "queue_update", "session_switched", "queue_update" }),
            "Queue records crossed their session switch boundary.");
        Check(events[0].Value.GetProperty("steering").GetArrayLength() == 0 &&
            events[0].Value.GetProperty("followUp").GetArrayLength() == 0 &&
            events[2].ToString().Contains("fresh queue", StringComparison.Ordinal), "Queue content changed across replacement.");
        Check(fixture.Response("old").Value.GetProperty("success").GetBoolean() &&
            fixture.Response("fresh").Value.GetProperty("success").GetBoolean(), "Committed queue admission lost its response.");
    }

    private sealed class HeldQueueOutput : MemoryStream
    {
        internal readonly TaskCompletionSource OldEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SwitchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseSwitch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            var record = JsonData.Parse(Encoding.UTF8.GetString(buffer.Span));
            if (Type(record) == "queue_update" && !OldEntered.Task.IsCompleted)
            { OldEntered.TrySetResult(); await ReleaseOld.Task; }
            else if (Type(record) == "session_switched")
            { SwitchEntered.TrySetResult(); await ReleaseSwitch.Task; }
            await base.WriteAsync(buffer, token);
        }
    }
    private static SessionEntry Entry(string id, string? parent, string type, object extra, string timestamp = "2026-10-02T00:00:00.000Z")
    {
        var body = JsonSerializer.SerializeToNode(new { type, id, parentId = parent, timestamp })!.AsObject();
        foreach (var pair in JsonSerializer.SerializeToNode(extra)!.AsObject()) body.Add(pair.Key, pair.Value?.DeepClone());
        return new SessionEntryCodec().Parse(body.ToJsonString());
    }
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static async Task<byte[]> Bytes(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream(); await file.CopyToAsync(copy); return copy.ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-rpc-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl");
        internal ReplaceableAgentSession Owner = null!;
        internal RpcSessionDispatcher Dispatcher = null!;
        private MemoryStream output = new();
        internal JsonData[] Records => Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
        internal JsonData Response(string id) => Records.Single(record => Type(record) == "response" && record.Value.GetProperty("id").GetString() == id);
        internal Task Send(object record) => Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(record)));
        internal static async Task<Fixture> Create(IEnumerable<SessionEntry> entries, RpcDispatchOptions? options = null, MemoryStream? output = null,
            IChatTransport? transport = null)
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.Root);
            if (output is not null) { fixture.output.Dispose(); fixture.output = output; }
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "source",
                timestamp = "2026-10-02T00:00:00.000Z", cwd = fixture.Root }));
            await using (var store = await SessionLogStore.CreateNewAsync(fixture.Source, header))
            { var sourceEntries = entries.ToImmutableArray(); if (!sourceEntries.IsEmpty) await store.AppendAsync(sourceEntries); }
            var registry = new SessionRuntimeRegistry([new(Model, transport ?? new NoTransport())], [], new NoPolicy());
            Task<PersistentAgentSession> Open(string path, CancellationToken token) => PersistentAgentSession.OpenWithRegistryAsync(path,
                registry, () => 0, () => Guid.NewGuid().ToString("N"), fallbackModel: Model, cancellationToken: token);
            var source = await Open(fixture.Source, default);
            fixture.Owner = new(source, (request, token) => Open(request.Path, token),
                new PersistentSessionLifecycle(registry, () => 0, () => Guid.NewGuid().ToString("N")));
            fixture.Dispatcher = new(source, new JsonlWriter(fixture.output, ownership: JsonlStreamOwnership.Borrowed),
                () => 0, [new(Model, ModelWire)], options, RpcSessionOwnership.Borrowed, sessionOwner: fixture.Owner);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Dispatcher.DisposeAsync(); await Owner.DisposeAsync(); output.Dispose();
            var path = Path.GetFullPath(Root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(path) != parent || !Path.GetFileName(path).StartsWith("pisharp-rpc-lifecycle-", StringComparison.Ordinal)) throw new Exception("Unowned cleanup path.");
            Directory.Delete(path, true);
        }
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Lifecycle must not invoke a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Lifecycle must not invoke a tool.");
    }
}
