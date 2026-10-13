// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): the ops corpus and the reverse direction. Captured upstream evidence, like CompatCases.cs.
// - compat.ops.document-N: Fixtures/upstream-sessions/ops-corpus.json holds seeded edits that tools/DurableFixtures/ops-corpus.mjs made through
//   the pinned Chord tracker, with the operations it emitted and each resulting value. PiSharp's DeltaApply replays upstream's operations to
//   upstream's value; PiSharp's tracker, making the same edits, reaches the same value, emits the same operations, and its operations replay
//   on upstream's base to that value. Values compare by their JSON text, key order included.
// - compat.reverse.upstream-reads-pisharp-sessions: PiSharp writes every scenario on SQLite and JSONL, then tools/DurableFixtures/verify-reverse.mjs
//   has the pinned upstream open each session under Node and read records equal to PiSharp's own reading, resume PiSharp's interrupted
//   sessions to upstream's captured resumed records, and replay PiSharp's tracker operations. It runs when PISHARP_DURABLE_UPSTREAM names a
//   Pi checkout at the pinned commit with node_modules (the convention of PiSharp.DurableCli.Tests' interop case) and node is on PATH;
//   otherwise it is reported as SKIPPED_NO_UPSTREAM.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CompatOpsCases() => CompatTests.OpsCases();
}

internal static partial class CompatTests
{
    private static readonly Lazy<JsonObjectValue> Corpus = new(() => Load("ops-corpus.json"));

    internal static IEnumerable<(string, Func<Task>)> OpsCases()
    {
        var documents = (JsonArrayValue)Corpus.Value["documents"];
        for (var index = 0; index < documents.Count; index++)
        {
            var (document, number) = ((JsonObjectValue)documents[index], index);
            yield return Case($"compat.ops.document-{number}", () => { ReplayDocument(document, $"document {number}"); });
        }
        yield return Case("compat.reverse.upstream-reads-pisharp-sessions", ReverseAsync);
        // Last: the fixtures are exactly the captured files (no case wrote beside them, as a SQLite connection's -wal and -shm would).
        yield return Case("compat.manifest.fixtures-are-the-captured-files", () =>
        {
            var listed = ((JsonArrayValue)Load("manifest.json")["files"]).Cast<JsonObjectValue>()
                .Select(file => $"{S(file["path"])} {N(file["bytes"])}").Order(StringComparer.Ordinal);
            var present = Directory.GetFiles(Fixtures, "*", SearchOption.AllDirectories)
                .Select(path => (Name: Path.GetRelativePath(Fixtures, path).Replace('\\', '/'), Bytes: new FileInfo(path).Length))
                .Where(file => file.Name is not ("manifest.json" or ".gitattributes"))
                .Select(file => $"{file.Name} {file.Bytes}").Order(StringComparer.Ordinal);
            Equal(string.Join("\n", listed), string.Join("\n", present), "fixture files");
        });
    }

    /// <summary>Replays one corpus document; returns PiSharp's tracker operations for each batch.</summary>
    private static List<JsonValue> ReplayDocument(JsonObjectValue document, string what)
    {
        var current = document["base"];
        var tracker = Tracker.Track(current);
        var produced = new List<JsonValue>();
        var batches = (JsonArrayValue)document["batches"];
        for (var index = 0; index < batches.Count; index++)
        {
            var batch = (JsonObjectValue)batches[index];
            var label = $"{what} batch {index}";
            var expected = batch["value"].ToJson();
            var upstreamOps = ((JsonArrayValue)batch["ops"]).Select(Op.FromJson).ToList();
            Equal(expected, DeltaApply.Apply(current, upstreamOps).ToJson(), label + ": upstream's operations replayed by PiSharp");

            var change = tracker.BeginChange();
            foreach (var edit in ((JsonArrayValue)batch["edits"]).Cast<JsonArrayValue>()) Perform(change.State, edit);
            var prepared = change.Prepare();
            Equal(expected, prepared.Value.ToJson(), label + ": PiSharp's tracker value");
            var ops = JsonValue.Array(prepared.Ops.Select(op => (JsonValue)op.ToJson()));
            Equal(batch["ops"].ToJson(), ops.ToJson(), label + ": PiSharp's tracker operations");
            Equal(expected, DeltaApply.Apply(current, prepared.Ops).ToJson(), label + ": PiSharp's operations replayed on upstream's base");
            produced.Add(ops);
            tracker.Adopt(prepared);
            current = batch["value"];
        }
        return produced;
    }

