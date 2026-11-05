// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/extensions/{pi-ai-extension-view,virtual-modules,
// loader}.ts, packages/ai/src/{index,compat}.ts, packages/ai/src/utils/{validation,string-enum}.ts, packages/ai/src/providers/faux.ts,
// packages/agent/src/{proxy,agent-loop}.ts and packages/tui/src/components/markdown.ts.
using System.Diagnostics;
using System.Text;

// Pi 1.5.0 sync (bridge). The Node bridge's compatibility modules are the v1.5.0 packages ported by virtual/port-upstream.mjs. Authored
// expectations derived by reading the Pi v1.5.0 sources and tests above; nothing here is an upstream capture.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150BridgeCases() =>
    [
        ("sync150.bridge-extensions-import-the-pi-ai-extension-view-and-pi-ai-models", BridgeExtensionView),
        ("sync150.bridge-installed-pi-maps-pi-ai-models", BridgeInstalledPiAiModels),
        ("sync150.bridge-pi-ai-validates-tool-arguments-with-pi-schema", BridgePiSchemaValidation),
        ("sync150.bridge-faux-provider-counts-every-prompt-token-once", BridgeFauxCacheWrite),
        ("sync150.bridge-stream-proxy-parses-tool-call-arguments-incrementally", BridgeStreamProxyArguments),
        ("sync150.bridge-agent-identity-forwards-the-callers-identity-without-attribution", BridgeAgentIdentity),
        ("sync150.bridge-markdown-cjk-emphasis-display-math-quotes-urls-and-block-reuse", BridgeMarkdown),
    ];

    // virtual-modules.ts and loader.ts getAliases: extensions' `@earendil-works/pi-ai` and `/compat` (and the @mariozechner names) are the
    // coding agent's pi-ai extension view, pi-ai's compat entry plus TypeBox's Type and a TypeBox StringEnum, and pi-ai/models is mapped.
    // pi-ai itself no longer exports Type or the assistant message frame API. Offline, so the compatibility modules run the extension.
    private static async Task BridgeExtensionView()
    {
        using var sandbox = NodeSandbox("sync150-extension-view");
        sandbox.Vars["PI_OFFLINE"] = "1";
        sandbox.Vars["PISHARP_PI_RUNTIME_DIR"] = Path.Combine(sandbox.Root, "no-runtime");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "view.ts"), """
            import { Type, StringEnum, complete, AssistantMessageFrameEncoder, reduceAssistantMessageFrames } from "@earendil-works/pi-ai";
            import { Type as CompatType, StringEnum as CompatEnum } from "@mariozechner/pi-ai/compat";
            import { clampThinkingLevel, createModels } from "@earendil-works/pi-ai/models";
            import { clampThinkingLevel as legacyClamp } from "@mariozechner/pi-ai/models";
            import { Type as TypeBox } from "typebox";
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              const parameters = Type.Object({ mode: StringEnum(["a", "b"], { description: "Mode" }) });
              const model = { id: "m", api: "faux", provider: "p", reasoning: true, thinkingLevelMap: { xhigh: null } };
              appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["view", Type === TypeBox, CompatType === Type, CompatEnum === StringEnum,
                typeof complete, AssistantMessageFrameEncoder === undefined && reduceAssistantMessageFrames === undefined, parameters,
                clampThinkingLevel(model, "xhigh"), legacyClamp === clampThinkingLevel, typeof createModels().getProviders]) + "\n");
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        Equal("compatibility", host.Modules, "modules");
        Equal("""["view",true,true,true,"function",true,{"type":"object","required":["mode"],"properties":{"mode":{"type":"string","enum":["a","b"],"description":"Mode"}}},"high",true,"function"]""",
            LogRecords(sandbox, "view").Single().ToJsonString(), "extension view and pi-ai/models");
    }

    // loader.ts getAliases over an installed Pi: `@earendil-works/pi-ai/models` gets its own alias, so it is not resolved under the pi-ai
    // root's alias (Pi 1.2.0's packages have the ./models entry; their coding agent predates the view, so the root stays its compat entry).
    private static async Task BridgeInstalledPiAiModels()
    {
        RequirePiRuntime();
        using var sandbox = NodeSandbox("sync150-installed-models");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "models.ts"), """
            import { clampThinkingLevel } from "@earendil-works/pi-ai/models";
            import { clampThinkingLevel as legacyClamp } from "@mariozechner/pi-ai/models";
            import { Type } from "@earendil-works/pi-ai";
            import { appendFileSync } from "node:fs";
            export default function (pi: any) {
              const model = { id: "m", api: "faux", provider: "p", reasoning: true, thinkingLevelMap: { xhigh: null } };
              appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["models", clampThinkingLevel(model, "xhigh"), legacyClamp(model, "minimal"),
                typeof Type.Object]) + "\n");
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        Check(host.Modules.StartsWith("pi@", StringComparison.Ordinal), "installed Pi: " + host.Modules);
        Equal("""["models","high","minimal","function"]""", LogRecords(sandbox, "models").Single().ToJsonString(), "installed pi-ai/models");
    }

    // validation.ts: validateToolArguments compiles with pi-schema (coerce: true), returns a copy, and lists pi-schema's errors at the
    // property, then the arguments it received; string-enum.ts StringEnum keeps its JSON; the pi-ai entry has no Type.
    private static async Task BridgePiSchemaValidation()
    {
        var output = await BridgeModuleScript("sync150-pi-schema", """
            const ai = await load("pi-ai.mjs");
            const { Type } = await load("typebox.mjs");
            const tool = { name: "t", description: "", parameters: Type.Object({ count: Type.Number(), flag: Type.Optional(Type.Boolean()) }) };
            const message = (args) => { try { ai.validateToolArguments(tool, { type: "toolCall", id: "1", name: "t", arguments: args }); return "ok"; } catch (error) { return error.message; } };
            const input = { count: "3", flag: "false" };
            const coerced = ai.validateToolArguments(tool, { type: "toolCall", id: "1", name: "t", arguments: input });
            console.log(JSON.stringify({ coerced, copied: coerced !== input, missing: message({}), wrong: message({ count: "many" }),
              enumSchema: ai.StringEnum(["a", "b"], { description: "d" }), hasType: "Type" in ai }));
            """);
        Equal("""{"coerced":{"count":3,"flag":false},"copied":true,"missing":"Validation failed for tool \"t\":\n  - count: missing\n\nReceived arguments:\n{}","wrong":"Validation failed for tool \"t\":\n  - count: expected number, got string\n\nReceived arguments:\n{\n  \"count\": \"many\"\n}","enumSchema":{"type":"string","enum":["a","b"],"description":"d"},"hasType":false}""",
            output, "pi-schema validation");
    }

    // faux.ts withUsageEstimate: with a session, every prompt token is a cache read or a cache write (max(0, prompt - read)) and input is
    // 0; without caching every prompt token is input. "user:hello world" is 16 characters (4 tokens); with "user:next", 27 (7 tokens).
    private static async Task BridgeFauxCacheWrite()
    {
        var output = await BridgeModuleScript("sync150-faux", """
            const ai = await load("pi-ai.mjs");
            const registration = ai.registerFauxProvider();
            registration.setResponses(["a", "b", "c", "d"].map((text) => ai.fauxAssistantMessage(text)));
            const user = (content) => ({ role: "user", content, timestamp: 1 });
            const usage = async (messages, options) => {
              const { input, cacheRead, cacheWrite } = (await ai.complete(registration.getModel(), { messages }, options)).usage;
              return [input, cacheRead, cacheWrite];
            };
            const cached = { sessionId: "session-1", cacheRetention: "short" };
            const result = { first: await usage([user("hello world")], cached), extended: await usage([user("hello world"), user("next")], cached),
              edited: await usage([user("hello wide"), user("next")], cached), uncached: await usage([user("hello world")], { sessionId: "session-2", cacheRetention: "none" }) };
            registration.unregister();
            console.log(JSON.stringify(result));
            """);
        Equal("""{"first":[0,0,4],"extended":[0,4,3],"edited":[0,3,4],"uncached":[4,0,0]}""", output, "faux usage");
    }

    // proxy.ts streamProxy: each tool-call delta feeds a ToolCallArgumentsStream held outside the partial message, so the block carries
    // only type, id, name and arguments, the partial arguments grow, and 15,000 deltas of a 520 KB argument stay linear. fetch is faked.
    private static async Task BridgeStreamProxyArguments()
    {
        var output = await BridgeModuleScript("sync150-stream-proxy", """
            const core = await load("pi-agent-core.mjs");
            const ai = await load("pi-ai.mjs");
            const usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
            const content = 'line "quoted" \\ end\n'.repeat(20000);
            const text = JSON.stringify({ path: "a.ts", content });
            const events = [{ type: "start" }, { type: "toolcall_start", contentIndex: 0, id: "call_1", toolName: "write" }];
            for (let index = 0; index < text.length; index += 32) events.push({ type: "toolcall_delta", contentIndex: 0, delta: text.slice(index, index + 32) });
            events.push({ type: "toolcall_end", contentIndex: 0, toolCall: { type: "toolCall", id: "call_1", name: "write", arguments: { path: "a.ts", content } } },
              { type: "done", reason: "toolUse", usage });
            const requests = [];
            globalThis.fetch = async (url, init) => { requests.push([url, init.headers.Authorization]); return new Response(events.map((event) => `data: ${JSON.stringify(event)}\n\n`).join(""), { status: 200 }); };
            const started = performance.now();
            const stream = core.streamProxy({ id: "m", api: "faux", provider: "faux" }, { messages: [] }, { authToken: "test-token", proxyUrl: "http://127.0.0.1:9" });
            let deltas = 0, growing = true, shown = 0;
            const blockKeys = new Set();
            for await (const event of stream) {
              if (event.type !== "toolcall_delta") continue;
              deltas++;
              const block = event.partial.content[0];
              blockKeys.add(Object.keys(block).sort().join(","));
              const partial = block.arguments.content;
              if (typeof partial === "string") { if (partial.length < shown) growing = false; shown = partial.length; }
            }
            const result = await stream.result();
            const args = new ai.ToolCallArgumentsStream();
            console.log(JSON.stringify({ deltas, blockKeys: [...blockKeys], growing, complete: shown > content.length - 64,
              final: JSON.stringify(result.content) === JSON.stringify([{ type: "toolCall", id: "call_1", name: "write", arguments: { path: "a.ts", content } }]),
              fast: performance.now() - started < 10000, requests, steps: [args.append('{"path":"a'), args.append('.txt","n":1}')] }));
            """);
        Equal("""{"deltas":15001,"blockKeys":["arguments,id,name,type"],"growing":true,"complete":true,"final":true,"fast":true,"requests":[["http://127.0.0.1:9/api/stream","Bearer test-token"]],"steps":[{"path":"a"},{"path":"a.txt","n":1}]}""",
            output, "streamed tool-call arguments");
    }

    // agent.ts / agent-loop.ts: AgentOptions.identity and Agent.identity reach every request, merged field by field over
    // DEFAULT_AGENT_IDENTITY. Upstream's default attributes to Pi; PiSharp's port-upstream patch makes it { attribution: null } (owner
    // decision 2026-11-05, core.agent-identity), so every request carries no attribution unless the caller gives one.
    private static async Task BridgeAgentIdentity()
    {
        var output = await BridgeModuleScript("sync150-agent-identity", """
            const core = await load("pi-agent-core.mjs");
            const ai = await load("pi-ai.mjs");
            const model = { id: "m", name: "m", api: "faux", provider: "faux", baseUrl: "", reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 1000, maxTokens: 100 };
            const identities = [];
            const streamFn = (_model, _context, options) => {
              identities.push(options.identity ?? null);
              const stream = ai.createAssistantMessageEventStream();
              const message = { role: "assistant", content: [{ type: "text", text: "ok" }], api: "faux", provider: "faux", model: "m",
                usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }, stopReason: "stop", timestamp: 0 };
              queueMicrotask(() => stream.push({ type: "done", reason: "stop", message }));
              return stream;
            };
            await new core.Agent({ initialState: { model, systemPrompt: "", tools: [] }, streamFn }).prompt("hi");
            const agent = new core.Agent({ initialState: { model, systemPrompt: "", tools: [] }, streamFn, identity: { userAgent: "ext/1.0" } });
            await agent.prompt("hi");
            agent.identity = { attribution: { name: "ext" }, version: "2" };
            await agent.prompt("again");
            console.log(JSON.stringify({ defaultIdentity: core.DEFAULT_AGENT_IDENTITY, identities }));
            """);
        Equal("""{"defaultIdentity":{"attribution":null},"identities":[{"attribution":null},{"attribution":null,"userAgent":"ext/1.0"},{"attribution":{"name":"ext"},"version":"2"}]}""",
            output, "agent identity");
    }

    // markdown.ts at v1.5.0 (the bridge half of the tui Markdown rows): marked-cjk-friendly's emStrong (CJK punctuation next to a
    // delimiter run no longer blocks emphasis; CommonMark flanking elsewhere), an orphan $$ no longer swallows a following table, display
    // math wider than the content falls back to its raw source, 60 nested quotes render on one line, bare URLs stop at CJK punctuation,
    // and a code block is highlighted once across streamed text and resizes.
    private static async Task BridgeMarkdown()
    {
        var output = await BridgeModuleScript("sync150-markdown", """
            const tui = await load("pi-tui.mjs");
            const plain = (text) => text;
            const theme = { heading: plain, link: plain, linkUrl: plain, code: plain, codeBlock: plain, codeBlockBorder: plain, quote: plain, quoteBorder: plain, hr: plain,
              listBullet: plain, bold: (text) => `\x1b[1m${text}\x1b[22m`, italic: (text) => `\x1b[3m${text}\x1b[23m`, strikethrough: plain, underline: plain };
            const tagTheme = { ...theme, bold: (text) => `<b>${text}</b>`, italic: (text) => `<i>${text}</i>` };
            const sgr = (line) => line.replace(/\x1b\[[0-9;]*m/g, "");
            const render = (source, width = 80, padding = 0, style = theme) => new tui.Markdown(source, padding, 0, style).render(width).map((line) => sgr(line).trimEnd());
            const emphasis = ["**这是测试。**后面", "**一句话说清：**一台机器", "**要点到此为止。**20 小时", "字**「引用」**字", "*斜体。*后面", "**end.**Then next",
              "**bold *italic* bold**", "a_b_c and _x_", "`*code*` and \\*x\\*"].map((source) => render(source, 80, 0, tagTheme)[0]);
            const table = ["| A | B |", "|---|---|", "| 1 | 2 |"];
            const math = { orphan: render(["Intro", "", "$$", "", ...table, "", "After"].join("\n")),
              noPairing: render(["$$", "x = 1", "", ...table, "", "$$", "y^2", "$$"].join("\n")), closed: render(["$$", "a", "+ b", "$$", "", ...table].join("\n")) };
            const wide = ["\\[", String.raw`\frac{\partial^2 u}{\partial x^2} + \frac{\partial^2 u}{\partial y^2} = \sum_{i=1}^{n} \frac{1}{i^2} + \int_0^1 f(x)\,dx`, "\\]"].join("\n");
            const narrow = render(wide, 30);
            const fallback = { rendered: render(wide, 40).length, raw: [narrow[0], narrow.at(-1)], noBar: !narrow.some((line) => line.includes("─")), fits: narrow.every((line) => line.length <= 30) };
            const started = performance.now();
            const quotes = render(`${">".repeat(60)}x`, 80, 1);
            const deepQuote = { lines: quotes.length, endsWithX: quotes[0].endsWith("x"), fast: performance.now() - started < 2000 };
            tui.setCapabilities({ images: null, trueColor: false, hyperlinks: true });
            const urls = [];
            for (const source of ["https://example.com/中文?q=搜索。后面的中文", "www.example.com「后面", "https://example.com紧接着中文", "https://example.com.，后面的中文", "[文档](https://example.com/中文，路径。)"]) {
              const rendered = new tui.Markdown(source, 0, 0, theme).render(160).join("");
              urls.push([...rendered.matchAll(/\x1b\]8;;([^\x1b]*)\x1b\\/g)].map((match) => match[1]).filter(Boolean)[0] ?? null,
                sgr(rendered.replace(/\x1b\]8;;[^\x1b]*\x1b\\/g, "")).trimEnd() === source.replace(/^\[文档\]\((.*)\)$/, "文档"));
            }
            tui.resetCapabilitiesCache();
            const calls = [];
            const markdown = new tui.Markdown("```ts\nx\n```", 0, 0, { ...theme, highlightCode: (code, lang) => (calls.push(`${lang}:${code}`), code.split("\n")) });
            markdown.render(80);
            markdown.setText("```ts\nx\n```\n\nafter");
            markdown.render(40);
            const streamed = calls.length;
            markdown.invalidate();
            markdown.render(40);
            console.log(JSON.stringify({ emphasis, math, fallback, deepQuote, urls, highlights: [streamed, calls.length] }));
            """);
        var result = System.Text.Json.Nodes.JsonNode.Parse(output)!;
        Equal("""["<b>这是测试。</b>后面","<b>一句话说清：</b>一台机器","<b>要点到此为止。</b>20 小时","字<b>「引用」</b>字","<i>斜体。</i>后面","**end.**Then next","<b>bold <i>italic</i> bold</b>","a_b_c and <i>x</i>","*code* and *x*"]""",
            result["emphasis"]!.ToJsonString(Unescaped), "CJK-friendly emphasis");
        string[] table = ["┌───┬───┐", "│ A │ B │", "├───┼───┤", "│ 1 │ 2 │", "└───┴───┘"];
        Names(["Intro", "", "$$", "", .. table, "", "After"], Lines(result["math"]!["orphan"]!), "orphan $$ before a table");
        Names(["$$", "x = 1", "", .. table, "", "y²"], Lines(result["math"]!["noPairing"]!), "an orphan $$ does not pair across a table");
        Names(["a + b", "", .. table], Lines(result["math"]!["closed"]!), "closed display math before a table");
        Equal("""{"rendered":3,"raw":["\\[","\\]"],"noBar":true,"fits":true}""", result["fallback"]!.ToJsonString(Unescaped), "display math raw fallback");
        Equal("""{"lines":1,"endsWithX":true,"fast":true}""", result["deepQuote"]!.ToJsonString(), "60 nested quotes");
        Equal("""["https://example.com/中文?q=搜索",true,"http://www.example.com",true,"https://example.com紧接着中文",true,"https://example.com",true,"https://example.com/中文，路径。",true]""",
            result["urls"]!.ToJsonString(Unescaped), "bare URLs stop at CJK punctuation");
        Equal("[1,2]", result["highlights"]!.ToJsonString(), "highlighted once across streaming and resize, again after invalidate");

        static IEnumerable<string> Lines(System.Text.Json.Nodes.JsonNode node) => node.AsArray().Select(line => line!.GetValue<string>());
    }

    private static readonly System.Text.Json.JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Runs an ES module against the bridge's compatibility modules (copied next to the tests); <c>load(file)</c> imports one.
    /// Returns its trimmed standard output.</summary>
    private static async Task<string> BridgeModuleScript(string name, string body)
    {
        using var sandbox = NodeSandbox(name);
        var virtualDirectory = Path.Combine(AppContext.BaseDirectory, "node-bridge", "pi-host", "virtual");
        Check(File.Exists(Path.Combine(virtualDirectory, "pi-ai-extension-view.mjs")), "bridge virtual modules next to the tests: " + virtualDirectory);
        var script = sandbox.Write(name + ".mjs", """
            import path from "node:path";
            import { pathToFileURL } from "node:url";
            const virtual = process.argv[2];
            const load = (file) => import(pathToFileURL(path.join(virtual, file)).href);

            """ + body);
        var node = Environment.GetEnvironmentVariable("PISHARP_NODE") is { Length: > 0 } configured ? configured : "node";
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = sandbox.Root,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(virtualDirectory);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        Equal(0, process.ExitCode, "node exit code (" + (await stderr).Trim() + ")");
        return (await stdout).Trim();
    }
}
