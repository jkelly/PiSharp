using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;
using PiSharp.Tui;
using PiSharp.Tui.Input;

// Authored controls over the actual input loops and command; no real console, child process, provider call or Source capture.
internal static class TerminalStartupCases
{
    internal static readonly string[] RouteIds = ["remapped-submit", "disabled-submit", "newline-submit-conflict", "backslash-submit",
        "shifted-nonprintable", "remapped-layout", "kitty-mode-legacy-alt-and-lf", "remapped-cancel", "remapped-empty-exit", "producer-interrupt-held-paint"];
    internal static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TerminalKeybindingConfiguration Configuration(string json) =>
        TerminalKeybindingConfigurationLoader.Load("authored-agent", "win32", readText: _ => json);

    internal static void Run(ConsumerEvidence evidence, string reviewRoot)
    {
        foreach (var acknowledged in new[] { false, true })
        foreach (var id in RouteIds)
        {
            var category = acknowledged ? "acknowledged-route" : "legacy-route";
            evidence.Begin(category, id, new { route = category, authoredNotSourceCaptured = true });
            evidence.Complete(Route(acknowledged, id, evidence).GetAwaiter().GetResult());
        }
        evidence.Begin("startup", "official-composition-durable-reset", new { realCommand = true, nativeKittyMode = false, paidCalls = false });
        evidence.Complete(Official(evidence, reviewRoot).GetAwaiter().GetResult());
    }

