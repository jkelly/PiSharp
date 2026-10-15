// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/codemode-describe.test.ts ("code mode description and section"),
// as codemode.describe.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - codemodeDescription and codemodeSection are CodemodeDescribe.Description and Section (internal, as upstream's tests import them from the
//   source file); an undefined section is null.
// - The tools' Parameters are the JSON TypeBox prints for `Type.Object({ path: Type.String({ description: "File path" }) })`.
// - CODEMODE_SOURCE_GRAMMAR is PiSharp.Codemode's CodemodeSource.Grammar.
using PiSharp.Codemode;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CodemodeDescribeCases() => CodemodeDescribeTests.Cases();
}

internal static class CodemodeDescribeTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonObjectValue PathParameters =
        O("""{"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"File path"}}}""");

    private static ToolRegistration Tool(string name, string description, Func<ToolRegistration, ToolRegistration>? extra = null)
    {
        var tool = Define.Tool(new ToolRegistration
        {
            Name = name,
            Description = description,
            Parameters = PathParameters,
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty }),
        });
        return extra is null ? tool : extra(tool);
    }

    private static readonly CodemodeNamespace GitHub = new("github", "mcp__github__") { Description = "GitHub MCP server" };

    private static void Contains(string text, string part, string what) => Check(text.Contains(part, StringComparison.Ordinal), $"{what}: lacks <{part}>");
    private static void Lacks(string text, string part, string what) => Check(!text.Contains(part, StringComparison.Ordinal), $"{what}: has <{part}>");

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "codemode.describe.";

        yield return Case(prefix + "keeps-the-tool-description-the-same-for-every-conversation", () =>
        {
            var text = CodemodeDescribe.Description(false);
            Contains(text, "`await tools.<name>({ ...args })`", "description");
            Contains(text, "searchTools(query", "description");
            Contains(text, "The `codemode` section of the system prompt lists the tools scripts can call", "description");
            Lacks(text, "`models`", "description without models");
            Contains(CodemodeDescribe.Description(true), "`models`", "description with models");
        });

        yield return Case(prefix + "lists-callable-tools-as-declarations-grouped-by-namespace-with-namespace-descriptions", () =>
        {
            var section = CodemodeDescribe.Section([Tool("read", "Read a file."), Tool("mcp__github__list_issues", "List issues.")], [GitHub], 3000)!;
            Contains(section, "### `read`\nRead a file.", "section");
            Contains(section, "read(args: {", "section");
            Contains(section, "## github\nGitHub MCP server", "section");
            // Tools before namespaces; a namespace's tools after its heading.
            Check(section.IndexOf("### `read`", StringComparison.Ordinal) < section.IndexOf("## github", StringComparison.Ordinal), "tools first");
            Check(section.IndexOf("## github", StringComparison.Ordinal) < section.IndexOf("### `mcp__github__list_issues`", StringComparison.Ordinal),
                "a namespace's tools after its heading");
            Contains(section, "Promise<string>", "section");
        });

        yield return Case(prefix + "lists-what-fits-the-budget-every-namespace-before-any-is-complete-and-says-what-is-left-out", () =>
        {
            var github = Enumerable.Range(0, 30).Select(index => Tool($"mcp__github__tool{index}", new string('x', 200))).ToList();
            var section = CodemodeDescribe.Section([Tool("read", "Read a file."), .. github], [GitHub], 400)!;
            Contains(section, "### `read`", "section");
            Contains(section, "## github (some tools not listed)", "section");
            Lacks(section, "tool29", "section");
            Contains(CodemodeDescribe.Section([.. github], [GitHub], 10)!, "## github (tools not listed)", "tiny budget");
            Equal<string?>(null, CodemodeDescribe.Section([], [GitHub], 3000), "no tools");
        });

        yield return Case(prefix + "uses-the-longest-matching-namespace-prefix", () =>
        {
            var section = CodemodeDescribe.Section([Tool("mcp__github__x", "x")], [new CodemodeNamespace("git", "mcp__git"), GitHub], 3000)!;
            Contains(section, "## github", "section");
            Lacks(section, "## git\n", "section");
        });

        yield return Case(prefix + "renders-the-section-from-the-conversation-s-callable-tools-into-the-system-prompt", async () =>
        {
            var setup = Setup();
            setup.Registry.Install(DurableCodemode.CreateExtension(new CodemodeToolOptions { Sandbox = new CodemodeSandbox(), Namespaces = [GitHub] }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "tools",
                Tools =
                [
                    Tool("read", "Read a file."),
                    Tool("mcp__github__list_issues", "List issues.", tool => tool with { Callers = [ToolCaller.Tools] }),
                    Tool("mcp__github__get_issue", "Get an issue.", tool => tool with { Deferred = true }),
                ],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("hi"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var system = (JsonObjectValue)(await AllEntries(root)).First(entry => entry.Kind == "pi.system").Model![0];
            var codemode = S(((JsonObjectValue)system["sections"])["codemode"]);
            Contains(codemode, "### `read`", "section");
            Contains(codemode, "### `mcp__github__list_issues`", "section");
            // Deferred tools are found with searchTools(), not listed.
            Lacks(codemode, "mcp__github__get_issue", "section");
            // The code mode tool itself is not callable from scripts.
            Lacks(codemode, "### `codemode`", "section");
            // The tool declaration carries the script grammar; pi-ai applies it only for models that support it.
            var tool = ((JsonArrayValue)system["toolsAdded"]).Cast<JsonObjectValue>().First(candidate => S(candidate["name"]) == "codemode");
            SameJson(Obj(("type", "grammar"), ("variants", Obj(("openai_lark", CodemodeSource.Grammar)))).ToJson(), tool["constrainedSampling"], "constrainedSampling");
            await harness.CloseAsync(Context);
        });
    }
}
