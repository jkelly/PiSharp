// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/codemode/describe.ts.
// - renderToolSample and toCodemodeIdentifier are PiSharp.Codemode's CodemodeDeclarations.RenderToolSample and
//   CodemodeIdentifier.ToIdentifier (ports of pi-codemode's declarations.ts and identifier.ts); a tool's declaration is a CodemodeTool whose
//   schemas are the registration's JSON.
// - `localeCompare` of namespace names is StringComparer.InvariantCulture (ICU collation, as main's coding-agent code mode orders them);
//   Array.prototype.sort and OrderBy are both stable. `trim()` is the durable core's Js.Trim.
using System.Collections.Immutable;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>A group of tools named by a common prefix, such as an MCP server's <c>mcp__github__</c> (upstream <c>CodemodeNamespace</c>).
/// Tool names starting with <see cref="Prefix"/> belong to the namespace; the longest matching prefix wins.</summary>
public sealed record CodemodeNamespace(string Name, string Prefix)
{
    public string? Description { get; init; }
    public string? Instructions { get; init; }
}

/// <summary>
/// What the model reads about code mode, copied from the coding agent's code mode (upstream <c>describe.ts</c>): the tool's fixed description
/// (the script environment and its globals), and the <c>codemode</c> prompt section, which lists the tools scripts can call as TypeScript
/// declarations, grouped by namespace and limited to a token budget. Tools that do not fit are found with <c>searchTools()</c>. The section
/// renders from the conversation's callable tools, so it changes with them; the tool's description never does.
/// </summary>
internal static class CodemodeDescribe
{
    /// <summary>Default token budget of the tool declarations in the <c>codemode</c> section, as in the coding agent (upstream
    /// <c>DEFAULT_CODEMODE_INLINE_BUDGET</c>).</summary>
    public const int DefaultInlineBudget = 3000;
    /// <summary>Characters per token when estimating the cost of a tool section.</summary>
    private const int CharsPerToken = 4;

    private static readonly JsonData TextOutputSchema = JsonData.Parse("""{"type":"string"}""");

    private const string DescriptionIntro = "Run JavaScript that calls other tools. The input is raw JavaScript (not JSON, no code fence), run as an async function body in a QuickJS sandbox: top-level `await` and `return` work. No Node, file system, network, or timers.\n" +
        "- `await tools.<name>({ ...args })` resolves to a string, or an object if the tool's declaration says so, and rejects with an Error on failure. A tool whose declaration returns an object resolves on failure too when it returned that object; check its fields. Calls still running when the script ends are cancelled.\n" +
        "- The script survives a restart: it resumes where it was, without running finished calls again.\n" +
        "- Optional first line: `// @options: {\"max_output_tokens\": 10000, \"timeout_ms\": 60000}`";

    /// <summary>The code mode tool's description: the script environment and its globals, the same for every conversation.</summary>
    public static string Description(bool models)
    {
        string[] globals =
        [
            "Globals:",
            "- `text(value)`, `image(dataUrlOrImageBlock)`, `console.log(...)`, and top-level `return` add output; `exit()` ends the script. With several text items, each starts with a `==> text N/M <==` line, and `console` lines follow the other output in one `<console_output>` block. `image()` also saves the image to a temp file and the result names its path.",
            "- `store(key, value)` and `load(key)` keep JSON values across codemode calls.",
            "- `ALL_TOOLS`, `await searchTools(query, { limit?, namespace? })`, `await describeTool(name)`, `await describeNamespace(name)`: find tools the `codemode` section does not list.",
        ];
        return string.Join("\n\n", [
            DescriptionIntro,
            string.Join("\n", globals),
            .. models ? [CodemodeModels.Description] : Array.Empty<string>(),
            "The `codemode` section of the system prompt lists the tools scripts can call, by namespace, as far as space allows.",
        ]);
    }

    /// <summary>What a script sees of a tool: its declaration (upstream <c>declarationOf</c>). Tools without an output schema resolve to their
    /// text output.</summary>
    public static CodemodeTool DeclarationOf(ToolRegistration tool) => new(tool.Name)
    {
        Description = tool.Description,
        InputSchema = JsonData.Parse(tool.Parameters.ToJson()),
        OutputSchema = tool.StructuredOutputSchema is { } schema ? JsonData.Parse(schema.ToJson()) : TextOutputSchema,
    };

    /// <summary><c>toolSample</c> of a tool: its description and TypeScript declaration (upstream <c>sampleOf</c>).</summary>
    public static string SampleOf(ToolRegistration tool) => CodemodeDeclarations.RenderToolSample(DeclarationOf(tool));

