// Decision 0011 (run W4, integration fixes): the request options RunSpec gained after R0 (the fast and ultrafast tiers, max thinking,
// parallel tool calls, strict tools, text verbosity, reasoning summary, session id and timeout) and the signed parts of an assistant
// message (thinking blocks with their signatures, a tool call's thought signature). Over real routing with the fake provider (ProviderFake:
// no request leaves the process): each option reaches the provider's request body where its API has it and is refused, before anything is
// sent, where it has not; an Anthropic tool loop sends the model's signed thinking back; the capture hook stops a request whose response
// headers do not arrive within the run's timeout. The Tool Bench keeps a turn's signed parts and sends them back with its calls.
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RunOptionCases() =>
    [
        Case("runs.options.new-options-reach-the-responses-body-and-are-refused-elsewhere", async () =>
        {
            await using var host = RunsHost.Routing("runs-options-responses");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-proj-")));
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-")));
            var catalog = host.Services.GetRequiredService<IModelCatalog>();
            var gpt = ModelOf(catalog, "openai", "openai-responses", "gpt-4.1");
            Equal(string.Join(",", RunSpec.ServiceTiers), string.Join(",", host.Engine.Capabilities("openai", gpt)!.ServiceTiers), "OpenAI's Responses API takes all seven tiers");
            var lookup = new RunTool("lookup", "Look a word up", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["word"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("word"),
                ["additionalProperties"] = false,
            }, Strict: true);
            var spec = new RunSpec
            {
                Provider = "openai",
                Model = gpt,
                ServiceTier = "ultrafast",
                TextVerbosity = "low",
                SessionId = "session.a:1",
                ParallelToolCalls = true,
                TimeoutMs = 30_000,
                Tools = [lookup, new RunTool("other", "Not strict", new JsonObject { ["type"] = "object" })],
                Messages = [new RunMessage { Text = "responses options run" }],
            };
            var start = await host.Engine.StartAsync(spec);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
            var body = JsonNode.Parse(host.Provider.Requests.Single(request => request.Body?.Contains("responses options run", StringComparison.Ordinal) == true).Body!)!;
            Equal(("ultrafast", "low", true, "session.a:1"), (body["service_tier"]?.GetValue<string>(), body["text"]?["verbosity"]?.GetValue<string>(),
                body["parallel_tool_calls"]?.GetValue<bool>(), body["prompt_cache_key"]?.GetValue<string>()), "the tier, verbosity, parallel switch and session id");
            var tools = body["tools"]!.AsArray().OfType<JsonObject>().ToDictionary(tool => tool["name"]!.GetValue<string>());
            Equal(true, tools["lookup"]["strict"]?.GetValue<bool>(), "the strict tool is strict");
            Check(tools["other"]["strict"] is not JsonValue other || !other.GetValue<bool>(), "and only that one");

            // Where the API has no such parameter, the run is refused with the reason and nothing is sent.
            var sent = host.Provider.Requests.Count;
            async Task Refused(RunSpec refusedSpec, string option, string reason)
            {
                var error = await ThrowsAsync<RunOptionException>(() => host.Engine.StartAsync(refusedSpec), option);
                Check(error.Plan.Refused.Any(item => item.Option == option && item.Reason?.Contains(reason, StringComparison.Ordinal) == true),
                    $"{option} refused ({reason}): " + error.Message);
            }
            var claude = ModelOf(catalog, "anthropic", "anthropic-messages");
            var plain = new RunSpec { Provider = "anthropic", Model = claude, Messages = [new RunMessage { Text = "x" }] };
            await Refused(plain with { TextVerbosity = "high" }, RunModelOptions.TextVerbosity, "only the OpenAI Responses and Codex APIs");
            await Refused(plain with { SessionId = "s1" }, RunModelOptions.SessionId, "only the OpenAI Responses and Codex APIs");
            await Refused(plain with { ServiceTier = "fast" }, RunModelOptions.ServiceTier, "take a service tier");
            await Refused(plain with { Tools = [lookup] }, RunModelOptions.StrictTools, "strict tools");
            await Refused(spec with { ReasoningSummary = "concise" }, RunModelOptions.ReasoningSummary, "only while the model thinks");
            Equal(sent, host.Provider.Requests.Count, "nothing was sent for them");
            Throws<ArgumentException>(() => (spec with { Tools = null, ParallelToolCalls = false }).Normalize(), "parallel tool calls without a tool");
            Throws<ArgumentException>(() => (spec with { TimeoutMs = 5 }).Normalize(), "a timeout under a second");
            Throws<ArgumentException>(() => (spec with { SessionId = "has space" }).Normalize(), "a session id with a space");
            Throws<ArgumentException>(() => (spec with { TextVerbosity = "loud" }).Normalize(), "an unknown verbosity");
            if (catalog.Models("google").FirstOrDefault() is { } gemini)
            {
                var plan = host.Engine.Plan(new RunSpec { Provider = "google", Model = gemini.Id, Tools = [lookup with { Strict = null }], ParallelToolCalls = false, Messages = [new RunMessage { Text = "x" }] });
                Check(plan.Refused.Any(item => item.Option == RunModelOptions.ParallelToolCalls), "Gemini has no parallel switch: " + plan.RefusalMessage);
            }
        }),

        Case("runs.options.an-anthropic-tool-loop-sends-the-signed-thinking-back", async () =>
        {
            await using var host = RunsHost.Routing("runs-options-anthropic");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-")));
            var catalog = (RuntimeModelCatalog)host.Services.GetRequiredService<IModelCatalog>();
            var thinker = catalog.Models("anthropic").First(model => model.ThinkingLevels.Count > 1 && catalog.Find("anthropic", model.Id)!.Durable.Api == "anthropic-messages");
            var level = thinker.ThinkingLevels.First(item => item != "off");
            var weather = new RunTool("get_weather", "Weather", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } } });
            var signature = Planted("sig-");
            var spec = new RunSpec
            {
                Provider = "anthropic",
                Model = thinker.Id,
                Thinking = level,
                ParallelToolCalls = false,
                Tools = [weather],
                Messages =
                [
                    new RunMessage { Text = "anthropic signed loop" },
                    new RunMessage
                    {
                        Role = "assistant",
                        Thinking = [new RunThinking("The user wants Oslo's weather.", signature), new RunThinking("", Planted("redacted-"), Redacted: true)],
                        ToolCalls = [new RunToolCall("toolu_01", "get_weather", new JsonObject { ["city"] = "Oslo" })],
                    },
                    new RunMessage { Role = "toolResult", ToolCallId = "toolu_01", ToolName = "get_weather", Text = "12 C, light rain" },
                ],
            };
            var start = await host.Engine.StartAsync(spec);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
            Equal("Hello there", run.OutputText, "the provider's answer");
            var body = JsonNode.Parse(host.Provider.Requests.Single(request => request.Body?.Contains("anthropic signed loop", StringComparison.Ordinal) == true).Body!)!;
            var assistant = body["messages"]!.AsArray().OfType<JsonObject>().First(message => message["role"]?.GetValue<string>() == "assistant");
            var blocks = assistant["content"]!.AsArray().OfType<JsonObject>().ToList();
            Equal(("thinking", "The user wants Oslo's weather.", signature), (blocks[0]["type"]?.GetValue<string>(), blocks[0]["thinking"]?.GetValue<string>(),
                blocks[0]["signature"]?.GetValue<string>()), "the signed thinking block goes back first: " + assistant.ToJsonString());
            Equal("redacted_thinking", blocks[1]["type"]?.GetValue<string>(), "the redacted block as Anthropic sent it");
            Equal("tool_use", blocks[2]["type"]?.GetValue<string>(), "then the call");
            Equal(true, body["tool_choice"]?["disable_parallel_tool_use"]?.GetValue<bool>(), "parallel tool calls off, Anthropic's way");

            // A Google call's thought signature rides on its tool call, as Pi's wire JSON has it.
            var plan = RunModelOptions.Plan(new RunSpec
            {
                Provider = "anthropic",
                Model = thinker.Id,
                Tools = [weather],
                Messages = [new RunMessage { Text = "x" }, new RunMessage { Role = "assistant", ToolCalls = [new RunToolCall("c1", "get_weather", new JsonObject(), "g-sig-1")] },
                    new RunMessage { Role = "toolResult", ToolCallId = "c1", ToolName = "get_weather", Text = "ok" }],
            }.Normalize(), catalog.Find("anthropic", thinker.Id)!.Durable, thinker, 1);
            Check(string.Join("\n", plan.Messages.Select(message => message.ToString())).Contains("\"thoughtSignature\":\"g-sig-1\"", StringComparison.Ordinal),
                "the thought signature is on the call");
            Throws<ArgumentException>(() => (spec with { Messages = [new RunMessage { Text = "x", Thinking = [new RunThinking("no")] }] }).Normalize(), "a user message has no thinking");
        }),

        Case("runs.capture.a-timeout-stops-a-request-whose-headers-do-not-arrive", async () =>
        {
            using var handler = new RunCaptureHandler(new SilentProvider(), ownsInner: true);
            using var client = new HttpMessageInvoker(handler, disposeHandler: false);
            var started = System.Diagnostics.Stopwatch.StartNew();
            RunHttpScope.Current = new RunHttpContext { RunId = "r-timeout", Api = "openai-responses", TimeoutMs = RunSpec.MinTimeoutMs };
            try
            {
                var error = await ThrowsAsync<TimeoutException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/v1/responses"), CancellationToken.None),
                    "the request is stopped");
                Check(error.Message.Contains("1,000 ms", StringComparison.Ordinal), "it says why: " + error.Message);
                Check(started.Elapsed < TimeSpan.FromSeconds(20), "after the timeout, not the provider's: " + started.Elapsed);
                RunHttpScope.Current = new RunHttpContext { RunId = "r-no-timeout", Api = "openai-responses" };
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await ThrowsAsync<OperationCanceledException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/v1/responses"), cancel.Token),
                    "without a timeout the caller's cancellation still stops it");
            }
            finally
            {
                RunHttpScope.Current = null;
            }
        }),

        Case("bench.replay.keeps-a-turns-signed-thinking-and-thought-signatures", () =>
        {
            var output = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "Need the weather.", ["thinkingSignature"] = "sig-a" },
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["thinkingSignature"] = "opaque", ["redacted"] = true },
                    new JsonObject { ["type"] = "text", ["text"] = "Checking." },
                    new JsonObject { ["type"] = "toolCall", ["id"] = "c1", ["name"] = "get_weather", ["arguments"] = new JsonObject { ["city"] = "Oslo" }, ["thoughtSignature"] = "g-1" }),
                ["stopReason"] = "toolUse",
            };
            var outcome = BenchRunner.TurnOutcome.Of(new RunRecord { Id = "r1", Op = "run-r1", Source = "bench", Status = RunStatus.Completed, Output = output, Created = DateTimeOffset.UtcNow });
            Equal("Need the weather.", outcome.Thinking, "the thinking text the timeline shows");
            Equal(2, outcome.Blocks?.Count, "both blocks kept");
            Equal(("sig-a", true), (outcome.Blocks![0].Signature, outcome.Blocks[1].Redacted), "with their signatures");
            Equal("g-1", outcome.Calls.Single().Signature, "the call's thought signature");

            var session = new BenchSession
            {
                Id = "b1",
                Entries =
                [
                    new BenchEntry { Kind = "user", Text = "Weather in Oslo?" },
                    new BenchEntry { Kind = "assistant", Turn = new BenchTurn { Index = 1, RunId = "r1", Status = RunStatus.Completed, Text = "Checking.", Calls = ["c1"], ThinkingBlocks = outcome.Blocks } },
                ],
                Calls = [new BenchCall { Id = "c1", Tool = "get_weather", Turn = 1, Index = 1, Count = 1, Arguments = new JsonObject { ["city"] = "Oslo" }, ThoughtSignature = "g-1",
                    Result = new BenchResult { Text = "12 C" } }],
            };
            var messages = BenchRunner.Messages(session);
            var assistant = messages.Single(message => message.Role == "assistant");
            Equal(2, assistant.Thinking?.Count, "the next turn sends the thinking blocks back");
            Equal("sig-a", assistant.Thinking![0].Signature, "signed");
            Equal("g-1", assistant.ToolCalls!.Single().ThoughtSignature, "and the call's thought signature");
        }),
    ];

    /// <summary>A provider that never answers until the request is cancelled.</summary>
    private sealed class SilentProvider : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
