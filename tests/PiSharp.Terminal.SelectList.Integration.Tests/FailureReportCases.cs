using System.Text.Json;

internal static class FailureReportCases
{
    internal static async Task<object> RunAsync(string root, ConsumerEvidence parent)
    {
        if (File.Exists(root) || Directory.Exists(root)) throw new IOException("Fresh control directory required");
        Directory.CreateDirectory(root); var observations = new List<object>();
        var pair = new[] { new ConsumerCasePlan("fault-control", "first"), new ConsumerCasePlan("fault-control", "second") };
        async Task Check(string id, Func<ConsumerEvidence, Task> body, Func<JsonElement, bool> expect,
            bool admissionRequired = false, int expectedExit = 1)
        {
            var report = Path.Combine(root, id + ".json"); using var errors = new StringWriter();
            var exit = await ConsumerExecution.RunAsync(report, pair, new { faultControlOnly = true, id }, body, errors, admissionRequired);
            using var document = JsonDocument.Parse(File.ReadAllText(report)); var result = document.RootElement;
            var lines = File.ReadAllLines(report + ".journal.jsonl"); var sequential = true;
            for (var at = 0; at < lines.Length; at++)
            { using var line = JsonDocument.Parse(lines[at]); sequential &= line.RootElement.GetProperty("sequence").GetInt64() == at + 1; }
            var row = new { id, passed = exit == expectedExit && expect(result) && sequential && lines.Length > 0,
                exit, result = result.Clone(), journalEntries = lines.Length, errors = errors.ToString() };
            observations.Add(row); parent.Observe("nested-report-fault-control", row);
        }
        static bool Nonpass(JsonElement r) => !r.GetProperty("passed").GetBoolean() && r.GetProperty("incomplete").GetBoolean();
        await Check("after-one-completed-then-throw", evidence =>
        {
            evidence.Begin("fault-control", "first"); evidence.Complete(new { passed = true, marker = "completed retained" });
            evidence.Begin("fault-control", "second"); evidence.Observe("partial-marker", new { actual = 7 });
            throw new IOException("authored report fault marker");
        }, r => Nonpass(r) && r.GetProperty("completedCases").GetInt32() == 1 &&
            r.GetProperty("incompleteCases")[0].GetProperty("observations").GetArrayLength() == 1 &&
            r.GetProperty("exception").GetProperty("Type").GetString() == typeof(IOException).FullName);
        await Check("abort-before-first-case", _ => throw new InvalidDataException("admission marker"),
            r => Nonpass(r) && r.GetProperty("unexecutedCases").GetArrayLength() == 2, admissionRequired: true);
        await Check("missing-admission-cannot-pass", evidence =>
        {
            foreach (var item in pair) { evidence.Begin(item.Category, item.Id); evidence.Complete(new { passed = true }); }
            return Task.CompletedTask;
        }, r => Nonpass(r) && r.GetProperty("completedCases").GetInt32() == 2 && !r.GetProperty("admissionSucceeded").GetBoolean(), admissionRequired: true);
        var durable = false;
        await Check("durable-completion-before-final", evidence =>
        {
            evidence.Begin("fault-control", "first"); evidence.Complete(new { passed = true, marker = "durable marker" });
            durable = !File.Exists(Path.Combine(root, "durable-completion-before-final.json")) && ReadActiveJournal(evidence.JournalPath).Contains("durable marker", StringComparison.Ordinal);
            evidence.Begin("fault-control", "second"); evidence.Complete(new { passed = true }); return Task.CompletedTask;
        }, r => durable && r.GetProperty("passed").GetBoolean() && !r.GetProperty("incomplete").GetBoolean(), expectedExit: 0);
        foreach (var journal in new[] { false, true })
        {
            var id = journal ? "journal-collision" : "report-collision"; var report = Path.Combine(root, id + ".json");
            var existing = journal ? report + ".journal.jsonl" : report; const string original = "prior bytes retained";
            File.WriteAllText(existing, original); var called = false; using var errors = new StringWriter();
            var exit = await ConsumerExecution.RunAsync(report, pair, new { faultControlOnly = true }, _ =>
            { called = true; return Task.CompletedTask; }, errors, admissionRequired: false);
            var row = new { id, passed = exit == 1 && !called && File.ReadAllText(existing) == original, exit, called, errors = errors.ToString() };
            observations.Add(row); parent.Observe("nested-report-collision", row);
        }
        return new { passed = observations.All(row => JsonSerializer.SerializeToElement(row).GetProperty("passed").GetBoolean()),
            nestedFaultControls = observations.Count, observations };
    }
    private static string ReadActiveJournal(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }
}