    private static async Task<object> Route(bool acknowledged, string id, ConsumerEvidence evidence)
    {
        var json = id switch
        {
            "remapped-submit" or "backslash-submit" => "{\"tui.input.submit\":\"alt+j\",\"tui.input.newLine\":[]}",
            "disabled-submit" => "{\"tui.input.submit\":[]}",
            "newline-submit-conflict" => "{\"tui.input.submit\":\"alt+j\",\"tui.input.newLine\":\"alt+j\"}",
            "remapped-layout" => "{\"tui.editor.cursorUp\":\"ctrl+j\",\"tui.input.newLine\":[]}",
            "kitty-mode-legacy-alt-and-lf" => "{\"tui.editor.cursorLeft\":\"alt+j\",\"tui.input.newLine\":[]}",
            "remapped-cancel" => "{\"app.interrupt\":\"alt+q\"}",
            "remapped-empty-exit" => "{\"app.exit\":\"ctrl+e\"}",
            "producer-interrupt-held-paint" => "{\"app.clear\":\"ctrl+x\"}",
            _ => "{}"
        };
        var runs = new List<object>(); var passed = true;
        foreach (var mode in id == "kitty-mode-legacy-alt-and-lf" ? new[] { false, true } : new[] { false })
        {
            var configuration = Configuration(json); var host = new RouteHost(acknowledged, configuration, mode);
            var checks = new List<object>();
            void Check(string criterion, bool value) { passed &= value; checks.Add(new { criterion, passed = value }); }
            try
            {
                await host.Start();
                switch (id)
                {
                    case "remapped-submit":
                        await host.Step("abc", "abc", 3); await host.Step("\u001bj", "", 0);
                        Check("configured-submit-and-reset", host.Submitted.ToArray().SequenceEqual(new[] { "abc" })); break;
                    case "disabled-submit":
                        await host.Step("abc", "abc", 3); await host.Step("\r", "abc", 3);
                        Check("disabled-enter-does-not-submit", host.Submitted.IsEmpty); break;
                    case "newline-submit-conflict":
                        await host.Step("abc", "abc", 3); await host.Step("\u001bj", "abc\n", 4);
                        Check("source-newline-precedes-submit", host.Submitted.IsEmpty && configuration.CreateBindings().GetConflicts().Any(conflict => conflict.Key == "alt+j")); break;
                    case "backslash-submit":
                        await host.Step("abc\\", "abc\\", 4); await host.Step("\u001bj", "abc\n", 4);
                        Check("backslash-escapes-configured-submit", host.Submitted.IsEmpty);
                        await host.Step("\u001bj", "", 0); Check("later-submit-trims-newline", host.Submitted.ToArray().SequenceEqual(new[] { "abc" })); break;
                    case "shifted-nonprintable":
                        await host.Step("x", "x", 1); await host.Step("\u001b[97:13;2u", "x", 1);
                        await host.Step("\u001b[97:13;2:2u", "x", 1); Check("authoritative-null-press-and-repeat", host.Submitted.IsEmpty); break;
                    case "remapped-layout":
                        await host.Step("\u001b[200~a\nb\u001b[201~", "a\nb", 3);
                        await host.Step("\u001b[106;5u", "a\nb", 1); Check("configured-navigation-uses-current-view-map", host.Last!.Layout is not null); break;
                    case "kitty-mode-legacy-alt-and-lf":
                        Check("registry-matcher-mode-agrees-with-reader", configuration.CreateBindings(mode).Matches("\u001bj", "tui.editor.cursorLeft") == !mode);
                        await host.Step("xy", "xy", 2); await host.Step("\u001bj", "xy", mode ? 2 : 1);
                        // Source's raw LF compatibility remains a newline even with the configurable claim disabled.
                        await host.Step("\n", mode ? "xy\n" : "x\ny", mode ? 3 : 2);
                        Check("mode-fixed-per-owner-and-raw-lf-compatible", host.Submitted.IsEmpty); break;
                    case "remapped-cancel":
                        await host.Step("abc", "abc", 3); await host.Step("\u001bq", "", 0);
                        Check("configured-cancel-clears-owned-draft", acknowledged ? host.Cancellations == 1 : host.Submitted.ToArray().SequenceEqual(new[] { "/cancel" })); break;
                    case "remapped-empty-exit":
                        host.Sink.Queue("\u0005"); var result = await host.Running!.WaitAsync(Watchdog);
                        Check("configured-empty-draft-exit-with-open-input", result == TerminalInputExit.Quit && !host.Sink.Ended); break;
                    case "producer-interrupt-held-paint":
                        host.HoldNextPaint = true; host.Sink.Queue("x"); await host.HeldPaint.Task.WaitAsync(Watchdog);
                        var afterOldKey = host.Sink.NextReadAfterPacket(); host.Sink.Queue("\u0003"); await afterOldKey.WaitAsync(Watchdog);
                        Check("old-producer-key-disabled", host.Interrupts == 0);
                        host.Sink.Queue("\u0018"); await host.Interrupted.Task.WaitAsync(Watchdog);
                        Check("remapped-producer-interrupt-reaches-held-consumer", host.Interrupts == 1 && !host.Running!.IsCompleted);
                        host.ReleasePaint.TrySetResult(); await host.WaitPaint("x", 1); break;
                }
            }
            catch (Exception error)
            {
                evidence.Observe("route-incomplete-before-original-join", new { id, mode, error = ConsumerException.From(error),
                    runningCompleted = host.Running?.IsCompleted, terminal = host.Sink.Snapshot, host.Interrupts, receipts = host.Receipts.Snapshot });
                throw;
            }
            finally { await host.Join(); }
            var receipt = host.Receipts.Snapshot;
            var joined = host.Running!.IsCompleted && host.Sink.Joined && host.Sink.Disposals == 0 &&
                receipt.Pending == 0 && receipt.Accepted == 0 && receipt.RetainedUtf16 == 0 && host.FocusDetached;
            Check("all-owned-work-joined-and-borrowed-terminal-retained", joined);
            var row = new { mode, json, checks, submitted = host.Submitted.ToArray(), host.Cancellations, host.Interrupts,
                paints = host.Paints.ToArray().Select(paint => new { paint.Text, paint.CursorUtf16Offset, paint.EditorOwnsFocus }),
                receipts = new { receipt.Pending, receipt.Accepted, receipt.RetainedUtf16 }, terminal = host.Sink.Snapshot, host.Sink.Disposals, joined };
            runs.Add(row); evidence.Observe("actual-route-observation", row);
        }
        return new { id, passed, runs, authoredNotSourceCaptured = true, physicalConsoleAcquired = false };
    }

