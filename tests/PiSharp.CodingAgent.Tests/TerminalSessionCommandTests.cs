using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;
using PiSharp.Tui;

internal static class TerminalSessionCommandTests
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliPath)
    {
        Check(Path.IsPathFullyQualified(dotnetHost) && File.Exists(dotnetHost) && Path.IsPathFullyQualified(cliPath) && File.Exists(cliPath), "Compiled terminal workflow paths are unavailable.");
        foreach (var item in WindowsConPtyTerminalSessionFixture.Cases(dotnetHost, Assembly.GetExecutingAssembly().Location, cliPath)) yield return item;
        yield return ("terminal-session.actual-redirected-cli-admission", () => Redirected(dotnetHost, cliPath));
        yield return ("terminal-session.held-display-producer-failure", () => HeldDisplay(dotnetHost, cliPath, false));
        yield return ("terminal-session.held-display-control-c", () => HeldDisplay(dotnetHost, cliPath, true));
        yield return ("terminal-session.controlled-underlying-failure-diagnostic-bounds-and-provenance", FailureDiagnostics);
        yield return ("terminal-session.controlled-shutdown-diagnostic-phases-identity-and-bounds", ShutdownDiagnostics);
        yield return ("terminal-session.controlled-resize-original-stop-and-foreign-failures", ResizeStop);
        foreach (var item in TerminalReceiptCancellationTests.Cases()) yield return item;
    }

    private static async Task ResizeStop()
    {
        using var owner = new CancellationTokenSource(); using var foreign = new CancellationTokenSource();
        owner.Cancel(); foreign.Cancel();
        var original = Task.FromCanceled(owner.Token);
        Check(await TerminalSessionCommand.JoinResizeStopAsync(original, owner.Token) && original.IsCompleted,
            "Owned resize cancellation did not join its original task.");
        Check(!await TerminalSessionCommand.JoinResizeStopAsync(Task.CompletedTask, owner.Token),
            "Unexpected successful resize completion was classified as cancellation.");
        using var active = new CancellationTokenSource();
        foreach (var failure in new Exception[] { new OperationCanceledException(foreign.Token),
            new OperationCanceledException(active.Token), new IOException("authored resize failure") })
        {
            var failedOriginal = Task.FromException(failure); Exception? observed = null;
            try { _ = await TerminalSessionCommand.JoinResizeStopAsync(failedOriginal,
                failure is OperationCanceledException canceled && canceled.CancellationToken == active.Token ? active.Token : owner.Token); }
            catch (Exception error) { observed = error; }
            Check(ReferenceEquals(observed, failure) && failedOriginal.IsCompleted,
                "Resize shutdown lost its original foreign or uncanceled failure.");
        }
    }

    private static async Task ShutdownDiagnostics()
    {
        var marker = new IOException("private diagnostic content must not be exported");
        var settlement = new RpcSessionShutdownSettlement([marker]);
        _ = settlement.AcknowledgeTerminalStopped([marker]);
        var diagnostics = new TerminalShutdownDiagnostics(); diagnostics.Capture(settlement);
        var incomplete = await diagnostics.CaptureAfterJoinAsync("unused", null, marker);
        Check(!incomplete.GetProperty("runtimeCleanupCompleted").GetBoolean() &&
            !incomplete.GetProperty("settlementCompleted").GetBoolean(), "Diagnostic awaited or invented incomplete settlement.");
        settlement.CompleteRuntimeCleanup([marker]);
        settlement.Complete([new AggregateException(Enumerable.Range(0, 40).Select(_ => new IOException("private"))), marker]);
        var receipt = await diagnostics.CaptureAfterJoinAsync("unused", null, marker);
        var phases = receipt.GetProperty("phases").EnumerateArray().ToArray();
        var identity = phases[0].GetProperty("roots")[0].GetInt32();
        Check(phases.Length == 5 && phases.Take(4).All(phase => phase.GetProperty("roots")[0].GetInt32() == identity),
            "Diagnostic replaced original exception identity between phases.");
        Check(receipt.GetProperty("truncated").GetBoolean() && receipt.GetProperty("nodes").GetArrayLength() <= 16 &&
            Utf8.GetByteCount(receipt.GetRawText()) <= 8192 && !receipt.GetRawText().Contains("private", StringComparison.Ordinal),
            "Diagnostic graph exceeded its bounds or exported exception content.");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"diagnostic-tool\",\"isError\":true,\"content\":\"private transcript\"}}\n");
            var matched = await diagnostics.CaptureAfterJoinAsync(path, "diagnostic-tool", null);
            var tool = matched.GetProperty("tool");
            Check(tool.GetProperty("physicalResultPresent").GetBoolean() && tool.GetProperty("physicalResultIsError").GetBoolean() &&
                !matched.GetRawText().Contains("private", StringComparison.Ordinal), "Diagnostic lost matching durable error or exported transcript content.");
            var unmatched = await diagnostics.CaptureAfterJoinAsync(path, "other-tool", null);
            Check(!unmatched.GetProperty("tool").GetProperty("physicalResultPresent").GetBoolean(), "Diagnostic attributed an unrelated tool result.");
        }
        finally { File.Delete(path); }
    }

    private static Task FailureDiagnostics()
    {
        using var caller = new CancellationTokenSource(); using var input = new CancellationTokenSource();
        using var host = new CancellationTokenSource(); using var resize = new CancellationTokenSource(); using var foreign = new CancellationTokenSource();
        input.Cancel(); foreign.Cancel(); var diagnostics = new TerminalCommandFailureDiagnostics();
        foreach (var token in new[] { input.Token, foreign.Token })
        {
            var failure = new OperationCanceledException(token);
            diagnostics.Capture(new(failure, 1, true, caller.Token, input.Token, host.Token, resize.Token));
            var receipt = diagnostics.Receipt!.Value; var facts = receipt.GetProperty("provenance");
            Check(ReferenceEquals(diagnostics.OriginalFailure, failure) && facts.GetProperty("hasCancellation").GetBoolean() &&
                facts.GetProperty("exceptionTokenCanceled").GetBoolean() && !facts.GetProperty("callerCanceled").GetBoolean() &&
                facts.GetProperty("matchesInput").GetBoolean() == (token == input.Token) &&
                !facts.GetProperty("matchesCaller").GetBoolean() && !facts.GetProperty("matchesHost").GetBoolean() &&
                !facts.GetProperty("matchesResize").GetBoolean(), "Diagnostic invented or lost cancellation provenance.");
        }
        var large = new IOException(new string('\0', 10_000));
        diagnostics.Capture(new(large, 1, false, caller.Token, input.Token, host.Token, resize.Token));
        Check(ReferenceEquals(diagnostics.OriginalFailure, large) && diagnostics.Receipt!.Value.GetProperty("truncated").GetBoolean() &&
            Utf8.GetByteCount(diagnostics.Receipt.Value.GetRawText()) <= 8192, "Diagnostic lost exact exception or exceeded escaped UTF-8 bound.");
        return Task.CompletedTask;
    }

    private static async Task Redirected(string host, string cli)
    {
        var files = await WorkflowFiles.Create(host, cli, plugin: false); var before = await File.ReadAllBytesAsync(files.Session);
        var result = await Child(host, cli, files.Root, files.Args());
        Check(result.ExitCode == 1 && result.Output.Length == 0 && !result.Error.Contains('\u001b'), "Redirected terminal preview wrote VT or accepted a non-console route.");
        using var receipt = JsonDocument.Parse(result.Error);
        Check(receipt.RootElement.GetProperty("code").GetString() == (OperatingSystem.IsWindows() ? "NotConsole" : "UnsupportedPlatform"), "Redirected admission failure differs.");
        var after = await File.ReadAllBytesAsync(files.Session);
        Check(before.AsSpan().SequenceEqual(after) && !File.Exists(files.Target), "Terminal rejection mutated durable state or executed a tool.");
        await files.Retain(new { scenario = "redirected-cli", result.ExitCode, result.Output, result.Error, unchangedSession = true });
    }

    private static async Task HeldDisplay(string host, string cli, bool interrupt)
    {
        var files = await WorkflowFiles.Create(host, cli, plugin: false);
        using var errors = new StringWriter(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sink = new HeldConsole(); var diagnostics = new TerminalCommandFailureDiagnostics();
        var shutdownDiagnostics = new TerminalShutdownDiagnostics();
        var running = TerminalSessionCommand.RunObservedAsync(files.Args(), sink, sink, errors,
            (_, _) => ValueTask.CompletedTask, observeFailure: diagnostics.Capture, shutdownObserver: shutdownDiagnostics.Capture);
        try
        {
            await sink.Ready.Task.WaitAsync(deadline.Token); sink.HoldNext = true;
            await sink.Input.Writer.WriteAsync("x", deadline.Token); await sink.Held.Task.WaitAsync(deadline.Token);
            // Distinct Ctrl+C presses share one read so the held paint cannot consume the 500 ms shutdown window.
            await sink.Input.Writer.WriteAsync(interrupt ? "\u0003\u0003" : "\ud800x", deadline.Token);
            await sink.HoldCanceled.Task.WaitAsync(deadline.Token);
            Check(!running.IsCompleted && sink.Snapshot.ActiveWrites == 1, "Input shutdown bypassed the actual held display operation.");
            sink.Release.TrySetResult(); Check(await running == (interrupt ? 0 : 1), "Held-display shutdown outcome differs.");
            var snapshot = sink.Snapshot;
            Check(snapshot.ActiveReads == 0 && snapshot.ActiveWrites == 0 && snapshot.ReadWorkersStarted == snapshot.ReadWorkersSettled &&
                snapshot.WriteWorkersStarted == snapshot.WriteWorkersSettled && !sink.Disposed, "Terminal workflow detached a physical gate or disposed its borrowed lease.");
            Check(interrupt ? errors.ToString().Length == 0 : errors.ToString().Length > 0,
                "Graceful shutdown or terminal fault diagnostic differs.");
            Check(diagnostics.Receipt is not null && (interrupt ? diagnostics.OriginalFailure is null : diagnostics.OriginalFailure is not null),
                "Controlled command diagnostics lost its chosen underlying outcome.");
            await Complete(files.Session);
        }
        finally
        {
            sink.Release.TrySetResult(); sink.Input.Writer.TryComplete();
            try { await running; }
            finally
            {
                var shutdown = await shutdownDiagnostics.CaptureAfterJoinAsync(files.Session, null, diagnostics.OriginalFailure);
                await files.Retain(new { scenario = interrupt ? "held-control-c" : "held-producer-invalid-unicode", virtualSink = true,
                    cancellationObservedBeforeRelease = sink.HoldCanceled.Task.IsCompletedSuccessfully,
                    snapshot = sink.Snapshot, diagnostic = errors.ToString(), underlyingCommandFailure = diagnostics.Receipt,
                    shutdown, borrowedLeaseSurvived = !sink.Disposed });
            }
            Check(sink.Snapshot.ActiveReads == 0 && sink.Snapshot.ActiveWrites == 0, "Held workflow cleanup was not joined.");
        }
    }

    internal sealed class WorkflowFiles
    {
        private WorkflowFiles(string root) { Root = root; Nonce = Guid.NewGuid().ToString("N")[..8]; }
        internal string Root { get; }
        internal string Nonce { get; }
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string Script => Path.Combine(Root, "first-script.json");
        internal string ReopenScript => Path.Combine(Root, "reopen-script.json");
        internal string Target => Path.Combine(Root, "effect.txt");
        internal string Package => Path.Combine(Root, "published");
        internal string Manifest => Path.Combine(Root, "manifest.json");
        internal string Approval => Path.Combine(Root, "approval.json");
        internal string Snapshots => Path.Combine(Root, "snapshots");
        internal string Markers => Path.Combine(Root, "markers");
        internal bool Plugin { get; private set; }
        internal string Prompt => "nonce:" + Nonce;
        internal string Final => "FINAL:" + Nonce + " \u6587\U0001f642\0\u001b[2J\t\n";
        internal string Saved => "saved:" + Nonce + " \u6587\U0001f642\0\n";
        internal string[] MarkerLines => File.Exists(Path.Combine(Markers, "PublishedFixture.CliUi.markers")) ? File.ReadAllLines(Path.Combine(Markers, "PublishedFixture.CliUi.markers")) : [];
        internal string[] ExtensionArgs => Plugin ? ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval,
            "--extension-snapshot-root", Snapshots, "--enable-extension-tool", "fixture.cli.ui"] : [];
        internal string[] Args(bool reopen = false) => new[] { "session", "terminal", "--terminal-preview", "--session", Session, "--workspace", Root,
            "--offline-api", "openai-responses", "--offline-script", reopen ? ReopenScript : Script, "--allow-write", Target }.Concat(ExtensionArgs).ToArray();

        internal static async Task<WorkflowFiles> Create(string host, string cli, bool plugin = true)
        {
            var repository = new DirectoryInfo(Path.GetDirectoryName(cli)!);
            while (!File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent ?? throw new IOException("Compiled repository root is unavailable.");
            var parent = Path.Combine(repository.FullName, "artifacts", "terminal-session-workflow-runs"); Directory.CreateDirectory(parent);
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Check(!Directory.Exists(root), "Workflow evidence root already exists.");
            Directory.CreateDirectory(root); var files = new WorkflowFiles(root) { Plugin = plugin };
            await NewFile(Path.Combine(root, "ownership.json"), JsonSerializer.Serialize(new { schemaVersion = 1, root, files.Nonce, authoring = "runtime-owned fresh evidence" }));
            foreach (var path in new[] { files.Markers, files.Snapshots }) Directory.CreateDirectory(path);
            if (plugin)
            {
                var published = Path.Combine(repository.FullName, "artifacts", "extensions", "published-fixtures", "cli-ui");
                Check(File.Exists(Path.Combine(published, "PublishedFixture.CliUi.dll")), "The existing native UI package must be published by the root gate.");
                Directory.CreateDirectory(files.Package);
                foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(files.Package, Path.GetRelativePath(published, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, overwrite: false); }
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    hashes.Add(Path.GetRelativePath(files.Package, file).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
                var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.cli.ui", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" },
                    runtimeKind = "native", assembly = "PublishedFixture.CliUi.dll", entryType = "PublishedCliUiFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" }, declaredCapabilities = new[] { "tools", "observations" },
                    resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.CliUi.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
                await NewFile(files.Manifest, manifest);
                await NewFile(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package,
                    manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(manifest))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit",
                    policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = new[] { "fixture.cli.ui" } }));
            }
            object[] turns = plugin ? [Tool("fixture.cli.ui", "terminal-dialog", new { action = "confirm" }, [files.Prompt]),
                Tool("write", "terminal-write", new { path = files.Target, content = files.Saved }, ["ui:approved"]), Text(files.Final, ["Successfully wrote"])] : [Text("unused response")];
            await NewFile(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }));
            await NewFile(files.ReopenScript, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { Text("REOPEN:" + files.Nonce, ["FINAL:" + files.Nonce, "resume:" + files.Nonce]) } }));
            var create = new[] { "session", "create", "--session", files.Session, "--workspace", files.Root, "--offline-api", "openai-responses" }.Concat(files.ExtensionArgs).ToArray();
            var receipt = await Child(host, cli, files.Root, create);
            Check(receipt.ExitCode == 0 && receipt.Error.Length == 0, "Compiled CLI did not create the durable workflow session.");
            using var created = JsonDocument.Parse(receipt.Output); Check(created.RootElement.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Initial durable checkpoint was not acknowledged.");
            await files.Retain(new { scenario = "create", receipt.ExitCode, receipt.Output, receipt.Error }); return files;
        }
        internal Task Retain(object receipt) => NewFile(Path.Combine(Root, "receipt-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(receipt));
    }

    internal static async Task<SessionLogReadResult> Complete(string path)
    {
        var log = await new SessionLogReader().ReadFileAsync(path);
        Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length && new FileInfo(path).Length == log.OriginalBytes.Length,
            "Terminal command returned without a complete physical durable log.");
        await using var exclusive = await SessionLogStore.OpenAsync(path); Check(exclusive.Snapshot.CommittedByteLength == log.OriginalBytes.Length, "Durable file acknowledgement differs."); return log;
    }
    internal static SessionContextProjection Context(SessionLogReadResult log) => new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
    internal static string TextOf(JsonElement value) => value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
        ? string.Join('\n', content.EnumerateArray().Select(block => block.TryGetProperty("text", out var text) ? text.GetString() : "")) : "";
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object Tool(string name, string call, object arguments, string[] required) => new { requiredInputTexts = required, events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = JsonSerializer.Serialize(arguments) } }, Completed()
    } };
    private static object Text(string text, string[]? required = null) => new { requiredInputTexts = required ?? [], events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg-text", content = Array.Empty<object>() } },
        new { type = "response.output_text.delta", output_index = 0, item_id = "msg-text", delta = text },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg-text", content = new[] { new { type = "output_text", text } } } }, Completed()
    } };
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static async Task NewFile(string path, string text)
    { await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await file.WriteAsync(Utf8.GetBytes(text)); await file.FlushAsync(); }
    internal sealed record ChildResult(int ExitCode, string Output, string Error);
    private static async Task<ChildResult> Child(string host, string cli, string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.Environment["TEMP"] = directory; start.Environment["TMP"] = directory; start.Environment["PISHARP_NATIVE_FIXTURE_MARKERS"] = Path.Combine(directory, "markers");
        start.ArgumentList.Add(cli); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Compiled workflow child did not start.");
        _ = child.SafeHandle; child.StandardInput.Close(); var output = Drain(child.StandardOutput); var errors = Drain(child.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); Exception? failure = null;
        try { await child.WaitForExitAsync(deadline.Token); } catch (Exception error) { failure = error; }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (Exception error) { if (!child.HasExited) failure ??= error; }
            try { await child.WaitForExitAsync(CancellationToken.None); } catch (Exception error) { failure ??= error; }
            try { await Task.WhenAll(output, errors); } catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return new(child.ExitCode, await output, await errors);
    }
    private static async Task<string> Drain(TextReader reader)
    {
        var retained = new StringBuilder(); var data = new char[4096]; var overflow = false;
        while (true)
        {
            var count = await reader.ReadAsync(data.AsMemory(), CancellationToken.None); if (count == 0) break;
            var available = 262_144 - retained.Length; if (count > available) overflow = true;
            retained.Append(data, 0, Math.Min(count, available));
        }
        Check(!overflow, "Actual CLI output exceeded its bounded retention."); return retained.ToString();
    }

    private sealed class HeldConsole : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly object gate = new(); private int reads, writes; private long readStarted, readSettled, writeStarted, writeSettled;
        internal readonly Channel<string> Input = Channel.CreateBounded<string>(8);
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously), Held = new(TaskCreationOptions.RunContinuationsAsynchronously),
            HoldCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldNext, Disposed;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, reads, writes, readStarted, readSettled, writeStarted, writeSettled); } }
        public TerminalViewport ReadViewport() => new(96, 12, 0, 0, 96, 12);
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0, "Concurrent virtual physical reads."); reads++; readStarted++; }
            try
            {
                if (!await Input.Reader.WaitToReadAsync(token)) return 0;
                Check(Input.Reader.TryRead(out var text) && text.Length <= destination.Length, "Virtual read fixture exceeded the admitted chunk.");
                var value = text ?? throw new IOException("Virtual read fixture returned no data.");
                value.AsMemory().CopyTo(destination); return value.Length;
            }
            finally { lock (gate) { reads--; readSettled++; } }
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            lock (gate) { Check(writes == 0, "Concurrent virtual physical writes."); writes++; writeStarted++; }
            try
            {
                if (HoldNext)
                {
                    HoldNext = false; Held.TrySetResult(); using var cancellation = token.UnsafeRegister(_ => HoldCanceled.TrySetResult(), null);
                    await Release.Task; token.ThrowIfCancellationRequested();
                }
                if (frame.Span.Contains("[history]", StringComparison.Ordinal)) Ready.TrySetResult();
            }
            finally { lock (gate) { writes--; writeSettled++; } }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
