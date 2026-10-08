using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using static EventFixture;

// Regressions found in the full-parity assessment: Pi records every adapter's response diagnostics, and Pi's Mistral replay
// reads only the system fields it uses (packages/ai/src/api/mistral-conversations.ts), so stored extra fields never fail a resume.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> DiagnosticsCases() =>
    [
        Case("diagnostics.projector.records-and-reads-every-declared-adapter", DiagnosticAdapters),
        Case("diagnostics.rpc-host.mistral-failure-is-recorded-and-the-host-survives", MistralFailureThroughRpc),
        Case("mistral.replay-ignores-extra-system-and-user-fields", MistralExtraFields),
    ];

    private static Task DiagnosticAdapters()
    {
        foreach (var (adapter, name) in new[] { (NativeChatAdapter.OpenAICompletions, "openai-completions"), (NativeChatAdapter.PiMessages, "pi-messages"),
            (NativeChatAdapter.GoogleGenerativeAI, "google-generative-ai"), (NativeChatAdapter.MistralConversations, "mistral-conversations") })
        {
            var diagnostic = new NativeChatDiagnostic(adapter, NativeChatFailureCode.ProviderError);
            Check(NativeSessionDiagnosticProjector.IsValid(diagnostic), name + " is not a valid diagnostic.");
            var data = NativeSessionDiagnosticProjector.RecordData("assistant-1", 7, diagnostic, null);
            Equal(name, data.Value.GetProperty("diagnostic").GetProperty("adapter").GetString(), "recorded adapter");
            Equal(name, NativeSessionDiagnosticProjector.AdapterName(adapter), "adapter name");
        }
        Check(!NativeSessionDiagnosticProjector.IsValid(new((NativeChatAdapter)99, NativeChatFailureCode.ProviderError)), "An undeclared adapter was accepted.");
        return Task.CompletedTask;
    }

    private static async Task MistralFailureThroughRpc()
    {
        await using var f = await CreateAsync(Response(StopReason.Error, error: "Mistral API error (400): bad request"));
        f.Transport.ErrorDiagnostic = new(NativeChatAdapter.MistralConversations, NativeChatFailureCode.ProviderError);
        var rpc = f.StartRpc(); await f.PromptAsync();
        var diagnostic = f.Frames().Single(frame => Type(frame) == "pisharp_chat_diagnostic").Value.GetProperty("data");
        Equal("mistral-conversations", diagnostic.GetProperty("diagnostic").GetProperty("adapter").GetString(), "diagnostic adapter");
        Equal("ProviderError", diagnostic.GetProperty("diagnostic").GetProperty("code").GetString(), "diagnostic code");
        Check(f.Frames().Any(frame => Type(frame) == "agent_settled") && !rpc.Completion.IsCompleted && f.Session.Snapshot.Fault is null,
            "A Mistral diagnostic faulted the session or the RPC host.");
        var recorded = f.Session.GetNativeDiagnostics().Entries.Single();
        Equal(NativeChatAdapter.MistralConversations, recorded.Diagnostic!.Adapter, "recorded adapter");
        // A later prompt still runs on the same host.
        f.Transport.Responses.Enqueue(Response(text: "after")); f.Transport.ErrorDiagnostic = null;
        await f.PromptAsync("again", "again");
        Check(f.Frames().Count(frame => Type(frame) == "agent_settled") == 2, "The host did not survive the Mistral failure.");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal JsonData? Body; internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = JsonData.Parse(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private static async Task MistralExtraFields()
    {
        var model = new ModelDescriptor("parity-mistral", "mistral-conversations", "mistral");
        foreach (var midConversation in new[] { false, true })
        {
            using var handler = new CaptureHandler(); using var client = new HttpClient(handler);
            var options = new MistralTextOptions(new("https://fixture.invalid/"), true, new(0, 0, 0, 0), "offline-fixture")
            { ApiKey = "explicit", SupportsMidConversationSystemMessages = midConversation };
            var transport = new MistralTextHttpSseTransport(client, model, options);
            ImmutableArray<TranscriptEntry> messages =
            [
                new("system", JsonData.Parse("""{"role":"system","content":"system text","timestamp":0,"toolsAdded":[],"offlineApi":null}""")),
                new("user", JsonData.Parse("""{"role":"user","content":"next","timestamp":1,"pisharpExtra":true}"""))
            ];
            StreamEvent? last = null; await foreach (var observation in transport.StreamAsync(new(model, messages, 4))) last = observation;
            Check(last is StreamDone && handler.Calls == 1, "A stored extra field failed the Mistral replay (midConversation=" + midConversation + ").");
            var sent = handler.Body!.Value.GetProperty("messages");
            Equal("""{"role":"system","content":"system text"}""", sent[0].GetRawText(), "system payload");
            Equal("""{"role":"user","content":"next"}""", sent[1].GetRawText(), "user payload");
        }
    }
}
