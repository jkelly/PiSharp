// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/codemode/tool.ts and
// packages/coding-agent/src/extensions/codemode/index.ts (readMode, readInlineBudget).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Codemode;

/// <summary>settings-manager.ts <c>CodemodeMode</c>: how the codemode tool presents the loadout while it is active.</summary>
public enum CodemodeMode { On, Only }

/// <summary>A tool a script may call, as the session describes it.</summary>
public sealed record CodemodeNestedTool(string Name, string Description, JsonData Parameters)
{
    /// <summary>Tools that declare one resolve to their <c>structuredContent</c>; others to their text.</summary>
    public JsonData? OutputSchema { get; init; }
    public ToolNamespace? Namespace { get; init; }
    public ImmutableArray<string> PromptGuidelines { get; init; } = [];
}

/// <summary>The <c>codemode</c> tool's identity, model-facing description and loadout presentation.</summary>
public static class CodemodeToolDefinition
{
    public const string Name = "codemode";
    /// <summary>Custom entry type holding one script's <c>store()</c> writes: <c>{ set, delete }</c>.</summary>
    public const string StoreEntryType = "codemode-store";
    /// <summary>Default token budget for tool declarations in the description.</summary>
    public const int DefaultInlineBudget = 3000;
    private const int CharsPerToken = 4;
    private static readonly JsonData TextOutputSchema = JsonData.Parse("""{"type":"string"}""");

    public const string PromptSnippet = "Run JavaScript that calls other tools";
    public static ImmutableArray<string> PromptGuidelines { get; } =
        ["Use codemode to batch independent tool calls (Promise.allSettled), chain them, or filter large output, instead of many separate calls."];

    /// <summary>The tool's parameters (tool.ts codemodeSchema).</summary>
    public static JsonData Parameters => PiSharp.Extensions.Mcp.Discovery.McpDiscoveryToolIdentity.CodemodeSchema;

