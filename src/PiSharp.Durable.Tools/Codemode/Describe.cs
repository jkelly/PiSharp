// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/src/codemode/describe.ts.
// - renderToolSample and toCodemodeIdentifier are PiSharp.Codemode's CodemodeDeclarations.RenderToolSample and
//   CodemodeIdentifier.ToIdentifier (ports of pi-codemode's declarations.ts and identifier.ts); a tool's declaration is a CodemodeTool whose
//   schemas are the registration's JSON.
// - `localeCompare` of namespace names is StringComparer.InvariantCulture (ICU collation, as main's coding-agent code mode orders them);
//   Array.prototype.sort and OrderBy are both stable. `trim()` is the durable core's Js.Trim.
// - Pi v1.6.0: namespaces come from the extensions (Extension.Namespace, a ToolNamespace), not from name prefixes. CodemodeNamespace, its
//   Prefix, NamespaceOf(name, namespaces) and CodemodeToolOptions.Namespaces are gone with no fallback, as upstream; namespacesOf() is
//   DurableCodemode.NamespacesOf and declaredNamespaces() is CodemodeDescribe.DeclaredNamespaces.
using System.Collections.Immutable;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>
/// What the model reads about code mode, copied from the coding agent's code mode (upstream <c>describe.ts</c>): the tool's fixed description
/// (the script environment and its globals), and the <c>codemode</c> prompt section, which lists the tools scripts can call as TypeScript
/// declarations, grouped by the namespaces their extensions declare and limited to a token budget. Tools that do not fit, and deferred
/// tools, are found with <c>searchTools()</c>. Every selected extension's namespace is listed (with its description) whether or not it has
/// tools yet, so the section stays the same when an extension's deferred tools come and go (an MCP server connecting); the tool's description
/// never changes.
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

    /// <summary>The namespace of each tool the extensions bring, by tool name: its extension's <see cref="Extension.Namespace"/> (upstream
    /// <c>namespacesOf</c>). A tool several extensions name belongs to the first one's (with none when the first declares none).</summary>
    public static IReadOnlyDictionary<string, ToolNamespace> NamespacesOf(IEnumerable<Extension> extensions)
    {
        var namespaces = new Dictionary<string, ToolNamespace>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
            foreach (var tool in extension.Tools ?? [])
            {
                if (!seen.Add(tool.Name)) continue;
                if (extension.Namespace is { } found) namespaces[tool.Name] = found;
            }
        return namespaces;
    }

    /// <summary>The namespaces the extensions declare, by name (the first declaration of a name wins), sorted by name (upstream
    /// <c>declaredNamespaces</c>).</summary>
    public static IReadOnlyList<ToolNamespace> DeclaredNamespaces(IEnumerable<Extension> extensions)
    {
        var byName = new List<ToolNamespace>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
            if (extension.Namespace is { } found && names.Add(found.Name)) byName.Add(found);
        return [.. byName.OrderBy(found => found.Name, StringComparer.InvariantCulture)];
    }

    /// <summary><c>### `id` (`raw name`)</c> followed by the tool's description and declaration.</summary>
    private static string RenderToolSection(ToolRegistration tool)
    {
        var id = CodemodeIdentifier.ToIdentifier(tool.Name);
        var heading = id == tool.Name ? $"### `{id}`" : $"### `{id}` (`{tool.Name}`)";
        return $"{heading}\n{JsString.Trim(SampleOf(tool))}";
    }

    private sealed record CatalogEntry(string Name, string Section, int Cost);
    private sealed record CatalogGroup(ToolNamespace? Namespace, List<CatalogEntry> Entries);

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
    /// The <c>codemode</c> section: the tools scripts can call (ungrouped ones first), then every namespace the selected
    /// <paramref name="extensions"/> declare, by name, as <c>## &lt;name&gt;</c> and its description, each followed by its tools' declarations
    /// as far as the budget allows (upstream <c>codemodeSection</c>). Headers carry no counts, so the text changes only with the declared
    /// tools. Null without callable tools and namespaces.
    /// </summary>
    public static string? Section(IReadOnlyList<ToolRegistration> tools, IReadOnlyList<Extension> extensions, int budget)
    {
        var namespaces = DeclaredNamespaces(extensions);
        if (tools.Count == 0 && namespaces.Count == 0) return null;
        var toolNamespaces = NamespacesOf(extensions);
        var ungrouped = new CatalogGroup(null, []);
        var groups = namespaces.ToDictionary(found => found.Name, found => new CatalogGroup(found, []), StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var group = toolNamespaces.TryGetValue(tool.Name, out var found) ? groups[found.Name] : ungrouped;
            var section = RenderToolSection(tool);
            group.Entries.Add(new CatalogEntry(tool.Name, section, (int)Math.Ceiling(section.Length / (double)CharsPerToken)));
        }
        List<CatalogGroup> ordered = [ungrouped, .. namespaces.Select(found => groups[found.Name])];
        var shown = SelectCatalog(ordered, budget);

        var toolSections = new List<string> { "Tools scripts can call (`await tools.<name>(args)`):" };
        if (shown.Count < tools.Count) toolSections.Add("Some tools are not listed: find them with `searchTools()`.");
        foreach (var group in ordered)
        {
            if (group.Namespace is { } found) toolSections.Add($"## {found.Name}\n{JsString.Trim(found.Description)}");
            toolSections.AddRange(group.Entries.Where(entry => shown.Contains(entry.Name)).Select(entry => entry.Section));
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
