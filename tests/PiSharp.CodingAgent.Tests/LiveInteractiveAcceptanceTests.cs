using System.Net;
using System.Text.Json;
using static LiveInteractiveAcceptanceFixtures;

internal static class LiveInteractiveAcceptanceTests
{
    // Register through the existing console runner once the real composition seam is supplied.
    // The adapter must only translate arguments into the actual production command API.
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke) =>
    [
        ("live-interactive.injected-http-text-visible-before-completion", () => Streaming(invoke)),
        ("live-interactive.injected-http-real-permitted-read-and-continuation", () => ToolContinuation(invoke)),
        ("live-interactive.injected-http-abort-joins-body-and-next-prompt", () => Abort(invoke)),
        ("live-interactive.injected-http-provider-error-and-next-prompt", () => ProviderError(invoke)),
        ("live-interactive.injected-http-durable-close-reopen-and-context", () => Persistence(invoke))
    ];

    private static async Task Streaming(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke)
    {
        var files = new Files(); var body = new Body(); body.Feed(Sse(Delta("STREAM-FIRST")));
        using var handler = new Handler((index, _, _) =>
        { Check(index == 0, "Text-only run unexpectedly continued/retried."); return Task.FromResult(Handler.Ok(body)); });
        await using var run = new Run(invoke, files, handler);
        try
        {
            await run.Prompt("stream a short answer"); await body.HeldRead.Task.WaitAsync(Bound);
            await run.Terminal.WaitText("STREAM-FIRST", run.Stop.Token);
            Check(!body.Disposed && !run.Running.IsCompleted, "First displayed delta was deferred until response cleanup.");
            body.Feed(Sse(Delta(" STREAM-LAST"), Stop, "[DONE]")); body.Complete();
            await run.Ended(); await run.Terminal.WaitText("STREAM-LAST", run.Stop.Token); await run.Finish();
            Check(body.Disposed && body.ActiveReads == 0 && handler.Requests.Length == 1, "Streaming response did not settle exactly once.");
            var log = await TerminalSessionCommandTests.Complete(files.Session);
            var messages = TerminalSessionCommandTests.Context(log).Messages;
            Check(messages.Any(message => message.Role == "assistant" &&
                TerminalSessionCommandTests.TextOf(message.WireBody.Value) == "STREAM-FIRST STREAM-LAST"), "Durable final text differs from received deltas.");
            await files.Receipt("streaming", new { firstDeltaDisplayedBeforeCompletion = true, requests = handler.Requests.Length, body.Disposed });
        }
        finally { body.Complete(); }
    }

    private static async Task ToolContinuation(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke)
    {
        var files = new Files(); await files.PrepareRead();
        using var handler = new Handler((index, request, _) =>
        {
            if (index == 0) return Task.FromResult(Handler.Ok(ReadCall(files.ReadTarget)));
            Check(index == 1, "Tool run performed an unexpected extra request.");
            var tool = request.GetProperty("messages").EnumerateArray().Single(message =>
                message.GetProperty("role").GetString() == "tool" && message.GetProperty("tool_call_id").GetString() == "acceptance-read-call");
            Check(tool.GetProperty("content").GetRawText().Contains(files.Marker, StringComparison.Ordinal),
                "Provider continuation arrived before actual permitted read output.");
            return Task.FromResult(Handler.Ok(Text("TOOL-CONTINUED")));
        });
        await using var run = new Run(invoke, files, handler, permittedRead: files.ReadTarget);
        await run.Prompt("read the explicitly permitted file and report its marker");
        await run.Ended(); await run.Terminal.WaitText("TOOL-CONTINUED", run.Stop.Token); await run.Finish();
        Check(handler.Requests.Length == 2, "Read result did not cause exactly one continuation.");
        var ended = run.Observations.Single(record => record.GetProperty("type").GetString() == "tool_execution_end");
        Check(ended.GetProperty("toolCallId").GetString() == "acceptance-read-call" &&
            ended.GetRawText().Contains(files.Marker, StringComparison.Ordinal), "Actual tool-end observation lacks the file marker.");
        var messages = TerminalSessionCommandTests.Context(await TerminalSessionCommandTests.Complete(files.Session)).Messages;
        Check(messages.Count(message => message.Role == "toolResult") == 1 && messages.Any(message => message.Role == "toolResult" &&
            message.WireBody.Value.GetRawText().Contains(files.Marker, StringComparison.Ordinal)), "Actual read result was not acknowledged on disk.");
        Check(messages.Any(message => message.Role == "assistant" && TerminalSessionCommandTests.TextOf(message.WireBody.Value) == "TOOL-CONTINUED"),
            "Continuation assistant was not acknowledged.");
        await files.Receipt("tool-continuation", new { actualReadMarker = files.Marker, requests = handler.Requests.Length });
    }

    private static async Task Abort(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke)
    {
        var files = new Files(); var body = new Body(); body.Feed(Sse(Delta("ABORT-FIRST")));
        using var handler = new Handler((index, _, _) =>
        {
            Check(index <= 1, "Abort caused an automatic retry/continuation.");
            return Task.FromResult(index == 0 ? Handler.Ok(body) : Handler.Ok(Text("AFTER-ABORT")));
        });
        await using var run = new Run(invoke, files, handler);
        try
        {
            await run.Prompt("hold this response"); await body.HeldRead.Task.WaitAsync(Bound);
            await run.Terminal.WaitText("ABORT-FIRST", run.Stop.Token); await run.Terminal.Feed("\u001b");
            await body.CanceledRead.Task.WaitAsync(Bound); await run.Ended();
            Check(body.Disposed && body.ActiveReads == 0 && handler.Requests.Length == 1,
                "Abort returned before the canceled HTTP body settled or sent an automatic continuation.");
            await run.Prompt("a fresh prompt after abort"); await run.Ended();
            await run.Terminal.WaitText("AFTER-ABORT", run.Stop.Token); await run.Finish();
            var messages = TerminalSessionCommandTests.Context(await TerminalSessionCommandTests.Complete(files.Session)).Messages;
            Check(messages.Any(message => message.Role == "assistant" && TerminalSessionCommandTests.TextOf(message.WireBody.Value) == "AFTER-ABORT"),
                "New prompt after terminal abort was not durably completed.");
            await files.Receipt("abort", new { canceledBodyJoined = true, requests = handler.Requests.Length });
        }
        finally { body.Complete(); }
    }

    private static async Task ProviderError(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke)
    {
        var files = new Files();
        using var handler = new Handler((index, _, _) =>
        {
            Check(index <= 1, "Nonretryable provider error retried unexpectedly.");
            return Task.FromResult(index == 0 ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"error\":{\"message\":\"authored rejected request\"}}") } : Handler.Ok(Text("AFTER-ERROR")));
        });
        await using var run = new Run(invoke, files, handler);
        await run.Prompt("provider rejects this prompt"); await run.Ended();
        var ended = run.Observations.Where(record => record.GetProperty("type").GetString() == "message_end").ToArray();
        Check(ended.Any(record => record.TryGetProperty("message", out var message) &&
            message.GetProperty("role").GetString() == "assistant" &&
            message.GetProperty("stopReason").GetString() == "error" &&
            message.TryGetProperty("errorMessage", out var error) && !string.IsNullOrWhiteSpace(error.GetString())),
            "Provider failure was not published as an assistant error.");
        await run.Terminal.WaitText("error", run.Stop.Token);
        await run.Prompt("recover from the provider error"); await run.Ended();
        await run.Terminal.WaitText("AFTER-ERROR", run.Stop.Token); await run.Finish();
        Check(handler.Requests.Length == 2, "Error recovery did not accept exactly one subsequent prompt.");
        await TerminalSessionCommandTests.Complete(files.Session);
        await files.Receipt("provider-error", new { nonretryableStatus = 400, nextPromptCompleted = true, requests = handler.Requests.Length });
    }

    private static async Task Persistence(Func<LiveInteractiveAcceptanceInput, Task<int>> invoke)
    {
        var files = new Files();
        using (var firstHandler = new Handler((index, _, _) =>
        { Check(index == 0, "First persisted turn unexpectedly continued."); return Task.FromResult(Handler.Ok(Text("PERSISTED-ANSWER"))); }))
        await using (var first = new Run(invoke, files, firstHandler))
        {
            await first.Prompt("remember PERSISTED-QUESTION"); await first.Ended();
            await first.Terminal.WaitText("PERSISTED-ANSWER", first.Stop.Token); await first.Finish();
        }
        var before = await TerminalSessionCommandTests.Complete(files.Session);
        using var handler = new Handler((index, request, _) =>
        {
            Check(index == 0, "Reopened session unexpectedly continued.");
            var messages = request.GetProperty("messages").EnumerateArray().ToArray();
            Check(messages.Any(message => message.GetProperty("role").GetString() == "user" &&
                message.GetRawText().Contains("PERSISTED-QUESTION", StringComparison.Ordinal)) &&
                messages.Any(message => message.GetProperty("role").GetString() == "assistant" &&
                message.GetRawText().Contains("PERSISTED-ANSWER", StringComparison.Ordinal)), "Reopened session lost earlier context in its actual HTTP request.");
            return Task.FromResult(Handler.Ok(Text("REOPENED-ANSWER")));
        });
        await using var reopened = new Run(invoke, files, handler, createNew: false);
        await reopened.Ready.Task.WaitAsync(Bound);
        await reopened.Terminal.WaitText("PERSISTED-ANSWER", reopened.Stop.Token);
        await reopened.Prompt("continue after reopening"); await reopened.Ended();
        await reopened.Terminal.WaitText("REOPENED-ANSWER", reopened.Stop.Token); await reopened.Finish();
        var after = await TerminalSessionCommandTests.Complete(files.Session);
        Check(after.OriginalBytes.Length > before.OriginalBytes.Length &&
            after.OriginalBytes.AsSpan(0, before.OriginalBytes.Length).SequenceEqual(before.OriginalBytes.AsSpan()), "Reopen rewrote the acknowledged log prefix.");
        var context = TerminalSessionCommandTests.Context(after);
        Check(context.Messages.Count(message => message.Role == "assistant") == 2, "Durable reopen lost or duplicated assistant turns.");
        await files.Receipt("persistence", new { priorBytes = before.OriginalBytes.Length, finalBytes = after.OriginalBytes.Length, earlierContextSent = true });
    }
}
