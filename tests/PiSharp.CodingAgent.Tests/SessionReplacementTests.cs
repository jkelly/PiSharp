using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Cli.Output;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionReplacementTests
{
    private static readonly ModelDescriptor Model = new("replacement", "openai-responses", "fixture");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-replacement custom entries publish only actual disk acknowledgments", DurableEntries),
        ("session-replacement custom entry exact byte boundaries and Unicode identities", ExactBounds),
        ("session-replacement custom entry invalid data and ID cancellation avoid effects", EntryAdmission),
        ("session-replacement A B A rejects retained authority despite matching session IDs", RoundTrip),
        ("session-replacement own command switches and checkpoints without self-wait", InputCallback),
        ("session-replacement abort and disposal cancel the retired originating callback", AbortRetiredCallback),
        ("session-replacement target failure veto and precommit cancellation roll back", Rollback),
        ("session-replacement held writes order switches and retain truthful storage faults", HeldWrites),
        ("session-replacement busy staged and initial attachments reject before publication", BusyAttachment),
        ("session-replacement preflight target checkpoint rejects with source still usable", TargetPreflightCheckpoint),
        ("session-replacement reserves retained source and target handles until publication", ReservedAttachment),
        ("session-replacement shutdown cancels a postcommit lifecycle callback before joining transition", LifecycleShutdown),
        ("session-replacement JSON writer self disposal rejects and external disposal joins handoff", JsonTransitionCleanup),
        ("session-replacement lifecycle reentrancy rejects and postcommit failure retains target", Lifecycle)
    ];
    private static SessionExtensionEntryDraft Draft(string text = "state") => new("sample.checkpoint", "checkpoint", 1,
        JsonData.Parse(JsonSerializer.Serialize(new { text, opaque = (object?)null, scale = 1.0 })));
    private static async Task DurableEntries()
    {
        using var files = new Files(); var factory = new Factory();
        await using var session = await Create(files.A, files, factory);
        var before = session.Snapshot; var bytes = await Bytes(files.A);
        var barrier = factory.Storage!.Arm(); using var cancel = new CancellationTokenSource();
        var append = session.AppendExtensionEntryAsync("A", Draft("汉字 🚀"), cancel.Token);
        try
        {
            await barrier.Entered.Task;
            Check(new FileInfo(files.A).Length > bytes.Length, "No physical append occurred.");
            Check(!append.IsCompleted && session.Snapshot.Log == before.Log && session.Snapshot.Context == before.Context,
                "Unacknowledged state was published.");
            Check(session.Snapshot.IsAppendingExtensionEntry && !session.WaitForIdleAsync().IsCompleted, "Write settlement was not held.");
            cancel.Cancel(); barrier.Release.TrySetResult(); var receipt = await append;
            Check(receipt.Append.DurableCheckpointAcknowledged && receipt.Append.ByteOffset == bytes.Length, "Receipt is not a durable acknowledgment.");
            Check(receipt.Append.ByteLength == new FileInfo(files.A).Length - bytes.Length, "Receipt does not describe actual bytes.");
            Check((await Bytes(files.A)).AsSpan(0, bytes.Length).SequenceEqual(bytes), "Prefix was rewritten.");
            Check(receipt.Entry.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("text").GetString() == "汉字 🚀", "Opaque data changed.");
            Check(session.Snapshot.Context.LlmMessages.IsEmpty, "Custom state entered the conversation.");
        }
        finally { barrier.Release.TrySetResult(); await Ignore(append); }
        await session.DisposeAsync();
        await using var reopened = await Open(files.A);
        Check(reopened.Snapshot.Context.Ancestry[^1].Type == "custom", "Independent reopen lost the acknowledged custom entry.");
    }
    private static async Task ExactBounds()
    {
        using var files = new Files(); await Seed(files);
        var seed = await Bytes(files.A); var unicodeId = "汉字-🚀";
        await using (var probe = await Open(files.A, next: () => unicodeId))
            await probe.AppendExtensionEntryAsync("A", Draft());
        var final = await Bytes(files.A); var added = final.Length - seed.Length;
        await File.WriteAllBytesAsync(files.A, seed);
        var exact = new PersistentAgentSessionOptions(SessionLogStoreOptions: new(ReaderOptions:
            new(MaximumInputBytes: final.Length, MaximumRecords: 4, MaximumLines: 4)));
        await using (var session = await Open(files.A, exact, () => unicodeId))
        {
            var receipt = await session.AppendExtensionEntryAsync("A", Draft());
            Check(receipt.Append.ByteLength == added, "Exact byte boundary was rejected or changed.");
            Check(receipt.Entry.Id == unicodeId, "Generated identity was narrowed to ASCII.");
        }
        await File.WriteAllBytesAsync(files.A, seed.AsMemory(0, seed.Length - 1).ToArray());
        await using (var session = await Open(files.A, exact, () => unicodeId))
            Check((await session.AppendExtensionEntryAsync("A", Draft())).Append.ByteLength == added + 1, "Missing newline separator was misbudgeted.");
        await File.WriteAllBytesAsync(files.A, seed);
        var shortBounds = exact with { SessionLogStoreOptions = new(ReaderOptions: new(MaximumInputBytes: final.Length - 1)) };
        await using (var session = await Open(files.A, shortBounds, () => unicodeId))
        {
            var error = await Throws<PersistentAgentSessionException>(() => session.AppendExtensionEntryAsync("A", Draft()));
            Check(error.Fault.Failure == PersistentAgentSessionFailure.ExtensionEntryLimitExceeded && session.Snapshot.Fault is null,
                "An unwritten resource rejection poisoned the coordinator.");
            Check((await Bytes(files.A)).SequenceEqual(seed), "Rejected boundary wrote bytes.");
        }
    }
    private static async Task EntryAdmission()
    {
        using var files = new Files(); await Seed(files); var before = await Bytes(files.A);
        var ids = 0; var clocks = 0;
        await using (var session = await PersistentAgentSession.OpenAsync(files.A, Configuration,
            () => { clocks++; return 0; }, () => { ids++; return "entry"; }))
        {
            foreach (var draft in new[] { Draft() with { SchemaVersion = 2 }, Draft() with { ExtensionId = "bad/id" },
                Draft() with { Data = JsonData.Parse(JsonSerializer.Serialize(new string('x', 65_537))) } })
                await Throws<PersistentAgentSessionException>(() => session.AppendExtensionEntryAsync("A", draft));
            await Throws<PersistentAgentSessionException>(() => session.AppendExtensionEntryAsync("wrong", Draft()));
            Check(ids == 0 && clocks == 0 && (await Bytes(files.A)).SequenceEqual(before), "Invalid admission consumed delegates or wrote bytes.");
        }
        using var cancel = new CancellationTokenSource();
        await using (var session = await PersistentAgentSession.OpenAsync(files.A, Configuration,
            () => { clocks++; return 0; }, () => { cancel.Cancel(); return "entry"; }))
        {
            await Throws<OperationCanceledException>(() => session.AppendExtensionEntryAsync("A", Draft(), cancel.Token));
            Check(clocks == 0 && session.Snapshot.Fault is null, "ID cancellation reached clock/projection or poisoned state.");
        }
    }
    private static async Task RoundTrip()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A);
        await using var owner = Owner(initial); var a = owner.Current;
        await owner.AppendExtensionEntryAsync(a, Draft("first A"));
        var replaced = await owner.SwitchAsync(a, new(files.B)); var b = replaced!.Current;
        await owner.AppendExtensionEntryAsync(b, Draft("B"));
        var back = (await owner.SwitchAsync(b, new(files.A)))!.Current;
        Check(back.Generation == 3 && back.Session.Snapshot.Log.Header.Id == a.Session.Snapshot.Log.Header.Id, "Round trip lost identity or generation.");
        var bytes = await Bytes(files.A);
        await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(a, Draft("stale")));
        Check((await Bytes(files.A)).SequenceEqual(bytes), "Matching session ID restored stale authority.");
        await owner.AppendExtensionEntryAsync(back, Draft("fresh A"));
        await owner.DisposeAsync();
        await using var reopenedA = await Open(files.A); await using var reopenedB = await Open(files.B);
        Check(Customs(reopenedA).SequenceEqual(new[] { "first A", "fresh A" }) && Customs(reopenedB).SequenceEqual(new[] { "B" }), "Independent reopens found wrong branch state.");
    }
    private static async Task InputCallback()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A);
        await using var owner = Owner(initial); var stale = owner.Current;
        var result = await initial.SubmitInputAsync(new("/checkpoint", PromptInputSource.Rpc), new Admission(async token =>
        {
            await owner.AppendExtensionEntryAsync(stale, Draft("callback A"), token);
            var fresh = (await owner.SwitchAsync(stale, new(files.B), cancellationToken: token))!.Current;
            await owner.AppendExtensionEntryAsync(fresh, Draft("callback B"), token);
            await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(stale, Draft("late A"), token));
            var back = (await owner.SwitchAsync(fresh, new(files.A), cancellationToken: token))!.Current;
            await owner.AppendExtensionEntryAsync(back, Draft("callback fresh A"), token);
            Check(initial.Snapshot.IsRetired, "Original coordinator did not retire.");
            await Throws<PersistentAgentSessionException>(() => initial.AppendExtensionEntryAsync("A", Draft("direct stale")));
        }));
        Check(result.Disposition == SubmittedInputDisposition.Handled && owner.Current.Generation == 3, "Own input replacement did not settle as handled.");
        await owner.DisposeAsync();
        Check(initial.Snapshot.IsDisposed, "Old callback coordinator was not joined.");
    }
    private static async Task Rollback()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A);
        await using var owner = Owner(initial); var a = owner.Current; var bBytes = await Bytes(files.B);
        await Throws<SessionLogStoreException>(() => owner.SwitchAsync(a, new(Path.Combine(files.Directory, "missing.jsonl"))));
        Check(await owner.SwitchAsync(a, new(files.B), (_, _, _) => ValueTask.FromResult(false)) is null, "Veto did not cancel replacement.");
        using var cancel = new CancellationTokenSource();
        await Throws<OperationCanceledException>(() => owner.SwitchAsync(a, new(files.B), (_, _, _) =>
        { cancel.Cancel(); return ValueTask.FromResult(true); }, cancellationToken: cancel.Token));
        Check(ReferenceEquals(owner.Current, a) && !initial.Snapshot.IsRetired, "Precommit failure replaced or retired A.");
        await owner.AppendExtensionEntryAsync(a, Draft("after rollback"));
        await using var target = await Open(files.B);
        Check((await Bytes(files.B)).SequenceEqual(bBytes), "Rollback mutated target bytes or leaked its writer.");
    }
    private static async Task AbortRetiredCallback()
    {
        foreach (var dispose in new[] { false, true })
        {
            using var files = new Files(); await Seed(files); var initial = await Open(files.A);
            await using var owner = Owner(initial); var a = owner.Current; var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var submission = initial.SubmitInputAsync(new("/checkpoint"), new Admission(async token =>
            {
                await owner.SwitchAsync(a, new(files.B), cancellationToken: token);
                await Throws<InvalidOperationException>(() => owner.DisposeAsync().AsTask());
                held.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { closed.TrySetResult(); }
            }));
            await held.Task;
            if (dispose) await owner.DisposeAsync(); else owner.Abort();
            await Throws<OperationCanceledException>(() => submission);
            Check(closed.Task.IsCompleted && owner.Current.Session.Path == files.B, "Retired callback cancellation lost the committed attachment or skipped finally.");
            if (!dispose) await owner.AppendExtensionEntryAsync(owner.Current, Draft("after abort"));
            await owner.DisposeAsync(); Check(initial.Snapshot.IsDisposed, "Shutdown did not join the original callback.");
        }
        using var stagingFiles = new Files(); await Seed(stagingFiles); var stagingInitial = await Open(stagingFiles.A);
        await using var stagingOwner = Owner(stagingInitial); var original = stagingOwner.Current;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = stagingOwner.SwitchAsync(original, new(stagingFiles.B), async (_, _, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return true; });
        await entered.Task; stagingOwner.Abort(); await Throws<OperationCanceledException>(() => switching);
        Check(ReferenceEquals(stagingOwner.Current, original), "Staged cancellation committed a replacement.");
        await stagingOwner.AppendExtensionEntryAsync(original, Draft("after staging abort"));
        await using var target = await Open(stagingFiles.B);
    }
    private static async Task HeldWrites()
    {
        using var files = new Files(); await Seed(files); var factory = new Factory();
        var initial = await Open(files.A, new(SessionLogStoreOptions: new(StorageFactory: factory)));
        await using var owner = Owner(initial); var a = owner.Current; var barrier = factory.Storage!.Arm();
        var append = owner.AppendExtensionEntryAsync(a, Draft()); using var cancel = new CancellationTokenSource();
        Task<AgentSessionReplacement?>? switching = null;
        try
        {
            await barrier.Entered.Task;
            switching = owner.SwitchAsync(a, new(files.B), cancellationToken: cancel.Token);
            Check(!switching.IsCompleted && ReferenceEquals(owner.Current, a), "Switch crossed a held durable write.");
            cancel.Cancel(); await Throws<OperationCanceledException>(() => switching);
            barrier.Release.TrySetResult(); await append;
        }
        finally { barrier.Release.TrySetResult(); await Ignore(append); if (switching is not null) await Ignore(switching); }
        var bad = factory.Storage.Arm(fail: true); var before = initial.Snapshot.Log;
        append = owner.AppendExtensionEntryAsync(a, Draft("uncertain"));
        try
        {
            await bad.Entered.Task; bad.Release.TrySetResult();
            var error = await Throws<PersistentAgentSessionException>(() => append);
            Check(error.Fault.MayHaveWritten && error.Fault.DurableFlushCompleted && initial.Snapshot.Log == before, "Uncertain write state was hidden or acknowledged.");
            await Throws<PersistentAgentSessionException>(() => owner.SwitchAsync(a, new(files.B)));
        }
        finally { bad.Release.TrySetResult(); await Ignore(append); }
    }
    private static async Task Lifecycle()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A);
        await using var owner = Owner(initial); var a = owner.Current;
        var replaced = await owner.SwitchAsync(a, new(files.B), async (_, _, _) =>
        {
            await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(a, Draft()));
            await Throws<InvalidOperationException>(() => owner.SwitchAsync(a, new(files.B)));
            return true;
        });
        var failure = await Throws<AgentSessionReplacementNotificationException>(() => owner.SwitchAsync(replaced!.Current, new(files.A),
            afterSwitch: _ => throw new IOException("authored lifecycle failure")));
        Check(failure.Replacement.Current == owner.Current && failure.InnerException is IOException, "Committed notification failure lost its receipt.");
        Check(owner.Current.Generation == 3 && owner.Current.Session.Path == files.A, "Postcommit notification failure pretended to roll back.");
        await owner.AppendExtensionEntryAsync(owner.Current, Draft("committed attachment"));
    }
    private static async Task TargetPreflightCheckpoint()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A);
        var factory = new Factory(); var target = await Open(files.B, new(SessionLogStoreOptions: new(StorageFactory: factory)));
        await using var owner = new ReplaceableAgentSession(initial, (_, _) => Task.FromResult(target));
        var original = owner.Current; var bytes = await Bytes(files.B); var checkpoint = factory.Storage!.Arm();
        Task<SessionExtensionEntryReceipt>? append = null;
        try
        {
            await Throws<InvalidOperationException>(() => owner.SwitchAsync(original, new(files.B), async (_, staged, _) =>
            {
                append = staged.AppendExtensionEntryAsync("B", Draft("preflight checkpoint"));
                await append;
                return true;
            }));
            Check(ReferenceEquals(owner.Current, original) && !initial.Snapshot.IsRetired && target.Snapshot.IsDisposed,
                "Preflight target write retired the source or escaped staged cleanup.");
            Check(!checkpoint.Entered.Task.IsCompleted && (await Bytes(files.B)).SequenceEqual(bytes),
                "Reserved target admitted a physical checkpoint.");
            await owner.AppendExtensionEntryAsync(original, Draft("source usable after rejected preflight"));
            await using var reopened = await Open(files.B);
        }
        finally { checkpoint.Release.TrySetResult(); if (append is not null) await Ignore(append); await target.DisposeAsync(); }
    }
    private static async Task ReservedAttachment()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A); var target = await Open(files.B);
        await using var owner = new ReplaceableAgentSession(initial, (_, _) => Task.FromResult(target));
        var original = owner.Current; var targetBytes = await Bytes(files.B);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedCheckpoint = false;
        owner.AttachmentChanged = async replacement =>
        {
            var receipt = await replacement.Current.Session.AppendExtensionEntryAsync("B", Draft("fresh publication callback"));
            publishedCheckpoint = receipt.Append.DurableCheckpointAcknowledged;
        };
        var switching = owner.SwitchAsync(original, new(files.B), async (_, _, _) =>
        { entered.TrySetResult(); await proceed.Task; return true; });
        try
        {
            await entered.Task;
            await Throws<InvalidOperationException>(() => target.AppendExtensionEntryAsync("B", Draft("factory retained handle")));
            await Throws<InvalidOperationException>(() => target.SubmitInputAsync(new("retained input", PromptInputSource.Rpc)));
            await Throws<InvalidOperationException>(() => target.ConfigureAsync(new()));
            await Throws<InvalidOperationException>(() => { target.ClearPendingInputQueues(); return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => { target.SteeringMode = AgentPendingInputMode.All; return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => target.DisposeAsync().AsTask());
            await Throws<InvalidOperationException>(() => initial.DisposeAsync().AsTask());
            Check(!target.Snapshot.IsDisposed && !initial.Snapshot.IsDisposed && !initial.Snapshot.IsRetired &&
                (await Bytes(files.B)).SequenceEqual(targetBytes), "Retained handle changed a reserved attachment.");
            proceed.TrySetResult(); var receipt = await switching;
            Check(receipt is not null && owner.Current.Generation == 2 && publishedCheckpoint,
                "Publication did not release the target before notifying its fresh callback.");
            await owner.AppendExtensionEntryAsync(owner.Current, Draft("fresh owner write"));
        }
        finally { proceed.TrySetResult(); await Ignore(switching); await owner.DisposeAsync(); await target.DisposeAsync(); }
    }
    private static async Task BusyAttachment()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A);
        var factory = new Factory(); var target = await Open(files.B, new(SessionLogStoreOptions: new(StorageFactory: factory)));
        var barrier = factory.Storage!.Arm(); var append = target.AppendExtensionEntryAsync("B", Draft("held factory write"));
        await barrier.Entered.Task;
        await Throws<ArgumentException>(() => Task.FromResult(new ReplaceableAgentSession(target, (_, _) => throw new InvalidOperationException())));
        await using var owner = new ReplaceableAgentSession(initial, (_, _) => Task.FromResult(target));
        var original = owner.Current; var switching = owner.SwitchAsync(original, new(files.B));
        try
        {
            Check(!switching.IsCompleted && ReferenceEquals(owner.Current, original), "Busy staged writer was published before its disposal joined.");
            barrier.Release.TrySetResult(); await append;
            await Throws<InvalidOperationException>(() => switching);
            Check(ReferenceEquals(owner.Current, original) && target.Snapshot.IsDisposed, "Busy attachment leaked or replaced A.");
            await owner.AppendExtensionEntryAsync(original, Draft("available A"));
        }
        finally { barrier.Release.TrySetResult(); await Ignore(append); await Ignore(switching); await target.DisposeAsync(); }
    }
    private static IEnumerable<string> Customs(PersistentAgentSession session) => session.Snapshot.Context.Ancestry
        .Where(entry => entry.Type == "custom").Select(entry => entry.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("text").GetString()!);
    private static async Task LifecycleShutdown()
    {
        using var files = new Files(); await Seed(files); var initial = await Open(files.A); await using var owner = Owner(initial);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var switching = owner.SwitchAsync(owner.Current, new(files.B), afterSwitch: async replacement =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, replacement.Current.LifetimeToken); });
        await entered.Task; await owner.DisposeAsync();
        var error = await Throws<AgentSessionReplacementNotificationException>(() => switching);
        Check(error.InnerException is OperationCanceledException && owner.Current.Generation == 2 && owner.Current.Session.Snapshot.IsDisposed,
            "Lifecycle shutdown lost committed identity or failed to join its target.");
    }
    private static async Task JsonTransitionCleanup()
    {
        foreach (var selfDispose in new[] { true, false })
        {
            using var files = new Files(); await Seed(files); var initial = await Open(files.A); await using var owner = Owner(initial);
            var writer = new TransitionWriter(); await using var output = new SessionJsonEventOutput(initial, writer, null, owner);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writer.OnReplacement = async () =>
            {
                if (selfDispose) await Throws<InvalidOperationException>(() => output.DisposeAsync().AsTask());
                else { entered.TrySetResult(); await release.Task; }
            };
            await output.StartAsync(); var switching = owner.SwitchAsync(owner.Current, new(files.B)); Task? disposing = null;
            try
            {
                if (!selfDispose)
                {
                    await entered.Task; disposing = output.DisposeAsync().AsTask();
                    Check(!disposing.IsCompleted, "Output disposal skipped an admitted held replacement."); release.TrySetResult();
                }
                await switching; if (disposing is not null) await disposing; else await output.DisposeAsync();
                Check(writer.Records.Count == 2 && writer.Records[1].Contains("session_switched", StringComparison.Ordinal), "Replacement record was omitted or retried.");
                await Ignore(owner.Current.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"after output disposal\",\"timestamp\":0}"))));
                await owner.Current.Session.WaitForIdleAsync();
                Check(output.Failure is null && writer.Records.Count == 2, "Disposed transition leaked a subscription into the new session.");
            }
            finally { release.TrySetResult(); await Ignore(switching); if (disposing is not null) await Ignore(disposing); }
        }
    }
    private sealed class TransitionWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        internal readonly List<string> Records = [];
        internal Func<Task>? OnReplacement;
        public override async Task WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        { var value = text.ToString(); if (value.Contains("session_switched", StringComparison.Ordinal) && OnReplacement is { } callback) await callback(); Records.Add(value); }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
    }
    private static AgentConfiguration Configuration => new(Model, new NoTransport(), []);
    private static ReplaceableAgentSession Owner(PersistentAgentSession initial) => new(initial,
        (request, token) => Open(request.Path, new(UseLatestLeaf: request.UseLatestLeaf, SelectedLeafId: request.SelectedLeafId), token: token));
    private static async Task Seed(Files files)
    { await using var a = await Create(files.A, files); await using var b = await Create(files.B, files); }
    private static Task<PersistentAgentSession> Create(string path, Files files, Factory? factory = null)
    {
        var id = Path.GetFileNameWithoutExtension(path); var sequence = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id,
            timestamp = "2026-10-02T00:00:00.000Z", cwd = files.Directory, opaque = new { future = (object?)null } }));
        return PersistentAgentSession.CreateAsync(path, header, Configuration, () => 0, () => "seed-" + ++sequence,
            new(SessionLogStoreOptions: factory is null ? null : new(StorageFactory: factory)));
    }
    private static Task<PersistentAgentSession> Open(string path, PersistentAgentSessionOptions? options = null,
        Func<string>? next = null, CancellationToken token = default) => PersistentAgentSession.OpenAsync(path, Configuration,
            () => 0, next ?? (() => "entry-" + Guid.NewGuid().ToString("N")), options, token);
    private sealed class Admission(Func<CancellationToken, Task> callback) : IPromptInputAdmission
    {
        public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token)
        { await callback(token); return new(PromptInputAction.Handled); }
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Checkpoint workflows must not call a provider.")); yield break; }
    }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-replacement-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(Directory, "A.jsonl"); public string B => Path.Combine(Directory, "B.jsonl");
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose()
        {
            var target = Path.GetFullPath(Directory); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(target) != temp || !Path.GetFileName(target).StartsWith("PiSharp-replacement-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned replacement fixture.");
            System.IO.Directory.Delete(target, true);
        }
    }
    private sealed class Factory : ISessionLogStorageFactory
    {
        public Storage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class Barrier(bool fail)
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask Wait() { Entered.TrySetResult(); await Release.Task; if (fail) throw new IOException("authored checkpoint fault"); }
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private Barrier? barrier;
        public Barrier Arm(bool fail = false) => barrier = new(fail);
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
        public async ValueTask BeforeCheckpointAsync() { await inner.BeforeCheckpointAsync(); var held = Interlocked.Exchange(ref barrier, null); if (held is not null) await held.Wait(); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<byte[]> Bytes(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        using var copy = new MemoryStream(); await stream.CopyToAsync(copy); return copy.ToArray();
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Ignore(Task task) { try { await task; } catch (Exception) { } }
}