    /// <summary>The namespace of a tool: the one with the longest prefix its name starts with (upstream <c>namespaceOf</c>).</summary>
    public static CodemodeNamespace? NamespaceOf(string name, IReadOnlyList<CodemodeNamespace> namespaces)
    {
        CodemodeNamespace? found = null;
        foreach (var candidate in namespaces)
            if (name.StartsWith(candidate.Prefix, StringComparison.Ordinal) && (found is null || candidate.Prefix.Length > found.Prefix.Length))
                found = candidate;
        return found;
    }

    /// <summary><c>### `id` (`raw name`)</c> followed by the tool's description and declaration.</summary>
    private static string RenderToolSection(ToolRegistration tool)
    {
        var id = CodemodeIdentifier.ToIdentifier(tool.Name);
        var heading = id == tool.Name ? $"### `{id}`" : $"### `{id}` (`{tool.Name}`)";
        return $"{heading}\n{JsString.Trim(SampleOf(tool))}";
    }

    private sealed record CatalogEntry(string Name, string Section, int Cost);
    private sealed record CatalogGroup(CodemodeNamespace? Namespace, List<CatalogEntry> Entries);

    /// <summary>
    /// Pick the tool sections that fit the budget: in each round every group (tools without a namespace first, then namespaces by name) places
    /// its cheapest remaining tool; a group whose next tool does not fit drops out while the others continue. Every namespace is represented
    /// before any namespace is complete.
    /// </summary>
    private static HashSet<string> SelectCatalog(IReadOnlyList<CatalogGroup> groups, int budget)
    {
        var queues = groups.Select(group => new Queue<CatalogEntry>(group.Entries.OrderBy(entry => entry.Cost))).ToList();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var remaining = budget;
        var active = queues.Where(queue => queue.Count > 0).ToList();
        while (active.Count > 0)
        {
            active = active.Where(queue =>
            {
                var next = queue.Peek();
                if (next.Cost > remaining) return false;
                remaining -= next.Cost;
                shown.Add(next.Name);
                queue.Dequeue();
                return queue.Count > 0;
            }).ToList();
        }
        return shown;
    }

    /// <summary>
    /// The <c>codemode</c> section: the tools scripts can call, grouped by namespace, with their declarations as far as the budget allows
    /// (upstream <c>codemodeSection</c>). Null without callable tools.
    /// </summary>
    public static string? Section(IReadOnlyList<ToolRegistration> tools, IReadOnlyList<CodemodeNamespace> namespaces, int budget)
    {
        if (tools.Count == 0) return null;
        var groups = new List<(string Key, CatalogGroup Group)> { ("", new CatalogGroup(null, [])) };
        foreach (var tool in tools)
        {
            var found = NamespaceOf(tool.Name, namespaces);
            var key = found is not null ? $"ns:{found.Name}" : "";
            var index = groups.FindIndex(entry => entry.Key == key);
            if (index < 0)
            {
                groups.Add((key, new CatalogGroup(found, [])));
                index = groups.Count - 1;
            }
            var section = RenderToolSection(tool);
            groups[index].Group.Entries.Add(new CatalogEntry(tool.Name, section, (int)Math.Ceiling(section.Length / (double)CharsPerToken)));
        }
        var ordered = groups.Select(entry => entry.Group).OrderBy(group => group.Namespace is null ? 0 : 1)
            .ThenBy(group => group.Namespace?.Name, StringComparer.InvariantCulture).ToList();
        var shown = SelectCatalog(ordered, budget);

        var toolSections = new List<string> { "Tools scripts can call (`await tools.<name>(args)`):" };
        foreach (var group in ordered)
        {
            if (group.Entries.Count == 0) continue;
            var visible = group.Entries.Where(entry => shown.Contains(entry.Name)).ToList();
            if (group.Namespace is { } found)
            {
                var listing = visible.Count == group.Entries.Count ? "" : visible.Count == 0 ? " (tools not listed)" : " (some tools not listed)";
                var description = found.Description is null ? null : JsString.Trim(found.Description);
                toolSections.Add($"## {found.Name}{listing}{(string.IsNullOrEmpty(description) ? "" : "\n" + description)}");
            }
            else if (visible.Count < group.Entries.Count)
            {
                toolSections.Add("Some tools are not listed: find them with `searchTools()`.");
            }
            toolSections.AddRange(visible.Select(entry => entry.Section));
        }
        return string.Join("\n\n", toolSections);
    }
}

/// <summary>JavaScript string helpers code mode needs.</summary>
internal static class JsString
{
    /// <summary>JavaScript's <c>String.prototype.trim()</c>.</summary>
    public static string Trim(string value) => Js.Trim(value);

    /// <summary>JavaScript's <c>text.length > max ? text.slice(0, max - 3) + "..." : text</c> (upstream <c>cut</c>).</summary>
    public static string Cut(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

    /// <summary>The JSON text of a durable JSON value as <c>JSON.stringify</c> prints it.</summary>
    public static string Stringify(JsonValue value) => value.ToJson();
}
