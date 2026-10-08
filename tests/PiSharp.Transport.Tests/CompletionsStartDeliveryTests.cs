using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

internal static class CompletionsStartDeliveryTests
{
    private static readonly ModelDescriptor Model = new("start-delivery", "openai-completions", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Finish = "{\"choices\":[{\"delta\":{\"content\":\"\\u03c0\\ud83d\\ude00\"},\"finish_reason\":\"stop\"}]}";
    private const string MultiFinish = "{\"choices\":[{\"delta\":{\"content\":\"first\",\"reasoning_content\":\"thought\",\"tool_calls\":[{\"index\":0,\"id\":\"paced-tool\",\"type\":\"function\",\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"path\\\":\\\"owned\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-start.synchronous-delivery-before-body-acquisition", SynchronousDelivery);
        yield return ("completions-start.awaited-factory-and-response-hook-before-delivery", AwaitedPreparation);
        yield return ("completions-start.acquisition-fault-after-start-and-hook-fault-before-start", PreparationFailures);
        yield return ("completions-start.held-read-and-done-eof-joined-physical-owner", JoinedOwnership);
        yield return ("completions-start.canonical-return-at-start-closes-unacquired-response", ReturnAtStart);
        yield return ("completions-start.caller-cancellation-at-handoff-and-throwing-callback", CancelAtStart);
        yield return ("completions-start.public-result-only-and-shared-disposal", PublicResultOnly);
        yield return ("completions-start.public-return-retains-provider-and-bounded-drain", PublicReturn);
        yield return ("completions-pacing.immutable-frames-share-one-demanded-sse-event", ImmutableFramePacing);
        yield return ("completions-pacing.capacity-one-multi-final-normal-fatal-and-held-cleanup", FinalBatchOwnership);
        yield return ("completions-pacing.early-close-and-cancel-at-immutable-frame-join-owner", CloseAndCancelAtImmutableFrame);
        yield return ("completions-pacing.chatrun-capacity-one-acquisition-before-return-and-joined-cleanup", ChatRunReturnOrdering);
        yield return ("completions-turn.synchronous-source-start-progress-and-late-live-alias", SourceStartupAliases);
        yield return ("completions-turn.cancel-before-await-retains-closed-read-and-joined-owner", CancelBeforeStartupAwait);
        yield return ("completions-turn.task-conversion-result-only-and-context-preservation", StartupTaskConversion);
    }

    private static async Task SourceStartupAliases()
    {
        const string progressChunk = "{\"choices\":[{\"delta\":{\"content\":\"\\u03c0\\ud83d\\ude00\"}}]}";
        const string finishChunk = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}";
        using var fixture = new Fixture(Wire(progressChunk, finishChunk, "[DONE]"), holdCleanup: true);
        await using var run = await fixture.Transport.StartAsync(Request());
        var frames = new List<CompletionsSourceEvent>(); var observed = new List<CompletionsSourceSnapshot>();
        try
        {
            Check(!run.SourceResult.IsCompleted && fixture.Body.Reads == 1 && fixture.Reader is not null,
                "The synchronous source decoded its first admitted read before its awaiting caller owned the run.");
            while (true)
            {
                var next = await run.NextAsync(); if (next.Done) break;
                frames.Add(next.Value!); observed.Add(next.Value!.Snapshot);
            }
            var start = observed[0].Raw.Value.GetProperty("value").GetProperty("partial");
            var progress = observed[1].Raw.Value.GetProperty("value").GetProperty("partial");
            Check(start.GetProperty("stopReason").GetString() == "pending" && start.GetProperty("content").GetArrayLength() == 0 &&
                progress.GetProperty("stopReason").GetString() == "pending" &&
                progress.GetProperty("content")[0].GetProperty("text").GetString() == "\u03c0\ud83d\ude00" &&
                frames.Select(frame => frame.Type).SequenceEqual(new[] { "start", "text_start", "text_delta", "text_end", "done" }),
                "The actual awaiting source consumer lost its initial or progressing live revision.");
            var source = await run.SourceResult.WaitAsync(Deadline);
            Check(ReferenceEquals(frames[0].Message, source) && frames[0].Snapshot.Raw.Value.GetProperty("value")
                .GetProperty("partial").GetProperty("stopReason").GetString() == "stop" &&
                frames[0].Emission.Raw.ToString() == observed[0].Raw.ToString() && !run.CanonicalCompletion.IsCompleted,
                "Startup delivery froze a live alias, rewrote an emission, or escaped held physical cleanup.");
            fixture.Body.ReleaseCleanup.TrySetResult();
            Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null && fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                "Source startup changed the joined canonical owner.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
    }

    private static async Task CancelBeforeStartupAwait()
    {
        var actualReads = new ConcurrentQueue<CompletionsBodyReadResult>();
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdRead: true, holdCleanup: true, onBodyRead: actualReads.Enqueue);
        using var caller = new CancellationTokenSource();
        var starting = fixture.Transport.StartAsync(Request(), caller.Token);
        await fixture.Body.ReadEntered.Task.WaitAsync(Deadline); caller.Cancel();
        await using var run = await starting;
        try
        {
            var source = await run.SourceResult.WaitAsync(Deadline);
            await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(actualReads.Count == 1 && actualReads.Single().Done && actualReads.Single().Value.IsEmpty &&
                source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "aborted" &&
                fixture.Body.Reads == 1 && !run.CanonicalCompletion.IsCompleted && !fixture.Content.Disposed,
                "Cancellation of an unawaited startup abandoned its actual closed read or physical owner.");
            var disposal = run.DisposeAsync().AsTask();
            fixture.Body.ReleaseCleanup.TrySetResult(); await disposal.WaitAsync(Deadline);
            Check((await run.CanonicalCompletion.WaitAsync(Deadline)).Failure?.Kind == ChatFailureKind.Cancelled &&
                fixture.Reader!.Cancels == 1 && fixture.Reader.Releases == 1 && fixture.Body.AsyncCloses == 1,
                "Startup cancellation lost its single read/cancel/release/disposal settlement.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
    }

    private static async Task StartupTaskConversion()
    {
        var context = new AsyncLocal<string?> { Value = "caller-owned-context" };
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdCleanup: true);
        await using var run = await fixture.Transport.StartAsync(Request()).AsTask().ConfigureAwait(false);
        try
        {
            var source = await run.SourceResult.WaitAsync(Deadline);
            Check(context.Value == "caller-owned-context" && source.Snapshot.Raw.Value.GetProperty("value")
                .GetProperty("stopReason").GetString() == "stop" && !run.CanonicalCompletion.IsCompleted,
                "Task conversion or result-only consumption lost caller context or retained owner authority.");
            var disposal = run.DisposeAsync().AsTask(); fixture.Body.ReleaseCleanup.TrySetResult(); await disposal.WaitAsync(Deadline);
            Check((await run.CleanupCompletion.WaitAsync(Deadline)).Succeeded && fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                "Task-converted startup stranded an unread source queue or its physical owner.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
    }

    private static async Task SynchronousDelivery()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"));
        await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
        try
        {
            Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted,
                "The actual synchronous invocation did not deliver Start.");
            fixture.Ledger.Enqueue("delivered-start");
            Check(fixture.Handler.SendCalls == 1 && fixture.Content.AcquireCalls == 0 && fixture.Body.Reads == 0 && fixture.Reader is null,
                "The synchronous producer acquired or read the body before delivering Start.");
            var frames = await Rest(events).WaitAsync(Deadline);
            Check(frames.OfType<StreamDone>().Count() == 1 && frames.OfType<TextDelta>().Single().Delta == "\u03c0\ud83d\ude00" &&
                fixture.Content.AcquireCalls == 1 && fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                "Resuming actual Start lost synchronous progress or joined response cleanup.");
            var ledger = fixture.Ledger.ToArray();
            Check(Array.IndexOf(ledger, "delivered-start") < Array.IndexOf(ledger, "acquire"), "Body acquisition overtook actual Start delivery.");
        }
        finally { fixture.Release(); await events.DisposeAsync(); }
    }

    private static async Task AwaitedPreparation()
    {
        var hookEntered = Gate(); var releaseHook = Gate(); var hookCalls = 0;
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdFactory: true, hooks: new()
        {
            OnResponse = async (response, model, token) =>
            {
                hookCalls++;
                Check(response.Status == 200 && response.Headers.Value.GetProperty("x-owned-start").GetString() == "actual" && model == Model,
                    "Response hook received fabricated metadata or the wrong model.");
                hookEntered.TrySetResult(); await releaseHook.Task.WaitAsync(token);
            }
        });
        await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
        var first = events.MoveNextAsync().AsTask();
        try
        {
            await fixture.FactoryEntered.Task.WaitAsync(Deadline);
            Check(!first.IsCompleted && fixture.Handler.SendCalls == 0 && fixture.Content.AcquireCalls == 0,
                "Held awaited construction admitted send, Start or body acquisition.");
            fixture.ReleaseFactory.TrySetResult(); await hookEntered.Task.WaitAsync(Deadline);
            Check(!first.IsCompleted && fixture.Handler.SendCalls == 1 && fixture.Content.AcquireCalls == 0,
                "Start or body acquisition overtook the actual awaited header hook.");
            releaseHook.TrySetResult();
            Check(await first.WaitAsync(Deadline) && events.Current is StreamStarted && fixture.Content.AcquireCalls == 0,
                "Actual header completion did not hand off Start before acquisition.");
            var frames = await Rest(events).WaitAsync(Deadline);
            Check(hookCalls == 1 && fixture.FactoryCalls == 1 && frames.OfType<StreamDone>().Count() == 1 && fixture.Content.Disposed,
                "Start handoff duplicated callbacks or abandoned the prepared response.");
        }
        finally { releaseHook.TrySetResult(); fixture.Release(); await Observe(first); await events.DisposeAsync(); }
    }

    private static async Task PreparationFailures()
    {
        using (var fixture = new Fixture(Wire(Finish), failAcquire: true))
        {
            await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted && fixture.Content.AcquireCalls == 0,
                "Reader acquisition failure preceded actual Start delivery.");
            var frames = await Rest(events).WaitAsync(Deadline);
            Check(frames.OfType<StreamError>().Count() == 1 && fixture.Content.AcquireCalls == 1 && fixture.Body.Reads == 0 && fixture.Content.Disposed,
                "Actual acquisition fault lost its terminal or response ownership.");
            PrivateAbsent(frames);
        }
        var hookCalls = 0;
        using (var fixture = new Fixture(Wire(Finish), hooks: new()
        {
            OnResponse = (_, _, _) => { hookCalls++; throw new IOException("PRIVATE_START_FAILURE"); }
        }))
        {
            await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            var frames = await Rest(events).WaitAsync(Deadline);
            Check(!frames.OfType<StreamStarted>().Any() && frames.OfType<StreamError>().Count() == 1 && hookCalls == 1 &&
                fixture.Content.AcquireCalls == 0 && fixture.Reader is null && fixture.Content.Disposed,
                "Failed actual header hook published Start, acquired a body or abandoned cleanup.");
            PrivateAbsent(frames);
        }
    }

    private static async Task JoinedOwnership()
    {
        foreach (var done in new[] { true, false })
        {
            using var fixture = new Fixture(done ? Wire(Finish, "[DONE]") : Wire(Finish), holdRead: true, holdCleanup: true);
            await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted && fixture.Content.AcquireCalls == 0,
                "Held-read control did not receive the pre-acquisition Start.");
            var frames = new ConcurrentQueue<StreamEvent>(); var reading = RestInto(events, frames);
            try
            {
                await fixture.Body.ReadEntered.Task.WaitAsync(Deadline);
                Check(!reading.IsCompleted && fixture.Content.AcquireCalls == 1 && fixture.Body.AsyncCloses == 0,
                    "Actual body read was not retained after the Start handoff.");
                fixture.Body.ReleaseRead.TrySetResult(); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!reading.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any() && !frames.OfType<TextEnded>().Any() &&
                    fixture.Reader!.Cancels == (done ? 1 : 0) && fixture.Reader!.Releases == 1 && !fixture.Content.Disposed,
                    "DONE/EOF changed real reader effects or released canonical authority before physical cleanup.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
                Check(frames.OfType<StreamDone>().Count() == 1 && fixture.Content.Disposed && fixture.Body.AsyncCloses == 1,
                    "Startup backpressure lost terminal consistency or joined cleanup.");
            }
            finally { fixture.Release(); await Observe(reading); await events.DisposeAsync(); }
        }
    }

    private static async Task ReturnAtStart()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdRead: true, holdCleanup: true);
        var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
        try
        {
            Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted,
                "Early-close control lost actual Start.");
            await events.DisposeAsync().AsTask().WaitAsync(Deadline);
            Check(fixture.Content.AcquireCalls == 0 && fixture.Body.Reads == 0 && fixture.Reader is null && fixture.Content.Disposed &&
                fixture.Body.Disposed && fixture.Body.AsyncCloses == 0 && fixture.RequestContent.DisposeCalls == 1,
                "Early canonical return acquired an unused body or failed to join its actual prepared response/request owner.");
        }
        finally { fixture.Release(); await events.DisposeAsync(); }
    }

    private static async Task CancelAtStart()
    {
        foreach (var throwing in new[] { false, true })
        {
            using var caller = new CancellationTokenSource();
            using var fixture = new Fixture(Wire(Finish, "[DONE]"), throwCancellationCallback: throwing);
            await using var events = fixture.Transport.StreamAsync(Request(), caller.Token).GetAsyncEnumerator();
            Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted,
                "Cancellation control lost actual Start delivery.");
            var callbackFault = false;
            try { caller.Cancel(); } catch (AggregateException) { callbackFault = true; }
            var frames = await Rest(events).WaitAsync(Deadline);
            Check(frames.OfType<StreamError>().Single().Reason == StopReason.Aborted && fixture.Content.AcquireCalls == 0 && fixture.Reader is null &&
                fixture.Content.Disposed && fixture.RequestContent.DisposeCalls == 1 && fixture.FactoryToken.IsCancellationRequested &&
                fixture.CancellationCalls == (throwing ? 1 : 0) && callbackFault == throwing,
                "Cancellation while holding Start lost actual canceled authority, duplicated callbacks or left its handoff owner unjoined.");
            PrivateAbsent(frames);
        }
    }

    private static async Task PublicResultOnly()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdCleanup: true);
        await using var run = await fixture.Transport.StartAsync(Request());
        try
        {
            var source = await run.SourceResult.WaitAsync(Deadline); await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                fixture.Content.AcquireCalls == 1 && !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted,
                "An unclaimed source event reader introduced a startup wait or lost the actual canonical cleanup barrier.");
            var first = run.DisposeAsync().AsTask(); var second = run.DisposeAsync().AsTask();
            Check(ReferenceEquals(first, second) && !first.IsCompleted && !fixture.Content.Disposed,
                "Concurrent disposal did not retain one actual bounded drain and physical cleanup settlement.");
            fixture.Body.ReleaseCleanup.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Check((await run.CanonicalCompletion).Failure is null && (await run.CleanupCompletion).Succeeded && fixture.Content.Disposed &&
                fixture.Body.AsyncCloses == 1 && fixture.RequestContent.DisposeCalls == 1,
                "Result-only source consumption did not dispose its retained queue and owner exactly once.");
        }
        finally { fixture.Release(); await run.DisposeAsync(); }
    }

    private static async Task PublicReturn()
    {
        using var fixture = new Fixture(Wire(Finish, "[DONE]"), holdRead: true, capacity: 1);
        await using var run = await fixture.Transport.StartAsync(Request());
        try
        {
            var first = await run.NextAsync().AsTask().WaitAsync(Deadline);
            Check(!first.Done && first.Value!.Type == "start", "Public source consumer did not receive actual Start.");
            await fixture.Body.ReadEntered.Task.WaitAsync(Deadline);
            var returned = await run.ReturnAsync().AsTask().WaitAsync(Deadline);
            Check(returned.Done && returned.Value is null && returned.Snapshot.OwnUndefinedPaths.SequenceEqual(new[] { "/value" }) &&
                !run.IsCancellationRequested && !run.SourceResult.IsCompleted && fixture.Reader!.Cancels == 0,
                "Public source return adopted canonical cancellation or fabricated its return DTO.");
            fixture.Body.ReleaseRead.TrySetResult(); var source = await run.SourceResult.WaitAsync(Deadline);
            Check(source.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                (await run.CanonicalCompletion.WaitAsync(Deadline)).Failure is null && fixture.Content.Disposed && fixture.Reader!.Cancels == 1,
                "Actual source return failed to retain, drain and join its bounded provider invocation.");
        }
        finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); }
    }

    private static async Task ImmutableFramePacing()
    {
        using var fixture = new Fixture(Wire(MultiFinish, "[DONE]"), framing: new(ReadBufferBytes: 1,
            EofBehavior: SseEofBehavior.DispatchPendingEvent) { Profile = SseFramingProfile.OpenAISdk719 });
        await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
        try
        {
            Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted && fixture.Body.Reads == 0,
                "Canonical demand did not hold actual Start before its body read.");
            var kinds = new[] { typeof(TextStarted), typeof(TextDelta), typeof(ThinkingStarted), typeof(ThinkingDelta),
                typeof(ToolCallStarted), typeof(ToolCallDelta) };
            var frames = new List<StreamEvent>();
            var eventBytes = Wire(MultiFinish).Length;
            foreach (var kind in kinds)
            {
                Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current.GetType() == kind,
                    "A demanded immutable frame was lost or reordered: " + kind.Name);
                frames.Add(events.Current);
                Check(fixture.Body.Reads == eventBytes && fixture.Body.AsyncCloses == 0 && fixture.Reader!.Cancels == 0,
                    "The producer read a later SSE event or began cleanup while an immutable frame was held: " + kind.Name);
            }
            var final = await Rest(events).WaitAsync(Deadline);
            Check(final.OfType<TextEnded>().Single().Content == "first" && final.OfType<ThinkingEnded>().Single().Content == "thought" &&
                final.OfType<ToolCallEnded>().Single().ToolCall.Arguments.ToString() == "{\"path\":\"owned\"}" &&
                final.OfType<StreamDone>().Single().Reason == StopReason.ToolUse &&
                frames.OfType<TextDelta>().Single().Delta == "first" && frames.OfType<ThinkingDelta>().Single().Delta == "thought",
                "Per-frame demand changed retained immutable data or final tool authority.");
        }
        finally { fixture.Release(); await events.DisposeAsync(); }
    }

    private static async Task FinalBatchOwnership()
    {
        foreach (var done in new[] { true, false })
        foreach (var fatal in new[] { false, true })
        {
            using var fixture = new Fixture(done ? Wire(MultiFinish, "[DONE]") : Wire(MultiFinish),
                holdCleanup: true, failCleanup: fatal, capacity: 1);
            await using var events = fixture.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            var frames = new ConcurrentQueue<StreamEvent>(); var reading = RestInto(events, frames);
            try
            {
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!reading.IsCompleted && frames.OfType<TextDelta>().Count() == 1 && frames.OfType<ThinkingDelta>().Count() == 1 &&
                    frames.OfType<ToolCallDelta>().Count() == 1 && !frames.Any(IsSuccessfulEnd) &&
                    !frames.OfType<StreamTerminalEvent>().Any() && !fixture.Content.Disposed &&
                    fixture.Reader!.Cancels == (done ? 1 : 0) && fixture.Reader.Releases == 1,
                    "A capacity-one final batch escaped the real held cleanup owner or lost demanded progress.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await reading.WaitAsync(Deadline);
                Check(fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed && fixture.RequestContent.DisposeCalls == 1,
                    "A capacity-one final batch duplicated or skipped actual cleanup.");
                if (fatal)
                    Check(!frames.Any(IsSuccessfulEnd) && frames.OfType<StreamError>().Single().Reason == StopReason.Error &&
                        !frames.OfType<StreamDone>().Any(), "Fatal physical cleanup released successful final blocks or terminal authority.");
                else
                    Check(frames.OfType<TextEnded>().Single().ContentIndex == 0 && frames.OfType<ThinkingEnded>().Single().ContentIndex == 1 &&
                        frames.OfType<ToolCallEnded>().Single().ContentIndex == 2 && frames.OfType<StreamDone>().Single().Reason == StopReason.ToolUse,
                        "The cleanup join deadlocked or dropped a multi-block capacity-one final batch.");
                PrivateAbsent(frames);
            }
            finally { fixture.Release(); await Observe(reading); await events.DisposeAsync(); }
        }
    }

    private static async Task CloseAndCancelAtImmutableFrame()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var caller = new CancellationTokenSource();
            using var fixture = new Fixture(Wire(MultiFinish, "[DONE]"), holdCleanup: true, capacity: 1);
            var events = fixture.Transport.StreamAsync(Request(), caller.Token).GetAsyncEnumerator();
            Task closing = Task.CompletedTask; var frames = new ConcurrentQueue<StreamEvent>();
            try
            {
                Check(await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is StreamStarted &&
                    await events.MoveNextAsync().AsTask().WaitAsync(Deadline) && events.Current is TextStarted && fixture.Body.AsyncCloses == 0,
                    "The close/cancellation control did not hold an actual immutable progress handoff.");
                if (cancel) { caller.Cancel(); closing = RestInto(events, frames); }
                else closing = events.DisposeAsync().AsTask();
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!closing.IsCompleted && !fixture.Content.Disposed && fixture.Reader!.Cancels == 1 && fixture.Reader.Releases == 1 &&
                    !frames.Any(IsSuccessfulEnd) && !frames.OfType<StreamTerminalEvent>().Any(),
                    "Close or cancellation abandoned its physical owner at an immutable-frame handoff.");
                fixture.Body.ReleaseCleanup.TrySetResult(); await closing.WaitAsync(Deadline);
                Check(fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed && fixture.RequestContent.DisposeCalls == 1 &&
                    (!cancel || frames.OfType<StreamError>().Single().Reason == StopReason.Aborted),
                    "Close or cancellation failed to join and settle the actual immutable-frame handoff once.");
                PrivateAbsent(frames);
            }
            finally { fixture.Release(); await Observe(closing); await events.DisposeAsync(); }
        }
    }

    private static bool IsSuccessfulEnd(StreamEvent frame) => frame is TextEnded or ThinkingEnded or ToolCallEnded;

    private static async Task ChatRunReturnOrdering()
    {
        using (var direct = new Fixture(Wire(MultiFinish, "[DONE]"), holdRead: true, holdCleanup: true, capacity: 1))
        {
            await using var first = direct.Transport.StreamAsync(Request()).GetAsyncEnumerator();
            Check(await first.MoveNextAsync().AsTask().WaitAsync(Deadline) && first.Current is StreamStarted &&
                direct.Content.AcquireCalls == 0 && direct.Body.Reads == 0 && direct.Reader is null,
                "Inline next-advance acknowledgment acquired the direct iterator body before its actual first Start delivery.");
            await first.DisposeAsync().AsTask().WaitAsync(Deadline);
            Check(direct.Content.Disposed && direct.Body.AsyncCloses == 0 && direct.RequestContent.DisposeCalls == 1,
                "Closing the direct first Start lost its unacquired response ownership.");
        }
        foreach (var cancel in new[] { false, true })
        {
            using var fixture = new Fixture(Wire(MultiFinish, "[DONE]"), holdRead: true, holdCleanup: true, capacity: 1);
            var client = new ChatClient(fixture.Transport, capacity: 1);
            await using var run = cancel ? await client.StartAsync(Request()) : await client.StartWithDetachedReaderAsync(Request());
            await using var reader = run.ReadEventsAsync().GetAsyncEnumerator();
            Task closing = Task.CompletedTask;
            try
            {
                Check(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline) && reader.Current is StreamStarted &&
                    fixture.Content.AcquireCalls == 1 && fixture.Body.Reads == 1 && fixture.Body.ReadEntered.Task.IsCompleted &&
                    fixture.Body.AsyncCloses == 0,
                    "The actual next transport advance did not acquire and enter the held read before outer ChatRun Start delivery.");
                fixture.Ledger.Enqueue("consumer-return");
                if (cancel) run.Cancel();
                await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
                var ledger = fixture.Ledger.ToArray();
                Check(Array.IndexOf(ledger, "acquire") < Array.IndexOf(ledger, "consumer-return") &&
                    Array.IndexOf(ledger, "read") < Array.IndexOf(ledger, "consumer-return"),
                    "Outer consumer return overtook the actual acknowledged acquisition/read.");
                if (cancel) closing = run.DisposeAsync().AsTask();
                else
                {
                    Check(!fixture.FactoryToken.IsCancellationRequested && !run.Completion.IsCompleted && fixture.Reader!.Cancels == 0,
                        "A detached outer return canceled its actual retained provider or settled the held read.");
                    fixture.Body.ReleaseRead.TrySetResult();
                }
                await fixture.Body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && (!cancel || !closing.IsCompleted) && !fixture.Content.Disposed &&
                    fixture.Reader!.Cancels == 1 && fixture.Reader.Releases == 1,
                    "Outer return/cancellation skipped its real reader effects or held physical cleanup join.");
                fixture.Body.ReleaseCleanup.TrySetResult();
                if (cancel) await closing.WaitAsync(Deadline);
                var result = await run.Completion.WaitAsync(Deadline);
                Check(fixture.Body.AsyncCloses == 1 && fixture.Content.Disposed && fixture.RequestContent.DisposeCalls == 1 &&
                    (cancel ? result.Failure?.Kind == ChatFailureKind.Cancelled : result.Failure is null && result.Message.StopReason == StopReason.ToolUse),
                    "Outer return/cancellation did not settle its same actual invocation and cleanup owner once.");
            }
            finally { fixture.Release(); run.Cancel(); await run.DisposeAsync(); await Observe(closing); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ConcurrentQueue<string> Ledger { get; } = new();
        public Body Body { get; }
        public StreamProbeContent Content { get; }
        public RequestProbeContent RequestContent { get; } = new();
        public FakeHttpHandler Handler { get; }
        public CompletionsHttpSseTransport Transport { get; }
        public Reader? Reader { get; private set; }
        public TaskCompletionSource FactoryEntered { get; } = Gate();
        public TaskCompletionSource ReleaseFactory { get; } = Gate();
        public CancellationToken FactoryToken { get; private set; }
        public int FactoryCalls { get; private set; }
        public int CancellationCalls { get; private set; }
        private readonly HttpClient _client;
        private CancellationTokenRegistration _registration;
        public Fixture(byte[] bytes, bool holdFactory = false, bool holdRead = false, bool holdCleanup = false,
            bool failAcquire = false, bool throwCancellationCallback = false, int capacity = 32, CompletionsLifecycleHooks? hooks = null,
            bool failCleanup = false, SseDecoderOptions? framing = null, Action<CompletionsBodyReadResult>? onBodyRead = null)
        {
            Body = new(bytes, Ledger, holdRead, holdCleanup, failCleanup);
            Content = new(Body, token =>
            {
                Ledger.Enqueue("acquire"); token.ThrowIfCancellationRequested();
                return failAcquire ? Task.FromException<Stream>(new IOException("PRIVATE_START_FAILURE")) : Task.FromResult<Stream>(Body);
            });
            Handler = new((_, _) =>
            {
                Ledger.Enqueue("send");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Content };
                response.Headers.Add("x-owned-start", "actual"); return Task.FromResult(response);
            });
            _client = new(Handler);
            Transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(_client, async (_, token) =>
            {
                FactoryCalls++; FactoryToken = token; Ledger.Enqueue("factory"); FactoryEntered.TrySetResult();
                if (throwCancellationCallback)
                    _registration = token.Register(() => { CancellationCalls++; throw new IOException("PRIVATE_START_FAILURE"); });
                if (holdFactory) await ReleaseFactory.Task.WaitAsync(token);
                return new(HttpMethod.Post, "https://start-delivery.invalid/completions") { Content = RequestContent };
            }, new(Framing: framing) { Hooks = hooks, SourceEventCapacity = capacity, OnBodyRead = onBodyRead,
                BodyReaderFactory = (body, _) => ValueTask.FromResult<ICompletionsResponseBodyReader>(Reader = new(body)) });
        }
        public void Release() { ReleaseFactory.TrySetResult(); Body.ReleaseRead.TrySetResult(); Body.ReleaseCleanup.TrySetResult(); }
        public void Dispose() { Release(); _registration.Dispose(); _client.Dispose(); if (!Body.Disposed) Body.Dispose(); }
    }

    private sealed class Reader(Stream body) : ICompletionsResponseBodyReader
    {
        private readonly ICompletionsResponseBodyReader _core = CompletionsResponseBodyReader.FromStream(body);
        public int Cancels { get; private set; }
        public int Releases { get; private set; }
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) => _core.ReadAsync(destination, token);
        public ValueTask CancelAsync() { Cancels++; return _core.CancelAsync(); }
        public void Release() { _core.Release(); Releases++; }
    }

    private sealed class Body(byte[] bytes, ConcurrentQueue<string> ledger, bool heldRead, bool heldCleanup, bool failCleanup) : Stream
    {
        private int _offset; private Task? _closing;
        public int Reads { get; private set; }
        public int AsyncCloses { get; private set; }
        public bool Disposed { get; private set; }
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
            Reads++; ledger.Enqueue("read"); ReadEntered.TrySetResult();
            if (heldRead && _offset == 0) await ReleaseRead.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var count = Math.Min(destination.Length, bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(destination); _offset += count; return count;
        }
        public override ValueTask DisposeAsync() => new(_closing ??= CloseOwnedBodyAsync());
        private async Task CloseOwnedBodyAsync()
        {
            AsyncCloses++; CleanupEntered.TrySetResult(); if (heldCleanup) await ReleaseCleanup.Task; Disposed = true;
            if (failCleanup) throw new IOException("PRIVATE_START_FAILURE");
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RequestProbeContent : HttpContent
    {
        public int DisposeCalls { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
    }
    private static async Task<List<StreamEvent>> Rest(IAsyncEnumerator<StreamEvent> events)
    { var frames = new List<StreamEvent>(); await RestInto(events, frames); return frames; }
    private static async Task RestInto(IAsyncEnumerator<StreamEvent> events, List<StreamEvent> frames)
    { while (await events.MoveNextAsync()) frames.Add(events.Current); }
    private static async Task RestInto(IAsyncEnumerator<StreamEvent> events, ConcurrentQueue<StreamEvent> frames)
    { while (await events.MoveNextAsync()) frames.Enqueue(events.Current); }
    private static ChatRequest Request() => new(Model, [], 123);
    private static byte[] Wire(params string[] values) => Encoding.UTF8.GetBytes(string.Concat(values.Select(value => "data: " + value + "\n\n")));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static void PrivateAbsent(IEnumerable<StreamEvent> frames)
    { foreach (var terminal in frames.OfType<StreamTerminalEvent>()) Check(!PiWireJson.WriteMessage(terminal.Message).ToString().Contains("PRIVATE_START_FAILURE", StringComparison.Ordinal), "Private exception text escaped canonical startup failure."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
