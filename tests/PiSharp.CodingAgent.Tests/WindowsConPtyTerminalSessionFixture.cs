using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Rpc.Ui;
using PiSharp.Tui;
using static TerminalSessionCommandTests;

internal static class WindowsConPtyTerminalSessionFixture
{
    public const string WorkerSwitch = "--windows-terminal-session-worker";
    private const int InitialColumns = 96, InitialRows = 12, MaximumReceiptCharacters = 32_768, MaximumOutputBytes = 65_536;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly List<string> receipts = [];
    private static readonly List<object> witnesses = [];
    public static string[] WorkerReceipts => receipts.ToArray();
    public static object[] ParentWitnesses => witnesses.ToArray();
    public static string PlatformEvidence { get; private set; } = "not-exercised";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string harness, string cli)
    {
        foreach (var scenario in new[] { "workflow", "standard-handles", "control-c", "conpty-input-close", "native-write-fault", "shrink-exit" })
        {
            var selected = scenario;
            yield return ("terminal-session.actual-" + selected, () => Run(host, harness, cli, selected));
        }
    }

    private static async Task Run(string host, string harness, string cli, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { PlatformEvidence = "unsupported-non-windows"; return; }
        var files = await WorkflowFiles.Create(host, cli); var initial = await File.ReadAllBytesAsync(files.Session);
        var first = await Execute(host, harness, files, scenario, reopen: false);
        var log = await Complete(files.Session); Check(log.OriginalBytes.AsSpan().StartsWith(initial), "Terminal workflow rewrote the session prefix.");
        var context = Context(log); var toolResults = context.LlmMessages.Where(entry => entry.WireBody.Value.GetProperty("role").GetString() == "toolResult").ToArray();
        if (scenario is "workflow" or "standard-handles")
        {
            Check(first.GetProperty("commandExit").GetInt32() == 0 && first.GetProperty("restorationConfirmed").GetBoolean(), "Whole terminal workflow did not exit and restore its actual console.");
            Check((await File.ReadAllBytesAsync(files.Target)).SequenceEqual(Utf8.GetBytes(files.Saved)), "Actual native write tool lost canonical Unicode/control data.");
            var assistant = context.LlmMessages.Last(entry => entry.WireBody.Value.GetProperty("role").GetString() == "assistant").WireBody.Value;
            Check(TextOf(assistant) == files.Final, "Durable final content was reconstructed from presentation deltas.");
            var ui = toolResults.First(entry => entry.WireBody.Value.GetProperty("toolName").GetString() == "fixture.cli.ui").WireBody.Value;
            Check(ui.GetProperty("details").GetProperty("approved").GetBoolean() && ui.GetProperty("details").GetProperty("mode").GetString() == "Rpc", "Real native dialog result or capability scope differs.");
            Check(toolResults.Any(entry => TextOf(entry.WireBody.Value).Contains("Successfully wrote", StringComparison.Ordinal)), "Actual durable native tool acknowledgement is missing.");
            Check(files.MarkerLines.Count(value => value == "tool:confirm") == 1 && files.MarkerLines.Count(value => value == "tool-closed") == 1 &&
                Directory.GetDirectories(files.Snapshots).Length == 0, "Published callback or package ownership was not joined.");
            var before = log.OriginalBytes.ToArray();
            var reopened = await Execute(host, harness, files, scenario, reopen: true);
            Check(reopened.GetProperty("commandExit").GetInt32() == 0 && reopened.GetProperty("restorationConfirmed").GetBoolean(), "Separate terminal reopen failed.");
            var after = await Complete(files.Session); Check(after.OriginalBytes.AsSpan().StartsWith(before), "Physical reopen changed previously acknowledged bytes.");
            Check(TextOf(Context(after).LlmMessages.Last(entry => entry.WireBody.Value.GetProperty("role").GetString() == "assistant").WireBody.Value) == "REOPEN:" + files.Nonce,
                "Separate process failed to resume the durable context.");
            Check(files.MarkerLines.Count(value => value == "tool:confirm") == 1 && (await File.ReadAllBytesAsync(files.Target)).SequenceEqual(Utf8.GetBytes(files.Saved)), "Reopen replayed the native tool or dialog.");
        }
        else if (scenario == "shrink-exit")
        {
            Check(first.GetProperty("commandExit").GetInt32() == 0 && first.GetProperty("mainGeometry").GetProperty("Columns").GetInt32() == 20 &&
                first.GetProperty("mainGeometry").GetProperty("Rows").GetInt32() == 4, "Shrink exit secretly restored the prior physical dimensions.");
            // A strict lease cannot restore an original coordinate outside the independently observed current buffer.
            var impossible = first.GetProperty("originalCursorOutsideCurrentBuffer").GetBoolean();
            Check(impossible ? first.GetProperty("restorationFailure").GetString() == "RestorationFailed" && !first.GetProperty("restorationConfirmed").GetBoolean()
                : first.GetProperty("restorationConfirmed").GetBoolean(), "Shrink exit did not report the actual strict restoration outcome.");
        }
        else
        {
            Check(first.GetProperty("commandExit").GetInt32() == (scenario == "control-c" ? 0 : 1), "Native shutdown outcome differs.");
            Check(first.GetProperty("restorationConfirmed").GetBoolean() && !File.Exists(files.Target) && !files.MarkerLines.Contains("approved"), "Shutdown granted approval, executed the later tool, or falsely restored the console.");
            Check(toolResults.Any(entry => entry.WireBody.Value.GetProperty("isError").GetBoolean()) && files.MarkerLines.Count(value => value == "tool-closed") == 1,
                "Shutdown did not join the actual dialog callback and preserve its durable error.");
            if (scenario == "native-write-fault") Check(first.GetProperty("nativeFaultObserved").GetBoolean(), "No actual native write failure was observed.");
            if (scenario == "conpty-input-close")
            {
                var close = first.GetProperty("consoleClose");
                Check(close.GetProperty("ObservedControlType").GetUInt32() == 2 && close.GetProperty("CancellationWorkJoined").GetBoolean() &&
                    !close.GetProperty("CleanupTimedOut").GetBoolean() && close.GetProperty("CallbackFailureType").ValueKind == JsonValueKind.Null &&
                    !close.GetProperty("CleanupSignaled").GetBoolean() && close.GetProperty("ActiveCallbacks").GetInt32() >= 1 &&
                    close.GetProperty("CallbacksStarted").GetInt64() - close.GetProperty("CallbacksSettled").GetInt64() == close.GetProperty("ActiveCallbacks").GetInt32() &&
                    first.GetProperty("closeDurableAcknowledged").GetBoolean() && first.GetProperty("closeNativeToolError").GetBoolean() &&
                    first.GetProperty("closeNoApprovalOrEffect").GetBoolean(), "Actual ConPTY close cleanup facts differ or were signaled before the final receipt.");
                Check(new FileInfo(Path.Combine(files.Root, "conpty-input-close-cleanup.json")).Length <= MaximumReceiptCharacters,
                    "The retained physical cleanup receipt exceeds its hard bound.");
                var retained = await File.ReadAllTextAsync(Path.Combine(files.Root, "conpty-input-close-cleanup.json"), Utf8);
                using var receipt = JsonDocument.Parse(retained);
                Check(receipt.RootElement.GetProperty("nonce").GetString() == files.Nonce && receipt.RootElement.GetProperty("restorationConfirmed").GetBoolean() &&
                    receipt.RootElement.GetProperty("consoleClose").GetProperty("ObservedControlType").GetUInt32() == 2,
                    "The physically flushed close cleanup receipt differs.");
            }
        }
        await files.Retain(new { scenario, physicalDurableBytes = new FileInfo(files.Session).Length, borrowedLeaseRestored = first.GetProperty("restorationConfirmed").GetBoolean(),
            nativeReadEofQualified = false, conptyControlCloseQualified = scenario == "conpty-input-close",
            shrinkRestorationQualified = scenario == "shrink-exit" && first.GetProperty("restorationConfirmed").GetBoolean() });
        PlatformEvidence = "actual-hidden-windows-terminal-session-workflow-source-default-editor-escaped-transcript";
    }

    private static async Task<JsonElement> Execute(string host, string harness, WorkflowFiles files, string scenario, bool reopen)
    {
        var pipe = "pisharp-terminal-session-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(Deadline);
        await using var control = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var classic = scenario == "standard-handles";
        var console = classic ? OwnedConsole.StartStandard(host, harness, pipe, scenario, files.Nonce) : OwnedConsole.Start(host, harness, pipe, scenario, files.Nonce);
        var packets = new List<string>(); JsonElement done = default, finalReceipt = default; Exception? failure = null; Exception? writerCleanupFailure = null;
        StreamReader? reader = null; StreamWriter? writer = null; var protocolComplete = false; var nativeInputClosed = false;
        try
        {
            await control.WaitForConnectionAsync(deadline.Token);
            reader = new StreamReader(control, Utf8, false, 1024, leaveOpen: true);
            writer = new StreamWriter(control, Utf8, 1024, leaveOpen: true) { AutoFlush = true };
            var ready = await Receive(reader);
            Check(ready.GetProperty("stage").GetString() == "ready" && ready.GetProperty("nonce").GetString() == files.Nonce, "Actual terminal worker readiness differs.");
            if (classic) Check(ready.GetProperty("aliases").GetProperty("InputType").GetUInt32() == 2 && ready.GetProperty("aliases").GetProperty("OutputType").GetUInt32() == 2,
                "Genuine standard handles were not console character devices.");
            await Send(writer, new { stage = "start", args = files.Args(reopen), root = files.Root }, deadline.Token);
            await Screen(reopen ? "FINAL:" + files.Nonce : "[history]");
            if (reopen)
            {
                Check(packets.Any(value => NativeScreen(value).Contains("FINAL:" + files.Nonce, StringComparison.Ordinal)), "Actual reopened screen omitted the durable prior assistant.");
                await Input("resume:" + files.Nonce); await Screen("resume:" + files.Nonce, blankDraft: false);
                await Input("\r"); await Screen("REOPEN:" + files.Nonce); await Screen("[settled]"); await Input("/quit\r");
            }
            else if (scenario == "shrink-exit")
            {
                await Input(files.Prompt); await Screen(files.Prompt, blankDraft: false);
                await Input("\u0003"); await Screen("[draft discarded]");
                console.Resize(20, 4); await Geometry(20, 4); await Input("/quit\r");
            }
            else
            {
                if (scenario == "workflow") { console.Resize(72, 10); await Geometry(72, 10); }
                await Input(files.Prompt); await Screen(files.Prompt, blankDraft: false);
                await Input("\r"); await Screen("[ui confirm] first");
                if (scenario == "workflow")
                {
                    await Input("/state\r"); await Screen("[state] running pending=0"); console.Resize(64, 9); await Geometry(64, 9);
                }
                if (scenario is "workflow" or "standard-handles")
                {
                    await Input("maybe\r"); await Screen("[ui] Enter yes/no"); await Input("yes\r");
                    await Screen("FINAL:" + files.Nonce); await Screen("[settled]");
                    Check(packets.Any(value => NativeScreen(value).Contains("\\u6587", StringComparison.Ordinal) && NativeScreen(value).Contains("\\U0001f642", StringComparison.Ordinal)),
                        "Actual console cells omitted the explicit Unicode escape projection.");
                    await Input("/quit\r");
                }
                else if (scenario == "control-c")
                {
                    // Two distinct presses in one bounded input burst reach the producer within its 500 ms gesture window.
                    await Input("\u0003\u0003");
                }
                else if (scenario == "conpty-input-close") { console.CloseInput(); nativeInputClosed = true; }
                else
                {
                    await Send(writer, new { stage = "arm-fault" }, deadline.Token); await Stage("fault-armed");
                    await Input("/state\r"); await Stage("native-write-failure");
                }
            }
            done = await Stage("done"); Check(done.GetProperty("failure").ValueKind == JsonValueKind.Null, "Actual worker reported a workflow failure: " + done.GetRawText());
            var exitCode = await console.WaitForExit();
            Check(scenario == "conpty-input-close" ? (exitCode is 0 or 0xc000013a) && done.GetProperty("consoleClose").GetProperty("ObservedControlType").GetUInt32() == 2
                : exitCode == 0, "Actual terminal worker process failed or lacked its native close proof.");
            Check(packets.Any(value => IsDraftWitness(value, reopen ? "resume:" + files.Nonce : files.Prompt)),
                "The actual native draft cells/cursor did not witness this process's nonce input and output.");
            protocolComplete = true;

            async Task Input(string text)
            {
                if (classic) await Send(writer, new { stage = "input", text }, deadline.Token);
                else { await console.Input.WriteAsync(Utf8.GetBytes(text), deadline.Token); await console.Input.FlushAsync(deadline.Token); }
            }
            async Task<JsonElement> Receive(StreamReader source)
            {
                Check(packets.Count < 256, "Terminal worker exceeded bounded protocol packets."); var raw = await ReadLine(source, deadline.Token);
                Check(receipts.Count < 4096, "Terminal suite exceeded bounded aggregate receipt retention.");
                packets.Add(raw); receipts.Add(scenario + (reopen ? "/reopen:" : ":") + raw); using var json = JsonDocument.Parse(raw); var packet = json.RootElement.Clone();
                var stage = packet.GetProperty("stage").GetString();
                if (stage is "done" or "failed") finalReceipt = packet;
                Check(stage is not "failed" and not "native-witness-failure", "Native worker failure: " + raw); return packet;
            }
            async Task<JsonElement> Stage(string stage)
            {
                while (true)
                {
                    var packet = await Receive(reader); var actual = packet.GetProperty("stage").GetString();
                    if (actual == stage) return packet;
                    Check(actual != "done", "Native worker ended before " + stage + ": " + packet.GetRawText());
                }
            }
            async Task Screen(string text, bool blankDraft = true)
            {
                // Already observed native packets count; this does not substitute a synthetic screen.
                if (packets.Any(Matches)) return;
                while (true)
                {
                    var packet = await Receive(reader);
                    Check(packet.GetProperty("stage").GetString() != "done", "Native worker ended before screen " + text + ": " + packet.GetRawText());
                    if (Matches(packet.GetRawText())) return;
                }
                bool Matches(string raw)
                {
                    using var json = JsonDocument.Parse(raw); var packet = json.RootElement;
                    if (packet.GetProperty("stage").GetString() != "screen" || !NativeScreen(raw).Contains(text, StringComparison.Ordinal)) return false;
                    var columns = packet.GetProperty("geometry").GetProperty("Columns").GetInt32();
                    if (!blankDraft) return IsDraftWitness(raw, text);
                    return TerminalSourceAsciiExpectations.PacketMatches(packet, "");
                }
            }
            async Task Geometry(int columns, int rows)
            {
                while (true)
                {
                    var packet = await Receive(reader);
                    Check(packet.GetProperty("stage").GetString() != "done", "Native worker ended before resize observation: " + packet.GetRawText());
                    if (packet.GetProperty("stage").GetString() != "screen") continue;
                    var observed = packet.GetProperty("geometry");
                    if (observed.GetProperty("Columns").GetInt32() != columns || observed.GetProperty("Rows").GetInt32() != rows) continue;
                    var screen = packet.GetProperty("screen").EnumerateArray().Select(row => row.GetString()!).ToArray();
                    Check(screen.Length == rows && TerminalSourceAsciiExpectations.PacketMatches(packet, ""),
                        "Resize did not produce the exact actual idle draft row/cursor."); return;
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Every control write has already awaited AutoFlush. Preserve the primary
            // worker receipt when disposing after the peer has physically closed its pipe.
            try { writer?.Dispose(); }
            catch (Exception error) { writerCleanupFailure = error; if (!protocolComplete) failure ??= error; }
            try { reader?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { await console.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            if (nativeInputClosed && finalReceipt.ValueKind == JsonValueKind.Undefined && console.PhysicalExitCode is uint exit)
                failure = new IOException("Physical ConPTY input close ended the worker with native exit 0x" + exit.ToString("X8") +
                    "; final command and restoration receipts are absent. Console close cleanup failed; native read EOF remains unqualified.", failure);
            var evidence = console.Evidence(scenario, files.Nonce, false); witnesses.Add(evidence);
            await files.Retain(new { scenario, reopen, classicStandardConsole = classic, packets, physical = evidence,
                nativeInputClosed, finalWorkerReceiptMissing = finalReceipt.ValueKind == JsonValueKind.Undefined,
                actualNativeNonceOutputObserved = packets.Any(value => IsDraftWitness(value, reopen ? "resume:" + files.Nonce : files.Prompt)),
                writerCleanupFailureType = writerCleanupFailure?.GetType().Name, writerCleanupFailureMessage = writerCleanupFailure?.Message,
                failureType = failure?.GetType().Name, failureMessage = failure?.Message });
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Check(console.ExitConfirmed && console.CloseJoined && console.DrainJoined && !console.Truncated, "Actual terminal child/close/drain cleanup is incomplete.");
        if (!classic) Check(console.OutputBytes.Length > 0, "Actual ConPTY output was empty."); return done;
    }

    private static string NativeScreen(string raw)
    {
        using var json = JsonDocument.Parse(raw); return json.RootElement.TryGetProperty("screen", out var rows)
            ? string.Join('\n', rows.EnumerateArray().Select(row => row.GetString())) : "";
    }

    /// <summary>Opt-in original-source lane; shares the existing real process/ConPTY owner.</summary>
    internal static async Task ExecuteCommandInputAsync(string host, string harness, string root, string invocation,
        string nonce, string[] args, Func<CommandInputConsole, Task> exercise)
    {
        Check(OperatingSystem.IsWindows(), "Original command terminal workflow requires Windows.");
        Check(Path.IsPathFullyQualified(root) && Directory.Exists(root) && Path.IsPathFullyQualified(invocation) &&
            Directory.Exists(invocation) && Path.GetRelativePath(root, invocation).Split(Path.DirectorySeparatorChar)[0] != "..",
            "Explicit owned terminal invocation is outside its workspace.");
        var pipe = "pisharp-command-terminal-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var control = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var console = OwnedConsole.Start(host, harness, pipe, "command-input", nonce);
        var packets = new List<string>(); var records = new List<JsonElement>();
        StreamReader? reader = null; StreamWriter? writer = null; Exception? failure = null, writerFailure = null;
        JsonElement final = default; var complete = false; var retainedProtocolBytes = 0;
        try
        {
            await control.WaitForConnectionAsync(deadline.Token);
            reader = new StreamReader(control, Utf8, false, 1024, leaveOpen: true);
            writer = new StreamWriter(control, Utf8, 1024, leaveOpen: true) { AutoFlush = true };
            var ready = await Receive();
            Check(ready.GetProperty("stage").GetString() == "ready" && ready.GetProperty("nonce").GetString() == nonce,
                "Original command terminal worker readiness differs.");
            await Send(writer, new { stage = "start", args, root, commandInputOptions = Path.Combine(invocation, "options.json") }, deadline.Token);
            var interaction = new CommandInputConsole(console.Input, deadline.Token, packets, records, Receive);
            await interaction.RecordAsync(record => record.GetProperty("type").GetString() == "response" &&
                record.TryGetProperty("id", out var id) && id.GetString() == "chat-history", 0);
            // This nonce is actual unpredictable physical input, canceled before source admission.
            await interaction.DraftAsync("nonce:" + nonce); await interaction.EscapeAsync();
            await exercise(interaction);
            await interaction.LineAsync("/quit");
            while (final.ValueKind == JsonValueKind.Undefined) await Receive();
            Check(final.GetProperty("stage").GetString() == "done" && final.GetProperty("failure").ValueKind == JsonValueKind.Null,
                "Original command terminal worker failed: " + final.GetRawText());
            Check(final.GetProperty("commandExit").GetInt32() == 0 && final.GetProperty("restorationConfirmed").GetBoolean() &&
                final.GetProperty("diagnosticText").GetString() == "", "Original command terminal did not exit cleanly and restore its real lease.");
            Check(await console.WaitForExit() == 0, "Original command terminal contained process failed.");
            Check(packets.Any(packet => IsDraftWitness(packet, "nonce:" + nonce)), "Original command terminal lacks real nonce cells/cursor.");
            complete = true;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { writer?.Dispose(); } catch (Exception error) { writerFailure = error; if (!complete) failure ??= error; }
            try { reader?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { await console.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            var physical = console.Evidence("command-input", nonce, false); witnesses.Add(physical);
            try
            {
                // Preserve complete actual packets separately, without serializing raw JSON inside JSON again.
                var protocol = Utf8.GetBytes(string.Join('\n', packets) + "\n");
                Check(protocol.Length <= 2_097_152, "Original command terminal protocol sidecar exceeds its bound.");
                await Persist("terminal.protocol.jsonl", protocol);
                var raw = Utf8.GetBytes(JsonSerializer.Serialize(new { schemaVersion = 1, scenario = "command-input", nonce,
                    borrowedTerminalWrapper = true, productionCliBootstrapQualified = false,
                    packetSidecar = "terminal.protocol.jsonl", packetBytes = protocol.Length, packetCount = packets.Count, recordCount = records.Count,
                    packetSha256 = Convert.ToHexStringLower(SHA256.HashData(protocol)),
                    finalWorkerReceipt = final.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : final,
                    protocolComplete = complete, physical, actualNativeNonceOutputObserved = packets.Any(packet => IsDraftWitness(packet, "nonce:" + nonce)),
                    writerCleanupFailure = writerFailure?.ToString(), failure = failure?.ToString() }) + "\n");
                Check(raw.Length <= 2_097_152, "Original command terminal retained evidence exceeds its bound.");
                await Persist("terminal.receipt.json", raw);
            }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Check(console.ExitConfirmed && console.CloseJoined && console.DrainJoined && !console.Truncated && console.OutputBytes.Length > 0,
            "Original command terminal physical process/close/drain did not join within retained bounds.");
        PlatformEvidence = "actual-hidden-windows-terminal-session-workflow-source-default-editor-escaped-transcript";

        async Task<JsonElement> Receive()
        {
            Check(packets.Count < 256 && receipts.Count < 4096, "Original command terminal exceeded bounded receipt retention.");
            var raw = await ReadLine(reader!, deadline.Token); packets.Add(raw); receipts.Add("command-input:" + raw);
            retainedProtocolBytes += Utf8.GetByteCount(raw) + 1;
            Check(retainedProtocolBytes <= 1_572_864, "Original command terminal aggregate protocol budget exceeded; last complete packet retained.");
            using var document = JsonDocument.Parse(raw); var packet = document.RootElement.Clone(); var stage = packet.GetProperty("stage").GetString();
            if (stage == "rpc-record") records.Add(packet.GetProperty("record").Clone());
            if (stage is "done" or "failed") final = packet;
            Check(stage is not "failed" and not "native-witness-failure", "Original command terminal native failure: " + raw);
            return packet;
        }
        async Task Persist(string name, byte[] raw)
        {
            await using var retained = new FileStream(Path.Combine(invocation, name), FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 8192, FileOptions.Asynchronous);
            await retained.WriteAsync(raw); await retained.FlushAsync(); retained.Flush(flushToDisk: true);
        }
    }

    internal sealed class CommandInputConsole(Stream input, CancellationToken token, List<string> packets,
        List<JsonElement> records, Func<Task<JsonElement>> receive)
    {
        private long lines;
        internal JsonElement[] Records => records.ToArray();
        internal int RecordCount => records.Count;
        internal async Task DraftAsync(string text)
        {
            var from = packets.Count; await Input(text);
            await ScreenAsync(text, from, draft: true);
        }
        internal async Task<string> LineAsync(string text)
        {
            await DraftAsync(text); var id = "chat-" + (++lines).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Input("\r"); return id;
        }
        internal async Task EscapeAsync()
        { var from = packets.Count; ++lines; await Input("\u001b"); await ScreenAsync("[draft discarded]", from); }
        internal async Task CancelDialogAsync() { ++lines; await Input("\u001b"); }
        internal Task PublishedAsync(string id) => PresentationAsync("ui-published", id, null);
        internal Task RetiredAsync(string id, string outcome) => PresentationAsync("ui-retired", id, outcome);
        private async Task PresentationAsync(string stage, string id, string? outcome)
        {
            while (true)
            {
                var found = packets.Select(raw => { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); })
                    .FirstOrDefault(packet => packet.GetProperty("stage").GetString() == stage &&
                        packet.GetProperty("identity").GetProperty("RequestId").GetString() == id);
                if (found.ValueKind != JsonValueKind.Undefined)
                {
                    if (outcome is not null) Check(found.GetProperty("outcome").GetString() == outcome, "Actual terminal UI retirement outcome differs.");
                    Check(found.GetProperty("identity").GetProperty("OwnerId").GetString() == "fixture.node.command-input" &&
                        found.GetProperty("identity").GetProperty("OwnerGeneration").GetInt64() == 1 &&
                        found.GetProperty("identity").GetProperty("ConnectionGeneration").GetInt64() > 0 &&
                        found.GetProperty("identity").GetProperty("SessionGeneration").GetInt64() > 0, "Actual terminal UI identity escaped its approved owner.");
                    if (outcome is not null) Check(found.GetProperty("PublicationSucceeded").GetBoolean() && found.GetProperty("PresentationEntered").GetBoolean(),
                        "Actual terminal UI retirement did not retain its joined publication/presentation facts.");
                    return;
                }
                var packet = await receive(); Check(packet.GetProperty("stage").GetString() != "done", "Terminal ended before actual UI " + stage + " barrier.");
            }
        }
        internal Task<JsonElement> ResponseAsync(string id) => RecordAsync(record => record.GetProperty("type").GetString() == "response" &&
            record.TryGetProperty("id", out var actual) && actual.GetString() == id, 0);
        internal Task<JsonElement> UiAsync(string method, int from) => RecordAsync(record => record.GetProperty("type").GetString() == "extension_ui_request" &&
            record.GetProperty("method").GetString() == method, from);
        internal async Task<JsonElement> RecordAsync(Func<JsonElement, bool> predicate, int from)
        {
            while (true)
            {
                var found = records.Skip(from).FirstOrDefault(predicate); if (found.ValueKind != JsonValueKind.Undefined) return found;
                var packet = await receive(); Check(packet.GetProperty("stage").GetString() != "done", "Original terminal ended before actual RPC observer barrier.");
            }
        }
        internal async Task ScreenAsync(string text, int from = 0, bool draft = false)
        {
            while (true)
            {
                if (packets.Skip(from).Any(Matches)) return;
                var packet = await receive(); Check(packet.GetProperty("stage").GetString() != "done", "Original terminal ended before actual screen " + text + ".");
            }
            bool Matches(string raw)
            {
                using var document = JsonDocument.Parse(raw); var packet = document.RootElement;
                if (packet.GetProperty("stage").GetString() != "screen" || !NativeScreen(raw).Contains(text, StringComparison.Ordinal)) return false;
                if (draft) return IsDraftWitness(raw, text);
                var columns = packet.GetProperty("geometry").GetProperty("Columns").GetInt32();
                return TerminalSourceAsciiExpectations.PacketMatches(packet, "");
            }
        }
        private async Task Input(string text)
        { await input.WriteAsync(Utf8.GetBytes(text), token); await input.FlushAsync(token); }
    }

    private sealed class CommandInputPresentation(Func<object, CancellationToken, Task> packet) : IRpcExtensionUiPresentationObserver
    {
        public ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken token) =>
            new(packet(new { stage = "ui-published", identity = presentation.Identity, request = presentation.Request.Value }, token));
        public ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken token) =>
            new(packet(new { stage = "ui-retired", identity = retirement.Identity, outcome = retirement.Outcome.ToString(),
                retirement.PublicationSucceeded, retirement.PresentationEntered, retirement.UnavailableReason }, token));
    }

    private static bool IsDraftWitness(string raw, string text)
    {
        using var json = JsonDocument.Parse(raw); var packet = json.RootElement;
        if (packet.GetProperty("stage").GetString() != "screen") return false;
        var geometry = packet.GetProperty("geometry"); var columns = geometry.GetProperty("Columns").GetInt32();
        var rows = geometry.GetProperty("Rows").GetInt32(); var screen = packet.GetProperty("screen"); var cursor = packet.GetProperty("cursor");
        return TerminalSourceAsciiExpectations.PacketMatches(packet, text);
    }

    public static async Task<int?> TryRunWorkerAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerSwitch) return null;
        if (args.Length != 4 || !OperatingSystem.IsWindows() || args[3].Length != 8 || !args[3].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f')) return 2;
        var scenario = args[2]; var nonce = args[3]; using var deadline = new CancellationTokenSource(scenario == "command-input" ? TimeSpan.FromSeconds(60) : Deadline);
        await using var control = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous); await control.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(control, Utf8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(control, Utf8, 1024, leaveOpen: true) { AutoFlush = true }; using var sending = new SemaphoreSlim(1, 1);
        SafeFileHandle? input = null, output = null; WindowsConsoleTerminal? lease = null; TerminalConsoleState? original = null; Aliases? aliases = null;
        Exception? failure = null; string? restorationFailure = null; TerminalLeaseSnapshot? finalSnapshot = null; Geometry? mainGeometry = null;
        WindowsConsoleCloseScope? closeScope = null; string? evidenceRoot = null;
        var commandExit = -1; var outside = false; var nativeFaultObserved = false; var diagnosticText = "";
        var commandFailure = new TerminalCommandFailureDiagnostics();
        var shutdownDiagnostics = new TerminalShutdownDiagnostics();
        JsonElement? shutdownDiagnosticsReceipt = null;
        try
        {
            var classic = scenario == "standard-handles";
            input = classic ? new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-10)), false) : OpenConsole("CONIN$");
            output = classic ? new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-11)), false) : OpenConsole("CONOUT$");
            aliases = ReadAliases(); Check(Native.GetFileType(input) == 2 && Native.GetFileType(output) == 2, "Native workflow handles are not console character devices.");
            if (classic) SetPrivateGeometry(output, InitialColumns, InitialRows);
            RequireSize(ReadGeometry(output), InitialColumns, InitialRows);
            var cursor = new CursorInfo { Size = 33, Visible = true }; Api(Native.SetConsoleCursorInfo(output, ref cursor), "SetConsoleCursorInfo");
            Api(Native.SetConsoleCursorPosition(output, scenario == "shrink-exit" ? new(90, 10) : new(3, 2)), "SetConsoleCursorPosition");
            original = ReadState(input, output);
            var leaseOptions = new TerminalLeaseOptions { FollowActiveScreenBuffer = true };
            lease = classic ? await WindowsConsoleTerminal.OpenAsync(leaseOptions, deadline.Token) :
                await WindowsConsoleTerminal.OpenBorrowedHandlesAsync(input, output, leaseOptions, deadline.Token);
            Equal(original, lease.Snapshot.Original);
            await Packet(new { stage = "ready", nonce, aliases, standardAcquisition = classic, original, acquired = lease.Snapshot.Acquired,
                geometry = ReadGeometry(output), processId = Environment.ProcessId, os = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription }, deadline.Token);
            using var start = JsonDocument.Parse(await ReadLine(reader, deadline.Token)); Equal("start", start.RootElement.GetProperty("stage").GetString());
            var commandArgs = start.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var root = start.RootElement.GetProperty("root").GetString()!; Check(Path.IsPathFullyQualified(root) && Directory.Exists(root), "Owned workflow evidence root is absent.");
            evidenceRoot = root;
            if (scenario == "conpty-input-close") closeScope = WindowsConsoleCloseScope.Open(lease);
            // Only this contained test child changes its temporary/marker environment, never the executor or system.
            Environment.SetEnvironmentVariable("TEMP", root); Environment.SetEnvironmentVariable("TMP", root);
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS", Path.Combine(root, "markers"));
            if (scenario == "command-input")
            {
                var options = start.RootElement.GetProperty("commandInputOptions").GetString()!;
                Check(Path.IsPathFullyQualified(options) && File.Exists(options) &&
                    Path.GetRelativePath(root, options).Split(Path.DirectorySeparatorChar)[0] != "..",
                    "Original command terminal requires explicit task-local source options.");
                Environment.SetEnvironmentVariable("PISHARP_NATIVE_NODE_COMMAND_INPUT_OPTIONS", options);
            }
            using var commandCancellation = closeScope is null ? CancellationTokenSource.CreateLinkedTokenSource(deadline.Token) :
                CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, closeScope.CancellationToken);
            using var controlCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            using var diagnostics = new StringWriter();
            var witnessed = new WitnessConsole(lease, input, Packet);
            var command = scenario == "command-input"
                ? TerminalSessionCommand.RunObservedAsync(commandArgs, witnessed, lease, diagnostics,
                    (record, token) => new ValueTask(Packet(new { stage = "rpc-record", record = record.Value }, token)), commandCancellation.Token,
                    presentationObserver: new CommandInputPresentation(Packet))
                : scenario is "control-c" or "conpty-input-close"
                    ? TerminalSessionCommand.RunObservedAsync(commandArgs, witnessed, lease, diagnostics,
                        (_, _) => ValueTask.CompletedTask, commandCancellation.Token, observeFailure: commandFailure.Capture,
                        shutdownObserver: shutdownDiagnostics.Capture)
                    : TerminalSessionCommand.RunAsync(commandArgs, witnessed, lease, diagnostics, commandCancellation.Token);
            var controls = Controls();
            try
            {
                if (await Task.WhenAny(command, controls) == controls) { await controls; throw new IOException("Native workflow control ended before the command."); }
                commandExit = await command;
            }
            catch (Exception error) { failure = error; commandCancellation.Cancel(); }
            finally
            {
                controlCancellation.Cancel();
                try { commandExit = await command; } catch (Exception error) { failure ??= error; }
                try { await controls; } catch (OperationCanceledException) when (controlCancellation.IsCancellationRequested) { } catch (Exception error) { failure ??= error; }
                if (scenario is "control-c" or "conpty-input-close")
                    shutdownDiagnosticsReceipt = await shutdownDiagnostics.CaptureAfterJoinAsync(Path.Combine(root, "session.jsonl"),
                        "terminal-dialog|fc-terminal-dialog", commandFailure.OriginalFailure);
                diagnosticText = diagnostics.ToString(); Check(diagnosticText.Length <= 8192, "Terminal diagnostic exceeded bounded retention.");
            }
            nativeFaultObserved = witnessed.FaultObserved; mainGeometry = ReadGeometry(output);
            outside = original.CursorColumn >= mainGeometry.BufferColumns || original.CursorRow >= mainGeometry.BufferRows;
            if (scenario == "shrink-exit") RequireSize(mainGeometry, 20, 4);
            async Task Controls()
            {
                while (true)
                {
                    using var packet = JsonDocument.Parse(await ReadLine(reader, controlCancellation.Token));
                    switch (packet.RootElement.GetProperty("stage").GetString())
                    {
                        case "input":
                            Check(classic, "Private ConPTY input must use its physical parent pipe.");
                            Inject(input, packet.RootElement.GetProperty("text").GetString()!); break;
                        case "arm-fault": witnessed.ArmFault(); await Packet(new { stage = "fault-armed" }, controlCancellation.Token); break;
                        default: throw new IOException("Native workflow control command is unavailable.");
                    }
                }
            }
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            if (lease is not null)
            {
                try { await lease.DisposeAsync(); }
                catch (TerminalException error) when (scenario == "shrink-exit" && outside && error.Failure == TerminalFailure.RestorationFailed) { restorationFailure = error.Failure.ToString(); }
                catch (Exception error) { failure ??= error; }
                finalSnapshot = lease.Snapshot;
                try
                {
                    Check(finalSnapshot.ActiveReads == 0 && finalSnapshot.ActiveWrites == 0 && finalSnapshot.ReadWorkersStarted == finalSnapshot.ReadWorkersSettled &&
                        finalSnapshot.WriteWorkersStarted == finalSnapshot.WriteWorkersSettled, "Real workflow native workers are not joined.");
                    if (!outside) { Check(finalSnapshot.RestorationConfirmed, "Actual console restoration was not confirmed."); Equal(original, ReadState(input!, output!)); }
                    Equal(aliases, ReadAliases());
                }
                catch (Exception error) { failure ??= error; }
            }
            input?.Dispose(); output?.Dispose();
        }
        var closeDurableAcknowledged = false; var closeNativeToolError = false; var closeNoApprovalOrEffect = false; long closeDurableBytes = 0;
        if (closeScope is not null)
        {
            try { await closeScope.JoinCancellationAsync(); } catch (Exception error) { failure ??= error; }
            try
            {
                Check(closeScope.ObservedControlType == 2 && !closeScope.CleanupTimedOut && closeScope.CallbackException is null &&
                    closeScope.Snapshot.CancellationWorkJoined, "Actual input close did not finish managed cancellation under native event 2.");
                var root = evidenceRoot ?? throw new IOException("Close cleanup evidence root is absent.");
                var log = await Complete(Path.Combine(root, "session.jsonl")); closeDurableAcknowledged = true; closeDurableBytes = log.OriginalBytes.Length;
                closeNativeToolError = Context(log).LlmMessages.Any(entry => entry.WireBody.Value.GetProperty("role").GetString() == "toolResult" &&
                    entry.WireBody.Value.GetProperty("isError").GetBoolean());
                var markers = File.ReadAllLines(Path.Combine(root, "markers", "PublishedFixture.CliUi.markers"));
                closeNoApprovalOrEffect = !File.Exists(Path.Combine(root, "effect.txt")) && !markers.Contains("approved") &&
                    markers.Count(value => value == "tool-closed") == 1 && Directory.GetDirectories(Path.Combine(root, "snapshots")).Length == 0;
                Check(commandExit == 1 && finalSnapshot?.RestorationConfirmed == true && closeNativeToolError && closeNoApprovalOrEffect,
                    "Native close did not join and durably acknowledge its canceled callback without approval/effect.");
            }
            catch (Exception error) { failure ??= error; }
        }
        using var reporting = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var finalReceipt = new { stage = failure is null ? "done" : "failed", nonce, commandExit, original, finalSnapshot, mainGeometry,
            originalCursorOutsideCurrentBuffer = outside, restorationConfirmed = finalSnapshot?.RestorationConfirmed ?? false, restorationFailure,
            nativeFaultObserved, diagnosticText, underlyingCommandFailure = commandFailure.Receipt,
            shutdownDiagnostics = shutdownDiagnosticsReceipt,
            processId = Environment.ProcessId, consoleClose = closeScope?.Snapshot,
            closeDurableAcknowledged, closeDurableBytes, closeNativeToolError, closeNoApprovalOrEffect,
            failure = failure?.Message, failureType = failure?.GetType().Name };
        try
        {
            if (closeScope is not null)
            {
                var raw = Utf8.GetBytes(JsonSerializer.Serialize(finalReceipt)); Check(raw.Length <= MaximumReceiptCharacters, "Close cleanup receipt exceeds its hard bound.");
                var root = evidenceRoot ?? throw new IOException("Close cleanup evidence root is absent.");
                await using var file = new FileStream(Path.Combine(root, "conpty-input-close-cleanup.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await file.WriteAsync(raw, reporting.Token); await file.FlushAsync(reporting.Token); file.Flush(flushToDisk: true);
            }
            await Packet(finalReceipt, reporting.Token);
            closeScope?.SignalCleanupComplete();
        }
        finally { if (closeScope is not null) await closeScope.DisposeAsync(); }
        return failure is null ? 0 : 1;

        async Task Packet(object value, CancellationToken token)
        { await sending.WaitAsync(token); try { await Send(writer, value, token); } finally { sending.Release(); } }
    }

    private sealed class WitnessConsole(WindowsConsoleTerminal lease, SafeFileHandle input, Func<object, CancellationToken, Task> packet) : IConsoleTerminal
    {
        private int armed;
        internal bool FaultObserved { get; private set; }
        internal void ArmFault() => Interlocked.Exchange(ref armed, 1);
        public TerminalLeaseSnapshot Snapshot => lease.Snapshot;
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => lease.ReadAsync(destination, token);
        public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            if (frame.Span.StartsWith("\u001b[0m", StringComparison.Ordinal) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                using var invalid = new SafeFileHandle(new IntPtr(-1), false);
                var success = Native.WriteConsoleW(invalid, ['!'], 1, out var written, IntPtr.Zero); var error = Marshal.GetLastPInvokeError();
                Check(!success && written == 0 && error != 0, "Native closed-handle failure did not occur."); FaultObserved = true;
                await packet(new { stage = "native-write-failure", actualNativeCall = "WriteConsoleW", success, written, win32Error = error }, token);
                throw new TerminalException(TerminalFailure.NativeIoFailed);
            }
            await lease.WriteAsync(frame, token);
            if (!frame.Span.StartsWith("\u001b[0m", StringComparison.Ordinal)) return;
            // The pre-entry output alias belongs to the main screen buffer. Reopen
            // CONOUT$ after the awaited write to observe the actual active buffer.
            using var active = OpenConsole("CONOUT$");
            var geometry = ReadGeometry(active); var screen = ReadScreen(active, geometry); var state = ReadState(input, active);
            var cursor = new { Row = state.CursorRow - geometry.Top, Column = state.CursorColumn - geometry.Left, Visible = state.CursorVisible };
            var snapshot = lease.Snapshot;
            try
            {
                Check(snapshot.ActiveWrites == 0 && snapshot.WriteWorkersStarted == snapshot.WriteWorkersSettled, "Native write witness preceded its physical settlement.");
                Check(TerminalSourceAsciiExpectations.HardwareAnchorInBounds(geometry.Columns, geometry.Rows,
                    cursor.Row, cursor.Column, cursor.Visible), "Actual native cursor is outside the pinned Source editor row, visibility and original column bounds.");
                Check(screen.All(row => row.All(value => value is >= ' ' and <= '~' or '\u2500' or '\u2191' or '\u2193')),
                    "Actual source-default ASCII workflow cells contain uncaptured glyph or control data.");
            }
            catch (Exception error)
            {
                await packet(new { stage = "native-witness-failure", geometry, screen, cursor, nativeState = state, lease = snapshot,
                    failureType = error.GetType().Name, failure = error.Message }, token);
                throw;
            }
            await packet(new { stage = "screen", geometry, screen, cursor, nativeState = state, lease = snapshot }, token);
        }
        public ValueTask DisposeAsync() => throw new InvalidOperationException("The whole terminal command must not dispose its borrowed lease.");
    }

    private static async Task<string> ReadLine(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder(); var data = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(data.AsMemory(), token) == 0) throw new EndOfStreamException("Native workflow pipe ended before its bounded receipt.");
            if (data[0] == '\n') return line.ToString(); Check(line.Length < MaximumReceiptCharacters, "Native workflow receipt exceeds its hard bound."); line.Append(data[0]);
        }
    }
    private static Task Send(StreamWriter writer, object value, CancellationToken token)
    { var text = JsonSerializer.Serialize(value); Check(text.Length <= MaximumReceiptCharacters, "Native workflow receipt exceeds its hard bound."); return writer.WriteLineAsync(text.AsMemory(), token); }
    private static void Inject(SafeFileHandle input, string text)
    {
        Check(text.Length is > 0 and <= 4096, "Native input injection exceeds its fixture bound.");
        var events = new InputRecord[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            events[index * 2] = new() { Type = 1, Key = new() { Down = true, Repeat = 1, Character = text[index] } };
            events[index * 2 + 1] = new() { Type = 1, Key = new() { Down = false, Repeat = 1, Character = text[index] } };
        }
        Api(Native.WriteConsoleInputW(input, events, (uint)events.Length, out var count), "WriteConsoleInputW"); Check(count == events.Length, "Actual native input injection was incomplete.");
    }
    private static void SetPrivateGeometry(SafeFileHandle output, int columns, int rows)
    {
        var small = new SmallRect { Left = 0, Top = 0, Right = 0, Bottom = 0 }; Api(Native.SetConsoleWindowInfo(output, true, ref small), "SetConsoleWindowInfo(private-small)");
        Api(Native.SetConsoleScreenBufferSize(output, new((short)columns, (short)rows)), "SetConsoleScreenBufferSize(private)");
        var target = new SmallRect { Left = 0, Top = 0, Right = (short)(columns - 1), Bottom = (short)(rows - 1) }; Api(Native.SetConsoleWindowInfo(output, true, ref target), "SetConsoleWindowInfo(private)");
    }
    private sealed record Aliases(long Input, long Output, uint InputType, uint OutputType);
    private static Aliases ReadAliases()
    {
        using var input = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-10)), false);
        using var output = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-11)), false);
        return new(input.DangerousGetHandle().ToInt64(), output.DangerousGetHandle().ToInt64(), Native.GetFileType(input), Native.GetFileType(output));
    }
    private sealed record Geometry(int Columns, int Rows, short Left, short Top, short Right, short Bottom, short BufferColumns, short BufferRows);
    private static Geometry ReadGeometry(SafeFileHandle output)
    {
        Api(Native.GetConsoleScreenBufferInfo(output, out var value), "GetConsoleScreenBufferInfo");
        return new(value.Window.Right - value.Window.Left + 1, value.Window.Bottom - value.Window.Top + 1,
            value.Window.Left, value.Window.Top, value.Window.Right, value.Window.Bottom, value.Size.X, value.Size.Y);
    }
    private static void RequireSize(Geometry geometry, int columns, int rows)
    {
        Check(columns is >= 1 and <= 128 && rows is >= 1 and <= 16 && columns * rows <= 2048, "Native workflow geometry exceeds its hard bound.");
        Check(geometry.Columns == columns && geometry.Rows == rows && geometry.Left >= 0 && geometry.Top >= 0 && geometry.BufferColumns >= geometry.Left + columns &&
            geometry.BufferRows >= geometry.Top + rows, "Actual native window/buffer geometry differs.");
    }
    private static string[] ReadScreen(SafeFileHandle output, Geometry geometry)
    {
        RequireSize(geometry, geometry.Columns, geometry.Rows); var cells = new CharacterInfo[geometry.Columns * geometry.Rows];
        var region = new SmallRect { Left = geometry.Left, Top = geometry.Top, Right = geometry.Right, Bottom = geometry.Bottom };
        Api(Native.ReadConsoleOutputW(output, cells, new((short)geometry.Columns, (short)geometry.Rows), new(0, 0), ref region), "ReadConsoleOutputW");
        Check(region.Left == geometry.Left && region.Top == geometry.Top && region.Right == geometry.Right && region.Bottom == geometry.Bottom, "Actual native screen observation was clipped.");
        return Enumerable.Range(0, geometry.Rows).Select(row => new string(cells.AsSpan(row * geometry.Columns, geometry.Columns).ToArray().Select(cell => cell.Character).ToArray())).ToArray();
    }
    private static TerminalConsoleState ReadState(SafeFileHandle input, SafeFileHandle output)
    {
        Api(Native.GetConsoleMode(input, out var inputMode), "GetConsoleMode(input)"); Api(Native.GetConsoleMode(output, out var outputMode), "GetConsoleMode(output)");
        Api(Native.GetConsoleCursorInfo(output, out var cursor), "GetConsoleCursorInfo"); Api(Native.GetConsoleScreenBufferInfo(output, out var buffer), "GetConsoleScreenBufferInfo");
        return new(inputMode, outputMode, Native.GetConsoleCP(), Native.GetConsoleOutputCP(), cursor.Size, cursor.Visible, buffer.Cursor.X, buffer.Cursor.Y);
    }
    private static SafeFileHandle OpenConsole(string name)
    {
        var handle = Native.CreateFileW(name, 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("Opening private console failed (Win32 " + error + ").");
    }
    private static void Api(bool success, string api) { if (!success) throw new IOException(api + " failed (Win32 " + Marshal.GetLastPInvokeError() + ")."); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Actual native workflow observation differs.");

    private sealed class OwnedConsole : IAsyncDisposable
    {
        private IntPtr console;
        private SafeFileHandle? job, process;
        private FileStream? input, output;
        private Task? drain, closing;
        private readonly MemoryStream captured = new();
        private readonly object gate = new();
        private readonly TaskCompletionSource nonceOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] nonceBytes = [];
        private byte[] retained = [];
        private long outputCount;
        private bool contained, exited, closeJoined, drainJoined;
        private bool? jobTerminationSucceeded, processTerminationSucceeded;
        private int? jobTerminationError, processTerminationError;
        private uint? exitCode, waitResult;
        private int? processId;
        public Stream Input => input!;
        public bool ExitConfirmed => exited;
        public uint? PhysicalExitCode => exitCode;
        public int? PhysicalProcessId => processId;
        public bool CloseJoined => closeJoined;
        public bool DrainJoined => drainJoined;
        public bool Truncated { get { lock (gate) return outputCount > MaximumOutputBytes; } }
        public byte[] OutputBytes { get { lock (gate) return retained.ToArray(); } }

        public void CloseInput() { input?.Dispose(); input = null; }

        public static OwnedConsole StartStandard(string host, string dll, string pipe, string scenario, string nonce)
        {
            var result = new OwnedConsole { drain = Task.CompletedTask, closeJoined = true };
            SafeFileHandle? thread = null;
            try
            {
                result.job = Native.CreateJobObjectW(IntPtr.Zero, null); Check(!result.job.IsInvalid, "Private standard-console containment job failed.");
                var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
                Api(Native.SetInformationJobObject(result.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "SetInformationJobObject");
                var startup = new StartupEx { Startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>(), Flags = 1, ShowWindow = 0 } };
                var command = new StringBuilder(string.Join(" ", new[] { host, dll, WorkerSwitch, pipe, scenario, nonce }.Select(Quote)));
                Api(Native.CreateProcessW(host, command, IntPtr.Zero, IntPtr.Zero, false, 0x14, IntPtr.Zero,
                    Path.GetDirectoryName(dll), ref startup, out var info), "CreateProcessW(hidden-standard-console)");
                result.process = new SafeFileHandle(info.Process, true); thread = new SafeFileHandle(info.Thread, true); result.processId = checked((int)info.ProcessId);
                Api(Native.AssignProcessToJobObject(result.job, result.process), "AssignProcessToJobObject"); result.contained = true;
                Check(Native.ResumeThread(thread) != uint.MaxValue, "Resuming contained standard-console worker failed."); return result;
            }
            catch (Exception error)
            {
                Exception? cleanup = null; try { result.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception failure) { cleanup = failure; }
                witnesses.Add(new { stage = "partial-standard-launch-failure", type = error.GetType().Name, message = error.Message,
                    cleanupType = cleanup?.GetType().Name, cleanupMessage = cleanup?.Message, physical = result.Evidence(scenario, nonce, true) });
                if (cleanup is not null) throw new AggregateException(error, cleanup); throw;
            }
            finally { thread?.Dispose(); }
        }

        public static OwnedConsole Start(string host, string dll, string pipe, string scenario, string nonce)
        {
            var result = new OwnedConsole { nonceBytes = Encoding.ASCII.GetBytes("nonce:" + nonce) };
            SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, thread = null;
            var list = IntPtr.Zero; var initialized = false;
            try
            {
                Api(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0), "CreatePipe(input)");
                Api(Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0), "CreatePipe(output)");
                var status = Native.CreatePseudoConsole(new(InitialColumns, InitialRows), inputRead!, outputWrite!, 0, out result.console);
                Check(status == 0, "Creating private renderer ConPTY failed (HRESULT " + status.ToString("X8") + ").");
                result.input = new FileStream(inputWrite!, FileAccess.Write, 4096, isAsync: false); inputWrite = null;
                result.output = new FileStream(outputRead!, FileAccess.Read, 4096, isAsync: false); outputRead = null;
                result.drain = Task.Factory.StartNew(result.Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                result.job = Native.CreateJobObjectW(IntPtr.Zero, null); Check(!result.job.IsInvalid, "Private renderer containment job failed.");
                var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
                Api(Native.SetInformationJobObject(result.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "SetInformationJobObject");
                var bytes = IntPtr.Zero; _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                Check(bytes != IntPtr.Zero && bytes.ToInt64() <= 65_536, "Renderer ConPTY attribute allocation is invalid.");
                list = Marshal.AllocHGlobal(bytes);
                Api(Native.InitializeProcThreadAttributeList(list, 1, 0, ref bytes), "InitializeProcThreadAttributeList"); initialized = true;
                Api(Native.UpdateProcThreadAttribute(list, 0, (IntPtr)0x20016, result.console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");
                var startup = new StartupEx { Startup = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>(), Flags = 1, ShowWindow = 0 }, Attributes = list };
                var command = new StringBuilder(string.Join(" ", new[] { host, dll, WorkerSwitch, pipe, scenario, nonce }.Select(Quote)));
                Api(Native.CreateProcessW(host, command, IntPtr.Zero, IntPtr.Zero, false, 0x80004, IntPtr.Zero,
                    Path.GetDirectoryName(dll), ref startup, out var info), "CreateProcessW");
                result.process = new SafeFileHandle(info.Process, true); thread = new SafeFileHandle(info.Thread, true);
                result.processId = checked((int)info.ProcessId);
                Api(Native.AssignProcessToJobObject(result.job, result.process), "AssignProcessToJobObject");
                result.contained = true;
                Check(Native.ResumeThread(thread) != uint.MaxValue, "Resuming contained renderer worker failed.");
                return result;
            }
            catch (Exception error)
            {
                inputRead?.Dispose(); inputRead = null; outputWrite?.Dispose(); outputWrite = null;
                Exception? cleanupFailure = null;
                try { result.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception cleanup) { cleanupFailure = cleanup; }
                witnesses.Add(new { stage = "partial-launch-failure", type = error.GetType().Name, message = error.Message,
                    cleanupType = cleanupFailure?.GetType().Name, cleanupMessage = cleanupFailure?.Message,
                    physical = result.Evidence(scenario, nonce, true) });
                if (cleanupFailure is not null) throw new AggregateException(error, cleanupFailure);
                throw;
            }
            finally
            {
                thread?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
                if (initialized) Native.DeleteProcThreadAttributeList(list);
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            }
        }
        public void Resize(short columns, short rows)
        {
            Check(console != IntPtr.Zero && columns is >= 1 and <= 128 && rows is >= 1 and <= 16 && columns * rows <= 2048, "Renderer resize exceeds its owned console bounds.");
            var result = Native.ResizePseudoConsole(console, new(columns, rows)); Check(result == 0, "Actual ConPTY resize failed (HRESULT " + result.ToString("X8") + ").");
        }
        private void Drain()
        {
            var buffer = new byte[4096];
            while (true)
            {
                var count = output!.Read(buffer); if (count == 0) return;
                lock (gate)
                {
                    outputCount += count;
                    var retain = Math.Min(count, MaximumOutputBytes - checked((int)captured.Length));
                    if (retain > 0) captured.Write(buffer, 0, retain);
                    if (captured.GetBuffer().AsSpan(0, checked((int)captured.Length)).IndexOf(nonceBytes) >= 0) nonceOutput.TrySetResult();
                }
            }
        }
        public async Task<uint> WaitForExit(bool cleanup = false)
        {
            var owned = process ?? throw new Exception("Renderer worker process handle is unavailable.");
            var result = await Task.Factory.StartNew(() => Native.WaitForSingleObject(owned, cleanup ? uint.MaxValue : 5000), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waitResult = result; Check(result == 0, "Actual renderer worker did not signal exit within its native wait.");
            Api(Native.GetExitCodeProcess(owned, out var code), "GetExitCodeProcess"); exited = true; exitCode = code; return code;
        }
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? completion = null; Task task;
            lock (gate)
            {
                if (closing is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); closing = completion.Task; }
                task = closing;
            }
            if (completion is not null) _ = Close(completion); return new(task);
        }
        private async Task Close(TaskCompletionSource completion)
        {
            Exception? failure = null;
            try
            {
                if (process is not null && !exited)
                {
                    if (contained && job is not null && !job.IsInvalid)
                    {
                        jobTerminationSucceeded = Native.TerminateJobObject(job, 3);
                        if (jobTerminationSucceeded == false)
                        {
                            jobTerminationError = Marshal.GetLastPInvokeError();
                            failure = new IOException("Owned renderer job termination failed (Win32 " + jobTerminationError + ").");
                        }
                    }
                    // A successful contained job request already terminates this process.
                    // Issuing a second request races its settlement; a failed immediate poll
                    // cannot establish that the first request failed. Partial launches that
                    // never joined the job still require direct process termination.
                    if (jobTerminationSucceeded != true)
                    {
                        processTerminationSucceeded = Native.TerminateProcess(process, 3);
                        if (processTerminationSucceeded == false)
                        {
                            processTerminationError = Marshal.GetLastPInvokeError();
                            if (Native.WaitForSingleObject(process, 0) != 0)
                                failure ??= new IOException("Owned renderer process termination failed (Win32 " + processTerminationError + ").");
                        }
                    }
                    // After requesting termination, retain the real wait worker/handle until settlement.
                    // An unsupported native hang requires the root's contained process watchdog.
                    try { await WaitForExit(cleanup: true); } catch (Exception error) { failure ??= error; }
                }
                input?.Dispose(); input = null;
                if (console != IntPtr.Zero)
                {
                    var owned = console;
                    try
                    {
                        await Task.Factory.StartNew(() => Native.ClosePseudoConsole(owned), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                        console = IntPtr.Zero; closeJoined = true;
                    }
                    catch (Exception error) { failure ??= error; }
                }
                if (drain is not null)
                {
                    try { await drain; drainJoined = true; } catch (Exception error) { failure ??= error; }
                }
            }
            catch (Exception error) { failure ??= error; }
            finally
            {
                lock (gate) retained = captured.ToArray();
                input?.Dispose(); output?.Dispose(); job?.Dispose(); process?.Dispose(); captured.Dispose();
                input = output = null; job = process = null;
            }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
        public object Evidence(string scenario, string nonce, bool parentAbort)
        {
            var bytes = OutputBytes;
            return new { scenario, nonce, parentAbort, processId, exitConfirmed = exited, nativeWaitResult = waitResult, exitCode,
                contained, jobTerminationSucceeded, jobTerminationError, processTerminationSucceeded, processTerminationError,
                closeJoined, drainJoined, actualRawNonceBytesObserved = nonceOutput.Task.IsCompletedSuccessfully,
                outputBytes = outputCount, retainedBytes = bytes.Length, truncated = Truncated,
                rawSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), rawBase64 = Convert.ToBase64String(bytes) };
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? 2 * slashes + 1 : slashes).Append(character); slashes = 0;
            }
            return result.Append('\\', 2 * slashes).Append('"').ToString();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size; [MarshalAs(UnmanagedType.Bool)] public bool Visible; }
    [StructLayout(LayoutKind.Sequential)] private struct SmallRect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public ushort Attributes; public SmallRect Window; public Coord MaximumWindowSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup
    {
        public uint Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupObservation
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessUserTimeLimit, JobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)] private struct CharacterInfo
    { [FieldOffset(0)] public char Character; [FieldOffset(2)] public ushort Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyEvent
    { [MarshalAs(UnmanagedType.Bool)] public bool Down; public ushort Repeat, VirtualKey, Scan, Character; public uint Modifiers; }
    [StructLayout(LayoutKind.Explicit, Size = 20)] private struct InputRecord
    { [FieldOffset(0)] public ushort Type; [FieldOffset(4)] public KeyEvent Key; }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GetStdHandle(uint id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern void GetStartupInfoW(out StartupObservation startup);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetConsoleCP();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNumberOfConsoleInputEvents(SafeFileHandle handle, out uint events);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleCursorInfo(SafeFileHandle handle, out CursorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleScreenBufferInfo(SafeFileHandle handle, out BufferInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCursorInfo(SafeFileHandle handle, ref CursorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCursorPosition(SafeFileHandle handle, Coord coord);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, uint size);
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadConsoleOutputW(SafeFileHandle output, [Out] CharacterInfo[] buffer, Coord size, Coord origin, ref SmallRect region);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteConsoleInputW(SafeFileHandle input, [In] InputRecord[] events, uint length, out uint written);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteConsoleW(SafeFileHandle output, [In] char[] data, uint length, out uint written, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleScreenBufferSize(SafeFileHandle output, Coord size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleWindowInfo(SafeFileHandle output, [MarshalAs(UnmanagedType.Bool)] bool absolute, ref SmallRect rect);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    }
}