    /// <summary>ops-corpus.mjs <c>perform</c> on a PiSharp draft.</summary>
    private static void Perform(Draft root, JsonArrayValue edit)
    {
        var node = root;
        foreach (var segment in (JsonArrayValue)edit[1])
            node = segment is JsonStringValue key ? node.AsObject().GetDraft(key.Value) : node.AsArray().GetDraft(Index(segment));
        switch (S(edit[0]))
        {
            case "set":
                if (edit[2] is JsonStringValue setKey) node.AsObject().Set(setKey.Value, edit[3]);
                else node.AsArray().Set(Index(edit[2]), edit[3]);
                break;
            case "delete": node.AsObject().Delete(S(edit[2])); break;
            case "append":
                if (edit[2] is JsonStringValue appendKey) node.AsObject().AppendString(appendKey.Value, S(edit[3]));
                else node.AsArray().AppendString(Index(edit[2]), S(edit[3]));
                break;
            case "trim":
                if (edit[2] is JsonStringValue trimKey) node.AsObject().Set(trimKey.Value, S(node.AsObject().Get(trimKey.Value))[Index(edit[3])..]);
                else node.AsArray().Set(Index(edit[2]), S(node.AsArray().Get(Index(edit[2])))[Index(edit[3])..]);
                break;
            case "splice": node.AsArray().Splice(Index(edit[2]), Index(edit[3]), (JsonArrayValue)edit[4]); break;
            case "move":
            {
                var array = node.AsArray();
                var item = array.Splice(Index(edit[2]), 1)[0];
                array.Splice(Index(edit[3]), 0, item);
                break;
            }
            case "reverse": node.AsArray().Reverse(); break;
            case "sort": node.AsArray().Sort(); break;
            case "length": node.AsArray().SetLength(Index(edit[2])); break;
            default: throw new InvalidOperationException("unknown edit " + edit.ToJson());
        }
    }

    private static int Index(JsonValue value) => (int)N(value);

    // ─── reverse ────────────────────────────────────────────────────────────────

    private static async Task ReverseAsync()
    {
        var upstream = Environment.GetEnvironmentVariable("PISHARP_DURABLE_UPSTREAM");
        if (string.IsNullOrEmpty(upstream)) throw new CaseSkippedException("PISHARP_DURABLE_UPSTREAM unset");
        var root = Temp("compat-reverse");
        foreach (var scenario in CompatScenarios.All)
        {
            var dumps = new List<KeyValuePair<string, JsonValue>>();
            foreach (var backend in Backends)
            {
                var directory = Path.Combine(root, scenario.Name, backend);
                Directory.CreateDirectory(directory);
                await scenario.Run(() => OpenBackend(backend, directory));
                dumps.Add(KeyValuePair.Create(backend, (JsonValue)await Dump(await OpenBackend(backend, directory))));
            }
            File.WriteAllText(Path.Combine(root, scenario.Name, "records.json"), JsonValue.Object(dumps).ToJson());
        }
        var documents = (JsonArrayValue)Corpus.Value["documents"];
        File.WriteAllText(Path.Combine(root, "ops.json"), JsonValue.Array(documents.Select((document, index) =>
            (JsonValue)JsonValue.Array(ReplayDocument((JsonObjectValue)document, $"document {index}")))).ToJson());

        var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "--no-warnings", "--conditions=source", "--experimental-strip-types",
                     Path.Combine(RepositoryRoot(), "tools", "DurableFixtures", "verify-reverse.mjs"), upstream, root, Fixtures })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Console.Error.Write(await output);
        Check(process.ExitCode == 0, $"verify-reverse.mjs exited with {process.ExitCode}:\n{await output}\n{await errors}");
        Check((await output).Contains("verified ", StringComparison.Ordinal), "verify-reverse.mjs reported its checks");
    }
}

/// <summary>A case that could not run here (for example, an optional upstream checkout is not configured); reported as skipped.</summary>
internal sealed class CaseSkippedException(string reason) : Exception(reason);
