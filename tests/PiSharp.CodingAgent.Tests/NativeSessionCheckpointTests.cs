using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static partial class NativeExtensionSessionCommandTests
{
    internal static readonly List<object> CheckpointProcessEvidence = [];
    public static IEnumerable<(string Name, Func<Task> Run)> CheckpointCases(string host, string cli)
    {
        _ = Cases(host, cli);
        yield return ("native session checkpoint published RPC A B A reconstructs selected branch and rejects stale authority", CheckpointRpc);
        yield return ("native session checkpoint RPC staging failures veto abort and EOF preserve usable session", CheckpointRollback);
        yield return ("native session checkpoint RPC abort after publication settles original callback and current target", CheckpointAbortAfterSwitch);
        yield return ("native session checkpoint one shot report print and JSON settle and verify current replacement", CheckpointOneShot);
        yield return ("native session checkpoint real cooked CLI routes startup dialogs and A B A checkpoints", CheckpointChat);
    }
    private static async Task CheckpointRpc()
    {
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            using var files = await Files.CreateAsync("checkpoint"); await Create(files, api); await Script(files, Text(api, "unused checkpoint provider turn"));
            var target = await CheckpointTarget(files); var aBefore = await File.ReadAllBytesAsync(files.Session);
            var bBefore = await File.ReadAllBytesAsync(target.Path);
            await using (var child = new RpcChild(files, api))
            {
                // Ordinary frames may arrive before startup completes; the real reader must still route its UI reply.
                await child.Send(new { id = "startup-state", type = "get_state" });
                await ReplyCheckpoint(child, "Resume checkpoint session", 1, true);
                Response(await child.Response("startup-state"));
                await child.Send(new { id = "round-trip", type = "prompt", message = "/checkpoint " + JsonSerializer.Serialize(new
                { target = target.Path, leafId = target.Leaf, returnPath = files.Session, roundTrip = true, label = "汉字 🚀\ncheckpoint" }) });
                await ReplyCheckpoint(child, "Switch checkpoint session", 1, true);
                await ReplyReplacementResume(child, 2, 2, target.Path, "round-trip");
                await ReplyCheckpoint(child, "Switch checkpoint session", 2, true);
                await ReplyReplacementResume(child, 3, 3, files.Session, "round-trip");
                var response = Response(await child.Response("round-trip"));
                Equal("handled", response.GetProperty("data").GetProperty("disposition").GetString());
                var switches = child.Records.Where(row => Type(row) == "session_switched").ToArray(); Equal(2, switches.Length);
                Equal(2L, switches[0].Value.GetProperty("generation").GetInt64()); Equal(3L, switches[1].Value.GetProperty("generation").GetInt64());
                Check(child.Records.Any(row => Type(row) == "extension_ui_request" && row.Value.TryGetProperty("message", out var message) &&
                    message.GetString()!.Contains("stale context rejected=True", StringComparison.Ordinal)), "Published stale write proof did not execute.");
                await child.Send(new { id = "entries", type = "get_entries" });
                var entries = Response(await child.Response("entries")).GetProperty("data").GetProperty("entries");
                await using (var stream = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var actual = await new SessionLogReader().ReadAsync(stream);
                    Equal(actual.ValidatedPrefix.Length - 1, entries.GetArrayLength());
                    for (var index = 0; index < entries.GetArrayLength(); index++)
                        Equal(actual.ValidatedPrefix[index + 1].Entry.WireBody.ToString(), entries[index].GetRawText());
                }
                await child.Send(new { id = "final-state", type = "get_state" });
                Equal(files.Session, Response(await child.Response("final-state")).GetProperty("data").GetProperty("sessionFile").GetString());
                Check(!child.Records.Any(row => Type(row) is "agent_start" or "tool_execution_start"), "Checkpoint sample acquired a provider or tool.");
                Clean(await child.Finish());
            }
            await VerifyCheckpointFiles(files, target, aBefore, bBefore);
            CheckpointProcessEvidence.Add(new { workflow = "rpc-round-trip", api, naturalExit = 0, outputReadersJoined = true,
                physicalReopens = 2, generations = new[] { 1, 2, 3 }, selectedLeaf = target.Leaf,
                aSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(files.Session))),
                bSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(target.Path))) });
            var markers = await File.ReadAllLinesAsync(Path.Combine(files.Markers, "SessionCheckpoint.markers"));
            Equal(2, markers.Count(line => line == "initialize")); Equal(2, markers.Count(line => line == "dispose"));
            Equal(0, Directory.GetDirectories(files.Snapshots).Length);
        }
    }
    private static async Task CheckpointRollback()
    {
        const string api = "openai-responses";
        using var files = await Files.CreateAsync("checkpoint"); await Create(files, api); await Script(files, Text(api, "unused checkpoint provider turn"));
        var target = await CheckpointTarget(files); var originalA = await File.ReadAllBytesAsync(files.Session); var originalB = await File.ReadAllBytesAsync(target.Path);
        await using (var child = new RpcChild(files, api))
        {
            await ReplyCheckpoint(child, "Resume checkpoint session", 1, true);
            await child.Send(new { id = "missing", type = "switch_session", sessionPath = files.In("missing.jsonl") });
            Check(!((await child.Response("missing")).Value.GetProperty("success").GetBoolean()), "Missing target was accepted.");
            await child.Send(new { id = "missing-leaf", type = "switch_session", sessionPath = target.Path, leafId = "missing-leaf" });
            Check(!((await child.Response("missing-leaf")).Value.GetProperty("success").GetBoolean()), "Missing selected branch was accepted.");
            await child.Send(new { id = "veto", type = "switch_session", sessionPath = target.Path });
            await ReplyCheckpoint(child, "Switch checkpoint session", 1, false);
            Check(Response(await child.Response("veto")).GetProperty("data").GetProperty("cancelled").GetBoolean(), "False confirmation did not veto staging.");
            await child.Send(new { id = "direct-abort", type = "switch_session", sessionPath = target.Path });
            await CheckpointDialog(child, "Switch checkpoint session", 2);
            await child.Send(new { id = "direct-control", type = "abort" }); Response(await child.Response("direct-control"));
            Check(!((await child.Response("direct-abort")).Value.GetProperty("success").GetBoolean()), "Direct staged switch ignored abort.");
            await child.Send(new { id = "aborted-switch", type = "prompt", message = "/checkpoint " + target.Path });
            await CheckpointDialog(child, "Switch checkpoint session", 3);
            await child.Send(new { id = "abort", type = "abort" }); Response(await child.Response("abort"));
            Check(!((await child.Response("aborted-switch")).Value.GetProperty("success").GetBoolean()), "Canceled switch became handled success.");
            await child.Send(new { id = "state", type = "get_state" });
            Equal(files.Session, Response(await child.Response("state")).GetProperty("data").GetProperty("sessionFile").GetString());
            await child.Send(new { id = "save-after", type = "prompt", message = "/checkpoint" }); Response(await child.Response("save-after"));
            Clean(await child.Finish());
        }
        var a = await Complete(files); Check(a.OriginalBytes.AsSpan().StartsWith(originalA), "Rollback rewrote A prefix.");
        Check((await File.ReadAllBytesAsync(target.Path)).SequenceEqual(originalB), "Failure/veto/cancellation changed B.");
        Check(!CheckpointEntries(a).Any(entry => entry.GetProperty("data").GetProperty("phase").GetString() == "after-switch"), "Canceled replacement committed target state.");
        await using (var startup = new RpcChild(files, api))
        { await CheckpointDialog(startup, "Resume checkpoint session", 1); Clean(await startup.Finish()); }
        await using (var eof = new RpcChild(files, api))
        {
            await ReplyCheckpoint(eof, "Resume checkpoint session", 1, true);
            await eof.Send(new { id = "held", type = "switch_session", sessionPath = target.Path });
            await CheckpointDialog(eof, "Switch checkpoint session", 1); Clean(await eof.Finish());
        }
        Check((await File.ReadAllBytesAsync(target.Path)).SequenceEqual(originalB), "EOF left partial target state.");
        Equal(0, Directory.GetDirectories(files.Snapshots).Length);
        CheckpointProcessEvidence.Add(new { workflow = "rpc-rollback", physicalChildren = 3, naturalExit = 0,
            outputReadersJoined = true, failure = true, veto = true, abort = true, startupDialogEof = true, replacementDialogEof = true });
    }
    private static async Task CheckpointAbortAfterSwitch()
    {
        const string api = "openai-responses";
        foreach (var eof in new[] { false, true })
        {
            using var files = await Files.CreateAsync("checkpoint"); await Create(files, api);
            await Script(files, Text(api, "unused checkpoint provider turn"));
            var target = await CheckpointTarget(files); var bBytes = await File.ReadAllBytesAsync(target.Path);
            await using (var child = new RpcChild(files, api))
            {
                await ReplyCheckpoint(child, "Resume checkpoint session", 1, true);
                await child.Send(new { id = "held-after-switch", type = "prompt", message = "/checkpoint " + JsonSerializer.Serialize(new
                { target = target.Path, leafId = target.Leaf, confirmCheckpoint = true }) });
                await ReplyCheckpoint(child, "Switch checkpoint session", 1, true);
                await ReplyReplacementResume(child, 2, 2, target.Path, "held-after-switch");
                var dialog = await CheckpointDialog(child, "Save replacement checkpoint", 1);
                Check(child.Records.Count(row => Type(row) == "session_switched") == 1, "Held callback did not publish B.");
                if (!eof)
                {
                    await child.Send(new { id = "abort-postcommit", type = "abort" }); Response(await child.Response("abort-postcommit"));
                    var aborted = (await child.Response("held-after-switch")).Value;
                    Check(!aborted.GetProperty("success").GetBoolean(), "Aborted original callback reported handled.");
                    Check(aborted.GetProperty("error").GetString()!.Contains("replacement committed", StringComparison.Ordinal), "Postcommit abort pretended the switch had not been accepted.");
                    await child.Send(new { type = "extension_ui_response", id = dialog.Value.GetProperty("id").GetString(), confirmed = true });
                    await child.Send(new { id = "state-after-abort", type = "get_state" });
                    Equal(target.Path, Response(await child.Response("state-after-abort")).GetProperty("data").GetProperty("sessionFile").GetString());
                }
                Clean(await child.Finish());
            }
            Check((await File.ReadAllBytesAsync(target.Path)).SequenceEqual(bBytes), "Canceled checkpoint wrote into committed B.");
            Equal(0, Directory.GetDirectories(files.Snapshots).Length);
            CheckpointProcessEvidence.Add(new { workflow = eof ? "postcommit-eof" : "postcommit-abort", naturalExit = 0,
                outputReadersJoined = true, retainedOriginalCallbackJoined = true, targetStillCurrent = true, targetBytesUnchanged = true });
        }
    }
    private static async Task CheckpointOneShot()
    {
        const string api = "openai-responses";
        foreach (var mode in new[] { "report", "print", "json" })
        foreach (var roundTrip in new[] { false, true })
        {
            using var files = await Files.CreateAsync("checkpoint"); await Create(files, api);
            await Script(files, Text(api, "unused checkpoint provider turn")); var target = await CheckpointTarget(files);
            var result = await Command(files, "resume", api, "/checkpoint " + JsonSerializer.Serialize(new
            { target = target.Path, leafId = target.Leaf, returnPath = files.Session, roundTrip, confirmSwitch = false }), "--output", mode);
            Check(result.ExitCode == 0 && result.Error.Length == 0, "One-shot checkpoint failed: " + Describe(result));
            var expected = roundTrip ? files.Session : target.Path;
            if (mode == "report")
            {
                var report = JsonData.Parse(Utf8.GetString(result.Output)).Value;
                Equal(expected, report.GetProperty("sessionPath").GetString()); Equal(0, report.GetProperty("usedScriptTurns").GetInt32());
                Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "One-shot replacement omitted acknowledgment.");
            }
            else if (mode == "json")
            {
                var records = JsonLines(result.Output); var switches = records.Where(row => Type(row) == "session_switched").ToArray();
                Equal(roundTrip ? 2 : 1, switches.Length); Equal(expected, switches[^1].Value.GetProperty("sessionFile").GetString());
                Equal(roundTrip ? 3L : 2L, switches[^1].Value.GetProperty("generation").GetInt64());
                Check(!records.Any(row => Type(row) == "agent_start"), "Handled checkpoint acquired a provider in one-shot JSON mode.");
            }
            await using var reopened = await SessionLogStore.OpenAsync(expected);
            Equal(roundTrip ? "after-return" : "after-switch", reopened.Snapshot.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("phase").GetString());
            Equal(0, Directory.GetDirectories(files.Snapshots).Length);
            CheckpointProcessEvidence.Add(new { workflow = "one-shot-current-replacement", mode, roundTrip, naturalExit = 0,
                outputReadersJoined = true, finalWriterReacquired = true });
        }
    }
    private static async Task CheckpointChat()
    {
        const string api = "openai-responses";
        using var files = await Files.CreateAsync("checkpoint"); await Create(files, api);
        await Complete(files);
        var createMarkers = await File.ReadAllLinesAsync(Path.Combine(files.Markers, "SessionCheckpoint.markers"));
        Check(createMarkers.Contains("session-start:1") && createMarkers[^1] == "dispose",
            "Checkpoint create did not bind, publish startup, and join activation disposal before the cooked frontend.");
        await Script(files, Text(api, "unused checkpoint provider turn"));
        var target = await CheckpointTarget(files); var aBefore = await File.ReadAllBytesAsync(files.Session); var bBefore = await File.ReadAllBytesAsync(target.Path);
        using var process = Process.Start(Start(files, Base("chat", files, api).Concat(["--offline-script", files.Script]).Concat(files.ExtensionArgs)))
            ?? throw new InvalidOperationException("Actual cooked frontend did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var lines = new List<string>(); var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new object();
        var reading = ReadChat(); var errors = ReadBytes(process.StandardError.BaseStream, deadline.Token);
        try
        {
            await Wait("[ui confirm] Resume checkpoint session", 1); await Send("yes"); await Wait("[history]", 1);
            await Send("/checkpoint " + JsonSerializer.Serialize(new { target = target.Path, leafId = target.Leaf, returnPath = files.Session, roundTrip = true, label = "cooked" }));
            await Wait("[ui confirm] Switch checkpoint session", 1); await Send("yes");
            await Wait("[ui confirm] Resume checkpoint session", 2); await Send("yes");
            await Wait("[ui confirm] Switch checkpoint session", 2); await Send("yes");
            await Wait("[ui confirm] Resume checkpoint session", 3); await Send("yes");
            await Wait("Checkpoint round trip complete; stale context rejected=True", 1); await Wait("[accepted] prompt handled", 1);
            await Send("/state"); await Wait("[state] idle", 1); await Send("/quit"); process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token); await reading;
            var stderr = Utf8.GetString(await errors);
            Check(process.ExitCode == 0 && stderr.Length == 0, "Expected successful cooked checkpoint receipt; exitCode=" +
                process.ExitCode + ", stdout=" + Bounded(string.Join('\n', lines)) + ", stderr=" + Bounded(stderr));
            Equal(3, lines.Count(line => line.Contains("[ui confirm] Resume checkpoint session", StringComparison.Ordinal)));
            await VerifyCheckpointFiles(files, target, aBefore, bBefore);
            CheckpointProcessEvidence.Add(new { workflow = "actual-cooked-cli", processId = process.Id, naturalExit = process.ExitCode,
                outputReadersJoined = true, physicalReopens = 2, startupDialogReplied = true, replacementDialogsReplied = 2 });
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            foreach (var task in new Task[] { reading, errors }) try { await task; } catch (Exception) { }
        }
        async Task Send(string line) { await process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        async Task ReadChat()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    TaskCompletionSource notify;
                    lock (gate) { Check(lines.Count < 4096, "Cooked checkpoint output exceeded its bound."); lines.Add(line); notify = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                    notify.TrySetResult();
                }
            }
            finally { lock (gate) changed.TrySetResult(); }
        }
        async Task Wait(string text, int occurrence)
        {
            while (true)
            {
                Task pending;
                lock (gate) { if (lines.Count(line => line.Contains(text, StringComparison.Ordinal)) >= occurrence) return;
                    pending = changed.Task; }
                if (reading.IsCompleted)
                {
                    await reading; await process.WaitForExitAsync(deadline.Token);
                    throw new InvalidOperationException("Cooked checkpoint child ended before " + text + "; exit=" + process.ExitCode + "; stderr=" + Utf8.GetString(await errors));
                }
                await pending.WaitAsync(deadline.Token);
            }
        }
    }
    private static Task<JsonData> CheckpointDialog(RpcChild child, string title, int occurrence) => child.Wait(row =>
        Type(row) == "extension_ui_request" && row.Value.TryGetProperty("title", out var text) && text.GetString() == title &&
        child.Records.Where(record => Type(record) == "extension_ui_request" && record.Value.TryGetProperty("title", out var other) && other.GetString() == title)
            .Take(occurrence).LastOrDefault() == row && child.Records.Count(record => Type(record) == "extension_ui_request" &&
                record.Value.TryGetProperty("title", out var other) && other.GetString() == title) >= occurrence);
    private static async Task ReplyCheckpoint(RpcChild child, string title, int occurrence, bool confirm)
    { var dialog = await CheckpointDialog(child, title, occurrence); await child.Send(new { type = "extension_ui_response", id = dialog.Value.GetProperty("id").GetString(), confirmed = confirm }); }
    private static async Task ReplyReplacementResume(RpcChild child, int occurrence, long generation, string expectedPath, string pendingRequestId)
    {
        // session_start resume is an awaited postcommit observation, distinct from the pre-switch veto dialog.
        var dialog = await CheckpointDialog(child, "Resume checkpoint session", occurrence);
        var records = child.Records;
        var committed = records.TakeWhile(row => row != dialog).Where(row => Type(row) == "session_switched").ToArray();
        Equal(generation - 1, (long)committed.Length);
        Equal(generation, committed[^1].Value.GetProperty("generation").GetInt64());
        Equal(expectedPath, committed[^1].Value.GetProperty("sessionFile").GetString());
        Equal("Continue with session " + committed[^1].Value.GetProperty("sessionId").GetString() + "?", dialog.Value.GetProperty("message").GetString());
        Check(!records.Any(row => Type(row) == "response" && row.Value.TryGetProperty("id", out var id) && id.GetString() == pendingRequestId),
            "Original operation settled before its committed session_start dialog reply.");
        await child.Send(new { type = "extension_ui_response", id = dialog.Value.GetProperty("id").GetString(), confirmed = true });
    }
    private sealed record CheckpointTargetInfo(string Path, string Leaf, string Opaque);
    private static async Task<CheckpointTargetInfo> CheckpointTarget(Files files, string? baseLeafOverride = null)
    {
        var original = await Complete(files); var header = JsonNode.Parse(original.Header!.WireBody.ToString())!; header["id"] = "checkpoint-B";
        var codec = new SessionEntryCodec(); var entries = original.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToImmutableArray();
        var baseLeaf = baseLeafOverride ?? Context(original).LeafId; var path = files.In("B.jsonl");
        SessionEntry State(string id, string? parent, string phase) => codec.Parse(JsonSerializer.Serialize(new { type = "custom", id,
            parentId = parent, timestamp = "2026-10-02T00:00:00.000Z", customType = "pisharp.extension-state",
            data = new { extensionId = "sample.checkpoint", entryKind = "checkpoint", schemaVersion = 1, data = new { phase } } }));
        var opaque = codec.Parse(JsonSerializer.Serialize(new { type = "future_checkpoint", id = "b-opaque", parentId = "b-main",
            timestamp = "2026-10-02T00:00:00.000Z", future = new { sequence = new object?[] { 2, 1, null }, scale = 1.0 } }));
        await using var store = await SessionLogStore.CreateNewAsync(path, codec.Parse(header.ToJsonString()));
        await store.AppendAsync(entries.Add(State("b-main", baseLeaf, "seed-main")).Add(opaque).Add(State("b-sibling", baseLeaf, "seed-sibling")));
        return new(path, opaque.Id, opaque.WireBody.ToString());
    }
    private static IEnumerable<JsonElement> CheckpointEntries(SessionLogReadResult log) => log.ValidatedPrefix.Skip(1).Select(row => row.Entry.WireBody.Value)
        .Where(entry => entry.TryGetProperty("customType", out var kind) && kind.GetString() == "pisharp.extension-state")
        .Select(entry => entry.GetProperty("data"));
    private static async Task VerifyCheckpointFiles(Files files, CheckpointTargetInfo target, byte[] beforeA, byte[] beforeB)
    {
        var a = await Complete(files); await using var stream = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var b = await new SessionLogReader().ReadAsync(stream); Check(b.SourceComplete && b.Status == SessionLogReadStatus.Complete, "Independent B reopen was incomplete.");
        Check(a.OriginalBytes.AsSpan().StartsWith(beforeA) && b.OriginalBytes.AsSpan().StartsWith(beforeB), "Checkpoint replacement rewrote a durable prefix.");
        var aEntries = CheckpointEntries(a).ToArray(); var bEntries = CheckpointEntries(b).ToArray();
        Equal(2, aEntries.Length); Equal(3, bEntries.Length);
        Equal("before-switch", aEntries[0].GetProperty("data").GetProperty("phase").GetString());
        Equal("after-return", aEntries[1].GetProperty("data").GetProperty("phase").GetString());
        Equal(3L, aEntries[1].GetProperty("data").GetProperty("generation").GetInt64());
        Equal("after-switch", bEntries[^1].GetProperty("data").GetProperty("phase").GetString());
        Equal(2L, bEntries[^1].GetProperty("data").GetProperty("generation").GetInt64());
        Equal(1, bEntries[^1].GetProperty("data").GetProperty("restored").GetInt32());
        var selected = new SessionContextProjector().Project(b.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToImmutableArray(), b.ValidatedPrefix[^1].Entry.Id);
        Check(selected.Ancestry.Any(entry => entry.Id == target.Leaf) && !selected.Ancestry.Any(entry => entry.Id == "b-sibling"), "Replacement selected the wrong B ancestry.");
        Equal(target.Opaque, selected.Ancestry.Single(entry => entry.Id == target.Leaf).WireBody.ToString());
        Check(!aEntries.Concat(bEntries).Any(entry => entry.GetProperty("data").GetProperty("phase").GetString() == "stale"), "Stale callback left physical bytes.");
    }
}
