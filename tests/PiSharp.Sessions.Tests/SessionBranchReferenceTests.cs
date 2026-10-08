using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionBranchReferenceTests
{
    private const string Family = "fixtures/pi-v0.99.1/session-branches-replayed";
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const int EvidenceByteLimit = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] CaseIds =
    [
        "empty-root", "two-sibling-branch-settings", "three-sibling-hidden-state", "multi-root-forest",
        "global-labels-and-names", "compaction-preserves-raw-history", "context-edit-restore-and-siblings", "metadata-only-three-roots"
    ];

    public static IEnumerable<(string Name, Func<Task> Run)> Cases() => CaseIds.Select(caseId =>
        ("session-branch-reference complete unchanged source tree history context and system replay " + caseId,
            (Func<Task>)(() => CompareCase(caseId))));

    private static async Task CompareCase(string caseId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, Family, "manifest.json"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Branch reference fixture root was not found.");
        // The evidence document bound is independent of product record/context bounds.
        var manifestBytes = await ReadPinned("manifest.json", "821d179e03b51632b8a7361bd73f68ff432ac865d2a6e28ea006e4620f69d534");
        var inputBytes = await ReadPinned("core.input.json", "e3b207d3ffc1f30ee7a6512eae0470688d4475f58accd059a2f19ab4b2517239");
        var captureBytes = await ReadPinned("capture.json", "9b2ac41db76ed54c1958d9faeeca5d26cf1c2245614e856b38483bf503bd83b0");
        var lockBytes = await ReadPinned("oracle.lock.json", "5aa52de48f2b6d04a797ec3d04448d26089d81e32d12f46021ac65620c42a16b");
        using var manifest = Parse(manifestBytes); using var input = Parse(inputBytes); using var capture = Parse(captureBytes); using var oracleLock = Parse(lockBytes);
        Equal(SourceSha, manifest.RootElement.GetProperty("sourceSha").GetString());
        Equal(SourceSha, input.RootElement.GetProperty("sourceSha").GetString());
        Equal(SourceSha, capture.RootElement.GetProperty("sourceSha").GetString());
        Equal(19_050_274, captureBytes.Length); Equal(8, manifest.RootElement.GetProperty("caseCount").GetInt32());
        Equal(8, capture.RootElement.GetProperty("cases").GetArrayLength());
        var pins = oracleLock.RootElement.GetProperty("environmentPins"); Equal(SourceSha, pins.GetProperty("sourceSha").GetString());
        Equal(2093, pins.GetProperty("sourceFingerprint").GetProperty("canonicalGit").GetProperty("files").GetInt32());
        Equal("2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3", pins.GetProperty("sourceFingerprint").GetProperty("canonicalGit").GetProperty("sha256").GetString());
        Equal(8, pins.GetProperty("dependencies").GetArrayLength()); Equal("v24.19.0", pins.GetProperty("runtime").GetProperty("version").GetString());
        Check(oracleLock.RootElement.GetProperty("sourceUnchanged").GetBoolean() && oracleLock.RootElement.GetProperty("dependenciesUnchanged").GetBoolean(), "Source/dependency receipt changed.");
        var checks = capture.RootElement.GetProperty("checks");
        foreach (var control in new[] { "wholeSessionManagerLoaded", "clocksAndRngUnmodified", "scratchWriteGuard", "networkAndProcessesDenied" })
            Check(checks.GetProperty(control).GetBoolean(), "Capture control changed: " + control);
        Equal(0, checks.GetProperty("ownedOpenDescriptors").GetInt32());
        var captured = capture.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("caseId").GetString() == caseId);
        var authored = input.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("caseId").GetString() == caseId);
        SameField(authored.GetProperty("header"), captured.GetProperty("inputHeader"), caseId + "/inputHeader");
        SameField(authored.GetProperty("entries"), captured.GetProperty("inputEntries"), caseId + "/inputEntries");
        foreach (var relation in captured.GetProperty("relations").EnumerateObject()) Check(relation.Value.GetBoolean(), "Source relation failed: " + relation.Name);
        var projector = new SessionHistoryProjector();
        var phases = captured.GetProperty("phases"); Equal(authored.GetProperty("operations").GetArrayLength() + 1, phases.GetArrayLength());
        foreach (var phase in phases.EnumerateArray())
        {
            var phaseId = phase.GetProperty("phaseId").GetString()!;
            var before = phase.GetProperty("physicalFileBefore"); var after = phase.GetProperty("physicalFileAfter");
            Equal(before.GetProperty("base64").GetString(), after.GetProperty("base64").GetString());
            var read = await ReadPhysical(after); var header = read.Header ?? throw new InvalidOperationException("Captured phase lost its header.");
            var entries = read.ValidatedPrefix.Where(record => !record.Entry.IsHeader).Select(record => record.Entry).ToImmutableArray();
            var originals = entries.Select(entry => entry.WireBody.ToString()).ToArray();
            var matrix = phase.GetProperty("matrix"); Equal(entries.Length + 1, matrix.GetArrayLength());
            var observed = new HashSet<string?>(StringComparer.Ordinal);
            foreach (var selected in matrix.EnumerateArray())
            {
                var leafId = NullableString(selected.GetProperty("leafId")); Check(observed.Add(leafId), "A captured selection was duplicated.");
                var history = projector.Project(entries, leafId);
                using var actual = Frame(header, history, leafId);
                CompleteFrame(selected, actual.RootElement, caseId + "/" + phaseId + "/" + (leafId ?? "<root>"));
                for (var index = 0; index < entries.Length; index++) Equal(originals[index], entries[index].WireBody.ToString());
            }
            Check(observed.Contains(null) && entries.All(entry => observed.Contains(entry.Id)), "Not every entry/root selection was compared.");
            Check(read.OriginalBytes.AsSpan().SequenceEqual(Convert.FromBase64String(after.GetProperty("base64").GetString()!)), "Native reader changed actual source bytes.");
        }
        var reopened = captured.GetProperty("reopened");
        Equal(reopened.GetProperty("physicalFileBefore").GetProperty("base64").GetString(), reopened.GetProperty("physicalFileAfter").GetProperty("base64").GetString());
        var finalRead = await ReadPhysical(reopened.GetProperty("physicalFileAfter"));
        var finalEntries = finalRead.ValidatedPrefix.Where(record => !record.Entry.IsHeader).Select(record => record.Entry).ToImmutableArray();
        var finalLeaf = NullableString(reopened.GetProperty("leafId")); Equal(finalEntries.IsEmpty ? null : finalEntries[^1].Id, finalLeaf);
        using var finalFrame = Frame(finalRead.Header!, projector.ProjectLatest(finalEntries), finalLeaf);
        foreach (var field in reopened.EnumerateObject().Where(property => property.Name is not ("ownUndefinedPaths" or "physicalFileBefore" or "physicalFileAfter")))
            SameField(field.Value, finalFrame.RootElement.GetProperty(field.Name), caseId + "/reopen/" + field.Name);
        AssertAbsent(finalFrame.RootElement, reopened.GetProperty("ownUndefinedPaths"), caseId + "/reopen");
        // Query-only differential controls must never update the immutable corpus.
        _ = await ReadPinned("capture.json", "9b2ac41db76ed54c1958d9faeeca5d26cf1c2245614e856b38483bf503bd83b0");

        async Task<byte[]> ReadPinned(string file, string expectedHash)
        {
            var path = Path.Combine(root, Family, file); var info = new FileInfo(path);
            Check(info.Length is > 0 and <= EvidenceByteLimit, "Reference evidence exceeds its separate read bound.");
            var bytes = await File.ReadAllBytesAsync(path); Equal(info.Length, (long)bytes.Length);
            Equal(expectedHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()); return bytes;
        }
    }

    private static async Task<SessionLogReadResult> ReadPhysical(JsonElement physical)
    {
        Check(physical.GetProperty("exists").GetBoolean(), "Actual source capture file disappeared.");
        var bytes = Convert.FromBase64String(physical.GetProperty("base64").GetString()!);
        Equal(physical.GetProperty("bytes").GetInt32(), bytes.Length);
        Check(bytes.AsSpan().SequenceEqual(Utf8.GetBytes(physical.GetProperty("utf8").GetString()!)), "Physical UTF8/base64 receipts differ.");
        Equal(physical.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        await using var stream = new MemoryStream(bytes, writable: false);
        var read = await new SessionLogReader().ReadAsync(stream, leaveOpen: true);
        Equal(SessionLogReadStatus.Complete, read.Status); Check(read.Diagnostics.IsEmpty, "Native source reader recovered an admitted source fixture.");
        using var records = Write(writer => Entries(writer, read.ValidatedPrefix.Select(record => record.Entry)));
        SameField(physical.GetProperty("records"), records.RootElement, "complete physical records"); return read;
    }

    private static JsonDocument Frame(SessionEntry header, SessionHistoryProjection history, string? leaf) => Write(writer =>
    {
        var tree = history.Tree; writer.WriteStartObject(); writer.WriteString("selectionId", leaf); writer.WriteString("leafId", leaf);
        writer.WritePropertyName("selectionReasons"); writer.WriteStartArray();
        writer.WriteStringValue(leaf is null ? "explicit-root" : tree.RootIds.Contains(leaf) ? "structural-root" : tree.StructuralLeafIds.Contains(leaf) ? "structural-leaf" : "middle"); writer.WriteEndArray();
        writer.WritePropertyName("header"); header.WireBody.Value.WriteTo(writer);
        writer.WritePropertyName("entries"); Entries(writer, history.FullHistory);
        writer.WritePropertyName("tree"); writer.WriteStartArray(); foreach (var root in tree.RootIds) Node(root); writer.WriteEndArray();
        writer.WritePropertyName("children"); writer.WriteStartArray(); Children(null, tree.RootIds.Select(id => tree.ById[id].Entry));
        foreach (var entry in history.FullHistory) Children(entry.Id, tree.GetChildren(entry.Id)); writer.WriteEndArray();
        writer.WritePropertyName("labels"); writer.WriteStartArray();
        foreach (var entry in history.FullHistory)
        {
            writer.WriteStartObject(); writer.WriteString("entryId", entry.Id); var label = tree.GetLabel(entry.Id);
            if (label is not null) writer.WriteString("label", label.Label); writer.WriteEndObject();
        }
        writer.WriteEndArray(); if (tree.SessionName is not null) writer.WriteString("sessionName", tree.SessionName);
        writer.WritePropertyName("branch"); Entries(writer, history.BranchHistory);
        if (leaf is not null) { writer.WritePropertyName("leafEntry"); tree.GetEntry(leaf)!.WireBody.Value.WriteTo(writer); }
        writer.WritePropertyName("projection"); Context(writer, history.Context, projection: true);
        writer.WritePropertyName("context"); Context(writer, history.Context, projection: false);
        writer.WritePropertyName("llmMessages"); Messages(writer, history.Context.LlmMessages);
        if (history.EffectiveSystemMessage is not null) { writer.WritePropertyName("effectiveSystemMessage"); history.EffectiveSystemMessage.WireBody.Value.WriteTo(writer); }
        writer.WritePropertyName("effectiveTools"); writer.WriteStartArray(); foreach (var tool in history.SystemState.Tools) tool.Value.WriteTo(writer); writer.WriteEndArray();
        writer.WriteString("systemPrompt", history.SystemState.Prompt); writer.WriteEndObject();

        void Node(string id)
        {
            var node = tree.ById[id]; writer.WriteStartObject(); writer.WritePropertyName("entry"); node.Entry.WireBody.Value.WriteTo(writer);
            writer.WritePropertyName("children"); writer.WriteStartArray(); var ordered = tree.GetChronologicalChildren(id);
            Equal(SessionTreeOrderStatus.Completed, ordered.Status); foreach (var child in ordered.Entries) Node(child.Id); writer.WriteEndArray();
            if (node.ResolvedLabel is not null) { writer.WriteString("label", node.ResolvedLabel.Label); writer.WriteString("labelTimestamp", node.ResolvedLabel.Timestamp); }
            writer.WriteEndObject();
        }
        void Children(string? parent, IEnumerable<SessionEntry> children)
        { writer.WriteStartObject(); writer.WriteString("parentId", parent); writer.WritePropertyName("entries"); Entries(writer, children); writer.WriteEndObject(); }
    });

    private static void Context(Utf8JsonWriter writer, SessionContextProjection native, bool projection)
    {
        writer.WriteStartObject();
        if (projection)
        {
            writer.WritePropertyName("entries"); writer.WriteStartArray();
            foreach (var entry in native.ContextEntries)
            { writer.WriteStartObject(); writer.WritePropertyName("sourceEntry"); entry.SourceEntry.WireBody.Value.WriteTo(writer); writer.WritePropertyName("messages"); Messages(writer, entry.Messages); writer.WriteEndObject(); }
            writer.WriteEndArray();
        }
        writer.WritePropertyName("messages"); Messages(writer, native.Messages); writer.WriteString("thinkingLevel", native.ThinkingLevel);
        writer.WritePropertyName("model");
        if (native.Model is null) writer.WriteNullValue();
        else { writer.WriteStartObject(); writer.WriteString("provider", native.Model.Provider); writer.WriteString("modelId", native.Model.ModelId); writer.WriteEndObject(); }
        writer.WriteEndObject();
    }
    private static void Entries(Utf8JsonWriter writer, IEnumerable<SessionEntry> entries)
    { writer.WriteStartArray(); foreach (var entry in entries) entry.WireBody.Value.WriteTo(writer); writer.WriteEndArray(); }
    private static void Messages(Utf8JsonWriter writer, IEnumerable<TranscriptEntry> messages)
    { writer.WriteStartArray(); foreach (var message in messages) message.WireBody.Value.WriteTo(writer); writer.WriteEndArray(); }
    private static JsonDocument Write(Action<Utf8JsonWriter> write)
    { using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) { write(writer); writer.Flush(); } return Parse(output.ToArray()); }
    private static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    private static void CompleteFrame(JsonElement expected, JsonElement actual, string where)
    {
        var expectedFields = expected.EnumerateObject().Where(property => property.Name != "ownUndefinedPaths").ToArray();
        Equal(expectedFields.Length, actual.EnumerateObject().Count());
        foreach (var field in expectedFields)
        { Check(actual.TryGetProperty(field.Name, out var value), "Native field is absent: " + where + "/" + field.Name); SameField(field.Value, value, where + "/" + field.Name); }
        AssertAbsent(actual, expected.GetProperty("ownUndefinedPaths"), where);
    }
    private static void AssertAbsent(JsonElement actual, JsonElement paths, string where)
    {
        foreach (var value in paths.EnumerateArray())
        {
            var segments = value.GetString()![1..].Split('/'); var current = actual;
            for (var index = 0; index < segments.Length - 1; index++)
            { var segment = Decode(segments[index]); current = current.ValueKind == JsonValueKind.Array ? current[int.Parse(segment, CultureInfo.InvariantCulture)] : current.GetProperty(segment); }
            Check(current.ValueKind == JsonValueKind.Object && !current.TryGetProperty(Decode(segments[^1]), out _), "Own undefined must remain absent: " + where + value.GetString());
        }
        static string Decode(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
    private static void SameField(JsonElement expected, JsonElement actual, string where) => Check(Same(expected, actual), "Complete source/native field differs: " + where);
    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = new Dictionary<string, JsonElement>(StringComparer.Ordinal); var b = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in left.EnumerateObject()) if (!a.TryAdd(property.Name, property.Value)) return false;
            foreach (var property in right.EnumerateObject()) if (!b.TryAdd(property.Name, property.Value)) return false;
            return a.Count == b.Count && a.All(property => b.TryGetValue(property.Key, out var value) && Same(property.Value, value));
        }
        return left.ValueKind switch
        {
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            _ => left.GetRawText() == right.GetRawText()
        };
    }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ: " + expected + " / " + actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
