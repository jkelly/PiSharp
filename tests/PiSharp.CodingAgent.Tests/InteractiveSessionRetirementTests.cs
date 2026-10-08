using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Rpc;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class InteractiveSessionRetirementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string published = "";
    private static string failureRoot = "";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string cliPath)
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(cliPath))!);
        while (!File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
            root = root.Parent ?? throw new InvalidOperationException("Native fixture root is absent.");
        published = Path.Combine(root.FullName, "artifacts", "extensions", "published-fixtures", "cli-ui");
        failureRoot = Path.Combine(root.FullName, "artifacts", "native-dialog-retirement-failures");
        return
        [
            ("interactive.native-retirement-actual-timeout-restores-multiline-draft-and-editor-focus", TimeoutFocus),
            ("interactive.native-retirement-matches-all-generations-and-removes-only-retired-queued-dialog", ReplacementFocus),
            ("interactive.native-retirement-answer-awaits-host-and-keeps-control-data-and-legacy-wire", AnswerOwnership),
            ("interactive.native-retirement-real-host-abort-eof-join-held-callback-flush-and-durable-owner", HostShutdown)
        ];
    }

    private static async Task TimeoutFocus()
    {
        await using var fixture = new UiFixture();
        await fixture.Frontend.LineAsync("/edit", default);
        await fixture.Frontend.LineAsync("", default);
        await fixture.Frontend.LineAsync("draft\0\u001b[31m\u6587\U0001f642", default);
        await using var scope = fixture.Ui.OpenScope(new Context());
        var confirm = scope.ConfirmAsync("expires", "never approve", new(42.75)).AsTask();
        await fixture.Observer.WaitPublished(1); var old = fixture.Observer.Published[0];
        Equal(42.75, old.Request.Value.GetProperty("timeout").GetDouble());
        await fixture.Frontend.LineAsync("maybe", default); Equal(0, fixture.Commands.Count);
        fixture.Clock.Fire(); Equal(ExtensionUiOutcomeKind.TimedOut, (await confirm.WaitAsync(Deadline)).Kind);
        fixture.Reply(old.Identity.RequestId, confirmed: true); Equal(1, fixture.Observer.Retired.Count);
        await fixture.Frontend.LineAsync("", default);
        await fixture.Frontend.LineAsync("after timeout", default);
        await fixture.Frontend.LineAsync("/save", default);
        Equal("prompt", fixture.Commands.Single().Value.GetProperty("type").GetString());
        Equal("\ndraft\0\u001b[31m\u6587\U0001f642\n\nafter timeout", fixture.Commands.Single().Value.GetProperty("message").GetString());
        Check(fixture.View.Text.Contains("draft\\u0000\\u001b[31m", StringComparison.Ordinal) && !fixture.View.Text.Contains('\u001b'), "Retirement changed or executed inert draft controls.");

        await fixture.Frontend.LineAsync("/edit", default); await fixture.Frontend.LineAsync("main draft", default);
        using var caller = new CancellationTokenSource();
        var editor = scope.EditorAsync("canceled editor", "dialog prefill\r\n", caller.Token).AsTask();
        await fixture.Observer.WaitPublished(2);
        await fixture.Frontend.LineAsync("dialog only", default);
        caller.Cancel(); Equal(ExtensionUiOutcomeKind.Cancelled, (await editor.WaitAsync(Deadline)).Kind);
        await fixture.Frontend.LineAsync("main continuation", default); await fixture.Frontend.LineAsync("/save", default);
        Equal("main draft\nmain continuation", fixture.Commands[^1].Value.GetProperty("message").GetString());
        var freshEditor = scope.EditorAsync("fresh editor", "new prefill").AsTask(); await fixture.Observer.WaitPublished(3);
        await fixture.Frontend.LineAsync("", default); await fixture.Frontend.LineAsync("/save", default);
        Equal("new prefill\n", (await freshEditor.WaitAsync(Deadline)).Value);
        Equal(fixture.Observer.Published[2].Identity.RequestId, fixture.Commands[^1].Value.GetProperty("id").GetString());
        Check(!fixture.Commands[^1].Value.GetProperty("value").GetString()!.Contains("dialog only", StringComparison.Ordinal), "Retired editor leaked text into its successor.");
    }

    private static async Task ReplacementFocus()
    {
        await using var fixture = new UiFixture();
        var oldScope = fixture.Ui.OpenScope(new Context());
        var old = oldScope.ConfirmAsync("old owner", "deny").AsTask(); await fixture.Observer.WaitPublished(1);
        await using var queuedScope = fixture.Ui.OpenScope(new Context(OwnerGeneration: 2));
        var queued = queuedScope.InputAsync("queued owner").AsTask(); await fixture.Observer.WaitPublished(2);
        var queuedIdentity = fixture.Observer.Published[1].Identity;
        await using var retiredQueuedScope = fixture.Ui.OpenScope(new Context(OwnerGeneration: 4));
        var retiredQueued = retiredQueuedScope.EditorAsync("retired queued editor", "must never receive focus").AsTask();
        await fixture.Observer.WaitPublished(3);
        // A same-ID observation from another connection/session/owner generation cannot steal focus.
        foreach (var stale in new[] { queuedIdentity with { ConnectionGeneration = 8 }, queuedIdentity with { SessionGeneration = 12 },
            queuedIdentity with { OwnerId = "different-owner" }, queuedIdentity with { OwnerGeneration = 3 } })
            await fixture.Frontend.RetiredAsync(new(stale, ExtensionUiOutcomeKind.Cancelled, null, true, true), default);
        var current = fixture.Observer.Published[0].Identity;
        foreach (var stale in new[] { current with { ConnectionGeneration = 8 }, current with { SessionGeneration = 12 },
            current with { OwnerId = "different-owner" }, current with { OwnerGeneration = 2 } })
            await fixture.Frontend.RetiredAsync(new(stale, ExtensionUiOutcomeKind.Cancelled, null, true, true), default);
        await fixture.Frontend.LineAsync("not approval", default); Equal(0, fixture.Commands.Count);
        await retiredQueuedScope.DisposeAsync(); Equal(ExtensionUiOutcomeKind.Cancelled, (await retiredQueued).Kind);
        await oldScope.DisposeAsync(); Equal(ExtensionUiOutcomeKind.Cancelled, (await old).Kind);
        Check(fixture.View.Text.Contains("[ui input] queued owner", StringComparison.Ordinal), "A stale generation removed a live queued dialog.");
        Check(!fixture.View.Text.Contains("[ui editor] retired queued editor", StringComparison.Ordinal), "A retired queued dialog acquired focus.");
        await fixture.Frontend.LineAsync("queued exact answer", default); Equal("queued exact answer", (await queued.WaitAsync(Deadline)).Value);
        Equal(queuedIdentity.RequestId, fixture.Commands.Single().Value.GetProperty("id").GetString());
        fixture.Commands.Clear();
        await using var replacementScope = fixture.Ui.OpenScope(new Context(OwnerGeneration: 3));
        var replacement = replacementScope.ConfirmAsync("replacement", "explicit only").AsTask(); await fixture.Observer.WaitPublished(4);
        var replacementIdentity = fixture.Observer.Published[3].Identity;
        Check(replacementIdentity.RequestId != current.RequestId, "Replacement reused a request ID.");
        fixture.Reply(current.RequestId, confirmed: true); fixture.Reply(queuedIdentity.RequestId, text: "late");
        await fixture.Frontend.RetiredAsync(fixture.Observer.Retired[0], default);
        await fixture.Frontend.ObserveAsync(JsonData.Parse("{\"type\":\"agent_settled\"}"), default);
        await fixture.Frontend.LineAsync("maybe", default); Equal(0, fixture.Commands.Count);
        await fixture.Frontend.LineAsync("no", default); Equal(false, (await replacement.WaitAsync(Deadline)).Value);
        Equal(replacementIdentity.RequestId, fixture.Commands.Single().Value.GetProperty("id").GetString());
        Equal(4, fixture.Observer.Retired.Count);
        await fixture.Frontend.LineAsync("after replacement", default);
        Equal("prompt", fixture.Commands[^1].Value.GetProperty("type").GetString());
    }

    private static async Task AnswerOwnership()
    {
        await using var fixture = new UiFixture { ForwardReplies = false };
        await using var scope = fixture.Ui.OpenScope(new Context());
        var answer = scope.InputAsync("pending input").AsTask(); await fixture.Observer.WaitPublished(1);
        var identity = fixture.Observer.Published[0].Identity;
        const string exact = "literal\0\u001b[31m\u2028\u2029\u6587\U0001f642";
        await fixture.Frontend.LineAsync("/answer " + exact, default);
        await fixture.Frontend.LineAsync("must not become a prompt", default);
        Equal(1, fixture.Commands.Count); Equal(exact, fixture.Commands[0].Value.GetProperty("value").GetString());
        Equal(identity.RequestId, fixture.Commands[0].Value.GetProperty("id").GetString());
        Check(fixture.Commands[0].Value.EnumerateObject().All(field => field.Name is "type" or "id" or "value"), "Native identity leaked into a baseline reply.");
        Check(!answer.IsCompleted && fixture.View.Text.Contains("Awaiting host retirement", StringComparison.Ordinal), "Local answer released focus before actual retirement.");
        await fixture.Frontend.LineAsync("/steer retained steering", default);
        await fixture.Frontend.LineAsync("/follow-up retained followup", default);
        await fixture.Frontend.LineAsync("/state", default);
        Equal(new[] { "extension_ui_response", "steer", "follow_up", "get_state" }, fixture.Commands.Select(command => command.Value.GetProperty("type").GetString()!).ToArray());
        fixture.Ui.AcceptResponse(fixture.Commands[0]); Equal(exact, (await answer.WaitAsync(Deadline)).Value);
        fixture.Ui.AcceptResponse(fixture.Commands[0]); Equal(1, fixture.Observer.Retired.Count);
        await fixture.Frontend.LineAsync("after answer", default); Equal("prompt", fixture.Commands[^1].Value.GetProperty("type").GetString());

        using var legacyView = new StringWriter(); using var legacy = new InteractiveSessionFrontend(legacyView);
        var legacyCommands = new List<JsonData>(); legacy.Bind((record, _) => { legacyCommands.Add(record); return Task.CompletedTask; });
        await legacy.ObserveAsync(fixture.Observer.Published[0].Request, default);
        await legacy.LineAsync("legacy answer", default); await legacy.LineAsync("legacy prompt", default);
        Equal("extension_ui_response", legacyCommands[0].Value.GetProperty("type").GetString());
        Equal("prompt", legacyCommands[1].Value.GetProperty("type").GetString());
        await legacy.PublishedAsync(fixture.Observer.Published[0], default); await legacy.RetiredAsync(fixture.Observer.Retired[0], default);
        Check(!legacyView.ToString().Contains("ui retired", StringComparison.Ordinal), "Legacy observation opted into native metadata.");
    }

    private static async Task HostShutdown()
    {
        foreach (var abort in new[] { false, true })
        foreach (var held in new[] { "published-flush", "retired" })
        {
            var variant = (abort ? "abort" : "eof") + "-" + held;
            using var files = await HostFiles.Create(published);
            using var createOutput = new StringWriter(); using var errors = new StringWriter();
            // Registry reopen restores the durable toolsAdded loadout; activation at reopen alone does not add a tool.
            Equal(0, await SessionCommands.RunAsync(files.CreateArgs, createOutput, errors));
            var prefix = await File.ReadAllBytesAsync(files.Session);
            var created = await new SessionLogReader().ReadFileAsync(files.Session);
            var createdContext = new SessionContextProjector().ProjectLatest(created.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
            Check(createdContext.LlmMessages.Any(message => message.Role == "system" &&
                message.WireBody.Value.GetProperty("toolsAdded").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "fixture.cli.ui")),
                variant + ": the approved native UI tool is absent from the durable initial loadout.");
            await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[]
            {
                SessionCommandTests.CompletionsTool("fixture.cli.ui", "retirement-confirm", new { action = "confirm" }),
                SessionCommandTests.CompletionsTool("write", "must-not-follow", new { path = files.Effect, content = "must not execute" }),
                SessionCommandTests.CompletionsText("must not acquire")
            } }), Utf8);
            using var view = new View { HoldConfirmFlush = held == "published-flush" };
            using var frontend = new InteractiveSessionFrontend(view, nativePresentation: true);
            JsonData? lastState = null;
            await using var connection = new BoundedRpcConnection((record, token) =>
            {
                if (record.Value.GetProperty("type").GetString() == "response" && record.Value.GetProperty("command").GetString() == "get_state") lastState = record;
                return frontend.ObserveAsync(record, token);
            }); frontend.Bind(connection.SendAsync);
            var observer = new Observer(frontend) { HoldRetirement = held == "retired" };
            using var cancellation = new CancellationTokenSource();
            var running = RpcSessionCommand.RunWithPresentationAsync(files.RpcArgs, connection.Input, connection.Output, errors, observer, cancellation.Token);
            var stage = "startup-history";
            try
            {
                await frontend.StartAsync(default); await frontend.Ready.WaitAsync(Deadline); await frontend.HistoryAsync(default);
                await view.Wait("[history]");
                await frontend.LineAsync("/edit", default); await frontend.LineAsync("main draft\0\u001b[31m", default);
                await frontend.LineAsync("/send actual held dialog", default);
                stage = "wait-dialog-" + held;
                if (held == "published-flush") await view.FlushEntered.Task.WaitAsync(Deadline);
                else await observer.WaitPublished(1);
                stage = "request-" + (abort ? "abort" : "eof");
                if (abort)
                {
                    await frontend.LineAsync("/steer retained steering", default); await frontend.LineAsync("/follow-up retained followup", default);
                    await frontend.LineAsync("/abort", default);
                }
                else connection.CompleteInput();
                stage = "wait-cancellation-or-retirement";
                if (held == "published-flush") await observer.PublicationCanceled.Task.WaitAsync(Deadline);
                else await observer.RetirementEntered.Task.WaitAsync(Deadline);
                Check(!running.IsCompleted, "Actual host skipped an entered native callback/flush join.");
                try { await using var escaped = await SessionLogStore.OpenAsync(files.Session); throw new InvalidOperationException("Held callback released the durable writer early."); }
                catch (SessionLogStoreException error) { Equal(SessionLogStoreFailure.OpenFailed, error.Failure); }
                view.FlushRelease.TrySetResult(); observer.RetirementRelease.TrySetResult();
                stage = "await-terminal-and-queues";
                if (abort)
                {
                    await view.Wait("[accepted] abort"); await view.Wait("[settled]");
                    await frontend.LineAsync("/state", default); await view.Wait("[state] idle pending=2");
                    await frontend.LineAsync("/clear-queue", default);
                    await view.Wait("[cleared] steering=[\"retained steering\"] followUp=[\"retained followup\"]");
                    connection.CompleteInput();
                }
                stage = "await-host-close";
                Equal(0, await running.WaitAsync(Deadline)); Equal("", errors.ToString());
                stage = "verify-focus-durable-and-package-close";
                Equal(1, observer.Published.Count); Equal(1, observer.Retired.Count);
                Equal(observer.Published[0].Identity, observer.Retired[0].Identity);
                Check(observer.Retired[0].Outcome != ExtensionUiOutcomeKind.Value, "Abort/EOF approved a dialog.");
                Check(!File.Exists(files.Effect) && !view.Text.Contains("must not acquire", StringComparison.Ordinal), "Shutdown admitted later effects or provider bytes.");
                var restored = new List<JsonData>(); frontend.Bind((record, _) => { restored.Add(record); return Task.CompletedTask; });
                await frontend.LineAsync("main after cleanup", default); await frontend.LineAsync("/save", default);
                Equal("prompt", restored.Single().Value.GetProperty("type").GetString());
                Equal("main draft\0\u001b[31m\nmain after cleanup", restored.Single().Value.GetProperty("message").GetString());
                Equal(0, view.ActiveFlushes); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
                var log = await new SessionLogReader().ReadFileAsync(files.Session);
                Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.OriginalBytes.AsSpan().StartsWith(prefix), "Shutdown returned before durable close or rewrote its prefix.");
                var context = new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
                Check(context.LlmMessages.Any(message => message.WireBody.Value.GetProperty("role").GetString() == "toolResult" && message.WireBody.Value.GetProperty("isError").GetBoolean()), "Actual canceled callback lost its durable tool result.");
                Check(!context.LlmMessages.Any(message => message.WireBody.ToString().Contains("retained steering", StringComparison.Ordinal) || message.WireBody.ToString().Contains("retained followup", StringComparison.Ordinal)), "Queued input became canonical on shutdown.");
                await using var reopened = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
            }
            catch (Exception error)
            {
                var diagnostics = await RetainHostFailure(variant, stage, files, view, errors, observer, running, lastState, error);
                throw new InvalidOperationException("Native retirement " + variant + " failed during " + stage + "; diagnostics=" + diagnostics +
                    "; view=" + JsonSerializer.Serialize(Bounded(view.Text, 4096)) + "; stderr=" + JsonSerializer.Serialize(Bounded(errors.ToString(), 2048)), error);
            }
            finally
            {
                view.FlushRelease.TrySetResult(); observer.RetirementRelease.TrySetResult(); connection.CompleteInput(); cancellation.Cancel(); await running;
            }
        }
    }

    private static async Task<string> RetainHostFailure(string variant, string stage, HostFiles files, View view,
        StringWriter errors, Observer observer, Task<int> running, JsonData? lastState, Exception error)
    {
        try
        {
            Directory.CreateDirectory(failureRoot);
            var path = Path.Combine(failureRoot, variant + "-" + Guid.NewGuid().ToString("N") + ".json");
            var record = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, variant, stage, hostStatus = running.Status.ToString(),
                view = Bounded(view.Text, 8192), stderr = Bounded(errors.ToString(), 4096),
                lastObservedState = Bounded(lastState?.ToString(), 4096), error = Bounded(error.ToString(), 4096),
                activeDisplayFlushes = view.ActiveFlushes, displayFlushEntered = view.FlushEntered.Task.IsCompleted,
                publicationCanceled = observer.PublicationCanceled.Task.IsCompleted,
                retirementEntered = observer.RetirementEntered.Task.IsCompleted, observer = observer.Snapshot(),
                api = "openai-completions", tool = "fixture.cli.ui", action = "confirm", publishedSource = Bounded(published, 512),
                createArgs = files.CreateArgs.Select(value => Bounded(value, 512)).ToArray(), rpcArgs = files.RpcArgs.Select(value => Bounded(value, 512)).ToArray(),
                scriptSha256 = await FileHash(files.Script), manifestSha256 = await FileHash(files.Manifest), approvalSha256 = await FileHash(files.Approval),
                publishedAssemblySha256 = await FileHash(Path.Combine(files.Package, "PublishedFixture.CliUi.dll")),
                sessionLength = new FileInfo(files.Session).Length, effectExists = File.Exists(files.Effect),
                snapshotDirectories = Directory.GetDirectories(files.Snapshots).Length
            }, new JsonSerializerOptions { WriteIndented = true });
            Check(Utf8.GetByteCount(record) + 1 <= 131_072, "Retirement failure diagnostics exceed their retained byte bound.");
            await File.WriteAllTextAsync(path, record + "\n", Utf8); return path;
        }
        catch (Exception diagnosticError) { return "retention-failed:" + diagnosticError.GetType().Name; }
    }
    private static async Task<string> FileHash(string path)
    { await using var source = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(source)); }
    private static string? Bounded(string? value, int maximum) => value is null || value.Length <= maximum ? value : value[..maximum] + "[truncated]";

    private sealed record Context(string OwnerId = "cooked-owner", long OwnerGeneration = 1,
        CancellationToken OperationCancellationToken = default, CancellationToken SessionCancellationToken = default,
        CancellationToken ExtensionLifetimeCancellationToken = default) : IExtensionContext;
    private sealed class UiFixture : IAsyncDisposable
    {
        public readonly View View = new(); public readonly InteractiveSessionFrontend Frontend;
        public readonly Observer Observer; public readonly Clock Clock = new();
        public readonly RpcExtensionUiCoordinator Ui; public readonly List<JsonData> Commands = [];
        private readonly BoundedRpcConnection connection; private readonly JsonlWriter writer;
        public bool ForwardReplies = true;
        public UiFixture()
        {
            Frontend = new(View, nativePresentation: true); Observer = new(Frontend); connection = new(Frontend.ObserveAsync);
            writer = new(connection.Output); Ui = new(timeProvider: Clock, connectionGeneration: 7, sessionGeneration: 11, presentationObserver: Observer);
            Ui.Attach(async (record, suppress, token) => { suppress.ThrowIfCancellationRequested(); await writer.WriteAsync(record, token); }, _ => throw new InvalidOperationException("Unexpected UI host failure."), 1_048_576);
            Frontend.Bind((record, _) => { Commands.Add(record); if (ForwardReplies && record.Value.GetProperty("type").GetString() == "extension_ui_response") Ui.AcceptResponse(record); return Task.CompletedTask; });
        }
        public void Reply(string id, bool? confirmed = null, string? text = null) => Ui.AcceptResponse(JsonData.Parse(confirmed is { } value ?
            JsonSerializer.Serialize(new { type = "extension_ui_response", id, confirmed = value }) : JsonSerializer.Serialize(new { type = "extension_ui_response", id, value = text })));
        public async ValueTask DisposeAsync()
        { Observer.RetirementRelease.TrySetResult(); View.FlushRelease.TrySetResult(); await Ui.DisposeAsync(); await writer.DisposeAsync(); await connection.DisposeAsync(); Frontend.Dispose(); View.Dispose(); }
    }
    private sealed class Observer(InteractiveSessionFrontend frontend) : IRpcExtensionUiPresentationObserver
    {
        public readonly List<RpcExtensionUiPresentation> Published = []; public readonly List<RpcExtensionUiRetirement> Retired = [];
        private readonly object gate = new(); private TaskCompletionSource changed = Signal(); private int publishedCompleted;
        public bool HoldRetirement;
        public readonly TaskCompletionSource PublicationCanceled = Signal(), RetirementEntered = Signal(), RetirementRelease = Signal();
        public async ValueTask PublishedAsync(RpcExtensionUiPresentation value, CancellationToken token)
        {
            using var registration = token.Register(() => PublicationCanceled.TrySetResult());
            lock (gate) Published.Add(value);
            await frontend.PublishedAsync(value, token);
            TaskCompletionSource signal; lock (gate) { publishedCompleted++; signal = changed; changed = Signal(); } signal.TrySetResult();
        }
        public async ValueTask RetiredAsync(RpcExtensionUiRetirement value, CancellationToken token)
        {
            lock (gate) Retired.Add(value); RetirementEntered.TrySetResult(); if (HoldRetirement) await RetirementRelease.Task;
            await frontend.RetiredAsync(value, token);
        }
        public object Snapshot()
        { lock (gate) return new { published = Published.Take(32).Select(value => value.Identity).ToArray(), retired = Retired.Take(32).ToArray(), publishedCompleted }; }
        public async Task WaitPublished(int count)
        { while (true) { Task next; lock (gate) { if (publishedCompleted >= count) return; next = changed.Task; } await next.WaitAsync(Deadline); } }
    }
    private sealed class View : TextWriter
    {
        private readonly object gate = new(); private readonly StringBuilder text = new(); private TaskCompletionSource changed = Signal();
        private bool pendingHold; private int active;
        public override Encoding Encoding => Utf8; public string Text { get { lock (gate) return text.ToString(); } }
        public bool HoldConfirmFlush; public int ActiveFlushes => Volatile.Read(ref active);
        public readonly TaskCompletionSource FlushEntered = Signal(), FlushRelease = Signal();
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var value = buffer.ToString();
            if (HoldConfirmFlush && value.StartsWith("[ui confirm]", StringComparison.Ordinal)) { HoldConfirmFlush = false; pendingHold = true; }
            TaskCompletionSource signal; lock (gate) { Check(buffer.Length <= 2_097_152 - text.Length, "Retirement view exceeded its test bound."); text.Append(value); signal = changed; changed = Signal(); } signal.TrySetResult(); return Task.CompletedTask;
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            Interlocked.Increment(ref active);
            try { if (pendingHold) { pendingHold = false; FlushEntered.TrySetResult(); await FlushRelease.Task; } token.ThrowIfCancellationRequested(); }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task Wait(string value)
        { while (true) { Task next; lock (gate) { if (text.ToString().Contains(value, StringComparison.Ordinal)) return; next = changed.Task; } await next.WaitAsync(Deadline); } }
    }
    private sealed class Clock : TimeProvider
    {
        private Timer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Equal(Timeout.InfiniteTimeSpan, period); return timer = new(callback, state); }
        public void Fire() => (timer ?? throw new InvalidOperationException("Actual UI timer is absent.")).Fire();
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed, fired;
            public void Fire() { Check(!disposed && !fired, "Actual UI timer was already closed."); fired = true; callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class HostFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-retirement-" + Guid.NewGuid().ToString("N"));
        public string Session => Path.Combine(Root, "session.jsonl"); public string Script => Path.Combine(Root, "script.json");
        public string Effect => Path.Combine(Root, "effect.txt"); public string Snapshots => Path.Combine(Root, "snapshots");
        public string Package => Path.Combine(Root, "published"); public string Manifest => Path.Combine(Root, "manifest.json"); public string Approval => Path.Combine(Root, "approval.json");
        private string[] ExtensionArgs => ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval, "--extension-snapshot-root", Snapshots, "--enable-extension-tool", "fixture.cli.ui"];
        public string[] CreateArgs => new[] { "session", "create", "--session", Session, "--workspace", Root, "--offline-api", "openai-completions" }.Concat(ExtensionArgs).ToArray();
        public string[] RpcArgs => new[] { "session", "rpc", "--session", Session, "--workspace", Root, "--offline-script", Script, "--offline-api", "openai-completions", "--allow-write", Effect }.Concat(ExtensionArgs).ToArray();
        public static async Task<HostFiles> Create(string published)
        {
            Check(File.Exists(Path.Combine(published, "PublishedFixture.CliUi.dll")), "Root must publish the existing native UI fixture before this test.");
            var files = new HostFiles();
            try
            {
                Directory.CreateDirectory(files.Root); Directory.CreateDirectory(files.Package); Directory.CreateDirectory(files.Snapshots);
                foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(files.Package, Path.GetRelativePath(published, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    hashes.Add(Path.GetRelativePath(files.Package, source).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.cli.ui", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native",
                    assembly = "PublishedFixture.CliUi.dll", entryType = "PublishedCliUiFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" }, requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" },
                    declaredCapabilities = new[] { "tools", "observations" }, resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.CliUi.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
                await File.WriteAllTextAsync(files.Manifest, manifest, Utf8);
                await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package,
                    manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(manifest))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0",
                    hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = new[] { "fixture.cli.ui" } }), Utf8);
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Check(Path.GetDirectoryName(target) == parent && Path.GetFileName(target).StartsWith("pisharp-retirement-", StringComparison.Ordinal), "Refusing unowned retirement-test cleanup.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Equal<T>(T expected, T actual) => Check(expected is Array left && actual is Array right ? left.Cast<object?>().SequenceEqual(right.Cast<object?>()) : EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
