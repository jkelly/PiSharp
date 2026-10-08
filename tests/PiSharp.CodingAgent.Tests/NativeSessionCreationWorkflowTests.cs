using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static partial class NativeExtensionSessionCommandTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> CreationCases(string host, string cli)
    {
        _ = Cases(host, cli);
        yield return ("native session checkpoint lifecycle RPC new fork clone tree and selected branch use actual durable files", CreationRpc);
        yield return ("native session checkpoint lifecycle published SDK fresh contexts work in report print and JSON", CreationOneShot);
        yield return ("native session checkpoint lifecycle veto invalid input abort and EOF create no replacement files", CreationRollback);
        yield return ("native session checkpoint lifecycle postcommit abort and EOF join original callback and keep fresh file", CreationPostcommitCleanup);
        yield return ("native session checkpoint lifecycle cooked frontend restores fork draft and routes new clone tree", CreationChat);
    }
    private const string CreationUser = "creation-user";
    private const string CreationOpaque = "creation-opaque";
    private const string CreationText = "alpha\U0001F642beta\n";
    private static async Task SeedCreation(Files files)
    {
        var source = await Complete(files); var leaf = Context(source).LeafId;
        var codec = new SessionEntryCodec();
        SessionEntry Entry(string type, string id, string? parent, object extra)
        {
            var body = JsonSerializer.SerializeToNode(new { type, id, parentId = parent, timestamp = "2026-10-02T00:00:00.000Z" })!.AsObject();
            foreach (var pair in JsonSerializer.SerializeToNode(extra)!.AsObject()) body.Add(pair.Key, pair.Value?.DeepClone());
            return codec.Parse(body.ToJsonString());
        }
        // Preserve a numeric token whose value cannot be represented by double, including its spelling.
        var opaque = codec.Parse("{\"type\":\"future_lifecycle\",\"id\":\"creation-opaque\",\"parentId\":\"creation-user\",\"timestamp\":\"2026-10-02T00:00:00.000Z\",\"opaque\":{\"huge\":1.00e400,\"nil\":null}}");
        var user = Entry("message", CreationUser, leaf, new { message = new { role = "user", timestamp = 0,
            content = new object[] { new { type = "text", text = "alpha\U0001F642" }, new { type = "image", data = "YQ==", mimeType = "image/png" },
                new { type = "text", text = "beta\n" } } } });
        var sibling = Entry("message", "creation-sibling", leaf, new { message = new { role = "user", content = "sibling", timestamp = 1 } });
        var label = Entry("label", "creation-label", sibling.Id, new { targetId = CreationOpaque, label = "Global label" });
        await using var store = await SessionLogStore.OpenAsync(files.Session);
        await store.AppendAsync([user, opaque, sibling, label]);
    }
    private static async Task<SessionLogReadResult> ReadCreation(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var result = await new SessionLogReader().ReadAsync(stream);
        Check(result.Status == SessionLogReadStatus.Complete && result.SourceComplete, "Created file did not independently reopen as complete JSONL.");
        return result;
    }
    private static async Task CreationRpc()
    {
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            using var files = await Files.CreateAsync("checkpoint", creationCommands: true); await Create(files, api);
            await SeedCreation(files); await Script(files, Text(api, "unused provider turn"));
            var target = await CheckpointTarget(files, CreationOpaque); var sourceBytes = await File.ReadAllBytesAsync(files.Session);
            var targetBytes = await File.ReadAllBytesAsync(target.Path); var created = new List<string>();
            await using (var child = new RpcChild(files, api))
            {
                await ReplyCheckpoint(child, "Resume checkpoint session", 1, true);
                await child.Send(new { id = "selected-target", type = "switch_session", sessionPath = target.Path, leafId = target.Leaf });
                await ReplyCheckpoint(child, "Switch checkpoint session", 1, true);
                await ReplyReplacementResume(child, 2, 2, target.Path, "selected-target");
                Response(await child.Response("selected-target"));
                await child.Send(new { id = "tree", type = "get_tree" }); var tree = Response(await child.Response("tree")).GetProperty("data");
                Equal(target.Leaf, tree.GetProperty("leafId").GetString());
                Check(tree.GetRawText().Contains("\"label\":\"Global label\"", StringComparison.Ordinal) &&
                    tree.GetRawText().Contains("\"labelTimestamp\":", StringComparison.Ordinal) && tree.GetRawText().Contains("1.00e400", StringComparison.Ordinal),
                    "Tree omitted global labels, timestamps or opaque numeric spelling.");
                await child.Send(new { id = "fork-messages", type = "get_fork_messages" });
                var messages = Response(await child.Response("fork-messages")).GetProperty("data").GetProperty("messages");
                Equal(CreationText, messages.EnumerateArray().Single(row => row.GetProperty("entryId").GetString() == CreationUser).GetProperty("text").GetString());
                await child.Send(new { id = "clone", type = "clone" }); await ReplyCheckpoint(child, "Create checkpoint session", 1, true);
                var clone = Response(await child.Response("clone")).GetProperty("data"); var clonePath = clone.GetProperty("sessionFile").GetString()!; created.Add(clonePath);
                var cloneLog = await ReadCreation(clonePath); var cloneEntries = cloneLog.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToArray();
                Check(cloneEntries.Any(entry => entry.Id == target.Leaf) && !cloneEntries.Any(entry => entry.Id == "b-sibling") &&
                    !cloneEntries.Any(entry => entry.Id == "creation-sibling"), "Clone used the physical latest sibling instead of selected ancestry.");
                Equal(target.Path, cloneLog.Header!.WireBody.Value.GetProperty("parentSession").GetString());
                Equal(3L, clone.GetProperty("generation").GetInt64());
                await child.Send(new { id = "fork", type = "fork", entryId = CreationUser }); await ReplyCheckpoint(child, "Create checkpoint session", 2, true);
                var fork = Response(await child.Response("fork")).GetProperty("data"); Equal(CreationText, fork.GetProperty("text").GetString());
                var forkPath = fork.GetProperty("sessionFile").GetString()!; created.Add(forkPath);
                var forkLog = await ReadCreation(forkPath);
                Check(!forkLog.ValidatedPrefix.Any(row => row.Entry.Id is CreationUser or CreationOpaque), "Fork before retained selected user/descendant.");
                Equal(clonePath, forkLog.Header!.WireBody.Value.GetProperty("parentSession").GetString());
                await child.Send(new { id = "new", type = "new_session", parentSession = target.Path }); await ReplyCheckpoint(child, "Create checkpoint session", 3, true);
                var fresh = Response(await child.Response("new")).GetProperty("data"); var freshPath = fresh.GetProperty("sessionFile").GetString()!; created.Add(freshPath);
                var freshLog = await ReadCreation(freshPath); Equal(1, freshLog.ValidatedPrefix.Length);
                Equal(target.Path, freshLog.Header!.WireBody.Value.GetProperty("parentSession").GetString());
                await child.Send(new { id = "fresh-state", type = "get_state" });
                Equal(freshPath, Response(await child.Response("fresh-state")).GetProperty("data").GetProperty("sessionFile").GetString());
                Check(!child.Records.Any(row => Type(row) is "agent_start" or "tool_execution_start"), "Lifecycle invoked a provider or tool.");
                Clean(await child.Finish());
            }
            Check((await File.ReadAllBytesAsync(files.Session)).SequenceEqual(sourceBytes) && (await File.ReadAllBytesAsync(target.Path)).SequenceEqual(targetBytes),
                "RPC creation changed source or existing target bytes.");
            foreach (var path in created) { await using var writer = await SessionLogStore.OpenAsync(path); }
            Equal(0, Directory.GetDirectories(files.Snapshots).Length);
            CheckpointProcessEvidence.Add(new { workflow = "durable-native-lifecycle-rpc", api, naturalExit = 0, outputReadersJoined = true,
                freshFiles = created.Count, sourcePrefixesUnchanged = true, noProviderTurns = true });
        }
    }
    private static async Task CreationOneShot()
    {
        const string api = "openai-responses";
        foreach (var mode in new[] { "report", "print", "json" })
        foreach (var kind in new[] { "new", "before", "at", "clone" })
        {
            using var files = await Files.CreateAsync("checkpoint", true); await Create(files, api); await SeedCreation(files);
            var before = await File.ReadAllBytesAsync(files.Session); await Script(files, Text(api, "unused provider turn"));
            var run = await Command(files, "resume", api, "/checkpoint-create " + JsonSerializer.Serialize(new
            { kind, entryId = kind is "before" or "at" ? kind == "before" ? CreationUser : CreationOpaque : null, confirm = false }), "--output", mode);
            Check(run.ExitCode == 0 && run.Error.Length == 0, "Published creation " + kind + "/" + mode + " failed; created files=" +
                Directory.GetFiles(files.Root, "*.jsonl").Length + ": " + Describe(run));
            var paths = Directory.GetFiles(files.Root, "*.jsonl").Where(path => path != files.Session).ToArray(); Equal(1, paths.Length);
            var path = paths[0]; var log = await ReadCreation(path); var entries = log.ValidatedPrefix.Skip(1).Select(row => row.Entry).ToArray();
            Equal("after-create", entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("phase").GetString());
            Equal(2L, entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("generation").GetInt64());
            Check(!entries.Any(entry => entry.WireBody.ToString().Contains("\"phase\":\"stale\"", StringComparison.Ordinal)), "Stale context wrote physical bytes.");
            if (kind == "at")
            { Check(entries.Any(entry => entry.Id == CreationOpaque) && !entries.Any(entry => entry.Id == "creation-sibling"), "Fork at included wrong ancestry.");
                Equal("1.00e400", entries.Single(entry => entry.Id == CreationOpaque).WireBody.Value.GetProperty("opaque").GetProperty("huge").GetRawText()); }
            if (kind != "new") Equal(files.Session, log.Header!.WireBody.Value.GetProperty("parentSession").GetString());
            if (mode == "report")
            { var report = JsonData.Parse(Utf8.GetString(run.Output)).Value; Equal(path, report.GetProperty("sessionPath").GetString()); Equal(0, report.GetProperty("usedScriptTurns").GetInt32()); }
            else if (mode == "json") Equal(path, JsonLines(run.Output).Single(row => Type(row) == "session_switched").Value.GetProperty("sessionFile").GetString());
            Check((await File.ReadAllBytesAsync(files.Session)).SequenceEqual(before), "SDK creation mutated its retired source.");
            await using var reopened = await SessionLogStore.OpenAsync(path); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
            CheckpointProcessEvidence.Add(new { workflow = "published-sdk-lifecycle", mode, kind, naturalExit = 0, noProviderTurns = true, freshWriterReacquired = true });
        }
    }
    private static async Task CreationRollback()
    {
        const string api = "openai-responses";
        using var files = await Files.CreateAsync("checkpoint", true); await Create(files, api); await SeedCreation(files);
        await Script(files, Text(api, "unused provider turn")); var before = await File.ReadAllBytesAsync(files.Session);
        await using (var child = new RpcChild(files, api))
        {
            await ReplyCheckpoint(child, "Resume checkpoint session", 1, true);
            await child.Send(new { id = "invalid", type = "fork", entryId = CreationUser, position = "invalid" });
            Check(!(await child.Response("invalid")).Value.GetProperty("success").GetBoolean(), "Invalid position was accepted.");
            await child.Send(new { id = "veto", type = "new_session" }); await ReplyCheckpoint(child, "Create checkpoint session", 1, false);
            Check(Response(await child.Response("veto")).GetProperty("data").GetProperty("cancelled").GetBoolean(), "Creation veto failed.");
            await child.Send(new { id = "held", type = "new_session" }); await CheckpointDialog(child, "Create checkpoint session", 2);
            await child.Send(new { id = "abort", type = "abort" }); Response(await child.Response("abort"));
            Check(!(await child.Response("held")).Value.GetProperty("success").GetBoolean(), "Canceled creation became successful.");
            await child.Send(new { id = "state", type = "get_state" }); Equal(files.Session, Response(await child.Response("state")).GetProperty("data").GetProperty("sessionFile").GetString());
            Clean(await child.Finish());
        }
        Check((await File.ReadAllBytesAsync(files.Session)).SequenceEqual(before), "Pre-effect rollback changed source bytes.");
        await using (var eof = new RpcChild(files, api))
        { await ReplyCheckpoint(eof, "Resume checkpoint session", 1, true); await eof.Send(new { id = "eof", type = "new_session" });
            await CheckpointDialog(eof, "Create checkpoint session", 1); Clean(await eof.Finish()); }
        Equal(1, Directory.GetFiles(files.Root, "*.jsonl").Length); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }
    private static async Task CreationPostcommitCleanup()
    {
        const string api = "openai-responses";
        foreach (var eof in new[] { false, true })
        {
            using var files = await Files.CreateAsync("checkpoint", true); await Create(files, api); await Script(files, Text(api, "unused provider turn"));
            var source = await File.ReadAllBytesAsync(files.Session); string path;
            await using (var child = new RpcChild(files, api))
            {
                await ReplyCheckpoint(child, "Resume checkpoint session", 1, true);
                await child.Send(new { id = "create-held", type = "prompt", message = "/checkpoint-create {\"kind\":\"new\",\"confirm\":false,\"confirmCheckpoint\":true}" });
                await CheckpointDialog(child, "Save created checkpoint", 1);
                path = child.Records.Single(row => Type(row) == "session_switched").Value.GetProperty("sessionFile").GetString()!;
                if (!eof)
                {
                    await child.Send(new { id = "abort", type = "abort" }); Response(await child.Response("abort"));
                    var response = (await child.Response("create-held")).Value;
                    Check(!response.GetProperty("success").GetBoolean() && response.GetProperty("error").GetString()!.Contains("replacement committed", StringComparison.Ordinal),
                        "Postcommit cancellation lost truthful fresh attachment status.");
                    await child.Send(new { id = "state", type = "get_state" }); Equal(path, Response(await child.Response("state")).GetProperty("data").GetProperty("sessionFile").GetString());
                }
                Clean(await child.Finish());
            }
            Equal(1, (await ReadCreation(path)).ValidatedPrefix.Length);
            Check((await File.ReadAllBytesAsync(files.Session)).SequenceEqual(source), "Postcommit cleanup wrote the retired source.");
            await using var writer = await SessionLogStore.OpenAsync(path); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
        }
    }
    private static async Task CreationChat()
    {
        const string api = "openai-responses";
        using var files = await Files.CreateAsync("checkpoint", true); await Create(files, api); await SeedCreation(files); await Script(files, Text(api, "unused provider turn"));
        using var process = Process.Start(Start(files, Base("chat", files, api).Concat(["--offline-script", files.Script]).Concat(files.ExtensionArgs)))
            ?? throw new InvalidOperationException("Actual lifecycle cooked frontend did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var lines = new List<string>(); var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new object();
        var reading = Read(); var errors = ReadBytes(process.StandardError.BaseStream, deadline.Token);
        try
        {
            await Wait("[ui confirm] Resume checkpoint session", 1); await Send("yes"); await Wait("[history]", 1);
            await Send("/fork " + CreationUser); await Wait("[ui confirm] Create checkpoint session", 1); await Send("yes");
            await Wait("[fork draft]", 1); await Send("/show"); await Wait("[draft]", 1);
            Check(lines.Any(line => line.Contains("alpha", StringComparison.Ordinal)) && lines.Any(line => line.Contains("beta", StringComparison.Ordinal)), "Fork selected text was not restored to the cooked draft.");
            await Send("/clone"); await Wait("[ui confirm] Create checkpoint session", 2); await Send("yes"); await Wait("[accepted] clone", 1);
            await Send("/new"); await Wait("[ui confirm] Create checkpoint session", 3); await Send("yes"); await Wait("[accepted] new_session", 1);
            await Send("/tree"); await Wait("[tree]", 1); await Send("/quit"); process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token); await reading; Equal(0, process.ExitCode); Equal("", Utf8.GetString(await errors));
            Equal(4, Directory.GetFiles(files.Root, "*.jsonl").Length); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
            foreach (var path in Directory.GetFiles(files.Root, "*.jsonl")) { await using var writer = await SessionLogStore.OpenAsync(path); }
            CheckpointProcessEvidence.Add(new { workflow = "actual-cooked-lifecycle", naturalExit = process.ExitCode, outputReadersJoined = true,
                forkDraftRestored = true, freshFiles = 3 });
        }
        finally
        { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            foreach (var task in new Task[] { reading, errors }) try { await task; } catch (Exception) { } }
        async Task Send(string line) { await process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        async Task Read()
        {
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            { lock (gate) { lines.Add(line); changed.TrySetResult(); changed = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
            lock (gate) changed.TrySetResult();
        }
        async Task Wait(string marker, int count)
        {
            while (true)
            { Task wait; lock (gate) { if (lines.Count(line => line.Contains(marker, StringComparison.Ordinal)) >= count) return; wait = changed.Task; }
                if (reading.IsCompleted) { await reading; throw new InvalidOperationException("Lifecycle frontend ended before " + marker); }
                await wait.WaitAsync(deadline.Token); }
        }
    }
}
