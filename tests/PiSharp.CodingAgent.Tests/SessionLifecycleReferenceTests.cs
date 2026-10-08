using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionLifecycleReferenceTests
{
    private const string Source = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private static readonly string[] Ids = ["memory-zero-files", "lazy-setup-no-file", "lazy-user-materializes",
        "lazy-assistant-materializes", "in-file-branch-keeps-siblings", "fork-selected-fresh-parent",
        "setup-only-fork-remains-lazy", "open-reload-hidden-state"];
    public static (string Name, Func<Task> Run)[] Cases(string cliDll)
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cliDll)!, "../../../../.."));
        return Ids.Select(id => ("session-lifecycle-reference unchanged whole SessionManager " + id,
            (Func<Task>)(() => RunCase(repo, id)))).ToArray();
    }
    private static async Task RunCase(string repo, string id)
    {
        var directory = Path.Combine(repo, "fixtures/pi-v0.99.1/session-lifecycle");
        var capturePath = Path.Combine(directory, "capture.json");
        using var lockDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        var locked = lockDocument.RootElement;
        Check(locked.GetProperty("sourceSha").GetString() == Source && locked.GetProperty("caseCount").GetInt32() == 8,
            "Genuine lifecycle fixture pin changed.");
        var bytes = await File.ReadAllBytesAsync(capturePath);
        Check(bytes.Length <= 2 * 1024 * 1024 && bytes.Length == locked.GetProperty("captureBytes").GetInt32() &&
            Convert.ToHexStringLower(SHA256.HashData(bytes)) == locked.GetProperty("captureSha256").GetString(), "Raw lifecycle capture bytes changed.");
        using var document = JsonDocument.Parse(bytes); var capture = document.RootElement;
        Check(capture.GetProperty("sourceSha").GetString() == Source && capture.GetProperty("checks").GetProperty("wholeSessionManagerLoaded").GetBoolean(), "Fixture is not a whole unchanged source capture.");
        var cases = capture.GetProperty("cases").EnumerateArray().ToArray();
        Check(cases.Select(test => test.GetProperty("caseId").GetString()).SequenceEqual(Ids), "Source lifecycle schedule inventory changed.");
        var test = cases.Single(test => test.GetProperty("caseId").GetString() == id);
        var checkpoints = test.GetProperty("rawObservations").GetProperty("checkpoints").EnumerateArray().ToArray();
        var contracts = test.GetProperty("contract").GetProperty("checkpoints").EnumerateArray().ToArray();
        Check(checkpoints.Length == contracts.Length && checkpoints.Length > 0, "Raw source checkpoints were omitted.");
        foreach (var relation in test.GetProperty("contract").GetProperty("relations").EnumerateObject())
            Check(relation.Value.ValueKind == JsonValueKind.True, "Actual source relation was not established: " + relation.Name);
        using var files = new Files();
        for (var index = 0; index < checkpoints.Length; index++)
            await ReplayCheckpoint(files.Root, id, index, checkpoints[index], contracts[index]);
        CompareSourceForks(checkpoints);
        Console.WriteLine($"SOURCE-LIFECYCLE {id}: {checkpoints.Length} complete raw checkpoints replayed; source fixture {locked.GetProperty("captureSha256").GetString()}");
    }
    private static async Task ReplayCheckpoint(string root, string caseId, int ordinal, JsonElement checkpoint, JsonElement contract)
    {
        var manager = checkpoint.GetProperty("manager"); var codec = new SessionEntryCodec();
        var header = codec.Read(manager.GetProperty("header"));
        var entries = manager.GetProperty("entries").EnumerateArray().Select(codec.Read).ToImmutableArray();
        var leaf = manager.GetProperty("leafId").ValueKind == JsonValueKind.Null ? null : manager.GetProperty("leafId").GetString();
        var memory = caseId == "memory-zero-files";
        var namespacePath = Path.Combine(root, "checkpoint-" + ordinal);
        if (!memory) Directory.CreateDirectory(namespacePath);
        var path = Path.Combine(namespacePath, "native.jsonl");
        var backend = new SessionStorageBackend(namespacePath, memory ? SessionStorageMode.InMemory : SessionStorageMode.LazyLocal);
        var options = new SessionLogStoreOptions(StorageFactory: backend);
        await using (var store = await SessionLogStore.CreateNewAsync(path, header, options))
        {
            if (!entries.IsEmpty) await store.AppendAsync(entries);
            JsonSame(manager.GetProperty("header"), store.Snapshot.Header.WireBody.Value, "complete raw header");
            JsonSame(manager.GetProperty("entries"), JsonSerializer.SerializeToElement(store.Snapshot.Entries.Select(entry => entry.WireBody.Value)), "complete raw physical entries");
            Check(File.Exists(path) == contract.GetProperty("fileExists").GetBoolean(), "Native materialization differs at " + caseId + "/" + checkpoint.GetProperty("name").GetString());
            Check(store.Snapshot.IsMaterialized == File.Exists(path), "Native checkpoint claimed unobserved file durability.");
        }
        Check(backend.ActiveWriterCount == 0, "Replay checkpoint left an owned writer.");
        var facade = new SessionLifecycleReadOnly(fileSystem: backend);
        var inspected = await facade.InspectAsync(path, useLatestLeaf: false, selectedLeafId: leaf);
        Check(inspected.Status == SessionLifecycleInspectionStatus.Available && inspected.View is not null, "Actual source checkpoint could not be read through shared native lifecycle.");
        var view = inspected.View!;
        JsonSame(manager.GetProperty("branch"), JsonSerializer.SerializeToElement(view.BranchEntries.Select(entry => entry.WireBody.Value)), "complete selected raw ancestry");
        JsonSame(manager.GetProperty("context").GetProperty("messages"), JsonSerializer.SerializeToElement(view.Context.Messages.Select(message => message.WireBody.Value)), "complete runtime messages");
        JsonSame(manager.GetProperty("llmMessages"), JsonSerializer.SerializeToElement(view.Context.LlmMessages.Select(message => message.WireBody.Value)), "complete model messages");
        Check(view.Context.ThinkingLevel == contract.GetProperty("thinkingLevel").GetString(), "Selected thinking setting differs.");
        if (manager.GetProperty("context").TryGetProperty("model", out var model) && model.ValueKind != JsonValueKind.Null)
            Check(view.Context.Model?.Provider == model.GetProperty("provider").GetString() && view.Context.Model?.ModelId == model.GetProperty("modelId").GetString(), "Selected model setting differs.");
        else Check(view.Context.Model is null, "Empty source context acquired a model setting.");
        Check(view.Entries.Length == contract.GetProperty("entryCount").GetInt32() && view.BranchEntries.Length == contract.GetProperty("branchCount").GetInt32(), "Source entry counts differ.");
        Check(view.SelectedLeafId == leaf && view.SessionId == manager.GetProperty("sessionId").GetString(), "Source identity or selected leaf changed.");
        if (File.Exists(path))
        {
            var actual = await new SessionLogReader().ReadFileAsync(path);
            Check(actual.Status == SessionLogReadStatus.Complete && actual.ValidatedPrefix.Length == contract.GetProperty("physicalRecordCount").GetInt32(), "Actual native file omitted a checkpoint record.");
            JsonSame(checkpoint.GetProperty("physicalFile").GetProperty("records"), JsonSerializer.SerializeToElement(actual.ValidatedPrefix.Select(record => record.Entry.WireBody.Value)), "complete physical file records");
        }
        else if (memory) Check(!Directory.Exists(namespacePath), "Memory source replay created its namespace.");
        // Selected export preserves source IDs/header and complete retained data rather than taking clone identity.
        var exportedPath = Path.Combine(namespacePath, "selected.jsonl");
        var selected = await facade.ExportSelectedBranchAsync(new(path, exportedPath, UseLatestLeaf: false, SelectedLeafId: leaf));
        Check(selected.Copy.Published && selected.OmittedFields == 0 && selected.OmittedRecords == entries.Length - view.BranchEntries.Length, "Selected source export lost its exclusion receipt.");
        var imported = await facade.InspectAsync(exportedPath);
        Check(imported.View is not null && imported.View.SessionId == view.SessionId, "Selected export invented a fresh identity.");
        JsonSame(manager.GetProperty("branch"), JsonSerializer.SerializeToElement(imported.View!.Entries.Select(entry => entry.WireBody.Value)), "all exported retained source records");
        JsonSame(manager.GetProperty("llmMessages"), JsonSerializer.SerializeToElement(imported.View.Context.LlmMessages.Select(message => message.WireBody.Value)), "exported model context");
        Check(backend.ActiveWriterCount == 0, "Source replay export left an owned writer.");
    }
    private static void CompareSourceForks(JsonElement[] checkpoints)
    {
        var codec = new SessionEntryCodec();
        for (var index = 1; index < checkpoints.Length; index++)
        {
            var name = checkpoints[index].GetProperty("name").GetString();
            if (name is not ("forked-left" or "setup-only-fork")) continue;
            var before = checkpoints[index - 1].GetProperty("manager"); var after = checkpoints[index].GetProperty("manager");
            var header = after.GetProperty("header"); var branch = after.GetProperty("branch").EnumerateArray().ToArray();
            Check(branch.Length > 0, "Actual source fork has no selected entry.");
            var plan = new SessionBranchPlanner().Fork(new(codec.Read(before.GetProperty("header")),
                before.GetProperty("entries").EnumerateArray().Select(codec.Read).ToImmutableArray(), branch[^1].GetProperty("id").GetString()!,
                SessionForkPosition.At, header.GetProperty("id").GetString()!, header.GetProperty("timestamp").GetString()!,
                header.TryGetProperty("parentSession", out var parent) ? parent.GetString() : null));
            JsonSame(header, plan.Header.WireBody.Value, "complete genuine fork header and parent link");
            JsonSame(after.GetProperty("entries"), JsonSerializer.SerializeToElement(plan.Entries.Select(entry => entry.WireBody.Value)), "complete genuine fork entries and retained IDs");
        }
    }
    private static void JsonSame(JsonElement expected, JsonElement actual, string subject)
    {
        if (!JsonElement.DeepEquals(expected, actual)) throw new InvalidOperationException("Native/source mismatch for " + subject + "\nSOURCE " + expected.GetRawText() + "\nNATIVE " + actual.GetRawText());
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-session-reference-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
