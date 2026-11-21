// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/codemode-describe.test.ts ("code mode description and section"),
// as codemode.describe.<case>. Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - codemodeDescription and codemodeSection are CodemodeDescribe.Description and Section (internal, as upstream's tests import them from the
//   source file); an undefined section is null.
// - The tools' Parameters are the JSON pi-schema builds for `obj({ path: str({ description: "File path" }) })`.
// - CODEMODE_SOURCE_GRAMMAR is PiSharp.Codemode's CodemodeSource.Grammar.
// - Pi v1.6.0 moved namespaces from name prefixes to extensions. The cases upstream rewrote are rewritten here under their new names
//   ("lists ungrouped tools first ...", "lists what fits the budget ... with no counts in headings", "renders the section from the
//   conversation's extensions and callable tools ..."); "uses the longest matching namespace prefix" is gone with the prefix API, as
//   upstream deleted it. The cases upstream added are in Sync160CodemodeCases.cs.
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
        O("""{"type":"object","properties":{"path":{"type":"string","description":"File path"}},"required":["path"],"additionalProperties":false}""");

    internal static ToolRegistration Tool(string name, string description, Func<ToolRegistration, ToolRegistration>? extra = null)
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

    internal static readonly ToolNamespace GitHub = new("mcp__github", "GitHub MCP server", "Pass repositories as owner/name.");

    /// <summary>An extension bringing <paramref name="tools"/>, in <paramref name="toolNamespace"/> when given (upstream <c>extension</c>).</summary>
    internal static Extension Ext(string name, IReadOnlyList<ToolRegistration> tools, ToolNamespace? toolNamespace = null) =>
        Define.Extension(new Extension { Name = name, Tools = tools, Namespace = toolNamespace });

    internal static void Contains(string text, string part, string what) => Check(text.Contains(part, StringComparison.Ordinal), $"{what}: lacks <{part}>");
    internal static void Lacks(string text, string part, string what) => Check(!text.Contains(part, StringComparison.Ordinal), $"{what}: has <{part}>");

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

        yield return Case(prefix + "lists-ungrouped-tools-first-then-each-namespace-s-heading-and-description-then-its-declarations", () =>
        {
            var read = Tool("read", "Read a file.");
            var issues = Tool("list_issues", "List issues.");
            var section = CodemodeDescribe.Section([read, issues], [Ext("coding", [read]), Ext("github", [issues], GitHub)], 3000)!;
            Contains(section, "### `read`\nRead a file.", "section");
            Contains(section, "read(args: {", "section");
            Contains(section, "## mcp__github\nGitHub MCP server", "section");
            // Instructions only through describeNamespace().
            Lacks(section, "owner/name", "section");
            Check(section.IndexOf("### `read`", StringComparison.Ordinal) < section.IndexOf("## mcp__github", StringComparison.Ordinal), "tools first");
            Check(section.IndexOf("## mcp__github", StringComparison.Ordinal) < section.IndexOf("### `list_issues`", StringComparison.Ordinal),
                "a namespace's tools after its heading");
            Contains(section, "Promise<string>", "section");
        });

        yield return Case(prefix + "lists-what-fits-the-budget-every-namespace-before-any-is-complete-with-no-counts-in-headings", () =>
        {
            var github = Enumerable.Range(0, 30).Select(index => Tool($"tool{index}", new string('x', 200))).ToList();
            var read = Tool("read", "Read a file.");
            var section = CodemodeDescribe.Section([read, .. github], [Ext("coding", [read]), Ext("github", github, GitHub)], 400)!;
            Contains(section, "### `read`", "section");
            Contains(section, "Some tools are not listed: find them with `searchTools()`.", "section");
            Contains(section, "## mcp__github\nGitHub MCP server", "section");
            Lacks(section, "tool29", "section");
            Lacks(section, "not listed)", "section");
        });

        yield return Case(prefix + "renders-the-section-from-the-conversation-s-extensions-and-callable-tools-into-the-system-prompt", async () =>
        {
            var setup = Setup();
            setup.Registry.Install(DurableCodemode.CreateExtension(new CodemodeToolOptions { Sandbox = new CodemodeSandbox() }));
            setup.Registry.Install(Define.Extension(new Extension { Name = "tools", Tools = [Tool("read", "Read a file.")] }));
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "mcp:github",
                Namespace = GitHub,
                Tools =
                [
                    Tool("list_issues", "List issues.", tool => tool with { Callers = [ToolCaller.Tools] }),
                    Tool("get_issue", "Get an issue.", tool => tool with { Deferred = true }),
                ],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("hi"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var system = (JsonObjectValue)(await AllEntries(root)).First(entry => entry.Kind == "pi.system").Model![0];
            var codemode = S(((JsonObjectValue)system["sections"])["codemode"]);
            Contains(codemode, "### `read`", "section");
            Contains(codemode, "## mcp__github\nGitHub MCP server", "section");
            Contains(codemode, "### `list_issues`", "section");
            // Deferred tools are found with searchTools(), not listed.
            Lacks(codemode, "get_issue", "section");
            // The code mode tool itself is not callable from scripts.
            Lacks(codemode, "### `codemode`", "section");
            // The tool declaration carries the script grammar; pi-ai applies it only for models that support it.
            var tool = ((JsonArrayValue)system["toolsAdded"]).Cast<JsonObjectValue>().First(candidate => S(candidate["name"]) == "codemode");
            SameJson(Obj(("type", "grammar"), ("variants", Obj(("openai_lark", CodemodeSource.Grammar)))).ToJson(), tool["constrainedSampling"], "constrainedSampling");
            await harness.CloseAsync(Context);
        });
    }
}
