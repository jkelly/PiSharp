using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

// Authored causal controls for R42-SIMPLE-CONTROL-OWNERSHIP-01. These are not
// executed results or captured Pi expectations. Short guards exercise the same
// retained owner used by the original ten controls, with real factory/run tasks.
internal static class SimpleRepairOwnershipCases
{
    private enum Hold { StartDelivery, Cancellation, Disposal }
    private const string Wire = "data: {\"choices\":[{\"delta\":{\"content\":\"owned\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    internal static IEnumerable<(string Id, Func<CancellationTokenSource, SimpleRepairOwnership, Task> Test)> Cases(
        JsonData metadata, ChatRequest request, Func<SimpleRepairProgress, Task> persist)
    {
        yield return ("ownership-held-start-delivery-retains-late-run", (deadline, evidence) => ControlledHold(Hold.StartDelivery, metadata, request, deadline, evidence, persist));
        yield return ("ownership-held-cancellation-retains-client", (deadline, evidence) => ControlledHold(Hold.Cancellation, metadata, request, deadline, evidence, persist));
        yield return ("ownership-held-disposal-observes-late-cleanup-fault", (deadline, evidence) => ControlledHold(Hold.Disposal, metadata, request, deadline, evidence, persist));
    }

    private static async Task ControlledHold(Hold hold, JsonData metadata, ChatRequest request,
        CancellationTokenSource controllerDeadline, SimpleRepairOwnership evidence, Func<SimpleRepairProgress, Task> persist)
    {
        var receiptWritten = Gate(); var startEntered = Gate(); var startRelease = Gate(); var readerAcquired = Gate();
        var caseEntered = Gate(); var executionCreated = Gate();
        CancellationTokenSource? caseDeadline = null;
        using var body = new HeldBody(hold == Hold.Disposal);
        HeldReader? reader = null; CompletionsRun? actualRun = null;
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new BodyContent(body) }));
        using var client = new HttpClient(handler);
        SimpleRepairProgress? incompleteReceipt = null;
        var expectedOperation = hold == Hold.StartDelivery ? "original-start" : "original-case-execution";
        SimpleRepairOwnership? probe = null;
        async Task PersistProbe(SimpleRepairProgress record)
        {
            await persist(record); // Actual journal flush precedes the causal signal.
            if (record.Kind == "original-join-deadline" && record.Operation == expectedOperation)
            { incompleteReceipt = record; receiptWritten.TrySetResult(); }
        }
        var readerGate = new object(); var releaseRequested = false;
        var options = new CompletionsSimpleOptions(metadata)
        {
            ApiKey = "authored-inert-ownership-key",
            HttpOptions = new() { Retry = new(0), BodyReaderFactory = hold == Hold.Cancellation ? AcquireReader : null }
        };
        var factory = new CompletionsSimpleRequestFactory(client, SimpleRepairRegressionCases.EndpointFor(metadata), request.Model, options);
        Task<SimpleRepairRegressionCases.Observation>? execution = null;
        var laterCases = 0; Exception? executionFailure = null;
        var batch = SimpleRepairRegressionCases.RunBatchAsync(
        [
            (evidence.CaseId + "/controlled-first", async (deadline, owner) =>
            {
                caseDeadline = deadline; probe = owner; caseEntered.TrySetResult();
                execution = SimpleRepairRegressionCases.Execute(factory, request, deadline, owner, Start);
                executionCreated.TrySetResult();
                try { await execution; } catch (Exception error) { executionFailure = error; throw; }
            }),
            (evidence.CaseId + "/controlled-next", (_, _) => { laterCases++; return Task.CompletedTask; })
        ], PersistProbe, TimeSpan.FromMilliseconds(20));
        try
        {
            await SimpleRepairRegressionCases.ReachAsync(caseEntered.Task, batch, controllerDeadline.Token, "controlled case admission");
            await SimpleRepairRegressionCases.ReachAsync(executionCreated.Task, batch, controllerDeadline.Token, "original execution creation");
            var originalExecution = execution ?? throw new Exception("Original execution was not started.");
            if (hold == Hold.Cancellation)
            {
                await SimpleRepairRegressionCases.ReachAsync(readerAcquired.Task, originalExecution, controllerDeadline.Token, "reader acquisition");
                await SimpleRepairRegressionCases.ReachAsync((reader ?? throw new Exception("Actual reader was not acquired.")).ReadEntered.Task,
                    originalExecution, controllerDeadline.Token, "held reader read");
            }
            else await SimpleRepairRegressionCases.ReachAsync(hold == Hold.StartDelivery ? startEntered.Task : body.DisposalEntered.Task,
                originalExecution, controllerDeadline.Token, "held " + hold);
            (caseDeadline ?? throw new Exception("Actual case deadline was not acquired.")).Cancel();
            if (hold == Hold.Cancellation) await SimpleRepairRegressionCases.ReachAsync(
                (reader ?? throw new Exception("Reader was not acquired.")).CancellationEntered.Task,
                originalExecution, controllerDeadline.Token, "held reader cancellation");
            await SimpleRepairRegressionCases.ReachAsync(receiptWritten.Task, batch, controllerDeadline.Token, "durable incomplete receipt");
            Require(incompleteReceipt is { Incomplete: true, Nonpassing: true }, "durable incomplete/nonpassing receipt before release");
            Require(incompleteReceipt!.RetainedOriginalTasks.Count > 0, "receipt includes actual retained task identity/status");
            var actualOwner = probe ?? throw new Exception("Actual batch owner was not acquired.");
            Require(!originalExecution.IsCompleted && !batch.IsCompleted && actualOwner.RetainedOriginalTaskCount > 0,
                "execution/batch keep original task ownership beyond diagnostic deadline");
            Require(!handler.Disposed && laterCases == 0, "borrowed resources stay alive and later admission stays stopped");
            Release();
            var batchFailure = await evidence.JoinAsync(batch, "controlled-original-batch");
            if (batchFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(batchFailure).Throw();
            Require(originalExecution.IsCompleted && batch.IsCompleted && actualOwner.RetainedOriginalTaskCount == 0,
                "all exact original tasks settle before scopes unwind");
            Require(actualOwner.DiagnosticDeadlineExceeded && actualOwner.StopLaterCases && laterCases == 0 && executionFailure is not null &&
                batch.Result.Failed == 1 && batch.Result.StoppedForOwnershipDeadline && batch.Result.Cases.Count == 1 &&
                batch.Result.UnexecutedCaseIds is { Count: 1 },
                "deadline scenario stays nonpassing after release; no next case admitted");
            Require(!handler.Disposed, "helper does not dispose borrowed client/handler");
            var joinedRun = actualRun ?? throw new Exception("Actual factory run was not acquired.");
            try { _ = joinedRun.NextAsync(); throw new Exception("Late run was not disposed."); }
            catch (ObjectDisposedException) { }
            Require(joinedRun.SourceResult.IsCompleted && joinedRun.CleanupCompletion.IsCompleted && joinedRun.CanonicalCompletion.IsCompleted,
                "late run's semantic, physical and canonical tasks all observed");
            if (hold == Hold.Disposal)
                Require(joinedRun.CleanupCompletion.IsCompletedSuccessfully && !joinedRun.CleanupCompletion.Result.Succeeded &&
                    joinedRun.CleanupCompletion.Result.Failure?.NativeDiagnostic?.Code == NativeChatFailureCode.CleanupFailed,
                    "real late stream-disposal fault remains physical cleanup failure");
            if (hold == Hold.Cancellation)
                Require(reader!.Cancels == 1 && reader.Releases == 1, "held actual cancel/release each settle once");
            evidence.Add(new { kind = "controlled-ownership-results", hold = hold.ToString(),
                scenarioStatus = "EXPECTED NONPASSING; ORIGINAL OWNERS JOINED", laterCases,
                execution = originalExecution.Status.ToString(), batch = batch.Status.ToString(), body.AsyncDisposals,
                incompleteReceipt, actualBatchReport = batch.Result, completeProbeObservations = actualOwner.Snapshot(),
                executionFailure = executionFailure?.ToString() });
        }
        finally
        {
            Release();
            // Controller assertion/deadline/journal failures still release the authored
            // gate and join the original batch, including a late returned run/fault.
            _ = await evidence.JoinAsync(batch, "controlled-final-batch-join");
            if (execution is not null) _ = await evidence.JoinAsync(execution, "controlled-final-execution-join");
        }

        async Task<CompletionsRun> Start(CompletionsSimpleRequestFactory current, ChatRequest input, CancellationToken token)
        {
            var acquired = await current.StartAsync(input, token); actualRun = acquired;
            startEntered.TrySetResult();
            if (hold == Hold.StartDelivery) await startRelease.Task; // Owns the real late-delivered run.
            return acquired;
        }
        ValueTask<ICompletionsResponseBodyReader> AcquireReader(Stream stream, CancellationToken _)
        {
            HeldReader acquired;
            lock (readerGate)
            {
                reader = acquired = new(CompletionsResponseBodyReader.FromStream(stream));
                if (releaseRequested) acquired.ReleaseCancellation();
            }
            readerAcquired.TrySetResult();
            return ValueTask.FromResult<ICompletionsResponseBodyReader>(acquired);
        }
        void Release()
        {
            lock (readerGate) { releaseRequested = true; reader?.ReleaseCancellation(); }
            startRelease.TrySetResult(); body.ReleaseDisposal();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
    private sealed class BodyContent(Stream body) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new Exception("Unexpected response buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class HeldBody(bool holdDisposal) : MemoryStream(Encoding.UTF8.GetBytes(Wire), writable: false)
    {
        public TaskCompletionSource DisposalEntered { get; } = Gate();
        private readonly TaskCompletionSource disposalRelease = Gate();
        public int AsyncDisposals { get; private set; }
        public void ReleaseDisposal() => disposalRelease.TrySetResult();
        public override async ValueTask DisposeAsync()
        {
            AsyncDisposals++; DisposalEntered.TrySetResult();
            if (holdDisposal) await disposalRelease.Task;
            await base.DisposeAsync();
            if (holdDisposal) throw new IOException("Authored late original stream-disposal failure.");
        }
    }
    private sealed class HeldReader(ICompletionsResponseBodyReader inner) : ICompletionsResponseBodyReader
    {
        public TaskCompletionSource ReadEntered { get; } = Gate();
        public TaskCompletionSource CancellationEntered { get; } = Gate();
        private readonly TaskCompletionSource cancellationRelease = Gate(), readRelease = Gate();
        public int Cancels { get; private set; }
        public int Releases { get; private set; }
        public void ReleaseCancellation() { cancellationRelease.TrySetResult(); readRelease.TrySetResult(); }
        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        { ReadEntered.TrySetResult(); await readRelease.Task; return await inner.ReadAsync(destination, CancellationToken.None); }
        public async ValueTask CancelAsync()
        { Cancels++; CancellationEntered.TrySetResult(); await cancellationRelease.Task; readRelease.TrySetResult(); await inner.CancelAsync(); }
        public void Release() { Releases++; inner.Release(); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool condition, string criterion) { if (!condition) throw new Exception(criterion); }
}
