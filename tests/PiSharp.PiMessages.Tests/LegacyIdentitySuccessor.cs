using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.PiMessages;

// Full-runner migration only. Frozen predecessor expectations and restriction criteria remain historical OPEN evidence.
internal static class LegacyIdentitySuccessor
{
    internal const string CaseId = "PM-TOOL-IDENTITY-REPLACEMENT";
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string OutputSha = "93c80e37db4fa20ef386594beeb6a609f5c9996738ed7de45373543dbfaf2a52";
    internal sealed record Outcome(string Id, string Status, string? Failure, JsonElement? Observations);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static JsonElement LoadExpected(string fixtureDirectory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, "identity-successor-output-r1.json"));
        Require(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == OutputSha, "Full-runner identity successor output pin differs; zero case assertions executed.");
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        Require(root.GetProperty("sourceSha").GetString() == SourceSha && root.GetProperty("historicalDomainGapRetained").GetBoolean() &&
            root.GetProperty("genuineSourceOutputPin").ValueKind == JsonValueKind.Null, "Full-runner identity output authority differs.");
        return root.GetProperty("expected").Clone();
    }
    internal static JsonElement CreateEffective(JsonElement historical, JsonElement expected)
    {
        Require(historical.GetProperty("id").GetString() == CaseId && historical.GetProperty("expected").GetProperty("domainGap").GetBoolean() &&
            expected.GetProperty("domainGap").GetBoolean(), "Identity successor must preserve the original named OPEN domain gap.");
        foreach (var unchanged in new[] { "payload", "request", "response" })
            Require(JsonElement.DeepEquals(historical.GetProperty("expected").GetProperty(unchanged), expected.GetProperty(unchanged)), "Identity successor changed original " + unchanged + " expectations.");
        var events = historical.GetProperty("input").GetProperty("events"); var provider = expected.GetProperty("provider");
        Require(events.GetArrayLength() == 5 && provider.GetArrayLength() == 5 && expected.GetProperty("frames").GetArrayLength() == 5,
            "Identity successor requires all five authored DTOs/callbacks/frames, including done.");
        for (var ordinal = 0; ordinal < 5; ordinal++)
            Require(JsonElement.DeepEquals(events[ordinal], provider[ordinal].GetProperty("value")), "Identity successor changed original DTO/callback order.");
        var effective = JsonNode.Parse(historical.GetRawText())!.AsObject(); effective["expected"] = JsonNode.Parse(expected.GetRawText());
        using var result = JsonDocument.Parse(effective.ToJsonString());
        foreach (var unchanged in new[] { "id", "input", "assertions", "requiredChecks" })
            Require(JsonElement.DeepEquals(historical.GetProperty(unchanged), result.RootElement.GetProperty(unchanged)), "Full-runner migration changed historical " + unchanged + ".");
        return result.RootElement.Clone();
    }
    internal static void RetainHistoricalCriteria(AssertionLedger ledger) => ledger.Open("domain-gap",
        "Historical rejection/reducer restriction criteria are retained, superseded by authorized identity replacement, and remain OPEN; authored successor checks grant no genuine Source qualification.");

    internal static async Task<Outcome[]> RunControlsAsync(JsonElement inputs, JsonElement dependencies, JsonElement expected)
    {
        var original = inputs.EnumerateArray().Single(item => item.GetProperty("id").GetString() == CaseId);
        var outcomes = new List<Outcome>(); var stop = false;
        foreach (var (id, run) in new (string, Func<Task<JsonElement>>)[]
        {
            ("legacy-identity.frozen-input-criteria-and-open-qualification", Admission),
            ("legacy-identity.complete-callbacks-authority-cleanup-and-post-terminal", Physical),
            ("legacy-identity.stale-four-callback-control-still-fails", Stale)
        })
        {
            if (stop) { outcomes.Add(new(id, "UNEXECUTED_AFTER_UNJOINED_OWNER", null, null)); continue; }
            try { outcomes.Add(new(id, "AUTHORED_CHECKS_COMPLETED", null, await run())); }
            catch (Exception error) { outcomes.Add(new(id, "FAIL", error.ToString(), null)); }
        }
        return outcomes.ToArray();

        Task<JsonElement> Admission()
        {
            var effective = CreateEffective(original, expected); var ledger = new AssertionLedger(effective, dependencies.GetProperty(CaseId));
            RetainHistoricalCriteria(ledger);
            var criterionStatuses = JsonSerializer.SerializeToElement(ledger.CriterionSummary([ledger], 1));
            Require(criterionStatuses.EnumerateArray().All(criterion => criterion.GetProperty("status").GetString() == "OPEN"), "Historical criteria were silently closed.");
            var classification = PiMessagesOutcomeReporting.Classify(ledger.VariantDependencyStatus(), true, null);
            Require(classification.Category == "OPEN_DOMAIN_GAP" && classification.Nonpassing && classification.Qualification == "OPEN", "Migration granted Source acceptance.");
            Require(original.GetProperty("expected").GetProperty("provider").GetArrayLength() == 4, "Frozen predecessor callback expectation changed.");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { historicalProviderCount = 4, successorProviderCount = 5, historicalCriteria = criterionStatuses, classification }));
        }
        async Task<JsonElement> Physical()
        {
            var observations = new List<JsonElement>();
            foreach (var schedule in new[] { Array.Empty<int>(), new[] { 1, int.MaxValue }, new[] { 2, 3, int.MaxValue } })
            {
                var effective = CreateEffective(original, expected);
                var node = JsonNode.Parse(effective.GetRawText())!.AsObject(); var input = node["input"]!.AsObject();
                // Controls add a held release and an ignored post-terminal DTO. The actual full-runner case keeps original input unchanged.
                input["cleanup"] = new JsonObject { ["holdReader"] = true };
                input["events"]!.AsArray().Add(new JsonObject { ["type"] = "unsupported-after-terminal" });
                using var fixture = JsonDocument.Parse(node.ToJsonString());
                var ledger = new AssertionLedger(fixture.RootElement, dependencies.GetProperty(CaseId)); RetainHistoricalCriteria(ledger);
                var harness = new HeldOwnershipHarnessR2();
                try { await Invoke(fixture.RootElement, harness, ledger, schedule); }
                finally { stop |= harness.Unjoined; }
                Require(harness.ProviderCallbacks == 5 && harness.Sends == 1 && harness.Effects == 0 && harness.TerminalPublications == 1 && harness.TerminalDeliveries == 1 && !harness.Unjoined,
                    "Complete callback count or original owner authority differs.");
                var final = harness.Deliveries.Last().Wire.Value;
                Require(final.GetProperty("type").GetString() == "done" && final.GetProperty("reason").GetString() == "toolUse", "Final replacement did not settle successfully.");
                var call = final.GetProperty("message").GetProperty("content")[0];
                Require(call.GetProperty("id").GetString() == "authored-replaced-call" && call.GetProperty("name").GetString() == "other" && call.GetProperty("arguments").GetProperty("value").GetInt32() == 7,
                    "Final identity/argument authority regressed.");
                Require(ledger.VariantDependencyStatus() == "OPEN", "Historical qualification gap was lost after complete behavioral assertions.");
                observations.Add(IdentitySuccessorAdmission.OwnReportObservations(new { schedule, actuals = harness.Observations(), ledger = ledger.Snapshot([ledger], 1) }));
            }
            return JsonSerializer.SerializeToElement(observations);
        }
        async Task<JsonElement> Stale()
        {
            var ledger = new AssertionLedger(original, dependencies.GetProperty(CaseId)); var harness = new HeldOwnershipHarnessR2(); Exception? failure = null;
            try { await Invoke(original, harness, ledger, []); } catch (Exception error) { failure = error; }
            finally { stop |= harness.Unjoined; }
            Require(failure is InvalidOperationException && failure.Message == "Complete provider callback count differs; no implicit retry or post-terminal callback.",
                "Stale four-callback expectation was silently accepted or failed for another reason.");
            Require(harness.ProviderCallbacks == 5 && harness.Effects == 0 && !harness.Unjoined && ledger.VariantDependencyStatus() == "FAILED", "Stale mismatch did not retain the exact primary assertion and joined owners.");
            return IdentitySuccessorAdmission.OwnReportObservations(new { expectedFailure = failure!.Message, actuals = harness.Observations(), ledger = ledger.Snapshot([ledger], 1) });
        }
    }
    private static async Task Invoke(JsonElement scenario, HeldOwnershipHarnessR2 harness, AssertionLedger ledger, int[] schedule)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var invocation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await Program.Run(scenario, typeof(PiMessagesOptions), typeof(PiMessagesHttpSseTransport), typeof(PiMessagesLifecycleHooks), typeof(PiMessagesKeyAuthRequestFactory),
            harness, ledger, invocation, deadline.Token, schedule, identitySuccessor: true);
    }
}
