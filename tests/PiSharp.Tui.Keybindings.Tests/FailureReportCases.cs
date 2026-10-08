using System.Text.Json;

internal static class FailureReportCases
{
    internal static object Run(JsonElement fixture, string root, ConsumerEvidence parentEvidence)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Fresh failure-control directory required");
        Directory.CreateDirectory(root);
        var rows = new List<object>();
        void Record(object row) { rows.Add(row); parentEvidence.Observe("failure-report-control", row); }
        var pair = new[] { new ConsumerCasePlan("control", "first"), new ConsumerCasePlan("control", "second") };

        void Check(string id, ConsumerCasePlan[] plan, Action<ConsumerEvidence, string> body,
            Func<JsonElement, bool> expected, bool admissionRequired = false, int expectedExit = 1)
        {
            var report = Path.Combine(root, id + ".json"); using var error = new StringWriter();
            var exit = ConsumerExecution.Run(report, plan, new { faultControlOnly = true, id,
                nativeQualification = false }, evidence => body(evidence, report), error, admissionRequired);
            using var result = JsonDocument.Parse(File.ReadAllText(report));
            var lines = File.ReadAllLines(report + ".journal.jsonl");
            var sequential = true;
            for (var i = 0; i < lines.Length; i++)
            {
                using var entry = JsonDocument.Parse(lines[i]);
                sequential &= entry.RootElement.GetProperty("sequence").GetInt64() == i + 1;
            }
            Record(new { id, passed = exit == expectedExit && expected(result.RootElement) && sequential && lines.Length > 0,
                expectedExit, actualExit = exit, report, journal = report + ".journal.jsonl",
                reportSha256 = EvidenceAdmission.Hash(report), journalSha256 = EvidenceAdmission.Hash(report + ".journal.jsonl"),
                sequential, stderr = error.ToString() });
        }

        bool Nonpassing(JsonElement result) => !result.GetProperty("passed").GetBoolean() && result.GetProperty("incomplete").GetBoolean();
        Check("admission-pin-exception", ConsumerExecution.StandardPlan(), (evidence, report) =>
        {
            var manifest = Path.Combine(root, "negative-manifest.json"); var receipt = Path.Combine(root, "negative-receipt.json");
            File.WriteAllText(manifest, "{}"); File.WriteAllText(receipt, "{}");
            var args = new[] { report, root, manifest, new string('0', 64), receipt, EvidenceAdmission.Hash(receipt) };
            ConsumerExecution.ObserveInputs(evidence, args); evidence.Stage("admission");
            evidence.Admit(new EvidenceAdmission(args));
        }, result => Nonpassing(result) && !result.GetProperty("admissionSucceeded").GetBoolean() &&
            result.GetProperty("completedCases").GetInt32() == 0 && result.GetProperty("unexecutedCases").GetArrayLength() == ConsumerExecution.StandardPlan().Length &&
            result.GetProperty("exception").GetProperty("Type").GetString() == typeof(InvalidDataException).FullName &&
            result.GetProperty("observations").GetArrayLength() >= 5, admissionRequired: true);

        Check("assertion-failure-then-exception", pair.Append(new ConsumerCasePlan("control", "remaining")).ToArray(), (evidence, _) =>
        {
            evidence.Begin("control", "first"); evidence.Complete(new { passed = false, failures = new[] { "deliberate assertion failure" } });
            evidence.Begin("control", "second"); evidence.Observe("partial-event", new { marker = "retained before exception" });
            throw new InvalidOperationException("deliberate case exception");
        }, result => Nonpassing(result) && result.GetProperty("assertionFailures").GetInt32() == 1 &&
            result.GetProperty("completedCases").GetInt32() == 1 && result.GetProperty("incompleteCases").GetArrayLength() == 1 &&
            result.GetProperty("incompleteCases")[0].GetProperty("observations").GetArrayLength() == 1 &&
            result.GetProperty("unexecutedCases").GetArrayLength() == 1);

        Check("real-registry-matcher-exception", ConsumerExecution.StandardPlan(), (evidence, _) =>
            RegistryCases.Run(fixture, evidence, (_, _) => throw new InvalidOperationException("deliberate matcher exception")),
            result => Nonpassing(result) && result.GetProperty("completedCases").GetInt32() == 14 &&
                result.GetProperty("incompleteCases")[0].GetProperty("Id").GetString() == "matches-iterates-configured-alternatives" &&
                result.GetProperty("incompleteCases")[0].GetProperty("observations").GetArrayLength() >= 1 &&
                result.GetProperty("unexecutedCases").GetArrayLength() == ConsumerExecution.StandardPlan().Length - 15);

