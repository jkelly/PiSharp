using System.Net;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;
using PiSharp.AI.Providers;

internal static class CompletionsSourceRunTests
{
    private static readonly ModelDescriptor Model = new("source-run", "openai-completions", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Finish = "{\"choices\":[{\"delta\":{\"content\":\"\\u03c0\\ud83d\\ude00\"},\"finish_reason\":\"stop\"}]}";
    private const string Tool = "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"owned-tool\",\"type\":\"function\",\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"path\\\":\\\"owned\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-source.actual-abort-result-before-held-cancel-and-joined-failure", AbortBoundary);
        yield return ("completions-source.normal-done-cancel-gate-and-eof-release-only", NormalBoundary);
        yield return ("completions-source.default-canonical-tool-final-and-physical-barrier", DefaultToolBarrier);
        yield return ("completions-source.actual-return-dto-bounded-drain-and-reader-exclusivity", ReturnAndDrain);
        yield return ("completions-source.live-alias-emission-ownership-and-explicit-public-failure", AliasAndFailure);
        yield return ("completions-source.noncooperative-read-throwing-callback-and-concurrent-dispose", CancellationOwnership);
        yield return ("completions-source.initial-admission-frame-bounds-numeric-ownership-and-fatal-cleanup", AdmissionAndPhysicalFailure);
        yield return ("completions-source.waiting-reader-live-revisions-reentrant-next-and-late-alias", WaitingReader);
        yield return ("completions-source.waiting-reader-return-retains-unread-result-and-held-owner", WaitingReaderReturn);
        yield return ("completions-source.actual-publication-witnesses-and-nonrecursive-observer-fault", PublicationWitnesses);
        yield return ("completions-source.safe-failure-observation-keeps-canonical-authority-and-compact-default", SafeFailureObservation);
        yield return ("completions-source.compact-cancel-retains-actual-owned-abort-observation", CompactAbortObservation);
        yield return ("completions-source.compact-cancel-rejects-oversized-foreign-and-invalid-observations", RejectedAbortObservations);
        yield return ("completions-source.joined-canonical-admitted-failure-bounds-and-failed-source-admission", CanonicalFailureBounds);
        yield return ("completions-source.cleanup-created-failure-keeps-native-authority", CleanupCreatedFailure);
        yield return ("completions-source.fixed-abort-final-requires-matched-literal-and-bounded-context", MatchedAbortFinal);
    }

    private static async Task WaitingReader()
    {
        var entered = Gate(); var release = Gate();
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), hooks: new()
        { OnResponse = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); } });
        await using var run = await fixture.Transport.StartAsync(Request());
        var frames = new List<CompletionsSourceEvent>(); var observed = new List<CompletionsSourceSnapshot>();
        var reading = Consume();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Check(!reading.IsCompleted && fixture.Reader is null, "The waiting-reader control did not hold actual response preparation.");
            release.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            await reading.WaitAsync(Deadline);
            var start = frames.Single(frame => frame.Type == "start");
            var early = observed[0].Raw.Value.GetProperty("value").GetProperty("partial");
            Check(early.GetProperty("stopReason").GetString() == "pending" && early.GetProperty("content").GetArrayLength() == 0 &&
                frames.Select(frame => frame.Type).SequenceEqual(new[] { "start", "text_start", "text_delta", "text_end", "done" }),
                "A waiting public continuation was overtaken or its nested NextAsync admission remained occupied.");
            var source = await run.SourceResult.WaitAsync(Deadline);
            Check(ReferenceEquals(start.Message, source) && start.Emission.Raw.ToString() == observed[0].Raw.ToString() &&
                start.Snapshot.Raw.Value.GetProperty("value").GetProperty("partial").GetProperty("stopReason").GetString() == "stop" &&
                !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted,
                "A deliberately late alias lost live identity, changed immutable emission, or escaped held physical cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult();
            Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null && fixture.Content.Disposed,
                "The waiting-reader handoff changed canonical ownership.");
        }
        finally { release.TrySetResult(); fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }

        async Task Consume()
        {
            while (true)
            {
                var next = await run.NextAsync(); if (next.Done) return;
                frames.Add(next.Value!); observed.Add(next.Value!.Snapshot);
            }
        }
    }

    private static async Task WaitingReaderReturn()
    {
        var entered = Gate(); var release = Gate();
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), capacity: 1, hooks: new()
        { OnResponse = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); } });
        await using var run = await fixture.Transport.StartAsync(Request());
        var returned = ConsumeAndReturn();
        try
        {
            await entered.Task.WaitAsync(Deadline); release.TrySetResult();
            Check((await returned.WaitAsync(Deadline)).Done, "Returning from the waiting consumer's actual continuation did not settle.");
            await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check((await run.SourceResult.WaitAsync(Deadline)).Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                !run.CanonicalCompletion.IsCompleted && !fixture.Body.LastToken.IsCancellationRequested,
                "A waiting-reader return canceled its producer, withheld unread semantic result, or escaped the physical join.");
            fixture.Body.ReleaseCleanup.TrySetResult();
            Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null && (await run.NextAsync()).Done,
                "A waiting-reader return failed its capacity-one discard drain.");
        }
        finally { release.TrySetResult(); fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(returned); }

        async Task<CompletionsIteratorResult> ConsumeAndReturn()
        {
            Check((await run.NextAsync()).Value!.Type == "start", "Waiting return control lost actual Start.");
            return await run.ReturnAsync();
        }
    }

    private static async Task PublicationWitnesses()
    {
        var actual = new ConcurrentQueue<CompletionsSourcePublication>();
        using (var fixture = new Fixture(Wire(Finish, "[DONE]"), hooks: new() { OnSourcePublished = actual.Enqueue }))
        {
            await using var run = await fixture.Transport.StartAsync(Request());
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                var source = await run.SourceResult.WaitAsync(Deadline); var witnesses = run.SourcePublications;
                Check(actual.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, witnesses.Length).Select(value => (long)value)) &&
                    actual.SequenceEqual(witnesses) && witnesses.Length == 5 && CompletionsSourcePublication.TimestampFrequency > 0 &&
                    witnesses.Zip(witnesses.Skip(1)).All(pair => pair.First.MonotonicTimestamp <= pair.Second.MonotonicTimestamp) &&
                    witnesses.Select(item => item.Emission).SequenceEqual(run.SourceEmissions),
                    "Publications were backfilled, reordered, duplicated or detached from their actual immutable emissions.");
                Check(witnesses[0].Emission.Raw.Value.GetProperty("value").GetProperty("partial").GetProperty("model").GetString() == Model.Id &&
                    witnesses[0].Emission.Raw.Value.GetProperty("value").GetProperty("partial").GetProperty("stopReason").GetString() == "pending" &&
                    source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" && !run.CanonicalCompletion.IsCompleted,
                    "An actual publication used a foreign context or retained the later live revision as its emission.");
                await run.ReturnAsync(); fixture.Body.ReleaseCleanup.TrySetResult();
                Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null, "Unread result or publication retention blocked owned completion.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
        }
        foreach (var admitted in new[] { false, true })
        {
            CompletionsRun? observedRun = null; var responseEntered = Gate(); var releaseResponse = Gate();
            var calls = 0; var selfDenied = false;
            using var fixture = new Fixture(Wire(Finish, "[DONE]"), hooks: new()
            {
                OnResponse = async (_, _, token) => { responseEntered.TrySetResult(); await releaseResponse.Task.WaitAsync(token); },
                OnSourcePublished = publication =>
                {
                    calls++;
                    if (publication.Emission.Raw.Value.GetProperty("value").GetProperty("type").GetString() != "text_delta") return;
                    try { _ = observedRun!.DisposeAsync(); } catch (InvalidOperationException) { selfDenied = true; }
                    if (admitted) throw new CompletionsPublicFailureException(new("Authored publication observer failure"));
                    throw new IOException("PRIVATE_SOURCE_FAILURE");
                }
            });
            await using var run = observedRun = await fixture.Transport.StartAsync(Request()); var reading = Drain(run);
            try
            {
                await responseEntered.Task.WaitAsync(Deadline); releaseResponse.TrySetResult();
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                var source = await run.SourceResult.WaitAsync(Deadline);
                Check(selfDenied && calls == 3 && run.SourcePublications.Length == 4 &&
                    run.SourcePublications[^1].Emission.Raw.Value.GetProperty("value").GetProperty("type").GetString() == "error" &&
                    source.Snapshot.Raw.Value.GetProperty("value").GetProperty("errorMessage").GetString() ==
                        (admitted ? "Authored publication observer failure" : "Completions operation failed.") && !run.CanonicalCompletion.IsCompleted,
                    "A publication observer self-joined, recursively observed its own failure, lost safe data or escaped cleanup.");
                PrivateAbsent(source.Snapshot.Raw.ToString()); fixture.Body.ReleaseCleanup.TrySetResult();
                await reading.WaitAsync(Deadline); var canonical = await run.CanonicalCompletion.WaitAsync(Deadline);
                Check(canonical.Failure is not null && fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed,
                    "A throwing publication observer retained an owner or granted canonical success.");
                Check(canonical.Failure!.Kind == ChatFailureKind.Provider && canonical.Message.StopReason == StopReason.Error &&
                    canonical.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed &&
                    canonical.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() ==
                        (admitted ? "Authored publication observer failure" : "Completions stream did not complete."),
                    "Joined observer failure projection lost admitted data, copied private text or changed native authority.");
                PrivateAbsent(PiWireJson.WriteMessage(canonical.Message).ToString());
            }
            finally { releaseResponse.TrySetResult(); fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        }
        var bounded = new ConcurrentQueue<CompletionsSourcePublication>();
        using (var fixture = new Fixture(Wire("{\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 2048) + "\"}}]}", "[DONE]"),
            maximumValue: 1024, hooks: new() { OnSourcePublished = bounded.Enqueue }))
        {
            await using var run = await fixture.Transport.StartAsync(Request()); var reading = Drain(run);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); fixture.Body.ReleaseCleanup.TrySetResult();
                await Observe(reading); await Observe(run.SourceResult);
                Check(bounded.Count == 1 && bounded.All(item => item.Emission.Raw.ToString().Length <= 1024) &&
                    (await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is not null && fixture.Content.Disposed,
                    "The publication callback observed an unadmitted oversized frame or retained an unsettled owner.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        }
    }

    private static async Task CompactAbortObservation()
    {
        using var caller = new CancellationTokenSource();
        using var fixture = new Fixture(Wire(Finish), holdRead: true, holdCancel: true, failCancel: true, captureSnapshots: true);
        await using var run = await new ChatClient(fixture.Transport, capacity: 1).StartAsync(Request(), caller.Token);
        var frames = new ConcurrentQueue<StreamEvent>(); var reading = Consume();
        try
        {
            await fixture.Body.ReadEntered.Task.WaitAsync(Deadline); caller.Cancel();
            await fixture.CancelEntered.Task.WaitAsync(Deadline);
            Check(!run.Completion.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any(), "Source abort observation granted authority before actual cancel join.");
            fixture.ReleaseCancel.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!run.Completion.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any(), "Compact abort diagnostic escaped held physical cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
            var terminal = frames.OfType<StreamError>().Single(); var result = await run.Completion.WaitAsync(Deadline);
            var observation = CompletionsSourceEventProjection.ReadDrain(terminal)!;
            Check(observation.Value.GetProperty("value").GetProperty("error").GetProperty("errorMessage").GetString() == "Request was aborted" &&
                terminal.SourceEmissionSnapshot is null && terminal.Reason == StopReason.Aborted && result.Failure?.Kind == ChatFailureKind.Cancelled &&
                result.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() == "Request was aborted" &&
                PiWireJson.WriteMessage(terminal.Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString() &&
                fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                "Compact cancellation lost its actual owned source observation or changed immutable terminal/result authority.");
            PrivateAbsent(observation.ToString()); PrivateAbsent(PiWireJson.WriteMessage(result.Message).ToString());
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        async Task Consume() { await foreach (var frame in run.ReadEventsAsync()) frames.Enqueue(frame); }
    }

    private static async Task SafeFailureObservation()
    {
        foreach (var captured in new[] { false, true })
        foreach (var admitted in new[] { false, true })
        {
            using var fixture = new Fixture(Wire(Finish), captureSnapshots: captured, acquisitionFailure: admitted ?
                new CompletionsPublicFailureException(new("Authored safe failure observation")) : new IOException("PRIVATE_SOURCE_FAILURE"));
            await using var run = await new ChatClient(fixture.Transport).StartAsync(Request());
            var frames = new ConcurrentQueue<StreamEvent>(); var reading = Consume();
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any(), "Safe failure observation escaped physical ownership.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
                var terminal = frames.OfType<StreamError>().Single(); var result = await run.Completion.WaitAsync(Deadline);
                Check(result.Failure is not null && terminal.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed &&
                    result.Failure.Kind == ChatFailureKind.Provider && result.Message.StopReason == StopReason.Error &&
                    terminal.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() ==
                        (admitted ? "Authored safe failure observation" : "Completions stream did not complete.") &&
                    PiWireJson.WriteMessage(terminal.Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString(),
                    "A source compatibility observation rewrote native failure classification or terminal/result consistency.");
                if (captured)
                {
                    var source = CompletionsSourceEventProjection.ReadEmission(terminal)!.Value.GetProperty("value").GetProperty("error");
                    Check(source.GetProperty("errorMessage").GetString() ==
                        (admitted ? "Authored safe failure observation" : "Completions operation failed.") &&
                        !source.TryGetProperty("openAICompletionsFailure", out _) &&
                        CompletionsSourceEventProjection.ReadDrain(terminal)!.ToString() == terminal.SourceEmissionSnapshot!.ToString(),
                        "Source failure projection lost explicit admitted data or copied native authority into the public view.");
                }
                else Check(terminal.SourceEmissionSnapshot is null && terminal.SourceDrainSnapshot is null,
                    "Callback-absent compact canonical mode acquired an unsolicited full source observation.");
                PrivateAbsent(PiWireJson.WriteMessage(result.Message).ToString());
                if (terminal.SourceEmissionSnapshot is { } observation) PrivateAbsent(observation.ToString());
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
            async Task Consume() { await foreach (var frame in run.ReadEventsAsync()) frames.Enqueue(frame); }
        }
    }

    private static async Task RejectedAbortObservations()
    {
        var native = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, StopReason.Aborted);
        var good = "{\"value\":{\"type\":\"error\",\"reason\":\"aborted\",\"error\":" + PiWireJson.WriteMessage(native) + "},\"ownUndefinedPaths\":[]}";
        foreach (var raw in new string?[] { null, "{}", good.Replace("\"ownUndefinedPaths\":[]", "\"ownUndefinedPaths\":null", StringComparison.Ordinal),
            good.Replace(Model.Id, "foreign-model", StringComparison.Ordinal),
            good[..^1] + ",\"padding\":\"" + new string('x', 8_388_608) + "\"}" })
        {
            using var caller = new CancellationTokenSource();
            var terminal = new StreamError(StopReason.Aborted, native) { SourceDrainSnapshot = raw is null ? null : JsonData.Parse(raw) };
            await using var run = await new ChatClient(new AuthoredAbort(terminal, caller.Cancel)).StartAsync(Request(), caller.Token);
            var frames = new List<StreamEvent>(); await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
            var result = await run.Completion.WaitAsync(Deadline); var compact = frames.OfType<StreamError>().Single();
            Check(compact.SourceDrainSnapshot is null && result.Failure?.Kind == ChatFailureKind.Cancelled &&
                result.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() == "Chat run was cancelled." &&
                PiWireJson.WriteMessage(compact.Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString(),
                "An absent, invalid, oversized or foreign source observation gained compact cancellation admission.");
        }
    }

    private static async Task CanonicalFailureBounds()
    {
        foreach (var failure in new[] { new string('x', 8192), new string('\u03c0', 4096) })
        {
            using var fixture = new Fixture(Wire(Finish), acquisitionFailure: new CompletionsPublicFailureException(new(failure)));
            await using var run = await fixture.Transport.StartAsync(Request()); var reading = Drain(run);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check((await run.SourceResult.WaitAsync(Deadline)).Snapshot.Raw.Value.GetProperty("value").GetProperty("errorMessage").GetString() == failure &&
                    !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted,
                    "Inclusive admitted failure limits or held canonical ownership changed.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
                var canonical = await run.CanonicalCompletion.WaitAsync(Deadline);
                Check(canonical.Failure?.Kind == ChatFailureKind.Provider && canonical.Message.StopReason == StopReason.Error &&
                    canonical.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() == failure &&
                    canonical.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed &&
                    fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                    "Joined canonical projection lost bounded admitted UTF-8 data or changed failure/cleanup authority.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        }
        using (var fixture = new Fixture(Wire(Finish), maximumValue: 1024,
            acquisitionFailure: new CompletionsPublicFailureException(new(new string('x', 8192)))))
        {
            await using var run = await fixture.Transport.StartAsync(Request()); var reading = Drain(run);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); await Observe(run.SourceResult);
                Check(run.SourceResult.IsFaulted && !run.CanonicalCompletion.IsCompleted,
                    "A failed configured source admission settled canonical ownership while cleanup was held.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await Observe(reading);
                var canonical = await run.CanonicalCompletion.WaitAsync(Deadline);
                Check(canonical.Failure?.Kind == ChatFailureKind.Provider && canonical.Message.StopReason == StopReason.Error &&
                    canonical.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() == "Completions stream did not complete." &&
                    canonical.NativeDiagnostic?.Code == NativeChatFailureCode.ResourceLimit && fixture.Content.Disposed,
                    "Canonical projection bypassed configured source admission bounds or overwrote the resource-limit terminal.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        }
    }

    private static async Task CleanupCreatedFailure()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), failPhysical: true,
            physicalFailure: new CompletionsPublicFailureException(new("Authored physical cleanup failure")));
        await using var run = await fixture.Transport.StartAsync(Request()); var reading = Drain(run);
        try
        {
            await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            var source = await run.SourceResult.WaitAsync(Deadline);
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted,
                "The cleanup-failure control lost its semantic success or actual physical join.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
            var canonical = await run.CanonicalCompletion.WaitAsync(Deadline); var cleanup = await run.CleanupCompletion.WaitAsync(Deadline);
            Check(!cleanup.Succeeded && cleanup.Failure?.Kind == ChatFailureKind.Provider && canonical.Failure?.Kind == ChatFailureKind.Provider &&
                canonical.Message.StopReason == StopReason.Error && canonical.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() ==
                    "Completions stream did not complete." &&
                canonical.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed &&
                fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                "An admitted cleanup exception rewrote the native cleanup-created failure or granted semantic success authority.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
    }

    private static async Task MatchedAbortFinal()
    {
        foreach (var scenario in new[] { "matched", "no-message", "private-text", "foreign-model", "unrelated-api", "oversized-bytes" })
        {
            using var caller = new CancellationTokenSource();
            var model = scenario == "unrelated-api" ? Model with { Api = "openai-responses" } : Model;
            var native = new AssistantMessage(model.Api, model.Provider, model.Id, 123, [], TokenUsage.Zero, StopReason.Aborted);
            var source = scenario == "no-message" ? native : native with
            { ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(scenario == "private-text" ? "\"PRIVATE_SOURCE_FAILURE\"" : "\"Request was aborted\"")) };
            if (scenario == "foreign-model") source = source with { Model = "foreign-model" };
            var raw = "{\"value\":{\"type\":\"error\",\"reason\":\"aborted\",\"error\":" + PiWireJson.WriteMessage(source) + "},\"ownUndefinedPaths\":[]}";
            if (scenario == "oversized-bytes") raw = raw[..^1] + ",\"padding\":\"" + new string('\u03c0', 4_194_304) + "\"}";
            var terminal = new StreamError(StopReason.Aborted, native) { SourceDrainSnapshot = JsonData.Parse(raw) };
            await using var run = await new ChatClient(new AuthoredAbort(terminal, caller.Cancel)).StartAsync(new(model, [], 123), caller.Token);
            var frames = new List<StreamEvent>(); await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
            var result = await run.Completion.WaitAsync(Deadline); var compact = frames.OfType<StreamError>().Single();
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && result.Failure!.Message == "Chat run was cancelled." &&
                compact.Reason == StopReason.Aborted && result.Message.ExtraProperties!.Values["errorMessage"].Value.GetString() ==
                    (scenario == "matched" ? "Request was aborted" : "Chat run was cancelled.") &&
                PiWireJson.WriteMessage(compact.Message).ToString() == PiWireJson.WriteMessage(result.Message).ToString(),
                "Fixed abort projection used an unmatched, unrelated, oversized or arbitrary source text, or changed native cancellation authority.");
            if (scenario is "foreign-model" or "unrelated-api" or "oversized-bytes")
                Check(compact.SourceDrainSnapshot is null, "Rejected abort context gained diagnostic admission.");
            else Check(compact.SourceDrainSnapshot!.ToString() == raw, "Abort text projection fabricated or rewrote the actual admitted observation.");
            PrivateAbsent(PiWireJson.WriteMessage(result.Message).ToString());
        }
    }

    private sealed class AuthoredAbort(StreamError terminal, Action cancel) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; cancel(); yield return terminal; }
    }

    private static async Task AbortBoundary()
    {
        using var fixture = new Fixture(Wire(Finish), holdRead: true, holdCancel: true, failCancel: true);
        using var caller = new CancellationTokenSource();
        await using var run = await fixture.Transport.StartAsync(Request(), caller.Token);
        var drain = Drain(run);
        try
        {
            await fixture.Body.ReadEntered.Task.WaitAsync(Deadline); caller.Cancel();
            await fixture.CancelEntered.Task.WaitAsync(Deadline);
            var source = await run.SourceResult.WaitAsync(Deadline);
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("errorMessage").GetString() == "Request was aborted" &&
                !run.CleanupCompletion.IsCompleted && !run.CanonicalCompletion.IsCompleted && !fixture.Body.Disposed,
                "Abort source result lost its separate actual owned-cleanup boundary.");
            await fixture.ReleaseEntered.Task.WaitAsync(Deadline);
            Check(fixture.Reader!.Releases == 1 && !fixture.Reader.Locked, "Independent release did not actually close the read lease.");
            await DeniedRead(fixture.Reader);
            fixture.ReleaseCancel.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!run.CanonicalCompletion.IsCompleted, "Rejected cancellation skipped physical cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
            var cleanup = await run.CleanupCompletion.WaitAsync(Deadline); var canonical = await run.CanonicalCompletion.WaitAsync(Deadline);
            Check(!cleanup.Succeeded && cleanup.Failure is not null && canonical.Failure is not null && canonical.Message.StopReason == StopReason.Aborted &&
                fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed, "Actual cancellation fault was lost or owned cleanup duplicated.");
            PrivateAbsent(source.Snapshot.Raw.ToString()); PrivateAbsent(PiWireJson.WriteMessage(canonical.Message).ToString());
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
    }

    private static async Task NormalBoundary()
    {
        foreach (var done in new[] { true, false })
        {
            using var fixture = new Fixture(done ? Wire(Finish, "[DONE]") : Wire(Finish), holdCancel: done, failCancel: done);
            await using var run = await fixture.Transport.StartAsync(Request()); var drain = Drain(run);
            try
            {
                if (done)
                {
                    await fixture.CancelEntered.Task.WaitAsync(Deadline);
                    Check(!run.SourceResult.IsCompleted && !run.CleanupCompletion.IsCompleted && fixture.Reader!.Releases == 0,
                        "Normal exact DONE settled before its real reader cancel promise.");
                    fixture.ReleaseCancel.TrySetResult();
                }
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                var source = await run.SourceResult.WaitAsync(Deadline);
                Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                    fixture.Reader!.Cancels == (done ? 1 : 0) && fixture.Reader.Releases == 1 && !run.CanonicalCompletion.IsCompleted,
                    "DONE/EOF changed source reader effects or canonical ownership.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
                Check((await run.CanonicalCompletion).Failure is null && (await run.CleanupCompletion).Succeeded,
                    "Exact DONE reader fault changed successful physical ownership policy.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
    }

    private static async Task DefaultToolBarrier()
    {
        using var fixture = new Fixture(Wire(Tool, "[DONE]"));
        await using var run = await new ChatClient(fixture.Transport, capacity: 1).StartAsync(Request());
        var events = new List<StreamEvent>(); var started = Gate(); var reading = ReadCanonical(run, events, started);
        try
        {
            await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            await started.Task.WaitAsync(Deadline);
            Check(events.OfType<ToolCallStarted>().Any() && !events.OfType<ToolCallEnded>().Any() && !events.OfType<StreamTerminalEvent>().Any() && !run.Completion.IsCompleted,
                "Default tool completion gained authority before actual physical cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
            var result = await run.Completion;
            Check(result.Failure is null && result.Message.StopReason == StopReason.ToolUse && events.OfType<ToolCallEnded>().Count() == 1 &&
                events.OfType<StreamDone>().Count() == 1 && fixture.Content.Disposed,
                "Joined default tool finalization changed after introducing source semantics.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        using var early = new Fixture(Wire(Tool, "[DONE]"), holdRead: true, capacity: 1);
        await using var reader = early.Transport.StreamAsync(Request()).GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current is StreamStarted, "Default early-return control lost Start.");
        // Establish an actual acquired reader before testing its held asynchronous cleanup.
        // Returning at Start now correctly leaves this body unacquired.
        var toolProgress = reader.MoveNextAsync().AsTask();
        await early.Body.ReadEntered.Task.WaitAsync(Deadline); early.Body.ReleaseRead.TrySetResult();
        Check(await toolProgress.WaitAsync(Deadline) && reader.Current is ToolCallStarted,
            "Default early-return control did not acquire its actual tool reader before close.");
        var closed = reader.DisposeAsync().AsTask();
        try
        {
            await early.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!closed.IsCompleted && !early.Content.Disposed, "Canonical early close abandoned its retained drain/physical owner.");
            early.Body.ReleaseCleanup.TrySetResult(); await closed.WaitAsync(Deadline);
            Check(early.Content.Disposed && early.Body.AsyncCloses == 1, "Early canonical drain did not share actual cleanup settlement.");
        }
        finally { early.Release(); await reader.DisposeAsync(); await Observe(closed); }
    }

    private static async Task ReturnAndDrain()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdRead: true, capacity: 1);
        await using var run = await fixture.Transport.StartAsync(Request());
        try
        {
            var first = await run.NextAsync(); Check(!first.Done && first.Value!.Type == "start", "Actual next operation lost Start.");
            var pending = run.NextAsync().AsTask(); await fixture.Body.ReadEntered.Task.WaitAsync(Deadline);
            var denied = false; try { await run.NextAsync(); } catch (InvalidOperationException) { denied = true; }
            Check(denied, "A second source reader gained queue authority.");
            var firstReturn = run.ReturnAsync().AsTask(); var secondReturn = run.ReturnAsync().AsTask();
            var result = await firstReturn.WaitAsync(Deadline); await secondReturn.WaitAsync(Deadline); await pending.WaitAsync(Deadline);
            Check(result.Done && result.Value is null && ReferenceEquals(firstReturn, secondReturn) &&
                result.Snapshot.Raw.Value.GetProperty("ownUndefinedPaths")[0].GetString() == "/value" && !fixture.Body.LastToken.IsCancellationRequested,
                "Actual return DTO, shared return settlement or producer continuation changed.");
            fixture.Body.ReleaseRead.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check((await run.SourceResult.WaitAsync(Deadline)).Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop",
                "Returning the consumer canceled its provider producer.");
            fixture.Body.ReleaseCleanup.TrySetResult(); Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null &&
                (await run.CleanupCompletion).Succeeded && (await run.NextAsync()).Done, "Discard drain did not join canonical ownership.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
        using var canceledReaderFixture = new Fixture(Wire(Finish), holdRead: true);
        using var readerToken = new CancellationTokenSource();
        await using var canceledRun = await canceledReaderFixture.Transport.StartAsync(Request());
        Check(!(await canceledRun.NextAsync(readerToken.Token)).Done, "Read-cancellation control lost Start.");
        var canceledNext = canceledRun.NextAsync(readerToken.Token).AsTask();
        try
        {
            await canceledReaderFixture.Body.ReadEntered.Task.WaitAsync(Deadline); readerToken.Cancel();
            await canceledRun.SourceResult.WaitAsync(Deadline); await canceledReaderFixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!canceledNext.IsCompleted && !canceledRun.CleanupCompletion.IsCompleted && !canceledRun.CanonicalCompletion.IsCompleted,
                "Explicit read cancellation escaped actual joined ownership.");
            canceledReaderFixture.Body.ReleaseCleanup.TrySetResult();
            var canceled = false; try { await canceledNext.WaitAsync(Deadline); } catch (OperationCanceledException) { canceled = true; }
            var canceledCanonical = await canceledRun.CanonicalCompletion;
            Check(canceled && canceledCanonical.Failure?.Kind == ChatFailureKind.Cancelled && canceledReaderFixture.Content.Disposed,
                "Read cancellation lost its actual canceled operation or physical cleanup barrier. nextCanceled=" + canceled +
                "; nextStatus=" + canceledNext.Status + "; canonicalFailure=" + canceledCanonical.Failure?.Kind +
                "; canonicalReason=" + canceledCanonical.Message.StopReason + "; responseDisposed=" + canceledReaderFixture.Content.Disposed + ".");
        }
        finally { canceledReaderFixture.Release(); canceledRun.Cancel(); await canceledRun.DisposeAsync(); await Observe(canceledNext); }
    }

    private static async Task AliasAndFailure()
    {
        foreach (var admitted in new[] { false, true })
        {
            using var fixture = new Fixture(Wire(Finish), acquisitionFailure: admitted ?
                new CompletionsPublicFailureException(new("Authored owned-response acquire fault")) : new IOException("PRIVATE_SOURCE_FAILURE"));
            await using var run = await fixture.Transport.StartAsync(Request());
            try
            {
                var start = (await run.NextAsync()).Value!; var emitted = start.Emission.Raw.ToString();
                var source = await run.SourceResult.WaitAsync(Deadline);
                Check(ReferenceEquals(start.Message, source) && start.Emission.Raw.ToString() == emitted &&
                    start.Emission.Raw.Value.GetProperty("value").GetProperty("partial").GetProperty("stopReason").GetString() == "pending" &&
                    start.Snapshot.Raw.Value.GetProperty("value").GetProperty("partial").GetProperty("stopReason").GetString() == "error",
                    "Queued source alias failed to observe its actual producer-owned late failure.");
                var text = source.Snapshot.Raw.Value.GetProperty("value").GetProperty("errorMessage").GetString();
                Check(text == (admitted ? "Authored owned-response acquire fault" : "Completions operation failed."),
                    "Source errors inferred unsafe arbitrary exception messages or lost admitted public data.");
                PrivateAbsent(source.Snapshot.Raw.ToString());
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); fixture.Body.ReleaseCleanup.TrySetResult();
                Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is not null && fixture.Content.Disposed,
                    "Source failure granted canonical success or retained the response.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
        }
    }

    private static async Task CancellationOwnership()
    {
        using var fixture = new Fixture(Wire(Finish), holdRead: true, ignoreCancellation: true, throwCallback: true);
        await using var run = await fixture.Transport.StartAsync(Request()); var drain = Drain(run);
        try
        {
            await fixture.Body.ReadEntered.Task.WaitAsync(Deadline); run.Cancel();
            var source = await run.SourceResult.WaitAsync(Deadline);
            var first = run.DisposeAsync().AsTask(); var second = run.DisposeAsync().AsTask();
            Check(!first.IsCompleted && ReferenceEquals(first, second) && !run.CleanupCompletion.IsCompleted &&
                !run.CanonicalCompletion.IsCompleted && fixture.Reader!.Releases == 1 && !fixture.Content.Disposed && fixture.Body.AsyncCloses == 0,
                "Logical source release abandoned noncooperative physical read ownership or duplicated disposal.");
            fixture.Body.ReleaseRead.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!run.CanonicalCompletion.IsCompleted && fixture.Reader!.Releases == 1 &&
                source.Snapshot.Raw.Value.GetProperty("value").GetProperty("content").GetArrayLength() == 0,
                "Late canceled bytes changed the source result or bypassed physical cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline); await drain.WaitAsync(Deadline);
            Check((await run.CanonicalCompletion).Failure is not null && !(await run.CleanupCompletion).Succeeded && fixture.Body.AsyncCloses == 1,
                "Caller callback failure or actual ownership settlement was lost.");
        }
        finally { fixture.Release(); await run.DisposeAsync(); await Observe(drain); }
        var entered = Gate(); var release = Gate(); CompletionsRun? callbackRun = null; var selfDenied = false;
        var hooks = new CompletionsLifecycleHooks
        {
            OnResponse = async (_, _, _) =>
            {
                entered.TrySetResult(); await release.Task;
                try { await callbackRun!.DisposeAsync(); } catch (InvalidOperationException) { selfDenied = true; }
            }
        };
        using var callbackFixture = new Fixture(Wire(Finish, "[DONE]"), hooks: hooks);
        await using var owned = callbackRun = await callbackFixture.Transport.StartAsync(Request()); var reading = Drain(owned);
        try
        {
            await entered.Task.WaitAsync(Deadline); release.TrySetResult();
            await callbackFixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(selfDenied, "A producer callback was allowed to await its own disposal.");
            callbackFixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
            Check((await owned.CanonicalCompletion).Failure is null, "Self-join denial disposed or corrupted the healthy invocation.");
        }
        finally { release.TrySetResult(); callbackFixture.Release(); owned.Cancel(); await owned.DisposeAsync(); await Observe(reading); }
    }

    private static async Task AdmissionAndPhysicalFailure()
    {
        using (var fixture = new Fixture(Wire(Finish, "[DONE]"), failPhysical: true))
        {
            await using var run = await fixture.Transport.StartAsync(Request()); var drain = Drain(run);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check((await run.SourceResult.WaitAsync(Deadline)).Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                    !run.CanonicalCompletion.IsCompleted, "Source semantic Stop lost its actual independent canonical barrier.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
                Check((await run.CanonicalCompletion).Message.StopReason == StopReason.Error && !(await run.CleanupCompletion).Succeeded,
                    "Late physical fault produced contradictory successful canonical completion.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
        using (var fixture = new Fixture(Wire(Finish, "[DONE]"), maximumValue: 2))
        {
            var denied = false; try { await fixture.Transport.StartAsync(Request()); } catch (StreamLimitException) { denied = true; }
            Check(denied && fixture.Handler.SendCalls == 0, "Initial source admission gained HTTP effects before failing.");
        }
        using (var fixture = new Fixture(Wire(Finish), holdAcquisition: true))
        {
            await using var run = await fixture.Transport.StartAsync(Request()); var drain = Drain(run);
            try
            {
                await fixture.AcquisitionEntered.Task.WaitAsync(Deadline); run.Cancel();
                await run.SourceResult.WaitAsync(Deadline); await drain.WaitAsync(Deadline);
                Check(fixture.AcquisitionToken.IsCancellationRequested && (await run.CanonicalCompletion.WaitAsync(Deadline)).Failure?.Kind == ChatFailureKind.Cancelled &&
                    (await run.CleanupCompletion).Succeeded && fixture.Content.Disposed,
                    "Run.Cancel did not own actual body acquisition cancellation and cleanup independently of a caller token.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
        using (var fixture = new Fixture(Wire("{\"choices\":[{\"delta\":{\"content\":\"" + new string('x', 2048) + "\"},\"finish_reason\":\"stop\"}]}", "[DONE]"), maximumValue: 1024))
        {
            await using var run = await fixture.Transport.StartAsync(Request()); var drain = Drain(run);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); fixture.Body.ReleaseCleanup.TrySetResult();
                await Observe(drain); await Observe(run.SourceResult);
                Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is not null && run.CleanupCompletion.IsCompleted && fixture.Content.Disposed,
                    "Source frame overflow left a promise or physical owner unsettled.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
        using (var fixture = new Fixture(Wire(Finish, "[DONE]")))
        {
            await using var run = await fixture.Transport.StartAsync(Request() with { Timestamp = 9007199254740993 }); var drain = Drain(run);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline); var source = await run.SourceResult.WaitAsync(Deadline);
                Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("timestamp").GetRawText() == "9007199254740993" &&
                    source.Snapshot.SerializedJson.Contains("9007199254740992", StringComparison.Ordinal), "Actual production numeric serialization changed retained raw ownership.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await drain.WaitAsync(Deadline);
                Check((await run.CanonicalCompletion).Message.Timestamp == 9007199254740993, "Compatibility view changed native numeric identity.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(drain); }
        }
        Check(new CompletionsPublicFailure(new string('x', 8192)).Message.Length == 8192 &&
            new CompletionsPublicFailure(new string('\u03c0', 4096)).Message.Length == 4096, "Inclusive public failure data limits rejected owned values.");
        foreach (var value in new[] { "", "bad\0data", "bad\ud800", new string('x', 8193), new string('\u03c0', 4097) })
        { var denied = false; try { _ = new CompletionsPublicFailure(value); } catch (Exception error) when (error is ArgumentException or StreamProtocolException or CompletionsRequestException) { denied = true; } Check(denied, "Malformed public failure data was admitted."); }
    }

    private sealed class Fixture : IDisposable
    {
        public Body Body { get; }
        public StreamProbeContent Content { get; }
        public FakeHttpHandler Handler { get; }
        public CompletionsHttpSseTransport Transport { get; }
        public Reader? Reader { get; private set; }
        public TaskCompletionSource CancelEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCancel { get; } = Gate();
        public TaskCompletionSource ReleaseEntered { get; } = Gate();
        public TaskCompletionSource AcquisitionEntered { get; } = Gate();
        public TaskCompletionSource ReleaseAcquisition { get; } = Gate();
        public CancellationToken AcquisitionToken { get; private set; }
        private readonly HttpClient _client;
        public Fixture(byte[] bytes, bool holdRead = false, bool holdCancel = false, bool failCancel = false, bool failPhysical = false,
            bool ignoreCancellation = false, bool throwCallback = false, int capacity = 32, int maximumValue = 8_388_608, Exception? acquisitionFailure = null,
            bool holdAcquisition = false, CompletionsLifecycleHooks? hooks = null, bool captureSnapshots = false, Exception? physicalFailure = null)
        {
            Body = new(bytes, holdRead, failPhysical, ignoreCancellation, throwCallback, physicalFailure);
            Content = holdAcquisition ? new(Body, async token =>
            { AcquisitionToken = token; AcquisitionEntered.TrySetResult(); await ReleaseAcquisition.Task.WaitAsync(token); return Body; }) : new(Body);
            Handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content })); _client = new(Handler);
            Transport = new(_client, (_, _) => new(HttpMethod.Post, "https://source-run.invalid/completions") { Content = new StringContent("{}") },
                new() { Hooks = hooks, SourceEventCapacity = capacity, MaximumSourceValueCharacters = maximumValue, MaximumSourceValueBytes = maximumValue,
                    BodyReaderFactory = (body, _) => acquisitionFailure is null ? ValueTask.FromResult<ICompletionsResponseBodyReader>(Reader = new(this, body, holdCancel, failCancel)) :
                        ValueTask.FromException<ICompletionsResponseBodyReader>(acquisitionFailure) },
                completionsOptions: captureSnapshots ? CompletionsSourceEventProjection.CaptureOwnedSnapshots() : null);
        }
        public void Release() { Body.ReleaseRead.TrySetResult(); Body.ReleaseCleanup.TrySetResult(); ReleaseCancel.TrySetResult(); ReleaseAcquisition.TrySetResult(); }
        public void Dispose() { Release(); _client.Dispose(); if (!Body.Disposed) Body.Dispose(); }
    }
    private sealed class Reader(Fixture owner, Stream body, bool held, bool fail) : ICompletionsResponseBodyReader
    {
        private readonly ICompletionsResponseBodyReader _core = CompletionsResponseBodyReader.FromStream(body);
        public int Cancels { get; private set; }
        public int Releases { get; private set; }
        public bool Locked { get; private set; } = true;
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) => _core.ReadAsync(destination, token);
        public async ValueTask CancelAsync()
        { Cancels++; owner.CancelEntered.TrySetResult(); await _core.CancelAsync(); if (held) await owner.ReleaseCancel.Task; if (fail) throw new IOException("PRIVATE_SOURCE_FAILURE"); }
        public void Release() { _core.Release(); Releases++; Locked = false; owner.ReleaseEntered.TrySetResult(); }
    }
    private sealed class Body(byte[] bytes, bool held, bool fail, bool ignore, bool throwing, Exception? physicalFailure) : Stream
    {
        private int _offset; private Task? _closing;
        public bool Disposed { get; private set; }
        public int AsyncCloses { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource ReleaseRead { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            LastToken = token; ReadEntered.TrySetResult(); using var registration = throwing ? token.Register(() => throw new IOException("PRIVATE_SOURCE_FAILURE")) : default;
            if (held && _offset == 0) { if (ignore) await ReleaseRead.Task; else await ReleaseRead.Task.WaitAsync(token); }
            if (_offset == bytes.Length) return 0;
            var count = Math.Min(destination.Length, bytes.Length - _offset); bytes.AsMemory(_offset, count).CopyTo(destination); _offset += count; return count;
        }
        public override ValueTask DisposeAsync() => new(_closing ??= CloseOwnedAsync());
        private async Task CloseOwnedAsync()
        { AsyncCloses++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Disposed = true; if (fail) throw physicalFailure ?? new IOException("PRIVATE_SOURCE_FAILURE"); }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static async Task Drain(CompletionsRun run) { await foreach (var _ in run.ReadSourceEventsAsync()) { } }
    private static async Task ReadCanonical(ChatRun run, List<StreamEvent> frames, TaskCompletionSource started)
    { await foreach (var frame in run.ReadEventsAsync()) { frames.Add(frame); if (frame is ToolCallStarted) started.TrySetResult(); } }
    private static async Task DeniedRead(ICompletionsResponseBodyReader reader)
    { var denied = false; try { await reader.ReadAsync(new byte[1]); } catch (InvalidOperationException) { denied = true; } Check(denied, "Released lease admitted a new read."); }
    private static ChatRequest Request() => new(Model, [], 123);
    private static byte[] Wire(params string[] values) => Encoding.UTF8.GetBytes(string.Concat(values.Select(value => "data: " + value + "\n\n")));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static void PrivateAbsent(string value) => Check(!value.Contains("PRIVATE_SOURCE_FAILURE", StringComparison.Ordinal), "Private exception text escaped the admitted source boundary.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