    private static async Task<object> Official(ConsumerEvidence evidence, string reviewRoot)
    {
        var directory = Path.Combine(reviewRoot, "artifacts", "terminal-startup-control-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var session = Path.Combine(directory, "session.jsonl"); var script = Path.Combine(directory, "offline.json");
        await NewFile(script, """
            {"schemaVersion":1,"turns":[{"requiredInputTexts":[],"events":[
              {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg-text","content":[]}},
              {"type":"response.output_text.delta","output_index":0,"item_id":"msg-text","delta":"unused response"},
              {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg-text","content":[{"type":"output_text","text":"unused response"}]}},
              {"type":"response.completed","response":{"status":"completed","output":[],"usage":{"input_tokens":8,"output_tokens":4,"total_tokens":12}}}]}]}
            """);
        using var output = new StringWriter(); using var createError = new StringWriter();
        var created = await SessionCommands.RunAsync(["session", "create", "--session", session, "--workspace", directory, "--offline-api", "openai-responses"], output, createError);
        evidence.Observe("startup-created-offline-session", new { directory, session, script, created, output = output.ToString(), error = createError.ToString(), scriptSha256 = EvidenceAdmission.Hash(script) });
        if (created != 0 || createError.ToString().Length != 0) throw new InvalidDataException("Authored offline startup session was not created.");
        var configuration = Configuration("{\"tui.input.submit\":\"alt+j\",\"tui.input.newLine\":[],\"app.exit\":\"ctrl+e\",\"app.interrupt\":\"alt+q\",\"app.clear\":\"ctrl+x\"}");
        var terminal = new TestTerminal(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var history = Gate(); var replaced = Gate(); string? replacementFile = null; long generation = 0;
        var records = new ConcurrentQueue<JsonElement>();
        var running = TerminalSessionCommand.RunObservedConfiguredAsync(["session", "terminal", "--terminal-preview", "--session", session,
            "--workspace", directory, "--offline-api", "openai-responses", "--offline-script", script], terminal, terminal, error, Observe, configuration, stop.Token);
        int exitCode; var exitedBeforeEof = false;
        try
        {
            await history.Task.WaitAsync(Watchdog); terminal.Queue("/new\u001bj"); await replaced.Task.WaitAsync(Watchdog);
            // The open input is deliberate: only the remapped empty-draft exit can settle this task.
            terminal.Queue("\u0005"); exitCode = await running.WaitAsync(Watchdog); exitedBeforeEof = !terminal.Ended;
        }
        catch (Exception failure)
        { evidence.Observe("startup-incomplete-before-original-join", new { error = ConsumerException.From(failure), records = records.ToArray(), replacementFile, generation, terminal = terminal.Snapshot, originalCompleted = running.IsCompleted }); throw; }
        finally { stop.Cancel(); terminal.End(); await running; }
        var reads = new List<object>(); var complete = true;
        foreach (var file in replacementFile is null ? new[] { session } : new[] { session, replacementFile! })
        {
            var result = await new SessionLogReader().ReadFileAsync(file);
            var valid = result.SourceComplete && result.Status == SessionLogReadStatus.Complete && result.ValidatedPrefixByteLength == result.OriginalBytes.Length;
            complete &= valid; reads.Add(new { file, sha256 = EvidenceAdmission.Hash(file), result.Status, result.SourceComplete, result.ValidatedPrefixByteLength, originalBytes = result.OriginalBytes.Length, valid });
        }
        var passed = exitCode == 0 && exitedBeforeEof && error.ToString().Length == 0 && generation > 1 && replacementFile is not null &&
            replacementFile != session && complete && terminal.Joined && terminal.Disposals == 0;
        var observation = new { exitCode, error = error.ToString(), replacementFile, generation, records = records.ToArray(), reads,
            terminal = terminal.Snapshot, terminal.Disposals, terminal.Ended, exitedBeforeEof, allOriginalTasksJoined = running.IsCompleted, defaultKittyMode = false };
        evidence.Observe("official-startup-and-reset-observation", observation);
        return new { passed, observation, retainedEvidenceDirectory = directory, authoredNotSourceCaptured = true,
            defaultEnvironmentPathRouteExecuted = false, physicalConsoleAcquired = false };

        ValueTask Observe(JsonData record, CancellationToken token)
        {
            var body = record.Value; records.Enqueue(body.Clone());
            if (body.GetProperty("type").GetString() == "session_switched")
            { replacementFile = body.GetProperty("sessionFile").GetString(); generation = body.GetProperty("generation").GetInt64(); }
            if (body.GetProperty("type").GetString() == "response")
            {
                var command = body.GetProperty("command").GetString();
                if (command == "get_messages") history.TrySetResult();
                if (command == "new_session")
                {
                    if (!body.GetProperty("success").GetBoolean() || body.GetProperty("data").GetProperty("cancelled").GetBoolean())
                        throw new InvalidDataException("New session was rejected or canceled.");
                    replaced.TrySetResult();
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    internal static readonly string[] DefaultStartupIds = ["default-startup-missing-settings", "default-startup-malformed-settings",
        "default-startup-migrated-remap-reset", "default-startup-disabled-submit"];

    internal static void RunDefault(ConsumerEvidence evidence, string reviewRoot)
    {
        foreach (var id in DefaultStartupIds)
        {
            evidence.Begin("default-startup", id, new { invokesDefaultStartup = true, configurationInjected = false, authoredNotSourceCaptured = true });
            var result = ConfigurationLoadingCases.IsolateDefault(reviewRoot, evidence, scope => OfficialDefault(id, scope, evidence)).GetAwaiter().GetResult();
            evidence.Complete(result);
        }
    }

    private static async Task<object> OfficialDefault(string id, ConfigurationLoadingCases.DefaultEnvironment scope, ConsumerEvidence evidence)
    {
        var remapped = id == "default-startup-migrated-remap-reset"; var disabled = id == "default-startup-disabled-submit";
        if (id == "default-startup-malformed-settings") scope.CreateSettings("{");
        if (remapped) scope.CreateSettings("\uFEFF{\"submit\":[\"alt+j\"],\"newLine\":[],\"exit\":\"ctrl+e\",\"interrupt\":\"alt+q\",\"clear\":\"ctrl+x\"}");
        if (disabled) scope.CreateSettings("{\"submit\":[],\"newLine\":[],\"exit\":\"ctrl+e\"}");
        var settingsBefore = scope.SettingsHash(); var loaded = TerminalKeybindingConfigurationLoader.LoadDefault();
        var expectedStatus = id == "default-startup-missing-settings" ? "missing" : id == "default-startup-malformed-settings" ? "unreadable-invalid-or-bounded" : "loaded";
        var loadingPassed = loaded.LoadStatus == expectedStatus && scope.SamePath(loaded.AgentDirectory, scope.AgentDirectory) &&
            scope.SamePath(loaded.ConfigPath, scope.SettingsPath) && loaded.Migrated == (remapped || disabled);
        evidence.Observe("default-startup-owned-settings", new { id, scope.AgentDirectory, scope.SettingsPath, settingsBefore,
            loaded.LoadStatus, loaded.Platform, loaded.Migrated, loadingPassed, realUserSettingsOpened = false });
        if (!loadingPassed) throw new InvalidDataException("Owned LoadDefault startup preflight did not match the authored setting vector.");
        var directory = Path.Combine(scope.AgentDirectory, "offline-workflow");
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Fresh offline workflow required.");
        Directory.CreateDirectory(directory); var session = Path.Combine(directory, "session.jsonl"); var script = Path.Combine(directory, "offline.json");
        await NewFile(script, """
            {"schemaVersion":1,"turns":[{"requiredInputTexts":[],"events":[
              {"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg-text","content":[]}},
              {"type":"response.output_text.delta","output_index":0,"item_id":"msg-text","delta":"unused response"},
              {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg-text","content":[{"type":"output_text","text":"unused response"}]}},
              {"type":"response.completed","response":{"status":"completed","output":[],"usage":{"input_tokens":8,"output_tokens":4,"total_tokens":12}}}]}]}
            """);
        using var output = new StringWriter(); using var createError = new StringWriter();
        var created = await SessionCommands.RunAsync(["session", "create", "--session", session, "--workspace", directory, "--offline-api", "openai-responses"], output, createError);
        evidence.Observe("default-startup-offline-session-created", new { created, output = output.ToString(), error = createError.ToString(), session, script });
        if (created != 0 || createError.ToString().Length != 0) throw new InvalidDataException("Offline default-startup session creation failed.");
        var terminal = new TestTerminal(); using var error = new StringWriter(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var history = Gate(); var replaced = Gate(); string? replacementFile = null; long generation = 0;
        var records = new ConcurrentQueue<JsonElement>();
        // This existing entrypoint passes null configuration, so production must invoke LoadDefault itself.
        var running = TerminalSessionCommand.RunObservedAsync(["session", "terminal", "--terminal-preview", "--session", session,
            "--workspace", directory, "--offline-api", "openai-responses", "--offline-script", script], terminal, terminal, error, Observe, stop.Token);
        int exitCode; var exitedBeforeEof = false;
        try
        {
            await history.Task.WaitAsync(Watchdog);
            if (disabled)
            {
                var readProgress = terminal.NextReadAfterPacket(); terminal.Queue("/new\r"); await readProgress.WaitAsync(Watchdog);
                terminal.End(); exitCode = await running.WaitAsync(Watchdog);
            }
            else
            {
                terminal.Queue("/new" + (remapped ? "\u001bj" : "\r")); await replaced.Task.WaitAsync(Watchdog);
                terminal.Queue(remapped ? "\u0005" : "\u0004"); exitCode = await running.WaitAsync(Watchdog); exitedBeforeEof = !terminal.Ended;
            }
        }
        catch (Exception failure)
        {
            evidence.Observe("default-startup-incomplete-before-original-join", new { id, error = ConsumerException.From(failure), records = records.ToArray(),
                replacementFile, generation, terminal = terminal.Snapshot, originalCompleted = running.IsCompleted }); throw;
        }
        finally { stop.Cancel(); terminal.End(); await running; }
        var complete = true; var logs = new List<object>();
        foreach (var file in replacementFile is null ? new[] { session } : new[] { session, replacementFile! })
        {
            var result = await new SessionLogReader().ReadFileAsync(file);
            var valid = result.SourceComplete && result.Status == SessionLogReadStatus.Complete && result.ValidatedPrefixByteLength == result.OriginalBytes.Length;
            complete &= valid; logs.Add(new { file, sha256 = EvidenceAdmission.Hash(file), result.Status, result.SourceComplete,
                result.ValidatedPrefixByteLength, originalBytes = result.OriginalBytes.Length, valid });
        }
        var replacements = records.Count(record => record.GetProperty("type").GetString() == "response" && record.GetProperty("command").GetString() == "new_session");
        var commandPassed = disabled ? replacements == 0 && replacementFile is null : exitedBeforeEof && replacements == 1 && replacementFile is not null && generation > 1;
        var settingsAfter = scope.SettingsHash(); var passed = loadingPassed && commandPassed && exitCode == 0 && error.ToString().Length == 0 &&
            settingsBefore == settingsAfter && complete && terminal.Joined && terminal.Disposals == 0 && running.IsCompleted;
        evidence.Observe("actual-default-startup-observation", new { id, exitCode, error = error.ToString(), settingsBefore, settingsAfter,
            replacements, replacementFile, generation, exitedBeforeEof, records = records.ToArray(), logs, terminal = terminal.Snapshot,
            terminal.Disposals, originalTasksJoined = running.IsCompleted, configurationInjected = false, environmentStillOwnedUntilJoin = true });
        return new { id, passed, actualDefaultStartupInvoked = true, physicalConsoleAcquired = false, retainedEvidenceDirectory = scope.AgentDirectory };

        ValueTask Observe(JsonData record, CancellationToken token)
        {
            var body = record.Value; records.Enqueue(body.Clone());
            if (body.GetProperty("type").GetString() == "session_switched")
            { replacementFile = body.GetProperty("sessionFile").GetString(); generation = body.GetProperty("generation").GetInt64(); }
            if (body.GetProperty("type").GetString() == "response")
            {
                var command = body.GetProperty("command").GetString(); if (command == "get_messages") history.TrySetResult();
                if (command == "new_session")
                {
                    if (!body.GetProperty("success").GetBoolean() || body.GetProperty("data").GetProperty("cancelled").GetBoolean())
                        throw new InvalidDataException("Default startup replacement was rejected or canceled.");
                    replaced.TrySetResult();
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private static async Task NewFile(string path, string content)
    { await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await file.WriteAsync(new UTF8Encoding(false).GetBytes(content)); await file.FlushAsync(); }

    private sealed class RouteHost(bool acknowledged, TerminalKeybindingConfiguration configuration, bool mode)
    {
        internal TestTerminal Sink { get; } = new();
        internal TerminalSubmissionReceipts Receipts { get; } = new();
        internal ConcurrentQueue<string> Submitted { get; } = new();
        internal ConcurrentQueue<TerminalDraftSnapshot> Paints { get; } = new();
        private readonly Channel<TerminalDraftSnapshot> painted = Channel.CreateUnbounded<TerminalDraftSnapshot>();
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        private readonly TerminalEditorFocusOwner focus = new(initiallyFocused: true);
        private TerminalSessionView? view;
        internal Task<TerminalInputExit>? Running;
        internal TerminalDraftSnapshot? Last;
        internal int Cancellations, Interrupts;
        internal bool HoldNextPaint, FocusDetached;
        internal TaskCompletionSource HeldPaint { get; } = Gate();
        internal TaskCompletionSource ReleasePaint { get; } = Gate();
        internal TaskCompletionSource Interrupted { get; } = Gate();
        internal async Task Start()
        {
            var registry = configuration.CreateBindings(mode);
            view = new TerminalSessionView(Sink, Sink, null, focus, registry); await view.StartAsync(stop.Token);
            var reader = new TerminalChatInput(Sink, null, focus, registry, mode);
            Running = acknowledged ? reader.RunAcknowledgedAsync((line, token) =>
            { Submitted.Enqueue(line.Text); Receipts.FinishLocal(line, true); return Task.FromResult(true); },
                token => { Interlocked.Increment(ref Cancellations); return Task.CompletedTask; }, Paint, Receipts,
                (editor, accepted) => { if (accepted.SessionReplaced) editor.Reset(); editor.AddToHistory(accepted.Text); },
                _ => Receipts.Complete(), Interrupt, stop.Token, view.CaptureEditorGeometry) :
                reader.RunAsync((line, token) => { Submitted.Enqueue(line); return Task.FromResult(true); }, Paint, Interrupt, stop.Token, view.CaptureEditorGeometry);
            await WaitPaint("", 0);
        }
        private void Interrupt() { Interlocked.Increment(ref Interrupts); Interrupted.TrySetResult(); }
        private async ValueTask Paint(TerminalDraftSnapshot snapshot, CancellationToken token)
        {
            if (HoldNextPaint) { HoldNextPaint = false; HeldPaint.TrySetResult(); await ReleasePaint.Task.WaitAsync(token); }
            await view!.SetDraftAsync(snapshot, token); Last = snapshot; Paints.Enqueue(snapshot); painted.Writer.TryWrite(snapshot);
        }
        internal async Task Step(string raw, string text, int cursor)
        { while (painted.Reader.TryRead(out _)) { } Sink.Queue(raw); await WaitPaint(text, cursor); }
        internal async Task WaitPaint(string text, int cursor)
        {
            using var timeout = new CancellationTokenSource(Watchdog);
            while (true)
            { var value = await painted.Reader.ReadAsync(timeout.Token); if (value.Text == text && value.CursorUtf16Offset == cursor) return; }
        }
        internal async Task Join()
        {
            ReleasePaint.TrySetResult(); Sink.End();
            try { if (Running is not null) await Running; }
            finally
            {
                Receipts.Complete(); stop.Cancel();
                try { if (view is not null) await view.DisposeAsync(); }
                finally
                {
                    try { await focus.SetEditorFocusAsync(true); }
                    catch (TerminalEditorFocusException failure) when (failure.Failure == TerminalEditorFocusFailure.Detached) { FocusDetached = true; }
                    stop.Dispose();
                }
            }
        }
    }

    private sealed class TestTerminal : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly object readGate = new(); private TaskCompletionSource? afterPacket;
        private int activeReads, activeWrites; private long readsStarted, readsSettled, writesStarted, writesSettled;
        internal int Disposals; internal bool Ended;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, Volatile.Read(ref activeReads), Volatile.Read(ref activeWrites),
            Interlocked.Read(ref readsStarted), Interlocked.Read(ref readsSettled), Interlocked.Read(ref writesStarted), Interlocked.Read(ref writesSettled));
        internal bool Joined { get { var value = Snapshot; return value.ActiveReads == 0 && value.ActiveWrites == 0 && value.ReadWorkersStarted == value.ReadWorkersSettled && value.WriteWorkersStarted == value.WriteWorkersSettled; } }
        internal void Queue(string raw)
        { if (raw.Length is < 1 or > 4096 || !input.Writer.TryWrite(raw)) throw new InvalidOperationException("Authored input packet was not bounded/admitted."); }
        internal Task NextReadAfterPacket() { lock (readGate) { afterPacket ??= Gate(); return afterPacket.Task; } }
        internal void End() { Ended = true; input.Writer.TryComplete(); }
        public TerminalViewport ReadViewport() => new(20, 24, 0, 0, 20, 24);
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            Interlocked.Increment(ref readsStarted); Interlocked.Increment(ref activeReads);
            try
            {
                lock (readGate) { afterPacket?.TrySetResult(); afterPacket = null; }
                if (!await input.Reader.WaitToReadAsync(token)) return 0;
                if (!input.Reader.TryRead(out var packet) || packet.Length > destination.Length) throw new InvalidOperationException("Invalid authored read packet.");
                packet.AsMemory().CopyTo(destination); return packet.Length;
            }
            finally { Interlocked.Decrement(ref activeReads); Interlocked.Increment(ref readsSettled); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            Interlocked.Increment(ref writesStarted); Interlocked.Increment(ref activeWrites);
            try { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
            finally { Interlocked.Decrement(ref activeWrites); Interlocked.Increment(ref writesSettled); }
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
