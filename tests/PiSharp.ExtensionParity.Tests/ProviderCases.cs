using System.Text.Json.Nodes;

// Extension providers (types.ts ProviderConfig.images/classifiers, model-registry.ts classify/generateImages) through the Node bridge:
// an extension registers a classifier and an image provider; a tool calls ctx.modelRegistry.classify and generateImages, which run
// through PiSharp's model operations registry back into the provider's Node implementation. Needs Node.js.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ProviderCases() =>
    [
        ("provider.classifier-and-image-providers-registered-by-an-extension", ClassifierAndImageProviders),
        ("provider.pi-ai-complete-from-extension-code", CompleteFromExtension),
    ];

    // summarize.ts/qna.ts style: an extension calls pi-ai complete() with ctx.model; the request streams through PiSharp's live route.
    private static async Task CompleteFromExtension()
    {
        using var sandbox = NodeSandbox("complete");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "summarize.ts"), """
            import { complete } from "@earendil-works/pi-ai";
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              pi.registerCommand("summarize", { description: "Summarize", handler: async (args: string, ctx: any) => {
                const reply = await complete(ctx.model, { systemPrompt: "You summarize.", messages: [{ role: "user", content: [{ type: "text", text: "Summarize: " + args }], timestamp: Date.now() }] });
                appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["reply", reply.stopReason, reply.content.map((c: any) => c.text ?? "").join(""), reply.errorMessage ?? null]) + "\n");
              } });
            }
            """);
        sandbox.Respond = (_, _) => AnthropicText("short summary");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/summarize the plan"]);
        Equal(0, code, "exit; " + stderr);
        Equal("""["reply","stop","short summary",null]""", LogLines(sandbox).Single(), "complete() result");
        var request = sandbox.Requests.Single().Json;
        Check(request.GetProperty("system").ToString().Contains("You summarize.", StringComparison.Ordinal), "system prompt sent");
        Check(request.GetProperty("messages")[0].ToString().Contains("Summarize: the plan", StringComparison.Ordinal), "user message sent");
    }

    private const string ProviderExtension = """
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerProvider("acme", {
            baseUrl: "https://acme.invalid/v1", apiKey: "ACME_KEY",
            models: [
              { id: "judge", name: "Judge", type: "classifier", api: "acme-classify", input: ["text"], cost: { input: 1, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 8000 },
              { id: "painter", name: "Painter", type: "image", api: "acme-images", input: ["text"], output: ["image"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } },
            ],
            classifiers: { "acme-classify": { async classify(model: any, context: any, options: any) {
              log("classify", model.id, model.provider, Object.keys(context.questions), options?.apiKey ?? null);
              return { api: model.api, provider: model.provider, model: model.id, answers: { safe: { type: "bool", probability: 0.75 } }, stopReason: "stop", timestamp: 1 };
            } } },
            images: { "acme-images": { async generateImages(model: any, context: any) {
              log("images", model.id, context.input[0].text);
              return { api: model.api, provider: model.provider, model: model.id, output: [{ type: "image", data: "aGk=", mimeType: "image/png" }], stopReason: "stop", timestamp: 2 };
            } } },
          });
          pi.registerTool({
            name: "judge", label: "Judge", description: "Classify and paint", parameters: { type: "object", properties: {} },
            async execute(id: string, params: any, signal: any, onUpdate: any, ctx: any) {
              const judge = ctx.modelRegistry.getModelOfType("classifier", "acme", "judge");
              const painter = ctx.modelRegistry.getModelOfType("image", "acme", "painter");
              const verdict = await ctx.modelRegistry.classify(judge, { state: { text: "hello" }, questions: { safe: { type: "bool", instructions: "Is it safe?", criteria: { true: "safe", false: "unsafe" } } } });
              const image = await ctx.modelRegistry.generateImages(painter, { input: [{ type: "text", text: "a cat" }] });
              log("results", verdict.stopReason, verdict.answers?.safe?.probability ?? null, verdict.errorMessage ?? null, image.stopReason, image.output?.[0]?.mimeType ?? null, image.errorMessage ?? null);
              return { content: [{ type: "text", text: "judged" }], details: {} };
            },
          });
        }
        """;

    private static async Task ClassifierAndImageProviders()
    {
        using var sandbox = NodeSandbox("providers");
        sandbox.Vars["ACME_KEY"] = "acme-secret";
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "acme.ts"), ProviderExtension);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("judge", new { }) : AnthropicText("done");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "judge it"]);
        Equal(0, code, "exit; " + stderr);
        var lines = LogLines(sandbox).Select(line => JsonNode.Parse(line)!.AsArray()).ToList();
        var classify = lines.Single(record => record[0]!.GetValue<string>() == "classify");
        Equal("""["classify","judge","acme",["safe"],"acme-secret"]""", classify.ToJsonString(), "classify reached the extension with the provider's resolved key");
        Check(lines.Any(record => record.ToJsonString() == """["images","painter","a cat"]"""), "generateImages reached the extension: " + string.Join("\n", LogLines(sandbox)));
        Equal("""["results","stop",0.75,null,"stop","image/png",null]""", lines.Single(record => record[0]!.GetValue<string>() == "results").ToJsonString(), "results returned to the tool");
    }
}
