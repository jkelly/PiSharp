using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Interactive;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Tui.Input;

// Authored only / UNEXECUTED. Real decoder -> native owner -> RPC -> captured core revision.
// No generated navigation response is used to authorize any session change or editor text.
internal static class TerminalSessionNavigationCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string Escape = "\u001b[27;1:1u", Up = "\u001b[A", Enter = "\r";
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases() =>
    [
        ("navigation-default-double-escape-current-leaf-noop-and-reset", NoOp),
        ("navigation-500ms-boundary-repeat-release-none-and-ecmascript-whitespace", Boundary),
        ("navigation-user-target-parent-and-root-actual-editor-receipt", UserTargets),
        ("navigation-nonuser-target-same-file-and-unchanged-whitespace-editor", NonUser),
        ("navigation-core-revision-change-rejects-displayed-stale-target", Revision),
        ("navigation-switch-retires-chooser-and-foreign-receipt-cannot-edit", Switch),
        ("navigation-held-original-rpc-receipt-cancel-joins-before-input-release", HeldReceipt),
        ("navigation-held-original-selector-paint-cancel-joins-before-input-release", HeldPaint),
        ("navigation-fork-newest-actual-fresh-attachment-editor-text-and-undo", Fork)
    ];

    private static async Task NoOp(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(); var f = n.Input;
        await Open(n); var before = n.Core.Owner.Current;
        await Confirm(n); await f.WaitDraft("");
        Check(ReferenceEquals(before, n.Core.Owner.Current) && before.Session.Snapshot.Context.LeafId == "a2", "Current-leaf selection changed authority.");
        Check(n.Core.LastSelection.GetProperty("disposition").GetString() == "NoOp", "No-op was synthesized instead of acknowledged.");
        await f.Key(Escape); Check(f.Frontend.CaptureSelectDialog() is null, "Gesture was not reset after opening.");
        await f.Key("\u001b[27;1:2u"); await f.Key("\u001b[27;1:3u");
        Check(f.Frontend.CaptureSelectDialog() is null && f.Replies().Length == 0, "Repeat/release opened a chooser or emitted an extension response.");
        e.Observe("actual-same-file-noop", n.Snapshot());
    }

    private static async Task Boundary(ConsumerEvidence e)
    {
        var clock = new Clock(); await using var n = await Lane.Create(clock: clock);
        await n.Input.TypeDraft("\uFEFF"); await n.Input.Key(Escape); clock.Advance(500); await n.Input.Key(Escape);
        Check(n.Input.Frontend.CaptureSelectDialog() is null, "Inclusive 500ms boundary opened navigation.");
        clock.Advance(499); n.ExpectedEscapeCompletions = 3; n.Opening = n.Input.Key(Escape); await n.Core.Wait(record => IsCapture(record));
        await WaitDialog(n, "Session tree"); Check(n.Input.LastDraft!.Text == "\uFEFF", "ECMAScript whitespace was cleared.");
        n.ExpectedEscapeCompletions++; await n.Input.Key(Escape); await WaitClosed(n); await n.Input.WaitDraft("\uFEFF");
        var cancelledView = n.Core.Records().Single(IsCapture).Value.GetProperty("data");
        await n.Core.Send(new { id = "cancelled-view-replay", type = "pisharp_select_navigation", generation = 1,
            mode = "tree", viewId = cancelledView.GetProperty("viewId").GetString(), targetId = "u1" });
        Check(!n.Core.SelectionResponse.Value.GetProperty("success").GetBoolean() && n.Core.Owner.Current.Session.Snapshot.Context.LeafId == "a2",
            "Ordinary chooser cancellation left host selection authority replayable.");
        await using var none = await Lane.Create(action: TerminalDoubleEscapeAction.None);
        await none.Input.Key(Escape); await none.Input.Key(Escape);
        Check(none.Core.Records().All(record => !IsCapture(record)), "None policy dispatched navigation.");
        await using var nonWhitespace = await Lane.Create(); await nonWhitespace.Input.TypeDraft("\u0085");
        await nonWhitespace.Input.Key(Escape); await nonWhitespace.Input.Key(Escape);
        Check(nonWhitespace.Core.Records().All(record => !IsCapture(record)), "Non-ECMAScript whitespace opened navigation.");
        e.Observe("actual-clock-and-policy-boundaries", new { tree = n.Snapshot(), none = none.Snapshot(), nonWhitespace = nonWhitespace.Snapshot() });
    }

    private static async Task UserTargets(ConsumerEvidence e)
    {
        foreach (var (steps, text, leaf) in new[] { (1, "second user", (string?)"a1"), (3, "first user", (string?)null) })
        {
            await using var n = await Lane.Create(); var attachment = n.Core.Owner.Current;
            var bytes = await n.Core.SourceBytes(); await Open(n);
            for (var i = 0; i < steps; i++) await n.Input.Key(Up);
            await Confirm(n); await n.Input.WaitDraft(text);
            Check(ReferenceEquals(attachment, n.Core.Owner.Current) && attachment.Session.Snapshot.Context.LeafId == leaf,
                "User selection did not publish its actual parent/root on the same attachment.");
            Check((await n.Core.SourceBytes()).SequenceEqual(bytes), "Same-file navigation wrote the durable log.");
            await n.Input.Key("!"); Check(n.Input.LastDraft!.Text == text + "!", "Native editor did not receive actual core text.");
            await n.Input.Key("\u001a"); Check(n.Input.LastDraft!.Text == text, "Receipt text did not participate in native undo.");
            e.Observe("actual-user-parent-root", n.Snapshot());
        }
    }

    private static async Task NonUser(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(); await n.Input.TypeDraft(" "); var attachment = n.Core.Owner.Current;
        await Open(n); await n.Input.Key(Up); await n.Input.Key(Up); await Confirm(n);
        await n.Input.WaitDraft(" ");
        Check(ReferenceEquals(attachment, n.Core.Owner.Current) && attachment.Session.Snapshot.Context.LeafId == "a1" &&
            n.Core.LastSelection.GetProperty("editorText").ValueKind == JsonValueKind.Null, "Non-user target did not select itself without editor text.");
        e.Observe("actual-nonuser-context", n.Snapshot());
    }

    private static async Task Revision(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(); await Open(n); await n.Input.Key(Up);
        var view = n.Input.Frontend.CaptureSelectDialog()!;
        await n.Core.Owner.AppendExtensionEntryAsync(n.Core.Owner.Current, new("navigation-fixture", "revision-change", 1, JsonData.Parse("{}")));
        var currentLeaf = n.Core.Owner.Current.Session.Snapshot.Context.LeafId;
        await n.Input.Key(Enter); await WaitDialog(n, "Navigate without summary?"); await n.Input.Key(Enter);
        await n.Core.Wait(record => IsSelection(record)); await WaitClosed(n);
        Check(!n.Core.SelectionResponse.Value.GetProperty("success").GetBoolean() &&
            n.Core.Owner.Current.Session.Snapshot.Context.LeafId == currentLeaf && n.Input.LastDraft!.Text == "",
            "Stale displayed revision changed context or installed text.");
        Check(view.HostNavigation && view.Identity is null, "Host workflow borrowed an extension identity.");
        e.Observe("actual-stale-revision-failure", n.Snapshot());
    }

    private static async Task Switch(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(); await Open(n);
        var old = n.Input.Frontend.CaptureSelectDialog()!; var editor = new TerminalTextEditorPasteController(); editor.SetText("foreign");
        await n.Core.Send(new { id = "external-switch", type = "new_session" }); await WaitClosed(n);
        Check(n.Core.Owner.Current.Generation == 2 && !await n.Input.Frontend.RespondSelectAsync(old, 0, false, default),
            "Retired chooser selected against a replacement.");
        var foreign = new TerminalSessionNavigationReceipt(1, editor.LayoutIdentity.EditorLifetimeId, "foreign", "wrong", "tree", "Selected");
        Check(!n.Input.Frontend.ApplySessionNavigation(foreign, editor) && editor.GetExpandedText() == "foreign", "Foreign-generation receipt changed text.");
        var wrongEditor = new TerminalSessionNavigationReceipt(2, Guid.NewGuid(), "foreign", "wrong", "fork", "Selected");
        Check(!n.Input.Frontend.ApplySessionNavigation(wrongEditor, editor), "Foreign editor lifetime was accepted.");
        var duplicate = new TerminalSessionNavigationReceipt(2, editor.LayoutIdentity.EditorLifetimeId, "foreign", null, "tree", "NoOp");
        Check(n.Input.Frontend.ApplySessionNavigation(duplicate, editor) && !n.Input.Frontend.ApplySessionNavigation(duplicate, editor), "Receipt was not single claim.");
        e.Observe("actual-switch-and-editor-authority", n.Snapshot());
    }

    private static async Task HeldReceipt(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(); await Open(n); await n.Input.Key(Up);
        await n.Input.Key(Enter); await WaitDialog(n, "Navigate without summary?");
        var held = n.Core.Output.Hold(IsSelection); n.Input.Terminal.Feed(Enter);
        try
        {
            await held.Entered.Task.WaitAsync(Deadline);
            Check(n.Core.Owner.Current.Session.Snapshot.Context.LeafId == "a1", "Receipt held before actual core publication.");
            n.Input.Cancellation.Cancel(); Check(!n.Input.Input.IsCompleted && !n.Core.LastSend!.IsCompleted, "Cancellation detached the entered RPC write.");
            e.Observe("held-actual-published-receipt", n.Snapshot());
        }
        finally { held.Release.TrySetResult(); await JoinCanceled(n.Input.Input); if (n.Core.LastSend is { } send) await JoinCanceled(send); }
        Check(n.Input.LastDraft!.Text == "", "Canceled input painted receipt text after stop.");
        Joined(n); e.Observe("joined-original-rpc-write", n.Snapshot());
    }

    private static async Task HeldPaint(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(); var held = n.Input.Terminal.HoldWrite("Session tree");
        await n.Input.Key(Escape); n.Input.Terminal.Feed(Escape);
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); n.Input.Cancellation.Cancel();
            await held.Canceled.Task.WaitAsync(Deadline);
            Check(!n.Input.Input.IsCompleted && !n.Core.LastSend!.IsCompleted, "Cancellation detached actual selector paint/publication.");
            e.Observe("held-original-selector-paint", n.Snapshot());
        }
        finally { held.Release.TrySetResult(); await JoinCanceled(n.Input.Input); if (n.Core.LastSend is { } send) await JoinCanceled(send); }
        Joined(n); e.Observe("joined-original-selector-paint", n.Snapshot());
    }

    private static async Task Fork(ConsumerEvidence e)
    {
        await using var n = await Lane.Create(action: TerminalDoubleEscapeAction.Fork); var old = n.Core.Owner.Current;
        var bytes = await n.Core.SourceBytes(); await Open(n, "Fork from user message");
        Check(n.Input.Frontend.CaptureSelectDialog()!.InitialIndex == 1, "Fork did not begin at actual newest user entry.");
        await n.Input.Key(Enter); await n.Core.Wait(record => IsSelection(record)); await WaitClosed(n); await n.Input.WaitDraft("second user");
        Check(n.Core.Owner.Current.Generation == 2 && n.Core.Owner.Current.Session.Path != old.Session.Path &&
            n.Core.Owner.Current.Session.Snapshot.Context.LeafId == "a1" && (await n.Core.SourceBytes()).SequenceEqual(bytes),
            "Fork did not acknowledge a fresh sibling preserving source bytes.");
        await n.Input.Key("!"); await n.Input.Key("\u001a"); Check(n.Input.LastDraft!.Text == "second user", "Fork text bypassed actual editor/undo.");
        Check(n.Input.Replies().Length == 0, "Host navigation sent an extension reply.");
        e.Observe("actual-core-fork-and-editor", n.Snapshot());
    }

    private static async Task Open(Lane lane, string title = "Session tree")
    {
        lane.ExpectedEscapeCompletions = lane.Input.CompletedRawCount(Escape) + 2;
        await lane.Input.Key(Escape); lane.Opening = lane.Input.Key(Escape); await WaitDialog(lane, title);
    }
    private static async Task Confirm(Lane lane)
    {
        await lane.Input.Key(Enter); await WaitDialog(lane, "Navigate without summary?"); await lane.Input.Key(Enter);
        await lane.Core.Wait(IsSelection); await WaitClosed(lane);
    }
    private static async Task WaitDialog(Lane lane, string title)
    {
        // A borrowed focus transition observes the original selector paint/owner turn.
        while (true)
        {
            var next = lane.Input.Frontend.CaptureNavigationChange();
            if (next.Dialog is { ResponsePending: false } && next.Dialog.Title == title) break;
            await next.Changed.WaitAsync(Deadline);
        }
        await lane.Input.Focus.SetEditorFocusAsync(false).WaitAsync(Deadline);
    }
    private static async Task WaitClosed(Lane lane)
    {
        // The input's Escape completion occurs only after the navigation finally restores focus.
        while (true)
        {
            var next = lane.Input.Frontend.CaptureNavigationChange(); if (next.Dialog is null) break;
            await next.Changed.WaitAsync(Deadline);
        }
        await lane.Input.Focus.SetEditorFocusAsync(true).WaitAsync(Deadline);
        await lane.Input.WaitCompletedRaw(Escape, lane.ExpectedEscapeCompletions);
        if (lane.Opening is { } opening) { await opening; lane.Opening = null; }
    }
    private static async Task JoinCanceled(Task original)
    { try { await original; } catch (OperationCanceledException) { } }
    private static void Joined(Lane lane) => Check(lane.Input.Terminal.ActiveReads == 0 && lane.Input.Terminal.ActiveWrites == 0 &&
        lane.Input.Terminal.ReadsStarted == lane.Input.Terminal.ReadsSettled && lane.Input.Terminal.WritesStarted == lane.Input.Terminal.WritesSettled,
        "Original read/write did not join.");
    private static bool IsCapture(JsonData record) => IsResponse(record, "pisharp_capture_navigation");
    private static bool IsSelection(JsonData record) => IsResponse(record, "pisharp_select_navigation");
    private static bool IsResponse(JsonData record, string command) => record.Value.GetProperty("type").GetString() == "response" &&
        record.Value.GetProperty("command").GetString() == command;
    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }

    private sealed class Clock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => now;
        internal void Advance(long milliseconds) => now += milliseconds;
    }
    private sealed class Lane : IAsyncDisposable
    {
        internal NavigationCore Core = null!; internal SelectorFixture Input = null!; internal Task? Opening; internal int ExpectedEscapeCompletions;
        internal static async Task<Lane> Create(TerminalDoubleEscapeAction action = TerminalDoubleEscapeAction.Tree, TimeProvider? clock = null)
        {
            var lane = new Lane();
            lane.Core = await NavigationCore.Create(async (record, token) => { await lane.Input.Frontend.ObserveAsync(record, token); });
            try { lane.Input = await SelectorFixture.Create(kitty: true, commandHandler: lane.Core.Send,
                inputClock: clock, doubleEscapeAction: action); return lane; }
            catch { await lane.Core.DisposeAsync(); throw; }
        }
        internal object Snapshot() => new { input = Input.Snapshot(), generation = Core.Owner.Current.Generation,
            path = Core.Owner.Current.Session.Path, leaf = Core.Owner.Current.Session.Snapshot.Context.LeafId, records = Core.Records() };
        public async ValueTask DisposeAsync()
        {
            Core.Output.Release();
            try { await Input.DisposeAsync(); if (Opening is { } opening) await JoinCanceled(opening); }
            finally { await Core.DisposeAsync(); }
        }
    }

    private sealed class NavigationOutput(Func<JsonData, CancellationToken, ValueTask> observe, Action changed) : MemoryStream
    {
        private readonly object gate = new(); private readonly List<JsonData> records = [];
        internal JsonData[] Records() { lock (gate) return records.ToArray(); }
        private (Func<JsonData, bool> Matches, SelectorHold Hold)? held;
        internal SelectorHold Hold(Func<JsonData, bool> matches) { var hold = new SelectorHold(); held = (matches, hold); return hold; }
        internal void Release() { held?.Hold.Release.TrySetResult(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            var record = JsonData.Parse(Encoding.UTF8.GetString(buffer.Span));
            if (held is { } activeHold && activeHold.Matches(record)) { activeHold.Hold.Entered.TrySetResult(); await activeHold.Hold.Release.Task; }
            await base.WriteAsync(buffer, token); lock (gate) records.Add(record); changed();
            await observe(record, token); changed();
        }
    }
    private sealed class NavigationCore : IAsyncDisposable
    {
        private static readonly ModelDescriptor Model = new("navigation", "openai-responses", "fixture");
        private static readonly JsonData Wire = JsonData.Parse("""{"id":"navigation","api":"openai-responses","provider":"fixture","name":"Offline","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
        private readonly object gate = new(); private TaskCompletionSource changed = SelectorFixture.Signal();
        internal string Root = Path.Combine(Path.GetTempPath(), "pisharp-navigation-terminal-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!;
        internal NavigationOutput Output = null!; internal Task? LastSend;
        internal JsonData[] Records() => Output.Records();
        internal JsonData SelectionResponse => Records().Last(IsSelection);
        internal JsonElement LastSelection => SelectionResponse.Value.GetProperty("data");
        private void Changed() { TaskCompletionSource signal; lock (gate) { signal = changed; changed = SelectorFixture.Signal(); } signal.TrySetResult(); }
        internal Task WaitChange() { lock (gate) return changed.Task; }
        internal async Task Wait(Func<JsonData, bool> predicate)
        { while (true) { Task next; lock (gate) { if (Records().Any(predicate)) return; next = changed.Task; } await next.WaitAsync(Deadline); } }
        internal Task Send(JsonData record, CancellationToken token)
        {
            var begin = SelectorFixture.Signal(); var original = SendOwned(); LastSend = original; begin.TrySetResult(); return original;
            async Task SendOwned() { await begin.Task; await Dispatcher.SubmitAsync(record, token); }
        }
        internal Task Send(object record) => Send(JsonData.Parse(JsonSerializer.Serialize(record)), default);
        internal async Task<byte[]> SourceBytes()
        { await using var file = new FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var copy = new MemoryStream(); await file.CopyToAsync(copy); return copy.ToArray(); }
        internal static async Task<NavigationCore> Create(Func<JsonData, CancellationToken, ValueTask> observe)
        {
            var core = new NavigationCore(); Directory.CreateDirectory(core.Root);
            var codec = new SessionEntryCodec(); var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "navigation-source", timestamp = "2026-10-03T00:00:00.000Z", cwd = core.Root }));
            SessionEntry Message(string id, string? parent, string role, string text) => codec.Parse(JsonSerializer.Serialize(new {
                type = "message", id, parentId = parent, timestamp = "2026-10-03T00:00:00.000Z", message = new {
                    role, timestamp = 0, content = role == "user" ? (object)text : new object[] { new { type = "text", text } },
                    api = "openai-responses", provider = "fixture", model = "navigation", stopReason = "stop", usage = new {
                        input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } } } }));
            await using (var log = await SessionLogStore.CreateNewAsync(core.Source, header))
                await log.AppendAsync([Message("u1", null, "user", "first user"), Message("a1", "u1", "assistant", "first reply"),
                    Message("u2", "a1", "user", "second user"), Message("a2", "u2", "assistant", "second reply")]);
            var registry = new SessionRuntimeRegistry([new(Model, new NoTransport())], [], new NoPolicy());
            Task<PersistentAgentSession> Open(string path, CancellationToken token) => PersistentAgentSession.OpenWithRegistryAsync(path,
                registry, () => 0, () => Guid.NewGuid().ToString("N"), fallbackModel: Model, cancellationToken: token);
            var session = await Open(core.Source, default);
            core.Owner = new(session, (request, token) => Open(request.Path, token), new PersistentSessionLifecycle(registry, () => 0, () => Guid.NewGuid().ToString("N")));
            core.Output = new(observe, core.Changed);
            core.Dispatcher = new(session, new JsonlWriter(core.Output, ownership: JsonlStreamOwnership.Borrowed), () => 0,
                [new(Model, Wire)], sessionOwnership: RpcSessionOwnership.Borrowed, sessionOwner: core.Owner);
            return core;
        }
        public async ValueTask DisposeAsync()
        {
            Output.Release(); await Dispatcher.DisposeAsync(); await Owner.DisposeAsync(); Output.Dispose();
            var root = Path.GetFullPath(Root); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(root) != temp || !Path.GetFileName(root).StartsWith("pisharp-navigation-terminal-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unowned cleanup directory.");
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Navigation must not call a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            throw new InvalidOperationException("Navigation must not call a tool.");
    }
}