    /// <summary>Grammar-constrained sampling for capable models: the script as raw text instead of a JSON-escaped string.</summary>
    public static JsonData ConstrainedSampling { get; } = JsonData.Parse(JsonSerializer.Serialize(new
        { type = "grammar", variants = new { openai_lark = CodemodeSource.Grammar } }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    /// <summary>The reference for scripts. PiSharp resolves it next to the application; see docs/compatibility/codemode-engine.md.</summary>
    public static string DocsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "docs", "codemode.md");

    private const string DescriptionIntro = "Run JavaScript that calls other tools. The input is raw JavaScript (not JSON, no code fence), run as an async function body in a QuickJS sandbox: top-level `await` and `return` work. No Node, file system, network, or timers.\n" +
        "- `await tools.<name>({ ...args })` resolves to a string, or an object if the tool's declaration says so, and rejects with an Error on failure. Calls still running when the script ends are cancelled.\n" +
        "- Optional first line: `// @options: {\"max_output_tokens\": 10000, \"timeout_ms\": 60000}`";

    private static string DescribeGlobals(bool models)
    {
        var lines = new List<string>
        {
            "Globals:",
            "- `text(value)`, `image(dataUrlOrImageBlock)`, `console.log(...)`, and top-level `return` add output; `exit()` ends the script. With several text items, each starts with a `==> text N/M <==` line, and `console` lines follow the other output in one `<console_output>` block. `image()` also saves the image to a temp file and the result names its path.",
            "- `store(key, value)` and `load(key)` keep JSON values across codemode calls.",
            "- `ALL_TOOLS`, `await searchTools(query, { limit?, namespace? })`, `await describeTool(name)`, `await describeNamespace(name)`: find unlisted tools, such as MCP tools.",
        };
        if (models) lines.Add($"- `models`: classifiers and image generation. Read {DocsPath} first.");
        return string.Join("\n", lines);
    }

    /// <summary>What a script sees of a tool: its description followed by its prompt guidelines. Tools without an output
    /// schema resolve to their text output.</summary>
    public static CodemodeTool ToDeclaration(CodemodeNestedTool tool, IReadOnlyList<string>? guidelines = null)
    {
        var bullets = (guidelines ?? []).Select(Js.Trim).Where(line => line.Length > 0).Select(line => "- " + line).ToList();
        return new(tool.Name)
        {
            Description = bullets.Count > 0 ? $"{Js.Trim(tool.Description)}\n\n{string.Join("\n", bullets)}" : tool.Description,
            InputSchema = tool.Parameters, OutputSchema = tool.OutputSchema ?? TextOutputSchema
        };
    }

    /// <summary>Tools a script may call: every given tool except the codemode tool itself.</summary>
    public static ImmutableArray<CodemodeNestedTool> Callable(IEnumerable<CodemodeNestedTool> tools) => [.. tools.Where(tool => tool.Name != Name)];

    private sealed record Entry(string Name, string Section, int Cost);
    private sealed record Group(ToolNamespace? Namespace, List<Entry> Entries);

    private static string RenderToolSection(CodemodeTool declaration)
    {
        var id = CodemodeIdentifier.ToIdentifier(declaration.Name);
        var heading = id == declaration.Name ? $"### `{id}`" : $"### `{id}` (`{declaration.Name}`)";
        return $"{heading}\n{Js.Trim(CodemodeDeclarations.RenderToolSample(declaration))}";
    }

    /// <summary>OpenCode-style catalog: each round every group places its cheapest remaining tool; a group whose next tool does
    /// not fit drops out while the others continue.</summary>
    private static HashSet<string> SelectCatalog(IReadOnlyList<Group> groups, int? budget)
    {
        if (budget is null) return groups.SelectMany(group => group.Entries.Select(entry => entry.Name)).ToHashSet(StringComparer.Ordinal);
        var queues = groups.Select(group => new Queue<Entry>(group.Entries.OrderBy(entry => entry.Cost))).ToList();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        long remaining = budget.Value;
        var active = queues.Where(queue => queue.Count > 0).ToList();
        while (active.Count > 0)
            active = active.Where(queue =>
            {
                var next = queue.Peek();
                if (next.Cost > remaining) return false;
                remaining -= next.Cost; shown.Add(next.Name); queue.Dequeue();
                return queue.Count > 0;
            }).ToList();
        return shown;
    }

    /// <summary>The model-facing description: helpers, MCP types when listed tools need them, and one section per listed tool,
    /// grouped by namespace. Deferred tools are never listed. Tool sections are limited to <paramref name="inlineBudget"/>.</summary>
    public static string CreateDescription(IEnumerable<CodemodeNestedTool> tools, bool models = false,
        IReadOnlySet<string>? deferred = null, IReadOnlyDictionary<string, ImmutableArray<string>>? guidelines = null, int? inlineBudget = null)
    {
        var listed = Callable(tools).Where(tool => deferred?.Contains(tool.Name) != true).ToList();
        var declarations = listed.Select(tool => (Tool: tool, Declaration: ToDeclaration(tool, guidelines?.GetValueOrDefault(tool.Name) is { IsDefault: false } lines ? lines : []))).ToList();
        var groups = new List<(string Key, Group Group)> { ("", new(null, [])) };
        foreach (var (tool, declaration) in declarations)
        {
            var key = tool.Namespace is { } ns ? "ns:" + ns.Name : "";
            var group = groups.FirstOrDefault(entry => entry.Key == key).Group;
            if (group is null) { group = new(tool.Namespace, []); groups.Add((key, group)); }
            var section = RenderToolSection(declaration);
            group.Entries.Add(new(tool.Name, section, (int)Math.Ceiling(section.Length / (double)CharsPerToken)));
        }
        // Tools without a namespace first, then namespaces by name (localeCompare; ordinal ignoring case approximates it).
        var ordered = groups.Select(entry => entry.Group).OrderBy(group => group.Namespace is null ? 0 : 1)
            .ThenBy(group => group.Namespace?.Name, StringComparer.InvariantCulture).ToList();
        var shown = SelectCatalog(ordered, inlineBudget);
        var sections = new List<string> { DescriptionIntro, DescribeGlobals(models) };
        if (declarations.Any(item => shown.Contains(item.Tool.Name) && CodemodeDeclarations.McpStructuredContentSchema(item.Declaration.OutputSchema?.Value) is not null))
            sections.Add($"Shared MCP Types:\n```ts\n{CodemodeDeclarations.McpTypeScriptPreamble}\n```");
        if (declarations.Count == 0) return string.Join("\n\n", sections);
        var toolSections = new List<string> { "Nested tools:" };
        foreach (var group in ordered)
        {
            var visible = group.Entries.Where(entry => shown.Contains(entry.Name)).ToList();
            if (group.Namespace is { } ns)
            {
                var listing = visible.Count == group.Entries.Count ? "" : visible.Count == 0 ? " (tools not listed)" : " (some tools not listed)";
                var description = ns.Description is null ? "" : Js.Trim(ns.Description);
                toolSections.Add($"## {ns.Name}{listing}{(description.Length > 0 ? "\n" + description : "")}");
            }
            toolSections.AddRange(visible.Select(entry => entry.Section));
        }
        sections.Add(string.Join("\n\n", toolSections));
        return string.Join("\n\n", sections);
    }

    /// <summary>What a script call resolves to, in one line: <c>a string</c>, the field names of an object, or the type.</summary>
    private static string DescribeOutput(JsonData? schema)
    {
        var type = CodemodeDeclarations.RenderToolOutputType(schema?.Value);
        if (type == "string") return "a string";
        if (schema?.Value is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("type", out var declared) &&
            declared.ValueKind == JsonValueKind.String && declared.GetString() == "object" &&
            value.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object &&
            CodemodeDeclarations.McpStructuredContentSchema(value) is null)
        {
            var required = value.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            var fields = ModelOperationsKeys(properties).Select(name => required.Contains(name) ? name : name + "?");
            return $"`{{ {string.Join(", ", fields)} }}`";
        }
        return $"`{CodemodeDeclarations.OneLine(type)}`";
    }

    private static IEnumerable<string> ModelOperationsKeys(JsonElement properties) =>
        PiSharp.Contracts.ModelOperations.ModelOperationJson.ObjectEntries(properties).Select(entry => entry.Key);

    private static string DescribeScriptCall(CodemodeNestedTool tool) =>
        $"{Js.Trim(tool.Description)}\n\nCodemode: `tools.{CodemodeIdentifier.ToIdentifier(tool.Name)}(args)` resolves to {DescribeOutput(ToDeclaration(tool).OutputSchema)}.";

    /// <summary>The loadout presentation (tool.ts prepareCodemodeLoadout). <c>on</c>: declared callable tools say how scripts
    /// call them, and the codemode description lists callable tools without <c>direct</c> exposure. <c>only</c>: the
    /// description lists every callable tool and requests leave out the declarations of active <c>direct</c> tools.</summary>
    public static ToolLoadoutChanges PrepareLoadout(ToolLoadout loadout, CodemodeMode mode, bool models, int? inlineBudget,
        Func<ToolLoadoutTool, JsonData?>? outputSchema = null)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        CodemodeNestedTool Describe(ToolLoadoutTool tool) => new(tool.Name, tool.Description,
            tool.Declaration.Value.TryGetProperty("parameters", out var parameters) ? JsonData.FromElement(parameters) : JsonData.EmptyObject)
        { Namespace = tool.Namespace, PromptGuidelines = tool.PromptGuidelines, OutputSchema = outputSchema?.Invoke(tool) };
        bool IsDirect(string name) => loadout.GetExposure(name) == ToolExposure.Direct;
        var callable = loadout.Callable.Where(tool => tool.Name != Name).ToList();
        var callableNames = callable.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var descriptions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (mode == CodemodeMode.On)
            foreach (var tool in loadout.Declared)
                if (callableNames.Contains(tool.Name)) descriptions[tool.Name] = DescribeScriptCall(Describe(tool));
        var listed = mode == CodemodeMode.Only ? callable : callable.Where(tool => !IsDirect(tool.Name)).ToList();
        descriptions[Name] = CreateDescription(listed.Select(Describe), models,
            listed.Where(tool => loadout.GetExposure(tool.Name) == ToolExposure.Deferred).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal),
            listed.ToDictionary(tool => tool.Name, tool => loadout.GetPromptGuidelines(tool.Name), StringComparer.Ordinal),
            inlineBudget ?? DefaultInlineBudget);
        var declared = loadout.Declared.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        return new()
        {
            Descriptions = descriptions.ToImmutable(),
            HiddenDeclarations = mode == CodemodeMode.Only
                ? [.. callable.Where(tool => IsDirect(tool.Name) && declared.Contains(tool.Name)).Select(tool => tool.Name)] : []
        };
    }

    /// <summary>index.ts readMode/readInlineBudget over the effective settings (<c>codemode.mode</c>, <c>codemode.inlineBudget</c>).</summary>
    public static (CodemodeMode Mode, int? InlineBudget) ReadSettings(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } values || !values.TryGetProperty("codemode", out var codemode) || codemode.ValueKind != JsonValueKind.Object)
            return (CodemodeMode.On, null);
        var mode = codemode.TryGetProperty("mode", out var configured) && configured.ValueKind == JsonValueKind.String && configured.GetString() == "only"
            ? CodemodeMode.Only : CodemodeMode.On;
        int? budget = codemode.TryGetProperty("inlineBudget", out var value) && value.ValueKind == JsonValueKind.Number &&
            double.IsFinite(value.GetDouble()) && value.GetDouble() >= 0 ? (int)Math.Min(int.MaxValue, Math.Floor(value.GetDouble())) : null;
        return (mode, budget);
    }
}
