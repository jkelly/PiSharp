// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/codemode-models.test.ts ("code mode models.*"), as
// codemode.models.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - pi-ai's `createProvider({ id: "test", models, classifiers, images })` set on the chat's models is TestProviderModels, an IDurableModels
//   over the faux models that adds the test provider's classifier and image models (pi-ai model JSON) and their implementations, as
//   pi-ai's Models would route classify() and generateImages() to them. Its auth always resolves, so getAvailableOfType lists its models.
// - setTimeout(..., 50) is Task.Delay(50); NodeExecutionEnv is LocalExecutionEnv.
using PiSharp.Codemode;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CodemodeModelsCases() => CodemodeModelsTests.Cases();
}

internal static class CodemodeModelsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly JsonObjectValue Usage = O(
        """{"input":10,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":12,"cost":{"input":0.001,"output":0.0002,"cacheRead":0,"cacheWrite":0,"total":0.0012}}""");

    private const string Cost = """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}""";

    /// <summary>The test provider's models: a classifier (with credential headers) and an image model.</summary>
    private static readonly JsonObjectValue[] TestModels =
    [
        O($$"""{"id":"judge","name":"Judge","api":"test-classifier","provider":"test","baseUrl":"https://example.invalid","input":["text"],"cost":{{Cost}},"headers":{"authorization":"secret"},"type":"classifier","contextWindow":1000}"""),
        O($$"""{"id":"painter","name":"Painter","api":"test-images","provider":"test","baseUrl":"https://example.invalid","input":["text"],"cost":{{Cost}},"type":"image","output":["image"]}"""),
    ];

    /// <summary>Calls of the test provider: classifications, image generations, classifications in flight and the most at once.</summary>
    internal sealed class Calls
    {
        private int classify;
        private int images;
        private int inFlight;
        private int maxInFlight;
        public int Classify => Volatile.Read(ref classify);
        public int Images => Volatile.Read(ref images);
        public int MaxInFlight => Volatile.Read(ref maxInFlight);
        public void Classified() => Interlocked.Increment(ref classify);
        public void Generated() => Interlocked.Increment(ref images);
        public int Enter()
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref maxInFlight)) && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
            return now;
        }
        public void Leave() => Interlocked.Decrement(ref inFlight);
    }

    /// <summary><c>atFour</c>: hold classifier calls until four run, then a little longer, so a fifth would show up.</summary>
    internal sealed class Gate
    {
        public volatile Deferred Open = new();
        public volatile bool AtFour;
    }

    /// <summary>The chat's models with the test provider's classifier and image model (see the file header).</summary>
    private sealed class TestProviderModels(IDurableModels inner, Calls calls, Gate gate) : IDurableModels
    {
        public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);
        public IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamSimple(DurableModel model, System.Collections.Immutable.ImmutableArray<JsonValue> messages,
            DurableStreamOptions? options, CancellationToken cancellationToken) => inner.StreamSimple(model, messages, options, cancellationToken);
        public Task<PiSharp.Contracts.AssistantMessage> CompleteSimpleAsync(DurableModel model, System.Collections.Immutable.ImmutableArray<JsonValue> messages,
            DurableStreamOptions? options, CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, options, cancellationToken);
        public IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, cancellationToken);
        public Task<PiSharp.Contracts.AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, cancellationToken);
        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, cancellationToken);

        public System.Collections.Immutable.ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) =>
            [.. TestModels.Where(model => S(model["type"]) == type && (provider is null || S(model["provider"]) == provider))];

        public Task<System.Collections.Immutable.ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider,
            CancellationToken cancellationToken) => Task.FromResult(GetModelsOfType(type, provider));

        public async Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken)
        {
            calls.Classified();
            if (((JsonObjectValue)context["state"]).TryGetValue("fail", out var fail) && fail is JsonBooleanValue { Value: true })
            {
                return Obj(("api", model["api"]), ("provider", model["provider"]), ("model", model["id"]), ("answers", JsonObjectValue.Empty),
                    ("stopReason", "error"), ("errorMessage", "quota exceeded"), ("timestamp", 0));
            }
            var now = calls.Enter();
            var open = gate.Open;
            if (gate.AtFour && now == 4) _ = Task.Delay(50).ContinueWith(_ => open.Resolve(), TaskScheduler.Default);
            await open.Task;
            calls.Leave();
            return Obj(("api", model["api"]), ("provider", model["provider"]), ("model", model["id"]), ("answers", O("""{"good":{"value":true}}""")),
                ("usage", Usage), ("stopReason", "stop"), ("timestamp", 0));
        }

        public Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken)
        {
            calls.Generated();
            return Task.FromResult(Obj(("api", model["api"]), ("provider", model["provider"]), ("model", model["id"]),
                ("output", JsonValue.Array(Obj(("type", "image"), ("data", Png), ("mimeType", "image/png")))), ("stopReason", "stop"), ("timestamp", 0)));
        }
    }

    internal sealed class ModelsChat
    {
        public ModelsChat()
        {
            var setup = Setup();
            Gate.Open.Resolve();
            Setup = setup.WithModels(new TestProviderModels(setup.Models, Calls, Gate));
            Setup.Registry.Install(DurableCodemode.CreateExtension(new CodemodeToolOptions { Sandbox = new CodemodeSandbox() }));
        }

        public ChatSetup Setup { get; }
        public Calls Calls { get; } = new();
        public Gate Gate { get; } = new();
    }

    private sealed record Ran(string Text, JsonObjectValue Message, JsonObjectValue? Usage)
    {
        public List<JsonObjectValue> Rows => [.. ((JsonArrayValue)((JsonObjectValue)Message["details"])["calls"]).Cast<JsonObjectValue>()];
    }

    private static async Task<Ran> Run(ModelsChat chat, string code, IExecutionEnv? env = null)
    {
        chat.Setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("codemode", Obj(("code", code)), "s1")], stopReason: "toolUse"),
            FauxAssistantMessage("done"));
        var (harness, root) = await OpenChat(new MemoryStorage(), chat.Setup, env is null ? null : (_, _) => Task.FromResult<IExecutionEnv?>(env));
        await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
        var entry = (await AllEntries(root)).First(candidate => candidate.Kind == "pi.tool-result");
        var message = (JsonObjectValue)entry.Model![0];
        var text = System.Text.RegularExpressions.Regex.Replace(CodemodeTests.ContentText(message),
            @"^Script (completed|failed)\nWall time [\d.]+ seconds\nOutput:\n", "");
        var usage = await harness.SnapshotAsync(UsageLedger.UsageDoc, root.Id, Context);
        await harness.CloseAsync(Context);
        return new Ran(text, message, usage);
    }

    private static void Contains(string text, string part, string what) => Check(text.Contains(part, StringComparison.Ordinal), $"{what}: <{text}> lacks <{part}>");

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "codemode.models.";

        yield return Case(prefix + "reads-the-model-catalog-without-credentials-in-headers", async () =>
        {
            var chat = new ModelsChat();
            var ran = await Run(chat, """
                const classifiers = await models.getModelsOfType("classifier", "test");
                			const one = await models.getModelOfType("classifier", "test", "judge");
                			const available = await models.getAvailableOfType("image");
                			return [classifiers.map((m) => m.id), one.id, "headers" in one, available.map((m) => m.id)];
                """);
            SameJson("""[["judge"],"judge",false,["painter"]]""", JsonValue.Parse(ran.Text), "result");
        });

        yield return Case(prefix + "classifies-through-an-internal-tool-a-nested-call-with-its-usage-hidden-from-tools-and-listings", async () =>
        {
            var chat = new ModelsChat();
            var ran = await Run(chat, """
                const model = await models.getModelOfType("classifier", "test", "judge");
                			const result = await models.classify(model, {
                				state: { text: "hi" },
                				questions: { good: { type: "bool", instructions: "Is it good?", criteria: { true: "yes", false: "no" } } },
                			});
                			const found = (await searchTools("classify generate images models")).length;
                			const described = (await describeTool("pi.codemode.models.classify")) !== undefined;
                			return [result.stopReason, result.answers.good.value, Object.keys(tools).some((name) => name.includes("models")), ALL_TOOLS.some((tool) => tool.name.includes("models")), found, described];
                """);
            SameJson("""["stop",true,false,false,0,false]""", JsonValue.Parse(ran.Text), "result");
            Equal(1, chat.Calls.Classify, "classify calls");
            Equal(1, ran.Rows.Count, "rows");
            MatchJson("""{"name":"models.classify","status":"ok"}""", ran.Rows[0], "row");
            // Counted once, under the internal tool, by the Harness.
            var total = ((JsonNumberValue)((JsonObjectValue)((JsonObjectValue)((JsonObjectValue)ran.Usage!["tools"])["pi.codemode.models.classify"])["cost"])["total"]).Value;
            Check(Math.Abs(total - 0.0012) < 0.00005, $"cost {total}");
        });

        yield return Case(prefix + "resolves-a-model-call-that-failed-with-its-result-as-the-coding-agent-does", async () =>
        {
            var chat = new ModelsChat();
            var ran = await Run(chat, """
                const model = await models.getModelOfType("classifier", "test", "judge");
                			const result = await models.classify(model, {
                				state: { fail: true },
                				questions: { good: { type: "bool", instructions: "?", criteria: { true: "y", false: "n" } } },
                			});
                			return [result.stopReason, result.errorMessage];
                """);
            SameJson("""["error","quota exceeded"]""", JsonValue.Parse(ran.Text), "result");
            // The row still shows the failure.
            Equal(1, ran.Rows.Count, "rows");
            MatchJson("""{"name":"models.classify","status":"error"}""", ran.Rows[0], "row");
        });

        yield return Case(prefix + "rejects-a-bad-classifier-context-with-the-expected-shape-before-any-request", async () =>
        {
            var chat = new ModelsChat();
            var ran = await Run(chat, """
                const model = await models.getModelOfType("classifier", "test", "judge");
                			try { await models.classify(model, { prompt: "x" }); } catch (error) { return error.message; }
                """);
            Contains(ran.Text, "context.state must be an object", "result");
            Contains(ran.Text, "Expected context:", "result");
            Equal(0, chat.Calls.Classify, "classify calls");
        });

        yield return Case(prefix + "runs-at-most-four-model-calls-at-once", async () =>
        {
            var chat = new ModelsChat();
            chat.Gate.Open = new Deferred();
            chat.Gate.AtFour = true;
            var ran = await Run(chat, """
                const model = await models.getModelOfType("classifier", "test", "judge");
                			const context = { state: {}, questions: { good: { type: "bool", instructions: "?", criteria: { true: "y", false: "n" } } } };
                			return (await Promise.all(Array.from({ length: 8 }, () => models.classify(model, context)))).length;
                """);
            Equal("8", ran.Text, "result");
            Equal(8, chat.Calls.Classify, "classify calls");
            Equal(4, chat.Calls.MaxInFlight, "most at once");
        });

        yield return Case(prefix + "does-not-start-a-model-call-that-waited-for-a-slot-after-the-script-ended", async () =>
        {
            var chat = new ModelsChat();
            chat.Gate.Open = new Deferred();
            var directory = Temp("codemode-models");
            try
            {
                var ran = await Run(chat, """
                    const model = await models.getModelOfType("classifier", "test", "judge");
                    				const context = { state: {}, questions: { good: { type: "bool", instructions: "?", criteria: { true: "y", false: "n" } } } };
                    				for (let i = 0; i < 6; i++) models.classify(model, context).catch(() => {});
                    				// A journaled call after them: by its result, the first four have been launched.
                    				await models.getModelsOfType("image");
                    				return "x".repeat(60000);
                    """, new SlowTempFiles(directory, chat.Gate));
                Contains(ran.Text, "[Full output: ", "result");
                Equal(4, chat.Calls.Classify, "classify calls");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        yield return Case(prefix + "notes-generated-images-the-script-did-not-show", async () =>
        {
            var hidden = await Run(new ModelsChat(), """
                const model = await models.getModelOfType("image", "test", "painter");
                			const result = await models.generateImages(model, { input: [{ type: "text", text: "a cat" }] });
                			return result.output.length;
                """);
            Contains(hidden.Text, "Note: models.generateImages() returned 1 image that the script did not show", "hidden");
            var shown = await Run(new ModelsChat(), """
                const model = await models.getModelOfType("image", "test", "painter");
                			const result = await models.generateImages(model, { input: [{ type: "text", text: "a cat" }] });
                			image(result.output[0]);
                """);
            Check(!shown.Text.Contains("Note:", StringComparison.Ordinal), "shown: " + shown.Text);
            Check(((JsonArrayValue)shown.Message["content"]).Cast<JsonObjectValue>().Any(item => S(item["type"]) == "image"), "image block");
        });
    }

    /// <summary>The result spills to a file after the script ended and before the tool returns. Then the running calls finish and free their
    /// slots, which the two waiting calls would take.</summary>
    private sealed class SlowTempFiles(string cwd, Gate gate) : LocalExecutionEnv(cwd)
    {
        public override async Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context)
        {
            gate.Open.Resolve();
            await Task.Delay(100);
            return await base.CreateTempDirAsync(prefix, context);
        }
    }
}
