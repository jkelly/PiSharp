using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI;
using PiSharp.AI.Media;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.AI;
using AiTextContent = Microsoft.Extensions.AI.TextContent;
using PiTextContent = PiSharp.Contracts.TextContent;
using PiToolCallContent = PiSharp.Contracts.ToolCallContent;

internal static class Program
{
    private static readonly ModelDescriptor TestModel = new("test-model-1", "openai-responses", "openai");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report"))
            throw new ArgumentException("Use [--report <fresh path>].");

        var cases = new List<(string Id, Func<Task> Run)>
        {
            ("pi-chat-client.non-streaming-text-thinking-usage-and-options", TestPiChatClientNonStreamingAsync),
            ("pi-chat-client.streaming-deltas-tools-and-terminal-usage", TestPiChatClientStreamingAsync),
            ("pi-chat-client.history-images-and-tool-results", TestPiChatClientHistoryAndToolsAsync),
            ("pi-chat-client.errors-abort-eof-and-dispose", TestPiChatClientErrorsAndDisposeAsync),
            ("pi-chat-client.get-service-and-metadata", TestPiChatClientMetadataAsync),
            ("pi-chat-client.preserves-tool-call-and-text-signatures-roundtrip", TestSignatureRoundTripAsync),
            ("pi-chat-client.streaming-authoritative-suffix-and-bedrock-finalized-metadata", TestStreamingAuthoritativeSuffixAndFinalizedMetadataAsync),
            ("pi-chat-client.non-object-tool-arguments-marked-with-exception", TestNonObjectToolArgumentsAsync),
            ("transport-adapter.streaming-reducer-conformance-and-dedup", TestTransportAdapterReducerConformanceAsync),
            ("transport-adapter.transcript-system-sections-tool-deltas-and-images", TestTransportAdapterTranscriptProjectionAsync),
            ("transport-adapter.errors-and-cancellation", TestTransportAdapterErrorsAndCancellationAsync),
            ("transport-adapter.error-and-cancel-preserve-open-buffers", TestErrorAndCancelPreserveOpenBuffersAsync),
            ("transport-adapter.informational-calls-content-filter-cleanup-and-base-options", TestAdapterEdgeCasesAsync),
            ("transport-adapter.usage-inclusive-uncached-roundtrip", TestUsageCacheAccountingAsync),
            ("transport-adapter.agent-multi-turn-tool-loop", TestTransportAdapterDrivesPiSharpAgentAsync),
            ("roundtrip.pi-chat-client-over-transport-adapter", TestRoundTripBridgeAsync),
            ("embedding-generator.vectors-options-usage-and-errors", TestEmbeddingGeneratorAsync),
            ("di.service-collection-extensions", TestServiceCollectionExtensionsAsync),
        };

