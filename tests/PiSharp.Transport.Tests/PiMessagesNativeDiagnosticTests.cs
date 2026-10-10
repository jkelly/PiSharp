using System.Net;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Agent;
using NativeAgent = PiSharp.Agent.Agent;
using PiSharp.Contracts;

/// <summary>Authored real HTTP/reader failure sites, independent of public text and Source JSON provenance.</summary>
internal static class PiMessagesNativeDiagnosticTests
{
    private static readonly ModelDescriptor Model = new("inert", "pi-messages", "authored");
    private static readonly JsonData Metadata = JsonData.Parse(
        """{"id":"inert","api":"pi-messages","provider":"authored","baseUrl":"https://pi-messages.invalid/v1","opaque":{"keep":null}}""");
    private const string Usage = """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""";
    private static string Success => Sse("""{"type":"start"}""", """{"type":"done","reason":"stop","usage":""" + Usage + """}""");
    private sealed class OwnedCaseState
    {
        public bool DeadlineExceeded;
        public string? IncompleteReceipt;
    }
    private static readonly OwnedCaseState CaseState = new();
    public static bool DeadlineExceeded => CaseState.DeadlineExceeded;
    public static string? IncompleteReceipt => CaseState.IncompleteReceipt;

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("pi-messages-native-diagnostics.inner-timeout-preserves-original-failure", InnerTimeoutPreservesFailure);
        yield return ("pi-messages-native-diagnostics.outer-deadline-retains-original-join", OuterDeadlineRetainsJoin);
        yield return ("pi-messages-native-diagnostics.all-eight-physical-terminal-result-codes", AllCodes);
        yield return ("pi-messages-native-diagnostics.held-primary-cleanup-precedence", HeldCleanupPrecedence);
        yield return ("pi-messages-native-diagnostics.narrow-chat-run-fallback-and-completions-preservation", ChatRunFallback);
        yield return ("pi-messages-native-diagnostics.postterminal-release-preserves-authority-and-joins-original", PostterminalRelease);
        yield return ("pi-messages-native-diagnostics.postterminal-completed-call-executor-paths-retain-joined-authority", PostterminalAgentAuthority);
        yield return ("pi-messages-native-diagnostics.actual-cancellation-callback-failure-owned-settlement", CancellationCallbackFallback);
        yield return ("pi-messages-native-diagnostics.wire-primary-error-survives-held-cleanup-fault", WireErrorCleanupPrecedence);
        yield return ("pi-messages-native-diagnostics.typed-preview-limits-and-admission-failures", PreviewFailureSites);
    }
    public static bool OwnsCase(string name) => name.StartsWith("pi-messages-native-diagnostics.", StringComparison.Ordinal);
    public static async Task RunOwnedCaseAsync(string name, Func<Task> run, string? report)
    {
        var original = run();
        using var deadlineLifetime = new CancellationTokenSource();
        try
        {
            await RunOwnedOperationAsync(name, original, report,
                Task.Delay(TimeSpan.FromSeconds(20), deadlineLifetime.Token), CaseState);
        }
        finally { deadlineLifetime.Cancel(); }
    }

    private static async Task RunOwnedOperationAsync(string name, Task original, string? report,
        Task deadline, OwnedCaseState state, Action? receiptWritten = null)
    {
        await Task.WhenAny(original, deadline);
        // Completed original failures retain their exception and stack, including inner gate timeouts.
        if (original.IsCompleted) { await original; return; }
        state.DeadlineExceeded = true;
        state.IncompleteReceipt = Path.Combine(report is null ? Path.GetTempPath() : Path.GetDirectoryName(report)!,
            "pi-messages-transport-incomplete-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(state.IncompleteReceipt)!);
            using var file = new FileStream(state.IncompleteReceipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { schemaVersion = 1, testId = name, status = "INCOMPLETE_NONPASSING", laterCaseAdmission = "STOPPED",
                    originalTaskOwned = true, runtimeAcceptance = false }));
            file.Write(bytes); file.Flush(flushToDisk: true);
            receiptWritten?.Invoke();
        }
        finally { try { await original; } catch { } } // Receipt failure cannot detach the original.
        throw new TimeoutException("Pi Messages case exceeded its diagnostic deadline; original task joined.");
    }

    private static async Task InnerTimeoutPreservesFailure()
    {
        foreach (var bothCompleted in new[] { false, true })
        {
            var state = new OwnedCaseState();
            var expected = new TimeoutException("injected inner gate timeout");
            async Task InnerGateFailure() { await Task.Yield(); throw expected; }
            var original = InnerGateFailure();
            // Also cover an already faulted original when the deadline is already complete.
            if (bothCompleted) { try { await original; } catch (TimeoutException) { } }
            var deadline = bothCompleted ? Task.CompletedTask : Gate().Task;
            try
            {
                await RunOwnedOperationAsync("control.inner-timeout", original, null, deadline, state);
                throw new InvalidOperationException("Inner failure passed.");
            }
            catch (TimeoutException actual)
            {
                Check(ReferenceEquals(expected, actual) && actual.StackTrace!.Contains(nameof(InnerGateFailure), StringComparison.Ordinal),
                    "Inner timeout lost its original exception or stack.");
                Check(!state.DeadlineExceeded && state.IncompleteReceipt is null, "Inner timeout was misclassified as outer expiry.");
            }
        }
    }

    private static async Task OuterDeadlineRetainsJoin()
    {
        foreach (var originalFails in new[] { false, true })
        {
            var state = new OwnedCaseState(); var release = Gate(); var receiptWritten = Gate();
            var returned = false; var deadlineObserved = false;
            async Task Original()
            {
                try { await release.Task; if (originalFails) throw new InvalidOperationException("injected original failure"); }
                finally { returned = true; }
            }
            var original = Original();
            var wrapper = RunOwnedOperationAsync("control.outer-deadline", original, null, Task.CompletedTask,
                state, () => receiptWritten.TrySetResult());
            try
            {
                await Task.WhenAny(receiptWritten.Task, wrapper).WaitAsync(TimeSpan.FromSeconds(5));
                Check(receiptWritten.Task.IsCompleted && state.DeadlineExceeded && !wrapper.IsCompleted && !returned,
                    "Outer deadline abandoned its pending original.");
                using var receipt = JsonDocument.Parse(File.ReadAllText(state.IncompleteReceipt!));
                Equal("INCOMPLETE_NONPASSING", receipt.RootElement.GetProperty("status").GetString());
                Equal("STOPPED", receipt.RootElement.GetProperty("laterCaseAdmission").GetString());
                Check(receipt.RootElement.GetProperty("originalTaskOwned").GetBoolean() &&
                    !receipt.RootElement.GetProperty("runtimeAcceptance").GetBoolean(), "Receipt granted acceptance or lost ownership.");
            }
            finally
            {
                release.TrySetResult();
                try { await wrapper; }
                catch (TimeoutException actual)
                {
                    Equal("Pi Messages case exceeded its diagnostic deadline; original task joined.", actual.Message);
                    deadlineObserved = true;
                }
            }
            Check(deadlineObserved && returned && original.IsCompleted && state.DeadlineExceeded,
                "Outer deadline passed or returned before original settlement.");
            Check(!DeadlineExceeded, "Expected control expiry contaminated real case admission.");
        }
    }

    private static async Task AllCodes()
    {
        foreach (var code in Enum.GetValues<NativeChatFailureCode>())
        foreach (var wrapped in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var wire = code switch
            {
                NativeChatFailureCode.MalformedStream => "data: {bad}\n\n",
                NativeChatFailureCode.UnexpectedEof => Sse("""{"type":"start"}"""),
                NativeChatFailureCode.UnsupportedFeature => Sse("""{"type":"start"}""", """{"type":"unsupported","contentIndex":0}"""),
                NativeChatFailureCode.ProviderError => """{"error":{"message":"same-public-text","code":"inert"}}""",
                _ => Success
            };
            var body = new Body(wire)
            {
                ReadFailure = code == NativeChatFailureCode.SourceFailed,
                FailCleanup = code == NativeChatFailureCode.CleanupFailed,
                CancelOnRead = code == NativeChatFailureCode.Cancelled ? cancellation.Cancel : null
            };
            BodyContent? responseContent = null; RequestContent? requestContent = null; var sends = 0;
            using var handler = new Handler(async (request, token) =>
            {
                sends++;
                await request.Content!.ReadAsStringAsync(token);
                requestContent = new RequestContent(request.Content); request.Content = requestContent;
                responseContent = new BodyContent(sends == 1 ? body : new Body(Success));
                return new HttpResponseMessage(sends == 1 && code == NativeChatFailureCode.ProviderError ?
                    HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = responseContent };
            });
            using var client = new HttpClient(handler);
            var options = new PiMessagesOptions(Metadata, "inert-key")
            { MaximumDataEvents = code == NativeChatFailureCode.ResourceLimit ? 1 : 4096 };
            var transport = new PiMessagesHttpSseTransport(client, Model, options);
            var frames = new List<StreamEvent>(); ChatResult? result = null;
            if (wrapped)
            {
                await using var run = await new ChatClient(transport).StartAsync(Request(), cancellation.Token);
                await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
                result = await run.Completion;
            }
            else await foreach (var frame in transport.StreamAsync(Request(), cancellation.Token)) frames.Add(frame);
            var terminal = frames.OfType<StreamTerminalEvent>().Single();
            Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, code), terminal.NativeDiagnostic);
            Equal(code == NativeChatFailureCode.Cancelled ? StopReason.Aborted : StopReason.Error, terminal.Reason);
            Check(body.Disposed && responseContent!.Disposed && requestContent!.Disposed &&
                body.AsyncDisposeCalls == 1, "Terminal escaped real request/response/reader release.");
            if (result is not null)
            {
                Equal(terminal.NativeDiagnostic, result.NativeDiagnostic);
                Equal(terminal.NativeDiagnostic, result.Failure!.NativeDiagnostic);
                Equal(terminal.NativeCleanupDiagnostic, result.NativeCleanupDiagnostic);
                Equal(PiWireJson.WriteMessage(terminal.Message).ToString(), PiWireJson.WriteMessage(result.Message).ToString());
            }
            if (code == NativeChatFailureCode.CleanupFailed)
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed), terminal.NativeCleanupDiagnostic);
            Check(!PiWireJson.WriteMessage(terminal.Message).ToString().Contains("nativeDiagnostic", StringComparison.Ordinal),
                "Native diagnostic entered the Source message.");
            Check(!handler.Disposed, "Transport disposed the borrowed client.");
            // Reuse the same borrowed client after a physical failure; use ordinary limits for this new invocation.
            var reused = await new ChatClient(new PiMessagesHttpSseTransport(client, Model, new(Metadata, "inert-key")))
                .CompleteAsync(Request());
            Check(reused.Failure is null && sends == 2 && !handler.Disposed, "Failed invocation poisoned the borrowed client.");
        }
    }

    private static async Task HeldCleanupPrecedence()
    {
        foreach (var cancelled in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var body = new Body("data: {bad}\n\n", held: true) { FailCleanup = true };
            BodyContent? response = null; RequestContent? requestOwner = null; var published = 0; var delivered = 0;
            using var handler = new Handler(async (request, token) =>
            {
                await request.Content!.ReadAsStringAsync(token); requestOwner = new RequestContent(request.Content); request.Content = requestOwner;
                response = new(body); return new HttpResponseMessage(HttpStatusCode.OK) { Content = response };
            });
            using var client = new HttpClient(handler);
            var transport = new PiMessagesHttpSseTransport(client, Model, new(Metadata, "inert-key")
            { Hooks = new() { OnEventPublished = frame =>
                {
                    if (frame is not StreamTerminalEvent) return;
                    Check(body.Disposed && response!.Disposed && requestOwner!.Disposed, "Terminal publication preceded physical release.");
                    published++;
                } } });
            var original = Observe();
            try
            {
                await body.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!original.IsCompleted && published == 0 && delivered == 0 &&
                    !response!.Disposed && !requestOwner!.Disposed, "Held cleanup published or delivered a terminal.");
                if (cancelled) cancellation.Cancel();
                body.ReleaseCleanup.TrySetResult();
                var result = await original;
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, cancelled ?
                    NativeChatFailureCode.Cancelled : NativeChatFailureCode.MalformedStream), result.NativeDiagnostic);
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed), result.NativeCleanupDiagnostic);
                Equal(1, published); Equal(1, delivered);
            }
            finally { body.ReleaseCleanup.TrySetResult(); cancellation.Cancel(); try { await original; } catch { } }
            async Task<ChatResult> Observe()
            {
                await using var run = await new ChatClient(transport).StartWithAbortSettlementAsync(Request(), cancellation.Token);
                StreamTerminalEvent? terminal = null;
                await foreach (var frame in run.ReadEventsAsync())
                    if (frame is StreamTerminalEvent end) { delivered++; terminal = end; Check(body.Disposed && response!.Disposed && requestOwner!.Disposed, "Terminal delivery preceded release."); }
                var result = await run.Completion;
                Equal(terminal!.NativeDiagnostic, result.NativeDiagnostic);
                Equal(terminal.NativeCleanupDiagnostic, result.NativeCleanupDiagnostic);
                Equal(result.NativeDiagnostic, result.Failure!.NativeDiagnostic);
                return result;
            }
        }
    }

    private static async Task WireErrorCleanupPrecedence()
    {
        const string ownedUsage = """{"input":3,"output":2,"cacheRead":1,"cacheWrite":0,"totalTokens":6,"cost":{"input":0.1,"output":0.2,"cacheRead":0.01,"cacheWrite":0,"total":0.31}}""";
        foreach (var wireReason in new[] { "error", "aborted" })
        foreach (var wrapped in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var end = "{\"type\":\"error\",\"reason\":\"" + wireReason + "\",\"usage\":" + ownedUsage +
                ",\"responseId\":\"provider-response\",\"errorMessage\":\"provider-primary\",\"providerThinkingLevel\":\"provider-level\"," +
                "\"rewrite\":{\"reason\":\"owned\",\"keep\":null}}";
            var body = new Body(Sse("""{"type":"start"}""",
                """{"type":"toolcall_start","contentIndex":0,"id":"owned-call","toolName":"inspect"}""",
                """{"type":"toolcall_end","contentIndex":0,"toolCall":{"type":"toolCall","id":"owned-call","name":"inspect","arguments":{"value":7},"opaqueSignature":"signature"}}""",
                end), held: true) { FailCleanup = true, CleanupFailureText = "cleanup-secondary" };
            BodyContent? response = null; RequestContent? requestOwner = null;
            StreamTerminalEvent? publishedTerminal = null; var published = 0; var delivered = 0;
            using var handler = new Handler(async (request, token) =>
            {
                await request.Content!.ReadAsStringAsync(token);
                requestOwner = new RequestContent(request.Content); request.Content = requestOwner;
                response = new(body); return new HttpResponseMessage(HttpStatusCode.OK) { Content = response };
            });
            using var client = new HttpClient(handler);
            var transport = new PiMessagesHttpSseTransport(client, Model, new(Metadata, "inert-key")
            { Hooks = new() { OnEventPublished = frame =>
                {
                    if (frame is not StreamTerminalEvent terminal) return;
                    Check(body.Disposed && response!.Disposed && requestOwner!.Disposed,
                        "Wire-error publication preceded actual HTTP cleanup.");
                    published++; publishedTerminal = terminal;
                } } });
            var original = Observe();
            try
            {
                await body.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!original.IsCompleted && published == 0 && delivered == 0 && !response!.Disposed && !requestOwner!.Disposed,
                    "Wire provider error escaped held cleanup.");
                if (cancelled) cancellation.Cancel();
                body.ReleaseCleanup.TrySetResult();
                var (terminal, result) = await original;
                var code = cancelled || wireReason == "aborted" ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.ProviderError;
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, code), terminal.NativeDiagnostic);
                Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed), terminal.NativeCleanupDiagnostic);
                Equal(cancelled ? StopReason.Aborted : wireReason == "aborted" ? StopReason.Aborted : StopReason.Error, terminal.Reason);
                Equal(1, published); Equal(1, delivered); Equal(1, body.AsyncDisposeCalls);
                Check(ReferenceEquals(publishedTerminal, terminal), "Delivery replaced the published wire error.");
                if (!cancelled)
                {
                    var expected = JsonData.Parse(
                        "{\"role\":\"assistant\",\"api\":\"pi-messages\",\"provider\":\"authored\",\"model\":\"inert\",\"timestamp\":123," +
                        "\"content\":[{\"type\":\"toolCall\",\"id\":\"owned-call\",\"name\":\"inspect\",\"arguments\":{\"value\":7},\"opaqueSignature\":\"signature\"}]," +
                        "\"usage\":" + ownedUsage + ",\"stopReason\":\"" + wireReason +
                        "\",\"errorMessage\":\"provider-primary\",\"responseId\":\"provider-response\",\"providerThinkingLevel\":\"provider-level\"," +
                        "\"diagnostics\":[{\"type\":\"pi_messages_rewrite\",\"timestamp\":123,\"details\":{\"reason\":\"owned\",\"keep\":null}}]}");
                    SameJson(expected.Value, PiWireJson.WriteMessage(terminal.Message).Value);
                    var observation = terminal.SourceEmissionSnapshot!.Value;
                    SameJson(expected.Value, observation.GetProperty("value").GetProperty("error"));
                    Equal(wireReason, observation.GetProperty("value").GetProperty("reason").GetString());
                    Equal(0, observation.GetProperty("ownUndefinedPaths").GetArrayLength());
                }
                else
                {
                    Equal(0, terminal.Message.Content.Length);
                    Equal(TokenUsage.Zero, terminal.Message.Usage);
                    Check(terminal.NativeDiagnostic!.Code != NativeChatFailureCode.CleanupFailed,
                        "Cancellation lost primary authority to cleanup.");
                }
                if (result is not null)
                {
                    Equal(terminal.NativeDiagnostic, result.NativeDiagnostic);
                    Equal(terminal.NativeCleanupDiagnostic, result.NativeCleanupDiagnostic);
                    Equal(terminal.NativeDiagnostic, result.Failure!.NativeDiagnostic);
                    SameJson(PiWireJson.WriteMessage(terminal.Message).Value, PiWireJson.WriteMessage(result.Message).Value);
                }
                Check(!handler.Disposed, "Wire error cleanup disposed the borrowed client.");
            }
            finally { body.ReleaseCleanup.TrySetResult(); cancellation.Cancel(); try { await original; } catch { } }
            async Task<(StreamTerminalEvent Terminal, ChatResult? Result)> Observe()
            {
                StreamTerminalEvent? terminal = null; ChatResult? result = null;
                if (wrapped)
                {
                    await using var run = await new ChatClient(transport).StartWithAbortSettlementAsync(Request(), cancellation.Token);
                    await foreach (var frame in run.ReadEventsAsync()) Deliver(frame);
                    result = await run.Completion;
                }
                else await foreach (var frame in transport.StreamAsync(Request(), cancellation.Token)) Deliver(frame);
                return (terminal ?? throw new InvalidOperationException("Wire error did not settle."), result);
                void Deliver(StreamEvent frame)
                {
                    if (frame is not StreamTerminalEvent endFrame) return;
                    Check(body.Disposed && response!.Disposed && requestOwner!.Disposed,
                        "Wire-error delivery preceded actual HTTP cleanup.");
                    terminal = endFrame; delivered++;
                }
            }
        }
    }

    private static async Task PreviewFailureSites()
    {
        var scenarios = new (string Name, string Delta, int Characters, int Depth, NativeChatFailureCode Code)[]
        {
            ("characters", new string(' ', 4097), 4096, 32, NativeChatFailureCode.ResourceLimit),
            ("depth", new string('[', 9), 1024, 8, NativeChatFailureCode.ResourceLimit),
            // pi-messages.ts:255-259 previews every toolcall_delta with parseStreamingJson (JSON.parse keeps 2^53, a lone surrogate
            // and the last duplicate name), so these deltas apply and the body then ends without a terminal event (pi-messages.ts:423).
            ("number", """{"value":9007199254740992}""", 1024, 32, NativeChatFailureCode.UnexpectedEof),
            ("unicode", "{\"value\":\"\\ud800\"}", 1024, 32, NativeChatFailureCode.UnexpectedEof),
            ("duplicate", """{"value":1,"value":2}""", 1024, 32, NativeChatFailureCode.UnexpectedEof)
        };
        foreach (var scenario in scenarios)
        foreach (var wrapped in new[] { false, true })
        foreach (var cleanupFails in new[] { false, true })
        {
            var delta = JsonSerializer.Serialize(new { type = "toolcall_delta", contentIndex = 0, delta = scenario.Delta });
            var body = new Body(Sse("""{"type":"start"}""",
                """{"type":"toolcall_start","contentIndex":0,"id":"x","toolName":"inspect"}""", delta))
            { FailCleanup = cleanupFails };
            BodyContent? response = null; RequestContent? requestOwner = null; var requests = 0;
            using var handler = new Handler(async (request, token) =>
            {
                requests++;
                await request.Content!.ReadAsStringAsync(token);
                requestOwner = new RequestContent(request.Content); request.Content = requestOwner;
                response = new(body); return new HttpResponseMessage(HttpStatusCode.OK) { Content = response };
            });
            using var client = new HttpClient(handler);
            var transport = new PiMessagesHttpSseTransport(client, Model, new(Metadata, "inert-key")
            { MaximumContentCharacters = scenario.Characters, MaximumJsonDepth = scenario.Depth });
            var frames = new List<StreamEvent>(); ChatResult? result = null;
            if (wrapped)
            {
                await using var run = await new ChatClient(transport).StartAsync(Request());
                await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
                result = await run.Completion;
            }
            else await foreach (var frame in transport.StreamAsync(Request())) frames.Add(frame);
            Check(requests == 1 && body.ReadCalls > 0 && frames.FirstOrDefault() is StreamStarted,
                "Preview fixture did not admit the HTTP read and startup envelope: " + scenario.Name);
            var terminal = frames.OfType<StreamTerminalEvent>().Single();
            // At EOF a body cleanup fault is the primary failure: readPiMessagesEvents' finally (pi-messages.ts:308-310) throws out
            // of the for-await before the "ended without a terminal event" error (pi-messages.ts:423) is reached.
            var primary = scenario.Code == NativeChatFailureCode.UnexpectedEof && cleanupFails ? NativeChatFailureCode.CleanupFailed : scenario.Code;
            Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, primary), terminal.NativeDiagnostic);
            Equal(StopReason.Error, terminal.Reason);
            // Limit failures occur inside preview parsing, before this delta is applied; accepted previews apply it. No tool is finalized.
            Equal(1, frames.OfType<ToolCallStarted>().Count());
            Equal(scenario.Code == NativeChatFailureCode.UnexpectedEof ? 1 : 0, frames.OfType<ToolCallDelta>().Count());
            Equal(0, frames.OfType<ToolCallEnded>().Count());
            Equal(cleanupFails ? new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed) : null,
                terminal.NativeCleanupDiagnostic);
            Check(body.Disposed && body.AsyncDisposeCalls == 1 && response!.Disposed && requestOwner!.Disposed,
                "Preview failure retained physical HTTP ownership: " + scenario.Name);
            if (result is not null)
            {
                Equal(terminal.NativeDiagnostic, result.NativeDiagnostic);
                Equal(terminal.NativeDiagnostic, result.Failure!.NativeDiagnostic);
                Equal(terminal.NativeCleanupDiagnostic, result.NativeCleanupDiagnostic);
            }
            Check(!handler.Disposed, "Preview failure disposed the borrowed client.");
        }
    }

    private static async Task ChatRunFallback()
    {
        Equal(1, (int)NativeChatAdapter.OpenAICompletions); Equal(2, (int)NativeChatAdapter.PiMessages);
        foreach (var api in new[] { "pi-messages", "openai-completions", "unknown" })
        foreach (var site in new[] { "empty", "source", "malformed", "typed", "cleanup", "cancel" })
        {
            var model = Model with { Api = api };
            using var cancellation = new CancellationTokenSource();
            if (site == "cancel") cancellation.Cancel();
            var transport = new Fallback(model, site);
            var result = await new ChatClient(transport).CompleteAsync(new(model, [], 123), cancellation.Token);
            if (site == "cleanup")
            {
                Check(result.Failure is null && result.Message.StopReason == StopReason.Stop, "Postterminal iterator cleanup rewrote success.");
                var adapter = api == "pi-messages" ? NativeChatAdapter.PiMessages : NativeChatAdapter.OpenAICompletions;
                Equal(api == "unknown" ? null : new NativeChatDiagnostic(adapter, NativeChatFailureCode.CleanupFailed), result.NativeCleanupDiagnostic);
            }
            else
            {
                var code = site switch { "empty" => NativeChatFailureCode.UnexpectedEof, "malformed" => NativeChatFailureCode.MalformedStream,
                    "typed" => NativeChatFailureCode.ResourceLimit, "cancel" => NativeChatFailureCode.Cancelled, _ => NativeChatFailureCode.SourceFailed };
                Equal(api == "pi-messages" ? new NativeChatDiagnostic(NativeChatAdapter.PiMessages, code) : null, result.NativeDiagnostic);
                Equal(result.NativeDiagnostic, result.Failure!.NativeDiagnostic);
            }
        }
        // Same public text from distinct real sites must retain distinct native provenance.
        var mapper = new PiMessagesEventMapper(Request(), new(Metadata, "inert-key"));
        var source = mapper.Error(new IOException("same-public-text"), aborted: false);
        var provider = new PiMessagesEventMapper(Request(), new(Metadata, "inert-key")).Convert(JsonData.Parse(
            """{"type":"error","reason":"error","errorMessage":"same-public-text","usage":""" + Usage + """}"""));
        Equal(NativeChatFailureCode.SourceFailed, source.NativeDiagnostic!.Code);
        Equal(NativeChatFailureCode.ProviderError, ((StreamTerminalEvent)provider).NativeDiagnostic!.Code);
    }

    private static async Task PostterminalRelease()
    {
        foreach (var api in new[] { "pi-messages", "openai-completions", "unknown", "google-generative-ai" })
        foreach (var reason in new[] { StopReason.Stop, StopReason.Length, StopReason.ToolUse, StopReason.Error, StopReason.Aborted, StopReason.Deferred })
        foreach (var calls in new[] { false, true })
        {
            var transport = new HeldTerminalRelease(reason, calls);
            await using var run = await new ChatClient(transport, 1).StartAsync(new(Model with { Api = api }, [], 123));
            StreamTerminalEvent? terminal = null;
            var originalReader = Read();
            try
            {
                await transport.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!originalReader.IsCompleted && !run.Completion.IsCompleted && !transport.Settled,
                    "Postterminal release abandoned the owned iterator.");
                transport.ReleaseCleanup.TrySetResult();
                await originalReader;
                var result = await run.Completion;
                var end = terminal ?? throw new InvalidOperationException("Original terminal was not delivered after cleanup.");
                var originalTerminal = transport.Terminal ?? throw new InvalidOperationException("Transport did not publish a terminal.");
                Check(transport.Settled && transport.Disposals == 1 && transport.ReadsAfterTerminal == 0,
                    "Original iterator release was skipped, repeated, or read beyond its terminal.");
                var originalError = reason is StopReason.Error or StopReason.Aborted;
                var primaryCleanup = !originalError && (api == "google-generative-ai" ||
                    api == "pi-messages" && (reason == StopReason.ToolUse || calls));
                if (primaryCleanup)
                {
                    Check(end is StreamError && result.Failure is not null && result.Message.StopReason == StopReason.Error,
                        "Cleanup-before-tool-authority policy changed.");
                    var primaryAdapter = api == "pi-messages" ? NativeChatAdapter.PiMessages : NativeChatAdapter.GoogleGenerativeAI;
                    Equal(new NativeChatDiagnostic(primaryAdapter, NativeChatFailureCode.CleanupFailed), result.NativeDiagnostic);
                    if (api == "pi-messages") Check(result.Message.Content.IsEmpty, "Failed tool cleanup retained executable call authority.");
                }
                else
                {
                    Equal(reason, end.Reason);
                    Check(ReferenceEquals(originalTerminal.Message, end.Message) && ReferenceEquals(end.Message, result.Message),
                        "Postterminal cleanup replaced original message identity or metadata.");
                    Equal(originalTerminal.NativeDiagnostic, result.NativeDiagnostic);
                    Check((result.Failure is null) == !originalError, "Release rewrote original terminal outcome.");
                }
                var adapter = api switch { "pi-messages" => NativeChatAdapter.PiMessages,
                    "openai-completions" => NativeChatAdapter.OpenAICompletions, _ => NativeChatAdapter.GoogleGenerativeAI };
                NativeChatDiagnostic? cleanup = api == "unknown" ? null : new(adapter, NativeChatFailureCode.CleanupFailed);
                Equal(cleanup, end.NativeCleanupDiagnostic); Equal(cleanup, result.NativeCleanupDiagnostic);
                Equal(end.NativeDiagnostic, result.NativeDiagnostic);
                if (result.Failure is not null) Equal(result.NativeDiagnostic, result.Failure.NativeDiagnostic);
            }
            finally
            {
                transport.ReleaseCleanup.TrySetResult(); run.Cancel();
                await originalReader;
            }
            async Task Read() { await foreach (var value in run.ReadEventsAsync()) if (value is StreamTerminalEvent end) terminal = end; }
        }
        // Local abort during the original join retains its separate cancellation precedence.
        var aborted = new HeldTerminalRelease(StopReason.ToolUse);
        await using var abortedRun = await new ChatClient(aborted, 1).StartAsync(Request());
        var originalAbortReader = ReadAbort();
        try
        {
            await aborted.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abortedRun.Cancel(); aborted.ReleaseCleanup.TrySetResult();
            await originalAbortReader; var result = await abortedRun.Completion;
            Equal(StopReason.Aborted, result.Message.StopReason);
            Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.Cancelled), result.NativeDiagnostic);
            Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed), result.NativeCleanupDiagnostic);
            Check(aborted.Settled && aborted.Disposals == 1, "Abort detached original postterminal release.");
        }
        finally { aborted.ReleaseCleanup.TrySetResult(); abortedRun.Cancel(); await originalAbortReader; }
        async Task ReadAbort() { await foreach (var _ in abortedRun.ReadEventsAsync()) { } }
    }
    private static async Task PostterminalAgentAuthority()
    {
        foreach (var path in new[] { "direct", "finalized", "prepared" })
        foreach (var mode in new[] { ToolExecutionMode.Sequential, ToolExecutionMode.Parallel })
        foreach (var reason in new[] { StopReason.Stop, StopReason.Length, StopReason.ToolUse, StopReason.Error, StopReason.Aborted, StopReason.Deferred })
        foreach (var calls in new[] { false, true })
        foreach (var cleanupFails in new[] { false, true })
        {
            var transport = new HeldTerminalRelease(reason, calls, cleanupFails);
            var prepared = path == "prepared";
            var direct = new DirectExecutor(); var finalized = new FinalizedExecutor();
            var adapter = new PreparedExecutor(); var policy = new ReleasePolicy();
            IToolExecutor executor = path switch { "prepared" => new ToolInvoker([adapter], policy), "finalized" => finalized, _ => direct };
            var sink = new AuthoritySink();
            await using var agent = new NativeAgent(new(Model, transport, [new("read", executor)], ExecutionMode: mode,
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123, sink,
                new(StreamCapacity: 1));
            Task<AgentLoopResult>? original = null;
            try
            {
                original = agent.PromptAsync([new("user", JsonData.Parse("""{"role":"user","content":"owned release","timestamp":123}"""))]);
                await transport.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!original.IsCompleted && sink.Terminal is null && direct.Executions == 0 && finalized.Executions == 0 && adapter.Prepares == 0 &&
                    adapter.Executions == 0 && policy.Authorizations == 0, "Held iterator cleanup admitted terminal/tool authority.");
                transport.ReleaseCleanup.TrySetResult();
                var revoked = cleanupFails && reason is not (StopReason.Error or StopReason.Aborted) &&
                    (calls || reason == StopReason.ToolUse);
                if (reason == StopReason.Deferred && !revoked)
                {
                    // Deferred is a valid transport terminal but is not a finalized
                    // assistant message that either scheduler path can dispatch.
                    try { await original; throw new InvalidOperationException("Deferred terminal reached dispatch."); }
                    catch (ArgumentException) { }
                    Equal(StopReason.Deferred, sink.Terminal!.Reason);
                }
                else
                {
                    var chat = (await original).Turns.Single().Result.Chat;
                    if (revoked)
                    {
                        Check(sink.Terminal is StreamError && chat.Message.Content.IsEmpty && chat.Failure is not null &&
                            sink.ToolStarts == 0, "Cleanup-failed completed call retained execution authority.");
                        Equal(new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed), chat.NativeDiagnostic);
                    }
                    else
                    {
                        Equal(reason, chat.Message.StopReason);
                        Check((chat.Failure is null) == (reason is not (StopReason.Error or StopReason.Aborted)),
                            "Nonexecutable terminal outcome changed.");
                    }
                    Equal(cleanupFails ? new NativeChatDiagnostic(NativeChatAdapter.PiMessages, NativeChatFailureCode.CleanupFailed) : null,
                        chat.NativeCleanupDiagnostic);
                }
                var expected = !cleanupFails && calls && (reason == StopReason.ToolUse || !prepared && reason == StopReason.Stop) ? 1 : 0;
                Equal(path == "direct" ? expected : 0, direct.Executions);
                Equal(path == "finalized" ? expected : 0, finalized.Executions); Equal(0, finalized.LegacyExecutions);
                Equal(prepared ? expected : 0, adapter.Prepares); Equal(prepared ? expected : 0, adapter.Executions);
                Equal(prepared ? expected : 0, policy.Authorizations);
                Check(transport.Settled && transport.Disposals == 1 && transport.ReadsAfterTerminal == 0,
                    "Agent returned without joining the original iterator release.");
            }
            finally
            {
                transport.ReleaseCleanup.TrySetResult(); agent.Abort();
                if (original is not null)
                    try { await original; } catch (ArgumentException) when (reason == StopReason.Deferred &&
                        (!cleanupFails || !calls)) { }
                await agent.WaitForIdleAsync();
            }
        }
    }
    private sealed class DirectExecutor : IToolExecutor
    {
        public int Executions;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executions++; return ValueTask.FromResult(ToolResult.Success("owned direct effect")); }
    }
    private sealed class PreparedExecutor : IPreparedToolAdapter
    {
        public string Name => "read"; public int Prepares, Executions;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Prepares++;
            return ValueTask.FromResult(new PreparedToolAction(Name, "read", PreparedToolActionKind.Path, "/owned",
                invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executions++; return ValueTask.FromResult(ToolResult.Success("owned prepared effect")); }
    }
    private sealed class FinalizedExecutor : IFinalizedToolExecutor
    {
        public int Executions, LegacyExecutions;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { LegacyExecutions++; return ValueTask.FromResult(ToolResult.Success("owned legacy effect")); }
        public ValueTask<FinalizedToolExecution> ExecuteFinalizedAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
            CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executions++; return ValueTask.FromResult(new FinalizedToolExecution(ToolResult.Success("owned finalized effect"), false)); }
    }
    private sealed class ReleasePolicy : IToolActionPolicy
    {
        public int Authorizations;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Authorizations++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class AuthoritySink : IAgentEventSink
    {
        public StreamTerminalEvent? Terminal; public int ToolStarts;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        {
            if (observation is TurnStreamObserved { Event: StreamTerminalEvent end }) Terminal = end;
            if (observation is ToolExecutionStarted) ToolStarts++;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class HeldTerminalRelease(StopReason reason, bool? completedCalls = null, bool failCleanup = true) : IChatTransport
    {
        public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool Settled; public int Disposals, ReadsAfterTerminal; public StreamTerminalEvent? Terminal;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var tool = new ToolCallContent("owned-terminal-tool", "read", JsonData.EmptyObject);
            var calls = completedCalls ?? reason == StopReason.ToolUse;
            var message = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp,
                calls ? [tool] : [new TextContent("owned terminal text")], TokenUsage.Zero, reason);
            var adapter = request.Model.Api switch { "pi-messages" => NativeChatAdapter.PiMessages,
                "openai-completions" => NativeChatAdapter.OpenAICompletions, _ => NativeChatAdapter.GoogleGenerativeAI };
            StreamTerminalEvent terminal = reason is StopReason.Error or StopReason.Aborted ? new StreamError(reason, message)
                { NativeDiagnostic = request.Model.Api == "unknown" ? null : new(adapter,
                    reason == StopReason.Aborted ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.ProviderError) } : new StreamDone(reason, message);
            Terminal = terminal;
            try
            {
                yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
                if (calls)
                {
                    yield return new ToolCallStarted(0, tool with { Arguments = JsonData.EmptyObject });
                    yield return new ToolCallDelta(0, "{}"); yield return new ToolCallEnded(0, tool);
                }
                else
                {
                    yield return new TextStarted(0, new("")); yield return new TextDelta(0, "owned terminal text");
                    yield return new TextEnded(0, "owned terminal text");
                }
                yield return terminal;
                ReadsAfterTerminal++;
            }
            finally
            {
                Disposals++; CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Settled = true;
                if (failCleanup) throw new IOException("authored postterminal release failure");
            }
        }
    }
    private sealed class Fallback(ModelDescriptor model, string site) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask;
            if (site == "empty") yield break;
            if (site == "malformed") throw new JsonException("same-public-text");
            if (site == "typed") throw new PiMessagesException(PiMessagesFailure.ResourceLimit, "same-public-text");
            if (site != "cleanup") throw new IOException("same-public-text");
            var final = new AssistantMessage(model.Api, model.Provider, model.Id, 123, [], TokenUsage.Zero, StopReason.Stop);
            try
            {
                yield return new StreamStarted(final with { StopReason = StopReason.Pending });
                yield return new StreamDone(StopReason.Stop, final);
            }
            finally { throw new IOException("same-public-text"); }
        }
    }
    private static async Task CancellationCallbackFallback()
    {
        foreach (var api in new[] { "pi-messages", "openai-completions" })
        {
            var transport = new CallbackFailureTransport();
            await using var run = await new ChatClient(transport).StartAsync(new(Model with { Api = api }, [], 123));
            var originalReader = Read();
            try
            {
                await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                run.Cancel();
                await transport.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!originalReader.IsCompleted && !run.Completion.IsCompleted, "Cancellation callback escaped owned cleanup.");
                transport.ReleaseCleanup.TrySetResult();
                await originalReader;
                var result = await run.Completion;
                var adapter = api == "pi-messages" ? NativeChatAdapter.PiMessages : NativeChatAdapter.OpenAICompletions;
                Equal(api == "pi-messages" ? new NativeChatDiagnostic(adapter, NativeChatFailureCode.Cancelled) : null, result.NativeDiagnostic);
                Equal(new NativeChatDiagnostic(adapter, NativeChatFailureCode.CleanupFailed), result.NativeCleanupDiagnostic);
                Equal(result.NativeCleanupDiagnostic, run.CleanupFailure!.NativeDiagnostic);
                Check(transport.Settled, "Original transport did not settle.");
            }
            finally
            {
                transport.ReleaseCleanup.TrySetResult(); run.Cancel();
                try { await originalReader; } catch { }
            }
            async Task Read() { await foreach (var _ in run.ReadEventsAsync()) { } }
        }
    }
    private sealed class CallbackFailureTransport : IChatTransport
    {
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        public bool Settled;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            using var callback = token.Register(() => throw new IOException("private callback failure"));
            try
            {
                Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
                yield break;
            }
            finally { CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; Settled = true; }
        }
    }
    private static ChatRequest Request() => new(Model, [], 123);
    private static string Sse(params string[] frames) => string.Concat(frames.Select(frame => "data: " + frame + "\n\n"));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void SameJson(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var fields = expected.EnumerateObject().ToArray(); Equal(fields.Length, actual.EnumerateObject().Count());
            foreach (var field in fields)
            { Check(actual.TryGetProperty(field.Name, out var value), "Missing property " + field.Name); SameJson(field.Value, value); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var index = 0; index < expected.GetArrayLength(); index++) SameJson(expected[index], actual[index]); }
        else if (expected.ValueKind == JsonValueKind.Number)
            Equal(BitConverter.DoubleToInt64Bits(expected.GetDouble()), BitConverter.DoubleToInt64Bits(actual.GetDouble()));
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
    }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { public bool Disposed; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); } }
    private sealed class RequestContent(HttpContent inner) : HttpContent
    {
        public bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed |= disposing; if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class BodyContent(Body body) : HttpContent
    {
        public bool Disposed;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
    private sealed class Body(string wire, bool held = false) : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        public bool ReadFailure, FailCleanup, Disposed; public int AsyncDisposeCalls, ReadCalls; public Action? CancelOnRead;
        public string CleanupFailureText = "same-public-text";
        public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource ReleaseCleanup { get; } = Gate();
        private Task? _cleanup;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            ReadCalls++;
            CancelOnRead?.Invoke(); token.ThrowIfCancellationRequested();
            if (ReadFailure) throw new IOException("same-public-text");
            return base.ReadAsync(buffer, token);
        }
        public override ValueTask DisposeAsync() => new(_cleanup ??= Cleanup());
        private async Task Cleanup()
        {
            AsyncDisposeCalls++; CleanupEntered.TrySetResult(); if (held) await ReleaseCleanup.Task;
            Disposed = true; base.Dispose(true); if (FailCleanup) throw new IOException(CleanupFailureText);
        }
    }
}
