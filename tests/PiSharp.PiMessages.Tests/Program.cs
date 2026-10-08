using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;
using static WholeValueAssertions;

// Authored expectations are frozen before the correction. Never grants Source qualification.
internal static class Program
{
    private const string CasesSha = "1b8a6fbb80726e6f1a6cb1848d2df9b7da41c9015d91592d9f077f2aa8c04891";
    private const string DependenciesSha = "5a2ede772b7b4e906671c92a12a26654d3e1f69588bcf0b519bcb96c43aabb81";
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--identity-successor-r1" or "--identity-controls-r1")
            return await IdentitySuccessorAdmission.RunAsync(args);
        if (args.Length != 2) throw new ArgumentException("Supply authored-cases-r2.json beside criterion-dependencies-r3.json, and a fresh results path.");
        var bytes = await File.ReadAllBytesAsync(args[0]);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Require(hash == CasesSha, "Frozen authored fixture identity mismatch; zero case assertions executed.");
        using var document = JsonDocument.Parse(bytes); var inputs = document.RootElement.GetProperty("cases");
        Require(inputs.GetArrayLength() == 46 && Text(document.RootElement, "sourceSha") == SourceSha, "Case/Source identity mismatch.");
        var dependencyBytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "criterion-dependencies-r3.json"));
        var dependencyHash = Convert.ToHexString(SHA256.HashData(dependencyBytes)).ToLowerInvariant();
        Require(dependencyHash == DependenciesSha, "Frozen criterion dependency identity mismatch; zero case assertions executed.");
        using var dependencyDocument = JsonDocument.Parse(dependencyBytes);
        Require(Text(dependencyDocument.RootElement, "predecessorFixtureSha256") == CasesSha &&
            dependencyDocument.RootElement.GetProperty("caseCount").GetInt32() == 46 && dependencyDocument.RootElement.GetProperty("criterionCount").GetInt32() == 66,
            "Criterion/input revision identity mismatch.");
        var dependencyCases = dependencyDocument.RootElement.GetProperty("cases");
        var identityExpected = LegacyIdentitySuccessor.LoadExpected(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        var assembly = typeof(IChatTransport).Assembly;
        var optionsType = assembly.GetType("PiSharp.AI.Protocols.PiMessages.PiMessagesOptions");
        var transportType = assembly.GetType("PiSharp.AI.Protocols.PiMessages.PiMessagesHttpSseTransport");
        var hooksType = assembly.GetType("PiSharp.AI.Protocols.PiMessages.PiMessagesLifecycleHooks");
        var factoryType = assembly.GetType("PiSharp.AI.Protocols.PiMessages.PiMessagesKeyAuthRequestFactory");
        var results = new List<object>(); var outcomeIndex = new List<object>();
        var failures = 0; var openQualificationOutcomes = 0; var unexecutedOutcomes = 0; var stop = false;
        foreach (var scenario in inputs.EnumerateArray())
        {
            var id = Text(scenario, "id"); var wireBytes = Wire(scenario.GetProperty("input"));
            var identitySuccessor = id == LegacyIdentitySuccessor.CaseId;
            var effectiveScenario = identitySuccessor ? LegacyIdentitySuccessor.CreateEffective(scenario, identityExpected) : scenario;
            var schedules = id == "PM-FRAME-CRLF-FRAGMENT"
                ? new[] { Array.Empty<int>() }.Concat(Enumerable.Range(1, wireBytes.Length - 1).Select(split => new[] { split, int.MaxValue })).ToArray()
                : new[] { Array.Empty<int>() };
            var completedVariants = 0;
            var variants = new List<(AssertionLedger Ledger, string Schedule, PiMessagesOutcomeReporting.Outcome Outcome, object Observations)>();
            foreach (var schedule in schedules)
            {
                var ledger = new AssertionLedger(scenario, dependencyCases.GetProperty(id!)); var h = new HeldOwnershipHarnessR2();
                var domainGap = scenario.GetProperty("expected").GetProperty("domainGap").GetBoolean();
                PiMessagesOutcomeReporting.Outcome outcome;
                if (stop || optionsType is null || transportType is null || hooksType is null || factoryType is null)
                { outcome = PiMessagesOutcomeReporting.Classify("UNEXECUTED", domainGap, null, unexecuted: true); }
                else
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var invocation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    try
                    {
                        if (identitySuccessor) LegacyIdentitySuccessor.RetainHistoricalCriteria(ledger);
                        await Run(effectiveScenario, optionsType, transportType, hooksType, factoryType, h, ledger, invocation, deadline.Token, schedule, identitySuccessor);
                        Require(!h.Unjoined, "An unjoined owned operation cannot earn a successful case result.");
                        outcome = PiMessagesOutcomeReporting.Classify(ledger.VariantDependencyStatus(), domainGap, null);
                        if (!outcome.Nonpassing) completedVariants++;
                    }
                    catch (Exception failure) { outcome = PiMessagesOutcomeReporting.Classify(ledger.VariantDependencyStatus(), domainGap, failure); }
                }
                stop |= h.Unjoined;
                if (outcome.Category == "BEHAVIORAL_FAIL") failures++;
                else if (outcome.Category == "UNEXECUTED") unexecutedOutcomes++;
                else if (outcome.Nonpassing) openQualificationOutcomes++;
                variants.Add((ledger, schedule.Length == 0 ? "one-byte" : "split-at-" + schedule[0], outcome, h.Observations()));
            }
            // Derive all-variant criteria only after every scheduled variant has an outcome.
            // Every variant report carries the same final aggregate status for that scope.
            var ledgers = variants.Select(v => v.Ledger).ToArray();
            foreach (var variant in variants)
            {
                // Index OPEN rows even when no exception occurred (including domain gaps).
                outcomeIndex.Add(new { id, readSchedule = variant.Schedule, category = variant.Outcome.Category,
                    behavior = variant.Outcome.Behavior, qualification = variant.Outcome.Qualification,
                    nonpassing = variant.Outcome.Nonpassing, domainGap = scenario.GetProperty("expected").GetProperty("domainGap").GetBoolean() });
                results.Add(new { id, readSchedule = variant.Schedule, status = variant.Outcome.Category, failure = variant.Outcome.Failure,
                    behavior = variant.Outcome.Behavior, qualification = variant.Outcome.Qualification,
                    assertions = variant.Ledger.Snapshot(ledgers, schedules.Length), observations = variant.Observations, authoredInputAndExpected = scenario.Clone(),
                    effectiveIdentitySuccessorExpected = identitySuccessor ? (JsonElement?)identityExpected : null });
            }
            if (id == "PM-FRAME-CRLF-FRAGMENT") results.Add(new { id, status = completedVariants == schedules.Length ? "ALL AUTHORED BYTE BOUNDARIES CHECKED" : "BYTE BOUNDARIES INCOMPLETE",
                expectedVariants = schedules.Length, completedVariants, allBytes = wireBytes.Length,
                namedCriteria = ledgers[0].CriterionSummary(ledgers, schedules.Length) });
        }
        IReadOnlyList<PiMessagesDebugUriControls.Outcome> authoredDebugUriControls = stop
            ? [new("pi-debug-uri.controls", "UNEXECUTED_AFTER_UNJOINED_OWNER", null)]
            : await PiMessagesDebugUriControls.RunAsync();
        failures += authoredDebugUriControls.Count(control => control.Status == "FAIL");
        var authoredOutcomeReportingControls = stop ? new OutcomeReportingControls.Outcome("UNEXECUTED_AFTER_UNJOINED_OWNER", null) : OutcomeReportingControls.Run();
        if (authoredOutcomeReportingControls.Status == "FAIL") failures++;
        IReadOnlyList<PiMessagesIdentityReplacementCases.Outcome> authoredIdentityReplacementControls = stop
            ? [new("pi-identity.controls", "UNEXECUTED_AFTER_UNJOINED_OWNER", null)]
            : await PiMessagesIdentityReplacementCases.RunAsync();
        failures += authoredIdentityReplacementControls.Count(control => control.Status == "FAIL");
        var authoredLegacyIdentityControls = stop
            ? new[] { new LegacyIdentitySuccessor.Outcome("legacy-identity.controls", "UNEXECUTED_AFTER_UNJOINED_OWNER", null, null) }
            : await LegacyIdentitySuccessor.RunControlsAsync(inputs, dependencyCases, identityExpected);
        failures += authoredLegacyIdentityControls.Count(control => control.Status == "FAIL");
        await using var report = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(report, new { status = "AUTHORED NATIVE CONSUMER; NO SOURCE DIFFERENTIAL ACCEPTANCE", casesSha256 = hash, criterionDependenciesSha256 = dependencyHash,
            genuineSourceCasesCaptured = 0, sourceQualificationBlockers = document.RootElement.GetProperty("qualificationBlockers").Clone(),
            all79ScopesRetained = true, allEightPhaseGates = "OPEN", qualificationStatus = "NONPASSING_SOURCE_QUALIFICATION_OPEN",
            failures, openQualificationOutcomes, unexecutedOutcomes, outcomeIndex, results, authoredDebugUriControls, authoredOutcomeReportingControls,
            authoredIdentityReplacementControls, authoredLegacyIdentityControls }, new JsonSerializerOptions { WriteIndented = true });
        // Completed authored behavior never grants Source qualification. OPEN rows
        // remain nonpassing even when the behavioral failure count is zero.
        return failures == 0 && openQualificationOutcomes == 0 && unexecutedOutcomes == 0 ? 0 : 1;
    }
    internal static async Task Run(JsonElement scenario, Type optionsType, Type transportType, Type hooksType, Type factoryType,
        HeldOwnershipHarnessR2 h, AssertionLedger ledger, CancellationTokenSource invocation, CancellationToken deadline, int[] schedule, bool identitySuccessor = false)
    {
        var input = scenario.GetProperty("input"); var expected = scenario.GetProperty("expected"); var rawModel = input.GetProperty("model");
        var rawOptions = input.GetProperty("options"); var inputBefore = input.GetRawText();
        var model = new ModelDescriptor(Text(rawModel, "id")!, Text(rawModel, "api")!, Text(rawModel, "provider")!); h.Model = model;
        var configured = Activator.CreateInstance(optionsType, [JsonData.FromElement(rawModel), Text(rawOptions, "apiKey")])!;
        foreach (var name in new[] { "temperature", "maxTokens", "reasoning", "cacheRetention", "sessionId", "toolChoice", "debug", "headers", "env" })
        {
            if (!rawOptions.TryGetProperty(name, out var value)) continue;
            var propertyName = name == "env" ? "Environment" : char.ToUpperInvariant(name[0]) + name[1..];
            var property = optionsType.GetProperty(propertyName)!;
            // Map by target contract: a string toolChoice is JsonData, not a CLR string.
            object? native = property.PropertyType == typeof(JsonData) ? JsonData.FromElement(value) : value.ValueKind switch
            { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidOperationException("Unsupported authored scalar option.") };
            property.SetValue(configured, native);
        }
        optionsType.GetProperty("EnvironmentLookup")!.SetValue(configured, (Func<string, string?>)h.EnvironmentLookup);
        if (input.TryGetProperty("hook", out var hook))
        {
            h.HookKind = Text(hook, "kind"); h.HookMode = Text(hook, "mode"); h.ReturnMode = Text(hook, "returnMode"); h.ThrowMessage = Text(hook, "throwMessage");
            h.HeldProviderOrdinal = hook.TryGetProperty("eventOrdinal", out var ordinal) ? ordinal.GetInt32() : 0;
            if (hook.TryGetProperty("replacement", out var replacement)) h.Replacement = JsonData.FromElement(replacement);
        }
        if (input.TryGetProperty("cleanup", out var cleanup))
        { h.HoldCleanup = cleanup.TryGetProperty("holdReader", out var held) && held.GetBoolean(); h.CleanupFault = Text(cleanup, "fault"); }
        h.TerminalObserverThrow = Text(input, "terminalObserverThrow");
        var hooks = Activator.CreateInstance(hooksType)!; h.BindHooks(hooks); optionsType.GetProperty("Hooks")!.SetValue(configured, hooks);
        var factory = Activator.CreateInstance(factoryType, [model, configured])!;
        var endpoint = ((Uri)factoryType.GetProperty("Endpoint")!.GetValue(factory)!).AbsoluteUri;
        h.Record("factory-endpoint", endpoint);
        var bodies = input.TryGetProperty("secondTurn", out var secondTurn) ? new[] { Wire(input), Wire(secondTurn) } : new[] { Wire(input) };
        using var client = h.Client(bodies, input, schedule);
        var transport = (IChatTransport)Activator.CreateInstance(transportType, [client, model, configured])!;
        var history = input.GetProperty("context").GetProperty("messages").EnumerateArray().Select(m => new TranscriptEntry(Text(m, "role")!, JsonData.FromElement(m))).ToImmutableArray();
        var ownedOperations = new ConcurrentBag<Task>(); var cleanupHeldChecked = false;
        async Task<StreamTerminalEvent?> Consume(ChatRequest current, bool early)
        {
            StreamTerminalEvent? terminal = null;
            var iterator = transport.StreamAsync(current, invocation.Token).GetAsyncEnumerator(invocation.Token);
            try
            {
                var count = 0;
                while (await iterator.MoveNextAsync())
                {
                    var frame = iterator.Current; h.Delivered(frame); count++;
                    if (frame is StreamTerminalEvent final) terminal = final;
                    if (early && count == input.GetProperty("earlyReturnAfter").GetInt32()) break;
                }
            }
            finally
            {
                var disposal = iterator.DisposeAsync().AsTask(); ownedOperations.Add(disposal);
                h.Record("consumer-iterator-disposal-start", new { completed = disposal.IsCompleted }); await disposal;
            }
            return terminal;
        }
        var earlyReturn = input.TryGetProperty("earlyReturnAfter", out _);
        var consumption = Consume(new ChatRequest(model, history, 123), earlyReturn); ownedOperations.Add(consumption);
        try
        {
            if (h.HookMode == "held")
            {
                await h.Entered.Task.WaitAsync(deadline);
                ledger.Check("hook-gate", () =>
                {
                    h.Record("controller-held-checkpoint", new { h.Sends, h.ProviderCallbacks, publications = h.Publications.Count,
                        deliveries = h.Deliveries.Count, h.Reads, h.Effects, resultCompleted = consumption.IsCompleted });
                    Require(!consumption.IsCompleted && h.TerminalPublications == 0 && h.TerminalDeliveries == 0 && h.Effects == 0, "Held callback cannot settle result or invoke effects.");
                    if (h.HookKind == "payload") Require(h.Sends == 0 && h.ProviderCallbacks == 0 && h.Publications.Count == 0 && h.Deliveries.Count == 0, "Held payload precedes all send/conversion.");
                    if (h.HookKind == "response") Require(h.Sends == 1 && h.ProviderCallbacks == 0 && h.Publications.Count == 0 && h.Deliveries.Count == 0 && h.Reads == 0 && h.Owners.Single().Acquisitions == 0, "Held response precedes acquisition/read/provider/publication.");
                    if (h.HookKind == "provider")
                    {
                        Require(h.ProviderCallbacks == h.HeldProviderOrdinal + 1 && h.Publications.Count == h.HeldProviderOrdinal && h.Deliveries.Count == h.HeldProviderOrdinal, "Held DTO is observed but not converted or published.");
                        CompareFrames(expected.GetProperty("frames").EnumerateArray().Take(h.HeldProviderOrdinal).ToArray(), h.Publications, expected, h, "held-prefix");
                        CompareValue(expected.GetProperty("provider")[h.HeldProviderOrdinal], h.Values.Last(), model, h, "held-DTO");
                    }
                });
                if (input.TryGetProperty("cancel", out _))
                {
                    invocation.Cancel(); await h.CancelledHookExited.Task.WaitAsync(deadline);
                    ledger.Check("hook-gate", () => Require(!h.Release.Task.IsCompleted && h.ProviderCallbacks == 3 && h.ProgressPublications == 2 && h.Effects == 0, "Cancellation exits token-aware hook without release or later conversion."));
                }
                else { h.Record("controller-explicit-hook-release", h.HookKind); h.Release.TrySetResult(); }
            }
            if (h.HoldCleanup)
            {
                await h.CleanupEntered.Task.WaitAsync(deadline);
                ledger.Check("cleanup-gate", () =>
                {
                    var owner = h.Owners.Single(); h.Record("controller-held-cleanup-checkpoint", new { owner = owner.Snapshot(), h.TerminalPublications, h.TerminalDeliveries, h.Effects, h.Sends, completed = consumption.IsCompleted });
                    Require(!consumption.IsCompleted && h.TerminalPublications == 0 && h.TerminalDeliveries == 0 && h.Effects == 0 && h.Sends == 1, "Held cleanup prevents terminal publication/delivery, tool effects, and next request.");
                    Require(owner.AsyncDisposalStarts == 1 && owner.AsyncDisposalSettled == 0 && owner.ResponseDisposalStarts == 0 && owner.RequestDisposalStarts == 0, "Held reader disposal causally precedes later owner release.");
                    cleanupHeldChecked = true;
                });
                h.Record("controller-explicit-cleanup-release", true); h.CleanupRelease.TrySetResult();
            }
            StreamTerminalEvent? terminal = null; Exception? enumerationFailure = null;
            try { terminal = await consumption.WaitAsync(deadline); }
            catch (Exception error) { enumerationFailure = error; h.Record("actual-enumeration-failure", error.ToString()); }
            if (earlyReturn)
            {
                ledger.Check("early-return", () =>
                {
                    Require(h.TerminalPublications == 0 && h.TerminalDeliveries == 0 && h.Effects == 0 && terminal is null, "Early return grants no terminal authority.");
                    h.AssertOwnership("early-return-joined");
                    if (h.CleanupFault is null) Require(enumerationFailure is null && cleanupHeldChecked, "Early return waits for explicit cleanup release.");
                    else Require(enumerationFailure is not null && enumerationFailure.GetType().GetProperty("Failure")?.GetValue(enumerationFailure)?.ToString() == "CleanupFailed" && enumerationFailure.Message == "Pi Messages iterator cleanup failed.", "Early return propagates typed CleanupFailed after owned releases.");
                });
            }
            else if (h.TerminalObserverThrow is not null)
            {
                ledger.Check("observer-fault", () =>
                {
                    Require(enumerationFailure is InvalidOperationException && enumerationFailure.Message == h.TerminalObserverThrow && h.TerminalPublications == 1 && h.TerminalDeliveries == 0 && terminal is null, "Terminal observer propagates once after cleanup without second error/delivery.");
                    h.AssertOwnership("observer-failure-joined");
                });
            }
            else Require(enumerationFailure is null && terminal is not null, "Enumeration must deliver exactly one terminal after owned cleanup.");
            ledger.Check("payload", () =>
            {
                if (Text(scenario, "id") == "PM-AUTH-MISSING") Require(h.Values.Count == 0, "Missing key has no payload/response/provider callbacks.");
                else CompareValue(expected.GetProperty("payload"), h.Values.Single(v => v.Kind == "payload" && v.Turn == 0), model, h, "payload");
            });
            ledger.Check("request", () =>
            {
                var requestExpected = expected.GetProperty("request");
                if (requestExpected.ValueKind == JsonValueKind.Null) Require(h.Sends == 0 && h.Requests.Count == 0, "Auth/hook failure has zero physical sends.");
                else { Require(endpoint == Text(requestExpected, "uri"), "Whole factory Endpoint differs."); CompareRequest(requestExpected, h.Requests.Single(r => r.Turn == 0), h); }
                if (Text(scenario, "id") == "PM-CACHE-SCOPED") Require(h.EnvironmentLookups.Count == 0, "Scoped long cache retention never calls ambient/injected fallback.");
            });
            ledger.Check("response", () =>
            {
                var actual = h.Values.Where(v => v.Kind == "response" && v.Turn == 0).ToArray();
                Require(actual.Length == (h.Sends == 0 ? 0 : 1), "Response callback count differs.");
                if (actual.Length == 1) CompareValue(expected.GetProperty("response"), actual[0], model, h, "response");
            });
            ledger.Check("provider", () =>
            {
                var actual = h.Values.Where(v => v.Kind == "provider" && v.Turn == 0).ToArray(); var providerExpected = expected.GetProperty("provider");
                Require(actual.Length == providerExpected.GetArrayLength(), "Complete provider callback count differs; no implicit retry or post-terminal callback.");
                for (var n = 0; n < actual.Length; n++) CompareValue(providerExpected[n], actual[n], model, h, "provider/" + n);
            });
            ledger.Check("frames", () =>
            {
                var frameExpected = expected.GetProperty("frames").EnumerateArray().ToArray();
                CompareFrames(frameExpected, h.Publications, expected, h, "publications");
                CompareFrames(h.TerminalObserverThrow is null ? frameExpected : frameExpected[..^1], h.Deliveries, expected, h, "deliveries");
                if (input.TryGetProperty("httpBody", out _))
                {
                    var diagnostic = terminal!.Message.ExtraProperties!.Values["diagnostics"].ToString();
                    Require(!diagnostic.Contains(Text(rawOptions, "apiKey")!, StringComparison.Ordinal), "HTTP diagnostic leaks inert API key.");
                }
            });
            ledger.Check("ownership", () =>
            {
                h.AssertOwnership("joined-consumer"); Require(h.OwnershipFailures.Count == 0, "Publication/delivery ownership assertion failed.");
                Require(!h.HoldCleanup || cleanupHeldChecked && h.CleanupRelease.Task.IsCompleted, "Held cleanup checkpoint/release missing.");
                if (h.CleanupFault is not null) Require(earlyReturn || terminal is StreamError && terminal.Message.Content.IsEmpty, "Cleanup fault must prevent successful terminal.");
            });
            ledger.Check("input-unchanged", () => Require(input.GetRawText() == inputBefore, "Raw immutable input history/options were changed."));
            if (Text(scenario, "id") is "PM-EOF-PARTIAL" or "PM-FRAME-EOF-PENDING") ledger.Check("eof", () => Require(h.Owners.Single().EofReads == 1 && h.Owners.Single().AsyncDisposalSettled == 1, "Actual EOF read and joined disposal required."));
            if (Text(scenario, "id") == "PM-FRAME-CRLF-FRAGMENT") ledger.Check("fragmentation", () =>
            {
                var reads = h.Owners.Single().ReadCounts.Where(n => n != 0).ToArray();
                Require(reads.Sum() == bodies[0].Length, "Fragmented wire bytes not consumed exactly.");
                if (schedule.Length == 0) Require(reads.All(n => n == 1), "One-byte fragmentation schedule not witnessed.");
                else Require(reads[0] == schedule[0] && reads.Skip(1).Sum() == bodies[0].Length - schedule[0], "Authored two-chunk byte split not witnessed.");
            });
            if (input.TryGetProperty("secondTurn", out _))
            {
                var call = terminal!.Message.Content.OfType<ToolCallContent>().Single(); h.ToolEffect(call);
                var continuation = expected.GetProperty("continuation");
                history = history.Add(new("assistant", PiWireJson.WriteMessage(terminal.Message))).Add(new("toolResult", JsonData.FromElement(continuation.GetProperty("toolResult"))));
                var next = Consume(new ChatRequest(model, history, 123), false); ownedOperations.Add(next);
                var nextTerminal = await next.WaitAsync(deadline);
                ledger.Check("continuation", () =>
                {
                    Require(h.Effects == 1 && h.Sends == 2 && h.TerminalDeliveries == 2 && nextTerminal is StreamDone, "Exactly one effect and two actual physical turns required.");
                    CompareValue(continuation.GetProperty("secondPayload"), h.Values.Single(v => v.Kind == "payload" && v.Turn == 1), model, h, "second-payload");
                    CompareRequest(continuation.GetProperty("secondRequest"), h.Requests.Single(r => r.Turn == 1), h);
                    var firstCount = expected.GetProperty("frames").GetArrayLength();
                    CompareFrames(continuation.GetProperty("secondFrames").EnumerateArray().ToArray(), h.Publications.Skip(firstCount).ToList(), expected, h, "second-publications");
                    CompareFrames(continuation.GetProperty("secondFrames").EnumerateArray().ToArray(), h.Deliveries.Skip(firstCount).ToList(), expected, h, "second-deliveries");
                    h.AssertOwnership("continuation-joined");
                });
                if (Text(scenario, "id") == "PM-NATIVE-TOOL-CONTINUATION") ledger.Open("integration-open", "Agent/ToolInvoker shared paths are unreleased; this provider driver cannot qualify orchestration.");
            }
            ledger.Check("effects", () => Require(h.Effects == expected.GetProperty("expectedEffects").GetInt32(), "Provider/parser may not invoke tools; only explicit continuation driver has one effect."));
            if (expected.GetProperty("domainGap").GetBoolean() && !identitySuccessor) ledger.Check("domain-gap", () =>
                Require(terminal is StreamError && terminal.Message.Content.IsEmpty && h.Effects == 0, "Native tool identity rejection stays an open Source/native conflict, with no effect or shared reducer change."));
        }
        finally
        {
            try { invocation.Cancel(); } catch (Exception error) { h.Record("owned-cancellation-failure", error.ToString()); }
            h.Release.TrySetResult(); h.CleanupRelease.TrySetResult();
            try { await consumption.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { h.Record("owned-primary-join-outcome", error.ToString()); if (!consumption.IsCompleted) h.Unjoined = true; }
            foreach (var operation in ownedOperations.ToArray())
                try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) { h.Record("owned-join-outcome", error.ToString()); if (!operation.IsCompleted) h.Unjoined = true; }
            try { ledger.Check("operation-join", () => Require(!h.Unjoined, "Every owned consumer/disposal task must settle before a dependent criterion completes.")); }
            catch (Exception error) { h.Record("criterion-operation-join-failure", error.ToString()); } // Preserve an earlier propagating failure.
        }
    }
    private static void CompareValue(JsonElement expected, HeldOwnershipHarnessR2.ValueObservation actual, ModelDescriptor model, HeldOwnershipHarnessR2 h, string label)
    {
        Compare(expected.GetProperty("value"), actual.Value.Value, h, label);
        Json(expected.GetProperty("ownUndefinedPaths"), actual.OwnUndefinedPaths, label + "/ownUndefinedPaths");
        Require(actual.SameDescriptor && actual.Id == model.Id && actual.Api == model.Api && actual.Provider == model.Provider, label + ": exact descriptor identity/fields required.");
    }
    private static void CompareRequest(JsonElement expected, HeldOwnershipHarnessR2.RequestObservation actual, HeldOwnershipHarnessR2 h)
    {
        Require(Text(expected, "uri") == actual.Uri && Text(expected, "method") == actual.Method, "Whole physical URI/method differs.");
        var sourceHeaders = expected.GetProperty("headers");
        Compare(sourceHeaders, JsonSerializer.SerializeToElement(actual.BeforeBodyReadHeaders), h, "request/source-boundary-headers");
        // ByteArrayContent materialization adds a native Content-Length. Derive its
        // exact expected value from frozen source body bytes; retain every other header.
        var nativeHeaders = sourceHeaders.EnumerateObject().ToDictionary(field => field.Name,
            field => field.Value.EnumerateArray().Select(value => value.GetString()!).ToArray(), StringComparer.Ordinal);
        var expectedBodyUtf8Base64 = Text(expected, "bodyUtf8Base64")
            ?? throw new JsonException("Expected request bodyUtf8Base64 must be a non-null string.");
        var expectedLength = Convert.FromBase64String(expectedBodyUtf8Base64).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!nativeHeaders.ContainsKey("content-length")) nativeHeaders.Add("content-length", [expectedLength]);
        h.Record("request-header-expectation-layers", new { sourceHeaders, nativeHeaders, expectedUtf8ByteLength = expectedLength });
        Compare(JsonSerializer.SerializeToElement(nativeHeaders), JsonSerializer.SerializeToElement(actual.Headers), h, "request/native-materialized-headers");
        Require(expectedBodyUtf8Base64 == actual.BodyUtf8Base64, "Whole physical body UTF-8 bytes differ.");
        using var body = JsonDocument.Parse(actual.RawBody); Compare(expected.GetProperty("body"), body.RootElement, h, "request/body");
    }
    private static void CompareFrames(JsonElement[] expected, List<HeldOwnershipHarnessR2.FrameObservation> actual,
        JsonElement caseExpected, HeldOwnershipHarnessR2 h, string label)
    {
        Require(expected.Length == actual.Count, label + ": complete frame count differs.");
        for (var n = 0; n < expected.Length; n++)
        {
            var template = expected[n]; var candidates = NativeFailureMessages(caseExpected.GetProperty("messageRule"));
            var matched = false; Exception? last = null;
            foreach (var candidate in candidates)
            {
                using var resolved = JsonDocument.Parse(ResolveMessages(template, candidate));
                try
                {
                    Equal(resolved.RootElement.GetProperty("wire"), actual[n].Wire.Value, label + "/" + n + "/wire");
                    Require(actual[n].Snapshot is not null, "Full immutable Source emission snapshot required.");
                    Equal(resolved.RootElement.GetProperty("snapshot"), actual[n].Snapshot!.Value, label + "/" + n + "/snapshot");
                    if (template.GetProperty("compareSourceSnapshotBytes").GetBoolean()) Require(EcmaBytes(actual[n].Snapshot!) == Text(template, "sourceSnapshotUtf8Base64"), label + ": Source-order ECMAScript snapshot bytes differ.");
                    matched = true; break;
                }
                catch (Exception error)
                {
                    last = error;
                    h.Record("all-frame-field-differences", new { label, index = n, candidate,
                        wire = Differences(resolved.RootElement.GetProperty("wire"), actual[n].Wire.Value),
                        snapshot = actual[n].Snapshot is null ? null : Differences(resolved.RootElement.GetProperty("snapshot"), actual[n].Snapshot!.Value),
                        expectedBytes = Text(template, "sourceSnapshotUtf8Base64"), actualBytes = actual[n].Snapshot is null ? null : EcmaBytes(actual[n].Snapshot!) });
                }
            }
            if (!matched)
            {
                h.Record("whole-frame-difference", new { label, index = n, completeExpected = template.Clone(), completeActual = actual[n], error = last?.ToString() });
                throw new InvalidOperationException(label + ": whole frame or serialization mismatch at " + n, last);
            }
        }
    }
    private static string[] NativeFailureMessages(JsonElement rule)
    {
        if (rule.ValueKind == JsonValueKind.Null) return [""];
        if (Text(rule, "kind") == "one-of") return rule.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToArray();
        try { using var invalid = JsonDocument.Parse(Text(rule, "text")!); throw new InvalidOperationException("Authored malformed JSON unexpectedly parsed."); }
        catch (JsonException error) { return [error.Message]; }
    }
    private static string ResolveMessages(JsonElement template, string message)
    {
        var root = JsonNode.Parse(template.GetRawText())!;
        void Visit(JsonNode? node)
        {
            if (node is JsonObject fields) foreach (var key in fields.Select(p => p.Key).ToArray())
            {
                var child = fields[key];
                if (child is JsonValue value && value.TryGetValue<string>(out var text) && text is "@native-json-parser" or "@cancel-message") fields[key] = message;
                else Visit(child);
            }
            else if (node is JsonArray items) foreach (var item in items) Visit(item);
        }
        Visit(root); return root.ToJsonString();
    }
    private static void Compare(JsonElement expected, JsonElement actual, HeldOwnershipHarnessR2 h, string label)
    {
        try { Equal(expected, actual, label); }
        catch (Exception error) { h.Record("whole-value-difference", new { label, completeExpected = expected.Clone(), completeActual = actual.Clone(), differences = Differences(expected, actual), error = error.ToString() }); throw; }
    }
    private static byte[] Wire(JsonElement input)
    {
        if (input.TryGetProperty("httpBody", out var rejection)) return Encoding.UTF8.GetBytes(rejection.GetString()!);
        if (input.TryGetProperty("rawBody", out var raw)) return Encoding.UTF8.GetBytes(raw.GetString()!);
        var wire = new StringBuilder();
        if (input.TryGetProperty("rawFrames", out var frames)) foreach (var frame in frames.EnumerateArray()) wire.Append(frame.GetString());
        var events = input.GetProperty("events").EnumerateArray().ToArray();
        for (var index = 0; index < events.Length; index++) wire.Append("data: ").Append(EcmaScriptJsonProjection.Project(JsonData.FromElement(events[index])))
            .Append(index == events.Length - 1 && input.TryGetProperty("terminalFrameSuffix", out _) ? "" : "\n\n");
        if (input.TryGetProperty("appendRawFrame", out var suffix)) wire.Append(suffix.GetString());
        var result = wire.ToString(); if (Text(input, "lineEnding") == "\r\n") result = result.Replace("\n", "\r\n", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(result);
    }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString() : null;
}