        var results = new List<object>();
        var failures = 0;
        foreach (var test in cases)
        {
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(60));
                results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" });
            }
            catch (Exception error)
            {
                failures++;
                results.Add(new { test.Id, status = "FAIL", failure = error.ToString() });
            }
        }

        var report = new
        {
            status = "AUTHORED NATIVE",
            cases = cases.Count,
            failures,
            results
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static async Task TestPiChatClientNonStreamingAsync()
    {
        ChatRequest? captured = null;
        var transport = new ScriptedTransport((req, _) =>
        {
            captured = req;
            var thinkingProps = JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"sig-xyz\""));
            var usageExtra = JsonFields.Empty.Set("reasoning", JsonData.Parse("7"));
            var usage = new TokenUsage(100, 42, 20, 5, 167, new UsageCost(0.01m, 0.02m, 0, 0, 0.03m), usageExtra);
            var msg = new AssistantMessage(
                req.Model.Api, req.Model.Provider, req.Model.Id, 1700000001000,
                [new ThinkingContent("Let me think", thinkingProps), new PiTextContent("Final answer")],
                usage, StopReason.Stop);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, 1700000001000, [], TokenUsage.Zero, StopReason.Pending)),
                new StreamDone(StopReason.Stop, msg)
            ];
        });

        var identity = new ClientIdentity { UserAgent = ClientUserAgent.Short };
        using var client = new PiChatClient(transport, TestModel, new PiChatClientOptions
        {
            ServiceTier = ServiceTiers.Priority,
            Identity = identity,
            TimeProvider = new FixedTimeProvider(1700000000000)
        });

        var options = new ChatOptions
        {
            ModelId = "override-model",
            Instructions = "Be concise.",
            ConversationId = "sess-42",
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [PiChatAdditionalProperties.DisableContinuation] = true
            }
        };

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")], options);

        Check(captured is not null, "request captured");
        Equal("override-model", captured!.Model.Id, "model override");
        Equal("high", captured.ThinkingLevel, "reasoning effort mapped");
        Equal(ServiceTiers.Priority, captured.ServiceTier, "default service tier");
        Equal("sess-42", captured.SessionId, "conversation id -> session id");
        Equal(true, captured.DisableContinuation, "disable continuation");
        Check(ReferenceEquals(identity, captured.Identity), "identity propagated");
        Equal(1700000000000L, captured.Timestamp, "timestamp from TimeProvider");
        Equal(2, captured.Messages.Length, "instructions + user message");
        Equal("system", captured.Messages[0].Role, "instructions emitted as system");
        Equal("user", captured.Messages[1].Role, "user entry");

        Equal("override-model", response.ModelId, "response model id");
        Check(response.ConversationId is null, "stateless PiChatClient does not echo affinity SessionId as stateful ConversationId");
        Equal("sess-42", response.AdditionalProperties![PiChatAdditionalProperties.SessionId], "session id preserved in AdditionalProperties");
        Equal(ChatFinishReason.Stop, response.FinishReason, "finish reason");
        Equal("Final answer", response.Text, "assistant text");
        var reasoning = response.Messages[0].Contents.OfType<TextReasoningContent>().Single();
        Equal("Let me think", reasoning.Text, "thinking text");
        Equal("sig-xyz", reasoning.ProtectedData, "thinking signature");
        Check(response.Usage is not null, "usage present");
        Equal(125L, response.Usage!.InputTokenCount, "inclusive input tokens (100 uncached + 20 cacheRead + 5 cacheWrite)");
        Equal(42L, response.Usage.OutputTokenCount, "output tokens");
        Equal(167L, response.Usage.TotalTokenCount, "total tokens");
        Equal(20L, response.Usage.CachedInputTokenCount, "cached tokens");
        Equal(7L, response.Usage.ReasoningTokenCount, "reasoning tokens");
        Equal(5L, response.Usage.AdditionalCounts!["cacheWriteTokens"], "cache write tokens");
        Check(response.RawRepresentation is AssistantMessage, "raw representation preserved");
    }

    private static async Task TestPiChatClientStreamingAsync()
    {
        var transport = new ScriptedTransport((req, _) =>
        {
            var sigProps = JsonFields.Empty.Set("thinkingSignature", JsonData.Parse("\"sig-stream\""));
            var toolCall = new PiToolCallContent("call-1", "get_weather", JsonData.Parse("{\"city\":\"Chicago\",\"units\":\"c\"}"));
            var finalMsg = new AssistantMessage(
                req.Model.Api, req.Model.Provider, req.Model.Id, 1700000002000,
                [new ThinkingContent("plan", sigProps), new PiTextContent("Checking "), toolCall],
                new TokenUsage(50, 25, 0, 0, 75, new UsageCost(0, 0, 0, 0, 0)),
                StopReason.ToolUse);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, 1700000002000, [], TokenUsage.Zero, StopReason.Pending)),
                new ThinkingStarted(0, new ThinkingContent("")),
                new ThinkingDelta(0, "pl"),
                new ThinkingDelta(0, "an"),
                new ThinkingEnded(0, "plan", sigProps),
                new TextStarted(1, new PiTextContent("")),
                new TextDelta(1, "Checking "),
                new TextEnded(1, "Checking "),
                new ToolCallStarted(2, new PiToolCallContent("call-1", "get_weather", JsonData.EmptyObject)),
                new ToolCallDelta(2, "{\"city\":\"Chicago\",\"units\":\"c\"}"),
                new ToolCallEnded(2, toolCall),
                new StreamDone(StopReason.ToolUse, finalMsg)
            ];
        });

        using var client = new PiChatClient(transport, TestModel);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Weather?")]))
            updates.Add(u);

        Check(updates.Count >= 5, "multiple streaming updates emitted");
        var responseIds = updates.Select(u => u.ResponseId).Distinct().ToList();
        Equal(1, responseIds.Count, "consistent response id across stream");

        var reasoningText = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
        Equal("plan", reasoningText, "streamed thinking text");
        var signature = updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Last(r => r.ProtectedData is not null).ProtectedData;
        Equal("sig-stream", signature, "streamed thinking signature");

        var text = string.Concat(updates.SelectMany(u => u.Contents).OfType<AiTextContent>().Select(t => t.Text));
        Equal("Checking ", text, "streamed text");

        var fnCall = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();
        Equal("call-1", fnCall.CallId, "tool call id");
        Equal("get_weather", fnCall.Name, "tool call name");
        Equal("Chicago", ((JsonElement)fnCall.Arguments!["city"]!).GetString(), "tool arg city");

        var terminal = updates[^1];
        Equal(ChatFinishReason.ToolCalls, terminal.FinishReason, "tool calls finish reason");
        var usage = terminal.Contents.OfType<UsageContent>().Single().Details;
        Equal(75L, usage.TotalTokenCount, "terminal usage");
    }

    private static async Task TestPiChatClientHistoryAndToolsAsync()
    {
        ChatRequest? captured = null;
        var transport = new ScriptedTransport((req, _) =>
        {
            captured = req;
            var msg = new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                [new PiTextContent("ok")], TokenUsage.Zero, StopReason.Stop);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                new StreamDone(StopReason.Stop, msg)
            ];
        });

        using var client = new PiChatClient(transport, TestModel);
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var weatherFn = AIFunctionFactory.Create((string city) => $"sunny in {city}", "get_weather", "Gets weather");

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System rule."),
            new(ChatRole.User, [new AiTextContent("Look at this"), new DataContent(pngBytes, "image/png")]),
            new(ChatRole.Assistant, [new FunctionCallContent("call-w1", "get_weather", new Dictionary<string, object?> { ["city"] = "Austin" })]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-w1", "72F sunny"),
                new FunctionResultContent("call-err", null) { Exception = new InvalidOperationException("boom") }
            ])
        };

        await client.GetResponseAsync(history, new ChatOptions { Tools = [weatherFn] });

        Check(captured is not null, "captured");
        // system (toolsAdded), system (System rule), user (text+image), assistant (toolCall), toolResult (w1), toolResult (err)
        Equal(6, captured!.Messages.Length, "all transcript entries projected");

        var toolsNode = JsonNode.Parse(captured.Messages[0].WireBody.ToString())!.AsObject()["toolsAdded"]!.AsArray();
        Equal(1, toolsNode.Count, "1 tool declared");
        Equal("get_weather", toolsNode[0]!["name"]!.GetValue<string>(), "tool name");

        var userContent = JsonNode.Parse(captured.Messages[2].WireBody.ToString())!.AsObject()["content"]!.AsArray();
        Equal("text", userContent[0]!["type"]!.GetValue<string>(), "user text block");
        Equal("image", userContent[1]!["type"]!.GetValue<string>(), "user image block");
        Equal(Convert.ToBase64String(pngBytes), userContent[1]!["data"]!.GetValue<string>(), "user image base64");
        Equal("image/png", userContent[1]!["mimeType"]!.GetValue<string>(), "user image mimeType");

        var toolOk = JsonNode.Parse(captured.Messages[4].WireBody.ToString())!.AsObject();
        Equal("call-w1", toolOk["toolCallId"]!.GetValue<string>(), "tool result call id");
        Equal("get_weather", toolOk["toolName"]!.GetValue<string>(), "tool name resolved from prior assistant call");
        Equal(false, toolOk["isError"]!.GetValue<bool>(), "non-error tool result");

        var toolErr = JsonNode.Parse(captured.Messages[5].WireBody.ToString())!.AsObject();
        Equal("call-err", toolErr["toolCallId"]!.GetValue<string>(), "error tool result call id");
        Equal(true, toolErr["isError"]!.GetValue<bool>(), "error tool result marked isError");
        Equal("boom", toolErr["content"]!.AsArray()[0]!["text"]!.GetValue<string>(), "exception message carried");
    }

    private static async Task TestPiChatClientErrorsAndDisposeAsync()
    {
        var errTransport = new ScriptedTransport((req, _) =>
        {
            var extra = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"rate limit exceeded\""));
            return
            [
                new StreamError(StopReason.Error, new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Error, extra))
            ];
        });
        using (var c = new PiChatClient(errTransport, TestModel))
        {
            var ex = await ThrowsAsync<InvalidOperationException>(() => c.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]), "StreamError throws InvalidOperationException");
            Equal("rate limit exceeded", ex.Message, "error message propagated");
        }

        var abortTransport = new ScriptedTransport((req, _) =>
        {
            var extra = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"user cancelled\""));
            return
            [
                new StreamError(StopReason.Aborted, new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Aborted, extra))
            ];
        });
        using (var c = new PiChatClient(abortTransport, TestModel))
        {
            await ThrowsAsync<OperationCanceledException>(() => c.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]), "Aborted throws OperationCanceledException");
        }

        var eofTransport = new ScriptedTransport((req, _) =>
        [
            new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending))
        ]);
        using (var c = new PiChatClient(eofTransport, TestModel))
        {
            await ThrowsAsync<InvalidOperationException>(() => c.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]), "Unexpected EOF throws");
        }

        var disposableTransport = new DisposableTransport();
        var client = new PiChatClient(disposableTransport, TestModel, new PiChatClientOptions { DisposeTransport = true });
        client.Dispose();
        client.Dispose();
        Equal(1, disposableTransport.DisposeCount, "transport disposed once");
        await ThrowsAsync<ObjectDisposedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]), "disposed client throws");
    }

    private static Task TestPiChatClientMetadataAsync()
    {
        var transport = new ScriptedTransport((_, _) => []);
        var uri = new Uri("https://api.example.test/v1");
        using var client = new PiChatClient(transport, TestModel, new PiChatClientOptions { ProviderUri = uri });

        var meta = client.GetService<ChatClientMetadata>();
        Check(meta is not null, "ChatClientMetadata resolved");
        Equal("openai", meta!.ProviderName, "provider name");
        Equal(uri, meta.ProviderUri, "provider uri");
        Equal("test-model-1", meta.DefaultModelId, "default model id");
        Check(ReferenceEquals(transport, client.GetService<IChatTransport>()), "underlying transport resolved");
        Equal(TestModel, client.GetService<ModelDescriptor>(), "model descriptor resolved");
        Check(client.GetService(typeof(ChatClientMetadata), "other-key") is null, "keyed lookup returns null");
        return Task.CompletedTask;
    }

    private static async Task TestSignatureRoundTripAsync()
    {
        var turn = 0;
        ChatRequest? turn2Request = null;
        var transport = new ScriptedTransport((req, _) =>
        {
            turn++;
            if (turn == 1)
            {
                var thinkingProps = JsonFields.Empty
                    .Set("thinkingSignature", JsonData.Parse("\"think-sig-1\""))
                    .Set("redacted", JsonData.Parse("true"));
                var textProps = JsonFields.Empty.Set("textSignature", JsonData.Parse("\"text-sig-1\""));
                var toolProps = JsonFields.Empty.Set("thoughtSignature", JsonData.Parse("\"gemini-thought-sig-99\""));
                var toolCall = new PiToolCallContent("call-g1", "lookup", JsonData.Parse("{\"q\":\"pi\"}"), toolProps);
                var msg = new AssistantMessage(
                    req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                    [new ThinkingContent("", thinkingProps), new PiTextContent("Checking", textProps), toolCall],
                    TokenUsage.Zero, StopReason.ToolUse);
                return
                [
                    new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                    new ThinkingStarted(0, new ThinkingContent("")),
                    new ThinkingEnded(0, "", thinkingProps),
                    new TextStarted(1, new PiTextContent("")),
                    new TextDelta(1, "Checking"),
                    new TextEnded(1, "Checking", textProps),
                    new ToolCallStarted(2, new PiToolCallContent("call-g1", "lookup", JsonData.EmptyObject)),
                    new ToolCallDelta(2, "{\"q\":\"pi\"}"),
                    new ToolCallEnded(2, toolCall),
                    new StreamDone(StopReason.ToolUse, msg)
                ];
            }

            turn2Request = req;
            var doneMsg = new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                [new PiTextContent("Done")], TokenUsage.Zero, StopReason.Stop);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                new StreamDone(StopReason.Stop, doneMsg)
            ];
        });

        using var client = new PiChatClient(transport, new ModelDescriptor("gemini-2.5-pro", "google-generative-ai", "google"));
        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Look up pi")]);
        var assistantMsg = first.Messages[0];

        var call = assistantMsg.Contents.OfType<FunctionCallContent>().Single();
        Equal("gemini-thought-sig-99", call.AdditionalProperties![PiChatAdditionalProperties.ThoughtSignature], "tool call thoughtSignature captured");
        var textItem = assistantMsg.Contents.OfType<AiTextContent>().Single();
        Equal("text-sig-1", textItem.AdditionalProperties![PiChatAdditionalProperties.TextSignature], "textSignature captured");
        var thinkingItem = assistantMsg.Contents.OfType<TextReasoningContent>().Single();
        Equal("think-sig-1", thinkingItem.ProtectedData, "thinkingSignature captured");
        Equal(true, thinkingItem.AdditionalProperties![PiChatAdditionalProperties.ThinkingRedacted], "redacted flag captured");

        // Pass the assistant message back in turn 2 (even after cloning/stripping RawRepresentation).
        var rebuiltAssistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent(thinkingItem.Text) { ProtectedData = thinkingItem.ProtectedData, AdditionalProperties = thinkingItem.AdditionalProperties },
            new AiTextContent(textItem.Text) { AdditionalProperties = textItem.AdditionalProperties },
            new FunctionCallContent(call.CallId, call.Name, call.Arguments) { AdditionalProperties = call.AdditionalProperties }
        ]);
        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Look up pi"),
            rebuiltAssistant,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-g1", "3.14159")])
        ]);

        Check(turn2Request is not null, "turn 2 request captured");
        var assistantEntry = JsonNode.Parse(turn2Request!.Messages[1].WireBody.ToString())!.AsObject();
        var blocks = assistantEntry["content"]!.AsArray();
        Equal("think-sig-1", blocks[0]!["thinkingSignature"]!.GetValue<string>(), "turn 2 thinkingSignature restored");
        Equal(true, blocks[0]!["redacted"]!.GetValue<bool>(), "turn 2 redacted flag restored");
        Equal("text-sig-1", blocks[1]!["textSignature"]!.GetValue<string>(), "turn 2 textSignature restored");
        Equal("gemini-thought-sig-99", blocks[2]!["thoughtSignature"]!.GetValue<string>(), "turn 2 thoughtSignature restored");

        // Also verify reverse ChatClientTransportAdapter preserves all three signatures and passes AssistantStreamReducer.
        var sigClient = new ScriptedChatClient((_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("") { ProtectedData = "rs-2", AdditionalProperties = new() { [PiChatAdditionalProperties.ThinkingRedacted] = true } }]),
            new ChatResponseUpdate(ChatRole.Assistant, [new AiTextContent("Hi") { AdditionalProperties = new() { [PiChatAdditionalProperties.TextSignature] = "ts-2" } }]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c-2", "fn", new Dictionary<string, object?>()) { AdditionalProperties = new() { [PiChatAdditionalProperties.ThoughtSignature] = "gs-2" } }])
            { FinishReason = ChatFinishReason.ToolCalls }
        ]);
        using var adapter = new ChatClientTransportAdapter(sigClient);
        var fallback = new AssistantMessage(TestModel.Api, TestModel.Provider, TestModel.Id, 77, [], TokenUsage.Zero, StopReason.Pending);
        var reducer = new AssistantStreamReducer(fallback);
        StreamDone? adaptedDone = null;
        await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("hi")], 77)))
        {
            reducer.Apply(ev);
            if (ev is StreamDone d) adaptedDone = d;
        }
        Check(adaptedDone is not null, "adapted stream done");
        Equal("rs-2", adaptedDone!.Message.Content[0].ExtraProperties!.TryGet("thinkingSignature", out var rs2) ? rs2!.Value.GetString() : null, "adapter thinkingSignature");
        Check(adaptedDone.Message.Content[0].ExtraProperties!.TryGet("redacted", out var red2) && red2!.Value.GetBoolean(), "adapter redacted");
        Equal("ts-2", adaptedDone.Message.Content[1].ExtraProperties!.TryGet("textSignature", out var ts2) ? ts2!.Value.GetString() : null, "adapter textSignature");
        Equal("gs-2", adaptedDone.Message.Content[2].ExtraProperties!.TryGet("thoughtSignature", out var gs2) ? gs2!.Value.GetString() : null, "adapter thoughtSignature");
    }

    private static async Task TestStreamingAuthoritativeSuffixAndFinalizedMetadataAsync()
    {
        // 1. TextEnded extending streamed delta emits the missing suffix; Bedrock ContentBlockFinalized emits metadata after ThinkingDelta.
        var sigProps = JsonFields.Empty
            .Set("thinkingSignature", JsonData.Parse("\"bedrock-sig-1\""))
            .Set("redacted", JsonData.Parse("true"));
        var transport = new ScriptedTransport((req, _) =>
        {
            var finalMsg = new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                [new ThinkingContent("deep thought", sigProps), new PiTextContent("Hello")],
                TokenUsage.Zero, StopReason.Stop);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                new ThinkingStarted(0, new ThinkingContent("")),
                new ThinkingDelta(0, "deep thought"),
                new ContentBlockFinalized(0, new ThinkingContent("deep thought", sigProps)),
                new TextStarted(1, new PiTextContent("")),
                new TextDelta(1, "Hel"),
                new TextEnded(1, "Hello"),
                new StreamDone(StopReason.Stop, finalMsg)
            ];
        });

        using (var client = new PiChatClient(transport, TestModel))
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
                updates.Add(u);

            var allText = string.Concat(updates.SelectMany(u => u.Contents).OfType<AiTextContent>().Select(t => t.Text));
            Equal("Hello", allText, "missing suffix emitted on TextEnded");
            var reasoningItems = updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().ToList();
            Equal("deep thought", string.Concat(reasoningItems.Select(r => r.Text)), "streamed thinking text");
            Check(reasoningItems.Any(r => r.ProtectedData == "bedrock-sig-1"
                && r.AdditionalProperties is not null
                && r.AdditionalProperties.TryGetValue(PiChatAdditionalProperties.ThinkingRedacted, out var red)
                && red is true), "ContentBlockFinalized metadata emitted after ThinkingDelta");
        }

        // 2. TextEnded contradicting already-streamed deltas throws StreamProtocolException instead of silently corrupting output.
        var rewriteTransport = new ScriptedTransport((req, _) =>
        [
            new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
            new TextStarted(0, new PiTextContent("")),
            new TextDelta(0, "Old"),
            new TextEnded(0, "Replaced"),
            new StreamDone(StopReason.Stop, new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [new PiTextContent("Replaced")], TokenUsage.Zero, StopReason.Stop))
        ]);
        using (var client = new PiChatClient(rewriteTransport, TestModel))
        {
            await ThrowsAsync<StreamProtocolException>(async () =>
            {
                await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
            }, "non-prefix replacement throws StreamProtocolException");
        }
    }

    private static async Task TestNonObjectToolArgumentsAsync()
    {
        var transport = new ScriptedTransport((req, _) =>
        {
            var arrayCall = new PiToolCallContent("call-arr", "bad_tool", JsonData.Parse("[1,2]"));
            var msg = new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                [arrayCall], TokenUsage.Zero, StopReason.ToolUse);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                new ToolCallStarted(0, new PiToolCallContent("call-arr", "bad_tool", JsonData.EmptyObject)),
                new ToolCallEnded(0, arrayCall),
                new StreamDone(StopReason.ToolUse, msg)
            ];
        });

        using var client = new PiChatClient(transport, TestModel);
        var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")]);
        var fc = resp.Messages[0].Contents.OfType<FunctionCallContent>().Single();
        Check(fc.Arguments is null, "non-object arguments not silently coerced to empty dictionary");
        Check(fc.Exception is JsonException, "non-object arguments surfaced on FunctionCallContent.Exception");
    }

    private static async Task TestAdapterEdgeCasesAsync()
    {
        // 1. InformationalOnly function call must be skipped and never reach StreamDone.
        var infoClient = new ScriptedChatClient((_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("info-1", "server_search", new Dictionary<string, object?> { ["q"] = "x" }) { InformationalOnly = true }]),
            new ChatResponseUpdate(ChatRole.Assistant, "Answer") { FinishReason = ChatFinishReason.Stop }
        ]);
        using (var adapter = new ChatClientTransportAdapter(infoClient))
        {
            StreamDone? done = null;
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 1)))
                if (ev is StreamDone d) done = d;
            Check(done is not null, "stream completed");
            Equal(StopReason.Stop, done!.Reason, "stop reason is Stop, not ToolUse");
            Equal(0, done.Message.Content.OfType<PiToolCallContent>().Count(), "informational call excluded from executable blocks");
        }

        // 2. ChatFinishReason.ContentFilter must settle as StreamError, never StreamDone.
        var filteredClient = new ScriptedChatClient((_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c-filt", "danger", null)]),
            new ChatResponseUpdate(ChatRole.Assistant, "blocked") { FinishReason = ChatFinishReason.ContentFilter }
        ]);
        using (var adapter = new ChatClientTransportAdapter(filteredClient))
        {
            var events = new List<StreamEvent>();
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 2)))
                events.Add(ev);
            Check(events.OfType<StreamDone>().Count() == 0, "filtered response never emits StreamDone");
            var err = events.OfType<StreamError>().Single();
            Equal(StopReason.Error, err.Reason, "filtered response emits StreamError(Error)");
        }

        // 3. DisposeAsync failure after ErrorContent preserves primary error; DisposeAsync failure after normal stream emits StreamError.
        using (var adapter = new ChatClientTransportAdapter(new ThrowingDisposeChatClient(emitErrorFirst: true)))
        {
            var events = new List<StreamEvent>();
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 3)))
                events.Add(ev);
            var err = events.OfType<StreamError>().Single();
            Equal("primary failure", err.Message.ExtraProperties!.TryGet("errorMessage", out var em) ? em!.Value.GetString() : null, "primary error preserved over cleanup failure");
        }
        using (var adapter = new ChatClientTransportAdapter(new ThrowingDisposeChatClient(emitErrorFirst: false)))
        {
            var events = new List<StreamEvent>();
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 4)))
                events.Add(ev);
            Check(events.OfType<StreamDone>().Count() == 0, "cleanup-only failure does not emit StreamDone");
            var err = events.OfType<StreamError>().Single();
            Equal("cleanup failed", err.Message.ExtraProperties!.TryGet("errorMessage", out var em) ? em!.Value.GetString() : null, "cleanup failure normalized to StreamError");
        }

        // 4. DisableContinuation clears BaseOptions.ContinuationToken & ConversationId; transcript tool deltas mutate cloned BaseOptions.Tools without mutating BaseOptions.
        ChatOptions? capturedOpts = null;
        var inspectClient = new ScriptedChatClient((_, opts) =>
        {
            capturedOpts = opts;
            return [new ChatResponseUpdate(ChatRole.Assistant, "ok") { FinishReason = ChatFinishReason.Stop }];
        });
        var baseAlpha = AIFunctionFactory.Create((string s) => s, "alpha", "baseline alpha");
        var baseBeta = AIFunctionFactory.Create((string s) => s, "beta", "baseline beta");
        var baseOptions = new ChatOptions
        {
            ConversationId = "stateful-conv",
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 1, 2, 3 }),
            Tools = [baseAlpha, baseBeta]
        };
        using (var adapter = new ChatClientTransportAdapter(inspectClient, new ChatClientTransportAdapterOptions { BaseOptions = baseOptions }))
        {
            var sys = new TranscriptEntry("system", JsonData.Parse("""
                {"role":"system","content":"hi","toolsRemoved":[{"name":"alpha"}],"toolsAdded":[{"name":"beta","description":"updated beta","parameters":{"type":"object","properties":{}}}]}
                """));
            var req = new ChatRequest(TestModel, [sys, UserEntry("go")], 5) { DisableContinuation = true };
            await foreach (var _ in adapter.StreamAsync(req)) { }
        }
        Check(capturedOpts is not null, "options captured");
        Check(capturedOpts!.ContinuationToken is null, "ContinuationToken cleared on DisableContinuation");
        Check(capturedOpts.ConversationId is null, "ConversationId cleared on DisableContinuation");
        Equal(1, capturedOpts.Tools!.Count, "alpha removed, beta replaced in place");
        Equal("beta", capturedOpts.Tools[0].Name, "remaining tool is beta");
        Equal("updated beta", capturedOpts.Tools[0].Description, "beta replaced with transcript declaration");
        Equal(2, baseOptions.Tools.Count, "BaseOptions.Tools was not mutated");
    }

    private static async Task TestErrorAndCancelPreserveOpenBuffersAsync()
    {
        // Mid-stream ErrorContent after partial thinking + text deltas must retain both buffers in StreamError.
        var midErrorClient = new ScriptedChatClient((_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("partial thought")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new AiTextContent("partial "), new AiTextContent("reply")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("upstream reset")])
        ]);
        using (var adapter = new ChatClientTransportAdapter(midErrorClient))
        {
            var fallback = new AssistantMessage(TestModel.Api, TestModel.Provider, TestModel.Id, 50, [], TokenUsage.Zero, StopReason.Pending);
            var reducer = new AssistantStreamReducer(fallback);
            StreamError? err = null;
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 50)))
            {
                reducer.Apply(ev);
                if (ev is StreamError e) err = e;
            }
            Check(err is not null && reducer.IsTerminal, "mid-stream error reached terminal");
            Equal(StopReason.Error, err!.Reason, "error stop reason");
            Equal(2, err.Message.Content.Length, "both closed thinking and open text preserved");
            Equal("partial thought", ((ThinkingContent)err.Message.Content[0]).Thinking, "thinking preserved");
            Equal("partial reply", ((PiTextContent)err.Message.Content[1]).Text, "open text buffer preserved in error terminal");
            Equal("partial reply", ((PiTextContent)reducer.Snapshot().Content[1]).Text, "reducer snapshot keeps partial text");
        }

        // Mid-stream cancellation after open thinking delta must preserve the partial thinking buffer.
        var midCancelClient = new MidStreamCancelingChatClient();
        using (var adapter = new ChatClientTransportAdapter(midCancelClient))
        {
            using var cts = new CancellationTokenSource();
            var fallback = new AssistantMessage(TestModel.Api, TestModel.Provider, TestModel.Id, 51, [], TokenUsage.Zero, StopReason.Pending);
            var reducer = new AssistantStreamReducer(fallback);
            StreamError? err = null;
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 51), cts.Token))
            {
                reducer.Apply(ev);
                if (ev is ThinkingDelta) cts.Cancel();
                if (ev is StreamError e) err = e;
            }
            Check(err is not null && reducer.IsTerminal, "mid-stream cancel reached terminal");
            Equal(StopReason.Aborted, err!.Reason, "aborted stop reason");
            Equal(1, err.Message.Content.Length, "open thinking buffer preserved on cancel");
            Equal("halfway thinking", ((ThinkingContent)err.Message.Content[0]).Thinking, "thinking content preserved on cancel");
        }
    }

    private static async Task TestUsageCacheAccountingAsync()
    {
        // External M.E.AI client reports inclusive InputTokenCount = 120, CachedInputTokenCount = 80, OutputTokenCount = 30, Total = null.
        var cachedClient = new ScriptedChatClient((_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, "ok"),
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
            {
                InputTokenCount = 120,
                CachedInputTokenCount = 80,
                OutputTokenCount = 30
            })]) { FinishReason = ChatFinishReason.Stop }
        ]);
        using var adapter = new ChatClientTransportAdapter(cachedClient);
        StreamDone? done = null;
        await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 10)))
            if (ev is StreamDone d) done = d;

        Check(done is not null, "stream done");
        Equal(40d, done!.Message.Usage.Input, "uncached input = 120 - 80");
        Equal(80d, done.Message.Usage.CacheRead, "cache read = 80");
        Equal(30d, done.Message.Usage.Output, "output = 30");
        Equal(150d, done.Message.Usage.TotalTokens, "total = 120 + 30 (no double counting)");

        // Exact round-trip PiSharp -> PiChatClient -> ChatClientTransportAdapter -> PiSharp.
        var innerTransport = new ScriptedTransport((req, _) =>
        {
            var usage = new TokenUsage(100, 42, 20, 5, 167, new UsageCost(0, 0, 0, 0, 0));
            var msg = new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                [new PiTextContent("ok")], usage, StopReason.Stop);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                new TextStarted(0, new PiTextContent("")),
                new TextDelta(0, "ok"),
                new TextEnded(0, "ok"),
                new StreamDone(StopReason.Stop, msg)
            ];
        });
        using var piClient = new PiChatClient(innerTransport, TestModel);
        using var rtAdapter = new ChatClientTransportAdapter(piClient);
        StreamDone? rtDone = null;
        await foreach (var ev in rtAdapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("go")], 11)))
            if (ev is StreamDone d) rtDone = d;

        Check(rtDone is not null, "roundtrip done");
        Equal(100d, rtDone!.Message.Usage.Input, "roundtrip uncached input exact");
        Equal(20d, rtDone.Message.Usage.CacheRead, "roundtrip cacheRead exact");
        Equal(5d, rtDone.Message.Usage.CacheWrite, "roundtrip cacheWrite exact");
        Equal(42d, rtDone.Message.Usage.Output, "roundtrip output exact");
        Equal(167d, rtDone.Message.Usage.TotalTokens, "roundtrip total exact");
    }

    private static async Task TestTransportAdapterReducerConformanceAsync()
    {
        var fakeClient = new ScriptedChatClient((_, _) =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("step 1")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(" step 2") { ProtectedData = "rsig-1" }]),
            new ChatResponseUpdate(ChatRole.Assistant, [new AiTextContent("Hello "), new AiTextContent("world")]),
            new ChatResponseUpdate(ChatRole.Assistant,
            [
                new FunctionCallContent("dup", "tool_a", new Dictionary<string, object?> { ["x"] = 1 }),
                new FunctionCallContent("dup", "tool_b", new Dictionary<string, object?> { ["y"] = "two" }),
                new FunctionCallContent("", "tool_c", null)
            ]),
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
            {
                InputTokenCount = 12,
                OutputTokenCount = 8,
                CachedInputTokenCount = 4,
                ReasoningTokenCount = 3,
                TotalTokenCount = 24
            })]) { FinishReason = ChatFinishReason.ToolCalls }
        ]);

        using var adapter = new ChatClientTransportAdapter(fakeClient);
        var req = new ChatRequest(TestModel, [UserEntry("go")], Timestamp: 123456);
        var fallback = new AssistantMessage(TestModel.Api, TestModel.Provider, TestModel.Id, 123456, [], TokenUsage.Zero, StopReason.Pending);
        var reducer = new AssistantStreamReducer(fallback);

        var events = new List<StreamEvent>();
        await foreach (var ev in adapter.StreamAsync(req))
        {
            events.Add(ev);
            reducer.Apply(ev);
        }

        Check(reducer.IsTerminal, "reducer reached terminal cleanly");
        var done = events.OfType<StreamDone>().Single();
        Equal(StopReason.ToolUse, done.Reason, "tool use stop reason");
        Equal(5, done.Message.Content.Length, "thinking + text + 3 tool calls");

        var thinking = (ThinkingContent)done.Message.Content[0];
        Equal("step 1 step 2", thinking.Thinking, "thinking content");
        Check(thinking.ExtraProperties is not null && thinking.ExtraProperties.TryGet("thinkingSignature", out var sig) && sig!.Value.GetString() == "rsig-1", "thinking signature");

        var text = (PiTextContent)done.Message.Content[1];
        Equal("Hello world", text.Text, "text content");

        var ids = done.Message.Content.OfType<PiToolCallContent>().Select(c => c.Id).ToList();
        Equal(3, ids.Distinct().Count(), "tool call IDs deduplicated & non-empty");
        Check(ids.All(id => !string.IsNullOrWhiteSpace(id)), "all call IDs non-empty");
        Equal(24d, done.Message.Usage.TotalTokens, "usage total tokens");
        Equal(8d, done.Message.Usage.Input, "uncached input = 12 - 4");
        Equal(4d, done.Message.Usage.CacheRead, "usage cache read");
    }

    private static async Task TestTransportAdapterTranscriptProjectionAsync()
    {
        IReadOnlyList<ChatMessage>? seenMessages = null;
        ChatOptions? seenOptions = null;

        var fakeClient = new ScriptedChatClient((msgs, opts) =>
        {
            seenMessages = msgs.ToList();
            seenOptions = opts;
            return
            [
                new ChatResponseUpdate(ChatRole.Assistant, "done") { FinishReason = ChatFinishReason.Stop }
            ];
        });

        using var adapter = new ChatClientTransportAdapter(fakeClient);
        var png = Convert.ToBase64String([1, 2, 3, 4]);
        var transcript = ImmutableArray.Create(
            new TranscriptEntry("system", JsonData.Parse("""
                {"role":"system","content":"Base prompt","sections":{"a":"Section A","b":"Remove me"},"toolsAdded":[
                    {"name":"alpha","description":"Alpha tool","parameters":{"type":"object","properties":{"n":{"type":"number"}}}},
                    {"name":"beta","description":"Beta tool","parameters":{"type":"object","properties":{}}}
                ]}
                """)),
            new TranscriptEntry("system", JsonData.Parse("""
                {"role":"system","content":"Extra prompt","sections":{"b":null,"c":"Section C"},"toolsRemoved":[{"name":"beta"}]}
                """)),
            new TranscriptEntry("user", JsonData.Parse($$"""
                {"role":"user","content":[{"type":"text","text":"Inspect"},{"type":"image","data":"{{png}}","mimeType":"image/png"}]}
                """)),
            new TranscriptEntry("assistant", JsonData.Parse("""
                {"role":"assistant","stopReason":"error","content":[{"type":"text","text":"dropped"}]}
                """)),
            new TranscriptEntry("toolResult", JsonData.Parse("""
                {"role":"toolResult","toolCallId":"c1","toolName":"alpha","content":[{"type":"text","text":"bad input"}],"isError":true}
                """))
        );

        var req = new ChatRequest(TestModel, transcript, 999)
        {
            ThinkingLevel = "medium",
            ServiceTier = ServiceTiers.Fast,
            SessionId = "s-99",
            DisableContinuation = true
        };

        await foreach (var _ in adapter.StreamAsync(req)) { }

        Check(seenMessages is not null && seenOptions is not null, "captured call");
        // system + user + toolResult (errored assistant dropped)
        Equal(3, seenMessages!.Count, "collapsed system + user + toolResult");
        Equal(ChatRole.System, seenMessages[0].Role, "system first");
        var sysText = seenMessages[0].Text;
        Check(sysText.Contains("Base prompt") && sysText.Contains("Extra prompt") && sysText.Contains("Section A") && sysText.Contains("Section C") && !sysText.Contains("Remove me"),
            $"system text merged and patched: {sysText}");

        var img = seenMessages[1].Contents.OfType<DataContent>().Single();
        Equal("image/png", img.MediaType, "image mime type");
        Equal(4, img.Data.Length, "image bytes");

        var toolResult = seenMessages[2].Contents.OfType<FunctionResultContent>().Single();
        Equal("c1", toolResult.CallId, "tool result id");
        Check(toolResult.Exception is not null && toolResult.Exception.Message == "bad input", "isError mapped to Exception");

        Equal(1, seenOptions!.Tools!.Count, "beta removed, alpha remains");
        var decl = (AIFunctionDeclaration)seenOptions.Tools[0];
        Equal("alpha", decl.Name, "remaining tool is alpha");
        Equal(ReasoningEffort.Medium, seenOptions.Reasoning?.Effort, "thinking level -> ReasoningEffort");
        Equal(ServiceTiers.Fast, seenOptions.AdditionalProperties![PiChatAdditionalProperties.ServiceTier], "service tier forwarded");
        Equal("s-99", seenOptions.AdditionalProperties![PiChatAdditionalProperties.SessionId], "session id forwarded in AdditionalProperties");
        Check(seenOptions.ConversationId is null, "SessionId kept separate from stateful ConversationId");
    }

    private static async Task TestTransportAdapterErrorsAndCancellationAsync()
    {
        var errorClient = new ScriptedChatClient((_, _) =>
            [new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("upstream unavailable")])]);
        using (var adapter = new ChatClientTransportAdapter(errorClient))
        {
            var events = new List<StreamEvent>();
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("hi")], 1)))
                events.Add(ev);
            var err = events.OfType<StreamError>().Single();
            Equal(StopReason.Error, err.Reason, "ErrorContent -> StreamError(Error)");
            Check(err.Message.ExtraProperties!.TryGet("errorMessage", out var msg) && msg!.Value.GetString() == "upstream unavailable", "error text");
        }

        var cancelClient = new CancelingChatClient();
        using (var adapter = new ChatClientTransportAdapter(cancelClient))
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var events = new List<StreamEvent>();
            await foreach (var ev in adapter.StreamAsync(new ChatRequest(TestModel, [UserEntry("hi")], 2), cts.Token))
                events.Add(ev);
            var err = events.OfType<StreamError>().Single();
            Equal(StopReason.Aborted, err.Reason, "OperationCanceledException -> StreamError(Aborted)");
        }
    }

    private static async Task TestTransportAdapterDrivesPiSharpAgentAsync()
    {
        var turn = 0;
        var fakeClient = new ScriptedChatClient((messages, options) =>
        {
            turn++;
            if (turn == 1)
            {
                Check(options?.Tools?.Any(t => t.Name == "add") == true, "PiSharp.Agent tool declared to IChatClient");
                return
                [
                    new ChatResponseUpdate(ChatRole.Assistant,
                    [
                        new FunctionCallContent("call-add-1", "add", new Dictionary<string, object?> { ["a"] = 19, ["b"] = 23 })
                    ]) { FinishReason = ChatFinishReason.ToolCalls }
                ];
            }

            var toolMsg = messages.Last(m => m.Role == ChatRole.Tool);
            var toolRes = toolMsg.Contents.OfType<FunctionResultContent>().Single();
            Equal("call-add-1", toolRes.CallId, "tool result callId delivered to turn 2");
            Equal("42", toolRes.Result?.ToString(), "tool execution result delivered to turn 2");
            return
            [
                new ChatResponseUpdate(ChatRole.Assistant, "The sum is 42.") { FinishReason = ChatFinishReason.Stop }
            ];
        });

        using var adapter = new ChatClientTransportAdapter(fakeClient);
        var addTool = new ToolDefinition("add", new AddToolExecutor());
        var systemEntry = new TranscriptEntry("system", JsonData.Parse("""
            {"role":"system","content":"You can add numbers.","toolsAdded":[
                {"name":"add","description":"Adds two integers.","parameters":{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"]}}
            ],"timestamp":1700000009000}
            """));

        var sink = new RecordingSink();
        await using var agent = new Agent(
            new AgentConfiguration(TestModel, adapter, [addTool]),
            () => 1700000010000L,
            sink);

        var result = await agent.PromptAsync([systemEntry, UserEntry("What is 19 + 23?")]);
        Equal(2, turn, "two model turns executed");
        Check(result.Turns.Length == 2, "agent recorded 2 turns");
        var finalAssistant = result.Turns[^1].Result.Chat.Message;
        Equal(StopReason.Stop, finalAssistant.StopReason, "final turn stopped cleanly");
        var answer = finalAssistant.Content.OfType<PiTextContent>().Single().Text;
        Equal("The sum is 42.", answer, "final agent answer from IChatClient");
    }

    private static async Task TestRoundTripBridgeAsync()
    {
        var baseTransport = new ScriptedTransport((req, _) =>
        {
            var finalMsg = new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp,
                [new PiTextContent($"tier={req.ServiceTier};think={req.ThinkingLevel}")],
                new TokenUsage(10, 5, 0, 0, 15, new UsageCost(0, 0, 0, 0, 0)), StopReason.Stop);
            return
            [
                new StreamStarted(new AssistantMessage(req.Model.Api, req.Model.Provider, req.Model.Id, req.Timestamp, [], TokenUsage.Zero, StopReason.Pending)),
                new TextStarted(0, new PiTextContent("")),
                new TextDelta(0, $"tier={req.ServiceTier};think={req.ThinkingLevel}"),
                new TextEnded(0, $"tier={req.ServiceTier};think={req.ThinkingLevel}"),
                new StreamDone(StopReason.Stop, finalMsg)
            ];
        });

        using var innerClient = new PiChatClient(baseTransport, TestModel);
        using var midAdapter = new ChatClientTransportAdapter(innerClient);
        using var outerClient = new PiChatClient(midAdapter, TestModel);

        var response = await outerClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [PiChatAdditionalProperties.ServiceTier] = ServiceTiers.Economy,
                    [PiChatAdditionalProperties.ThinkingLevel] = "low"
                }
            });

        Equal("tier=economy;think=low", response.Text, "roundtrip preserved tier and thinking level");
        Equal(15L, response.Usage?.TotalTokenCount, "roundtrip preserved usage");
    }

    private static async Task TestEmbeddingGeneratorAsync()
    {
        EmbeddingRequest? captured = null;
        MediaModel? capturedModel = null;
        var api = new FakeEmbeddingsApi((model, _, req, _, _) =>
        {
            capturedModel = model;
            captured = req;
            if (req.Input.Length == 1 && req.Input[0] == "FAIL")
                return Task.FromResult(new EmbeddingResult(model.Provider, model.Id, [], ModelOperationStopReason.Error) { ErrorMessage = "bad request" });
            if (req.Input.Length == 1 && req.Input[0] == "ABORT")
                return Task.FromResult(new EmbeddingResult(model.Provider, model.Id, [], ModelOperationStopReason.Aborted) { ErrorMessage = "cancelled" });
            var vectors = req.Input.Select((_, i) => new[] { (i * 2 + 1) * 0.1f, (i * 2 + 2) * 0.1f }).ToImmutableArray();
            return Task.FromResult(new EmbeddingResult(model.Provider, model.Id, vectors, ModelOperationStopReason.Stop)
            {
                InputTokens = 14,
                Cost = 0.0001
            });
        });

        var model = new MediaModel(MediaKind.Embedding, "openai", "text-embedding-3-small", "openai-embeddings") { Dimensions = 1536 };
        var endpoint = new MediaEndpoint("https://api.openai.com/v1", "test-key");
        using var gen = new PiEmbeddingGenerator(api, model, endpoint);

        var empty = await gen.GenerateAsync([]);
        Equal(0, empty.Count, "empty inputs short-circuit");
        Check(captured is null, "API not called for empty input");

        var opts = new EmbeddingGenerationOptions
        {
            ModelId = "text-embedding-3-large",
            Dimensions = 256,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [PiChatAdditionalProperties.TaskType] = "RETRIEVAL_QUERY"
            }
        };
        var result = await gen.GenerateAsync(["hello", "world"], opts);
        Equal("text-embedding-3-large", capturedModel!.Id, "model id override");
        Equal(256, captured!.Dimensions, "dimensions override");
        Equal("RETRIEVAL_QUERY", captured.TaskType, "task type forwarded");
        Equal(2, result.Count, "2 vectors returned");
        Equal(0.1f, result[0].Vector.Span[0], "vector 0[0]");
        Equal(0.4f, result[1].Vector.Span[1], "vector 1[1]");
        Equal(14L, result.Usage?.InputTokenCount, "embedding usage tokens");
        Equal(0.0001, (double)result.AdditionalProperties!["cost"]!, "embedding cost");

        var meta = gen.GetService<EmbeddingGeneratorMetadata>();
        Equal("openai", meta!.ProviderName, "embedding metadata provider");
        Equal(1536, meta.DefaultModelDimensions, "embedding metadata dimensions");

        await ThrowsAsync<InvalidOperationException>(() => gen.GenerateAsync(["FAIL"]), "error stop reason throws");
        await ThrowsAsync<OperationCanceledException>(() => gen.GenerateAsync(["ABORT"]), "aborted stop reason throws");

        // ModelId override without explicit Dimensions must not reuse the default model's Dimensions.
        captured = null;
        await gen.GenerateAsync(["one"], new EmbeddingGenerationOptions { ModelId = "other-embedding-model" });
        Check(captured!.Dimensions is null, "overridden ModelId does not inherit default model Dimensions");

        // Mismatched vector batch size must throw InvalidOperationException.
        var shortApi = new FakeEmbeddingsApi((m, _, _, _, _) =>
            Task.FromResult(new EmbeddingResult(m.Provider, m.Id, [[0.5f]], ModelOperationStopReason.Stop)));
        using var shortGen = new PiEmbeddingGenerator(shortApi, model, endpoint);
        await ThrowsAsync<InvalidOperationException>(() => shortGen.GenerateAsync(["a", "b"]), "mismatched vector count rejected");
    }

    private static Task TestServiceCollectionExtensionsAsync()
    {
        var services = new ServiceCollection();
        var transport = new ScriptedTransport((_, _) => []);
        var api = new FakeEmbeddingsApi((m, _, _, _, _) => Task.FromResult(new EmbeddingResult(m.Provider, m.Id, [], ModelOperationStopReason.Stop)));
        var mediaModel = new MediaModel(MediaKind.Embedding, "openai", "text-embedding-3-small", "openai-embeddings");
        var endpoint = new MediaEndpoint("https://api.openai.com/v1", null);

        services.AddSingleton<IChatTransport>(transport);
        services.AddPiSharpChatClient(TestModel);
        services.AddKeyedPiSharpChatClient("fast", _ => transport, TestModel with { Id = "fast-model" });
        services.AddSingleton<IEmbeddingsApi>(api);
        services.AddPiSharpEmbeddingGenerator(mediaModel, endpoint);

        using var provider = services.BuildServiceProvider();
        var chatClient = provider.GetRequiredService<IChatClient>();
        Equal("test-model-1", chatClient.GetService<ChatClientMetadata>()!.DefaultModelId, "default DI chat client");

        var fastClient = provider.GetRequiredKeyedService<IChatClient>("fast");
        Equal("fast-model", fastClient.GetService<ChatClientMetadata>()!.DefaultModelId, "keyed DI chat client");

        var embGen = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        Equal("text-embedding-3-small", embGen.GetService<EmbeddingGeneratorMetadata>()!.DefaultModelId, "DI embedding generator");

        // Reverse registration: IChatClient -> IChatTransport
        var reverseServices = new ServiceCollection();
        reverseServices.AddSingleton<IChatClient>(new ScriptedChatClient((_, _) => []));
        reverseServices.AddPiSharpChatClientTransport();
        using var reverseProvider = reverseServices.BuildServiceProvider();
        Check(reverseProvider.GetRequiredService<IChatTransport>() is ChatClientTransportAdapter, "reverse DI transport registered");

        return Task.CompletedTask;
    }

    private static TranscriptEntry UserEntry(string text) =>
        new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 1 })));

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>.");
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"{what}: expected {typeof(T).Name}.");
    }

    private sealed class FixedTimeProvider(long unixMs) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }

    private sealed class ScriptedTransport(Func<ChatRequest, CancellationToken, IEnumerable<StreamEvent>> script) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var ev in script(request, cancellationToken))
                yield return ev;
        }
    }

    private sealed class DisposableTransport : IChatTransport, IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ScriptedChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, IEnumerable<ChatResponseUpdate>> script) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var update in script(messages, options))
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MidStreamCancelingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("halfway thinking")]);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CancelingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingDisposeChatClient(bool emitErrorFirst) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            new ThrowingEnumerable(emitErrorFirst);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private sealed class ThrowingEnumerable(bool emitErrorFirst) : IAsyncEnumerable<ChatResponseUpdate>
        {
            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                new ThrowingEnumerator(emitErrorFirst);
        }

        private sealed class ThrowingEnumerator(bool emitErrorFirst) : IAsyncEnumerator<ChatResponseUpdate>
        {
            private bool _yielded;
            public ChatResponseUpdate Current { get; private set; } = null!;

            public ValueTask<bool> MoveNextAsync()
            {
                if (_yielded) return ValueTask.FromResult(false);
                _yielded = true;
                Current = emitErrorFirst
                    ? new ChatResponseUpdate(ChatRole.Assistant, [new ErrorContent("primary failure")])
                    : new ChatResponseUpdate(ChatRole.Assistant, "ok") { FinishReason = ChatFinishReason.Stop };
                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("cleanup failed"));
        }
    }

    private sealed class FakeEmbeddingsApi(Func<MediaModel, MediaEndpoint, EmbeddingRequest, MediaOptions, CancellationToken, Task<EmbeddingResult>> handler) : IEmbeddingsApi
    {
        public string Api => "fake-embeddings";
        public Task<EmbeddingResult> EmbedAsync(MediaModel model, MediaEndpoint endpoint, EmbeddingRequest request, MediaOptions options, CancellationToken cancellationToken) =>
            handler(model, endpoint, request, options, cancellationToken);
    }

    private sealed class AddToolExecutor : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var a = invocation.Call.Arguments.Value.GetProperty("a").GetInt32();
            var b = invocation.Call.Arguments.Value.GetProperty("b").GetInt32();
            return ValueTask.FromResult(new ToolResult([new PiTextContent((a + b).ToString())], JsonData.EmptyObject));
        }
    }

    private sealed class RecordingSink : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