        Check("real-editor-dispatch-exception", ConsumerExecution.StandardPlan(), (evidence, _) =>
            EditorBoundaryCases.Run(fixture, evidence, _ => throw new InvalidOperationException("deliberate editor dispatch exception")),
            result => Nonpassing(result) && result.GetProperty("completedCases").GetInt32() == 0 &&
                result.GetProperty("incompleteCases")[0].GetProperty("Id").GetString() == "remap-left-alt-j" &&
                result.GetProperty("incompleteCases")[0].GetProperty("observations").GetArrayLength() >= 2 &&
                result.GetProperty("unexecutedCases").GetArrayLength() == ConsumerExecution.StandardPlan().Length - 1);

        Check("malformed-row-exception", pair, (evidence, _) =>
        {
            evidence.Begin("control", "first"); evidence.Complete(new { passed = true });
            evidence.Begin("control", "second"); evidence.Complete(new { marker = "missing passed field must remain a failure" });
        }, result => Nonpassing(result) && result.GetProperty("completedCases").GetInt32() == 1 &&
            result.GetProperty("incompleteCases")[0].GetProperty("result").GetProperty("marker").GetString() is not null &&
            result.GetProperty("exception").GetProperty("Type").GetString() == typeof(KeyNotFoundException).FullName);

        Check("missing-admission-cannot-pass", pair, (evidence, _) =>
        {
            foreach (var item in pair) { evidence.Begin(item.Category, item.Id); evidence.Complete(new { passed = true }); }
        }, result => Nonpassing(result) && result.GetProperty("completedCases").GetInt32() == 2 &&
            !result.GetProperty("admissionSucceeded").GetBoolean(), admissionRequired: true);

        var durableBeforeFinal = false;
        Check("successful-reporting-control", pair, (evidence, report) =>
        {
            evidence.Begin("control", "first"); evidence.Complete(new { passed = true, marker = "durable completed row" });
            durableBeforeFinal = !File.Exists(report) && ReadActiveJournal(evidence.JournalPath).Contains("durable completed row", StringComparison.Ordinal);
            evidence.Begin("control", "second"); evidence.Complete(new { passed = true });
        }, result => durableBeforeFinal && result.GetProperty("passed").GetBoolean() && !result.GetProperty("incomplete").GetBoolean() &&
            result.GetProperty("unexecutedCases").GetArrayLength() == 0 && result.GetProperty("exception").ValueKind == JsonValueKind.Null,
            expectedExit: 0);

        foreach (var journalCollision in new[] { false, true })
        {
            var id = journalCollision ? "existing-journal-preserved" : "existing-report-preserved";
            var report = Path.Combine(root, id + ".json"); var existing = journalCollision ? report + ".journal.jsonl" : report;
            const string prior = "prior evidence must remain byte-identical"; File.WriteAllText(existing, prior);
            using var error = new StringWriter(); var bodyCalled = false;
            var exit = ConsumerExecution.Run(report, pair, new { faultControlOnly = true, id }, _ => bodyCalled = true,
                error, admissionRequired: false);
            Record(new { id, passed = exit == 1 && !bodyCalled && File.ReadAllText(existing) == prior &&
                (journalCollision ? !File.Exists(report) : !File.Exists(report + ".journal.jsonl")),
                actualExit = exit, bodyCalled, existing, sha256 = EvidenceAdmission.Hash(existing), stderr = error.ToString() });
        }

        var raceReport = Path.Combine(root, "final-report-collision.json");
        const string racePrior = "concurrently reserved evidence must survive";
        using (var error = new StringWriter())
        {
            var exit = ConsumerExecution.Run(raceReport, pair, new { faultControlOnly = true }, evidence =>
            {
                foreach (var item in pair) { evidence.Begin(item.Category, item.Id); evidence.Complete(new { passed = true }); }
                File.WriteAllText(raceReport, racePrior); // Simulate another owner winning final-path reservation.
            }, error, admissionRequired: false);
            Record(new { id = "final-report-collision", passed = exit == 1 && File.ReadAllText(raceReport) == racePrior &&
                File.ReadAllText(raceReport + ".journal.jsonl").Contains("case-completed", StringComparison.Ordinal),
                actualExit = exit, report = raceReport, stderr = error.ToString() });
        }

        var failures = rows.Count(row => !JsonSerializer.SerializeToElement(row).GetProperty("passed").GetBoolean());
        return new { passed = rows.Count == 10 && failures == 0, controls = rows.Count, failures, rows,
            faultControlReportsAreNotNativeQualification = true, artifactsRetained = true };
    }
    private static string ReadActiveJournal(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }
}
