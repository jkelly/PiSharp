using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class NativeSdkContextEditTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("sdk-edit", "openai-responses", "fixture");
    private const string Time = "2026-10-02T00:00:00.000Z", Command = "context-edit-check";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("native-sdk-context-edit actual checkpoints immutable views and stored opaque bytes survive repeated edits", Repeated),
        ("native-sdk-context-edit exact 4095 to 4096 snapshot admits and next edit rejects before physical write", CountBoundary),
        ("native-sdk-context-edit input finite policy and generated snapshot identity fail before physical write", InputAndIdentity),
        ("native-sdk-context-edit staged cancellation joins native authority and leaves source writable", StagedCancellation),
        ("native-sdk-context-edit replaced attachment rejects retained old context while fresh context edits B", ReplacementAuthority)
    ];
    private static async Task Repeated()
    {
        await using var f = await Fixture.Create(2); var original = await Bytes(f.Source);
        await f.Register(async (context, _) =>
        {
            var captured = ((IExtensionSessionContext)context).SessionSnapshot!;
            Check(captured.BranchEntries[0].Value.GetProperty("opaque").GetRawText() == "1.00e400", "Stored opaque token changed during capture.");
            var editable = (IExtensionSessionContextEditContext)context;
            var first = await editable.AppendContextEditAsync("e0", JsonData.Parse("{\"content\":\"changed\",\"wrapper\":{\"preserved\":true}}"));
            Check(first.Checkpoint.ByteLength > 0 && first.Checkpoint.DurableCheckpointAcknowledged &&
                first.Snapshot.BranchEntries.Length == 3 && first.Snapshot.Generation == 1 && captured.BranchEntries.Length == 2,
                "Actual SDK append did not return a checkpoint plus a distinct owned view.");
            Check(first.Checkpoint.Entry.Value.GetProperty("replacement").GetProperty("wrapper").GetProperty("preserved").GetBoolean(), "User wrapper shape was lost.");
            var omitted = await editable.AppendContextEditAsync("e0", JsonData.Null);
            Check(omitted.Snapshot.BranchEntries.Length == 4 && first.Snapshot.BranchEntries.Length == 3 &&
                omitted.Checkpoint.Sequence > first.Checkpoint.Sequence && f.Owner.Current.Session.Snapshot.Context.LlmMessages.IsEmpty,
                "Repeated edit omitted a different contribution, restored original content or mutated an old view.");
        });
        await f.Invoke(); var edited = await Bytes(f.Source);
        Check(edited.AsSpan(0, original.Length).SequenceEqual(original), "SDK editor rewrote original records.");
        await f.Owner.DisposeAsync(); await using var reopened = await f.Open(f.Source);
        Check(reopened.Snapshot.Context.LlmMessages.IsEmpty && reopened.Snapshot.Log.Entries.Length == 4, "SDK edit did not survive actual writer close/reopen.");
    }
    private static async Task CountBoundary()
    {
        await using var f = await Fixture.Create(4095);
        await f.Register(async (context, _) =>
        {
            var editor = (IExtensionSessionContextEditContext)context;
            var accepted = await editor.AppendContextEditAsync("e0", JsonData.Parse("{\"content\":\"boundary\"}"));
            Check(accepted.Snapshot.BranchEntries.Length == 4096 && accepted.Checkpoint.ByteLength > 0, "Exact SDK count boundary was rejected.");
            var before = await Bytes(f.Source);
            var error = await Throws<ExtensionRegistrationException>(() => editor.AppendContextEditAsync("e0", JsonData.Null).AsTask());
            Check(error.Failure == ExtensionRegistrationFailure.LimitExceeded && error.Operation == "capture-session-snapshot", "Next editor did not reuse eventual SDK policy.");
            Check((await Bytes(f.Source)).SequenceEqual(before) && f.Owner.Current.Session.Snapshot.Fault is null &&
                !f.Owner.Current.Session.Snapshot.IsEditingContext, "Rejected staged SDK snapshot wrote or poisoned source.");
        });
        await f.Invoke();
    }
    private static async Task InputAndIdentity()
    {
        foreach (var generatedId in new[] { "edit-ok", "invalid/id" })
        {
            await using var f = await Fixture.Create(2, generatedId: generatedId); var before = await Bytes(f.Source);
            await f.Register(async (context, _) =>
            {
                var editor = (IExtensionSessionContextEditContext)context;
                foreach (var raw in new[] { "{\"content\":\"x\",\"untrusted\":1e400}",
                    JsonSerializer.Serialize(new { content = new string('x', 65_536) }) })
                    await Throws<ExtensionRegistrationException>(() => editor.AppendContextEditAsync("e0", JsonData.Parse(raw)).AsTask());
                Check(f.AuthorCalls == 0 && (await Bytes(f.Source)).SequenceEqual(before), "Input policy consumed author callbacks or wrote.");
                if (generatedId.Contains('/'))
                {
                    await Throws<ExtensionRegistrationException>(() => editor.AppendContextEditAsync("e0", JsonData.Parse("{\"content\":\"x\"}")).AsTask());
                    Check(f.AuthorCalls == 1 && (await Bytes(f.Source)).SequenceEqual(before) && f.Owner.Current.Session.Snapshot.Fault is null,
                        "Generated staged identity failed after effects.");
                }
            });
            await f.Invoke();
        }
    }
    private static async Task StagedCancellation()
    {
        await using var f = await Fixture.Create(2, held: true); var before = await Bytes(f.Source);
        using var canceled = new CancellationTokenSource();
        await f.Register(async (context, _) => { await ((IExtensionSessionContextEditContext)context)
            .AppendContextEditAsync("e0", JsonData.Parse("{\"content\":\"cancel me\"}"), canceled.Token); });
        var work = f.Invoke();
        try
        {
            await f.Held!.Entered.Task.WaitAsync(Bound); Check(f.Owner.Current.Session.Snapshot.IsEditingContext, "No actual manager edit was held.");
            canceled.Cancel(); await Throws<OperationCanceledException>(() => work);
            Check((await Bytes(f.Source)).SequenceEqual(before) && !f.Owner.Current.Session.Snapshot.IsEditingContext, "Canceled SDK edit failed to settle before returning.");
            await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "continued", 1, JsonData.EmptyObject));
        }
        finally { canceled.Cancel(); f.Held!.Release.TrySetResult(); try { await work; } catch (OperationCanceledException) { } }
    }
    private static async Task ReplacementAuthority()
    {
        await using var f = await Fixture.Create(2); var original = await Bytes(f.Source);
        await f.Register(async (context, _) =>
        {
            var fresh = await ((IExtensionSessionCommandContext)context).SwitchSessionAsync(f.Other);
            Check(fresh is not null && fresh.SessionSnapshot!.Generation == 2, "Native SDK switch did not supply fresh authority.");
            var beforeFresh = await Bytes(f.Other);
            await Throws<OperationCanceledException>(() => ((IExtensionSessionContextEditContext)context).AppendContextEditAsync("e0", JsonData.Null).AsTask());
            Check(context.SessionCancellationToken.IsCancellationRequested && f.AuthorCalls == 0 &&
                (await Bytes(f.Other)).SequenceEqual(beforeFresh), "Retired SDK context reached authoring or fresh B effects.");
            var edited = await ((IExtensionSessionContextEditContext)fresh!).AppendContextEditAsync("e0", JsonData.Parse("{\"content\":\"B only\"}"));
            Check(edited.Checkpoint.Generation == 2 && f.Owner.Current.Session.Snapshot.Context.LlmMessages[0].WireBody.Value.GetProperty("content").GetString() == "B only",
                "Fresh SDK editor did not use actual B history.");
        });
        await f.Invoke(); Check((await Bytes(f.Source)).SequenceEqual(original), "Retired A received an edit.");
    }
    private static async Task<byte[]> Bytes(string path)
    { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var bytes = new MemoryStream(); await file.CopyToAsync(bytes); return bytes.ToArray(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-sdk-context-edit-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal ExtensionRegistry Registry = null!; internal HeldProvider? Held;
        internal int AuthorCalls; private string? authorIdentity; private readonly NoTransport transport = new();
        private SessionRuntimeRegistry runtime = null!;
        internal string NextId() { AuthorCalls++; return authorIdentity ?? "edit-" + AuthorCalls; }
        internal Task<PersistentAgentSession> Open(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0, NextId, fallbackModel: Model);
        internal static async Task<Fixture> Create(int count, string? generatedId = null, bool held = false)
        {
            var f = new Fixture { authorIdentity = generatedId }; Directory.CreateDirectory(f.Root);
            f.runtime = new([new(Model, f.transport)], [], new NoPolicy());
            async Task CreateFile(string path, int n)
            {
                var codec = new SessionEntryCodec(); var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                    id = Path.GetFileNameWithoutExtension(path), timestamp = Time, cwd = f.Root }));
                await using var store = await SessionLogStore.CreateNewAsync(path, header);
                var records = ImmutableArray.CreateBuilder<SessionEntry>();
                records.Add(codec.Parse("{\"type\":\"message\",\"id\":\"e0\",\"parentId\":null,\"timestamp\":\"" + Time +
                    "\",\"message\":{\"role\":\"user\",\"content\":\"original\",\"timestamp\":0},\"opaque\":1.00e400}"));
                for (var i = 1; i < n; i++) records.Add(codec.Parse(JsonSerializer.Serialize(new { type = "future", id = "e" + i,
                    parentId = "e" + (i - 1), timestamp = Time, data = new { inert = true } })));
                foreach (var batch in records.ToImmutable().Chunk(128)) await store.AppendAsync(batch.ToImmutableArray());
            }
            await CreateFile(f.Source, count); await CreateFile(f.Other, 2);
            var session = await f.Open(f.Source);
            f.Owner = new(session, (request, _) => f.Open(request.Path));
            var native = new NativeSessionSnapshotProvider(); native.Attach(f.Owner);
            IExtensionSessionViewProvider provider = native;
            if (held) { f.Held = new(native); provider = f.Held; }
            // This host bounds its callback views explicitly at 4096 entries (the defaults grow with the session).
            f.Registry = new(new() { MaximumSessionBranchEntries = 4_096, MaximumSessionCharacters = 1_048_576, MaximumSessionUtf8Bytes = 1_048_576 },
                null, provider); return f;
        }
        internal Task Register(Func<IExtensionCommandContext, CancellationToken, ValueTask> action) => Registry.ActivateAsync("sdk", new Plugin((registry, _) =>
        { registry.RegisterCommand(new("edit-registration", Command, "", (_, context, token) => action(context, token))); return ValueTask.CompletedTask; }));
        internal Task Invoke() => Registry.InvokeCommandAsync(Registry.CaptureSnapshot(), Command, JsonData.Null).AsTask();
        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync(); await Owner.DisposeAsync(); Check(transport.Calls == 0, "Editor invoked a provider.");
            var root = Path.GetFullPath(Root); Check(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("PiSharp-sdk-context-edit-", StringComparison.Ordinal) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0,
                "Unowned SDK fixture cleanup root.");
            foreach (var path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root, false);
        }
    }
    private sealed class Plugin(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class NoTransport : IChatTransport
    {
        internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("Context edit must not call a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Editor acquired a tool."); }
    private sealed class HeldProvider(NativeSessionSnapshotProvider inner) : IExtensionSessionContextEditProvider, IExtensionSessionOpaqueViewProvider
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => inner.Capture(context);
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) => new Scope((IExtensionSessionContextEditScope)inner.OpenScope(context, snapshot), this);
        private sealed class Scope(IExtensionSessionContextEditScope actual, HeldProvider owner) : IExtensionSessionContextEditScope
        {
            public ExtensionSessionSnapshot Snapshot => actual.Snapshot;
            public CancellationToken SessionCancellationToken => actual.SessionCancellationToken;
            public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int version, JsonData data, CancellationToken token) => actual.AppendAsync(kind, version, data, token);
            public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) => actual.SwitchAsync(path, latest, leaf, token);
            public ValueTask<ExtensionSessionContextEditAcknowledgment> AppendContextEditAsync(string target, JsonData? replacement,
                Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validate, CancellationToken token) => actual.AppendContextEditAsync(target, replacement,
                async (snapshot, stagedToken) => { await validate(snapshot, stagedToken); owner.Entered.TrySetResult(); await owner.Release.Task.WaitAsync(Bound, stagedToken); }, token);
            public ValueTask DisposeAsync() => actual.DisposeAsync();
        }
    }
}
