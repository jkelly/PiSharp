// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tool-search/tool-search.ts and tool-search/index.ts, the
// `@earendil-works/pi-durable/tool-search` entry point. Upstream's TOOL_SEARCH_TOOL_NAME, DEFAULT_TOOL_SEARCH_LIMIT, createToolSearchTool and
// createToolSearchExtension are ToolSearch.ToolName, DefaultLimit, CreateTool and CreateExtension; its `ToolSearchToolDetails` type,
// `{ loaded: string[] }`, is the result's details JSON. The ranking is PiSharp.Durable's Search (upstream search.ts).
// - The arguments are validated against the schema before execute, so `query` is a string and `limit`, when present, a number; JavaScript's
//   `trim()` and `Number.isInteger` are Js.Trim and a finite, whole check. A limit past int.MaxValue ranks as int.MaxValue, which no
//   document list reaches.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable.Tools;

/// <summary>
/// The <c>tool_search</c> tool, a copy of the coding agent's: the model searches the deferred tools it is not offered yet (<see cref="Agent.Deferred"/>),
/// and the matches are loaded with <c>control.addTools</c> (<see cref="ToolControl.AddTools"/>), so the next request offers them. Loading writes
/// the conversation's <c>modelTools</c> (its <c>add</c>), so loaded tools stay offered on that branch, also after a compaction.
/// </summary>
public static class ToolSearch
{
    /// <summary><c>tool_search</c> (upstream <c>TOOL_SEARCH_TOOL_NAME</c>).</summary>
    public const string ToolName = "tool_search";

    /// <summary>The number of tools a search loads at most when the call gives no <c>limit</c> (upstream <c>DEFAULT_TOOL_SEARCH_LIMIT</c>).</summary>
    public const int DefaultLimit = 8;

    /// <summary>Fixed, so it does not change while deferred tools are registered, for example as MCP servers connect.</summary>
    internal const string Description = "# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools for the next model call.\n\n" +
        "Some of the tools, such as tools of MCP servers, may not have been provided to you upfront, and you should use this tool (`" + ToolName +
        "`) to search for the required tools. For MCP tool discovery, always use `" + ToolName + "`.";

    /// <summary><c>Type.Object({ query: Type.String(...), limit: Type.Optional(Type.Number(...)) })</c>, as TypeBox prints it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["query"],"properties":{"query":{"type":"string","description":"Search query for deferred tools."},"limit":{"type":"number","description":"Maximum number of tools to return. Defaults to """ +
        DefaultLimit + """."}}}""");

    /// <summary>The <c>tool_search</c> tool (upstream <c>createToolSearchTool</c>). Only the model may call it: searching changes what the
    /// model is offered, and scripts have their own <c>searchTools()</c>. It is replay-safe. Each call creates a new registration.</summary>
    public static ToolRegistration CreateTool() => Define.Tool(new ToolRegistration
    {
        Name = ToolName,
        Description = Description,
        Parameters = Parameters,
        Callers = [ToolCaller.Model],
        Replay = ToolReplay.Safe,
        Execute = ExecuteAsync,
    });

    /// <summary>The <c>tool-search</c> extension (upstream <c>createToolSearchExtension</c>): install it and select it where the model should
    /// find deferred tools.</summary>
    public static Extension CreateExtension() => Define.Extension(new Extension { Name = "tool-search", Tools = [CreateTool()] });

    private static async Task<ToolExecutionResult> ExecuteAsync(JsonValue arguments, IToolExecutionApi api, DurableContext context)
    {
        var args = (JsonObjectValue)arguments;
        var query = ((JsonStringValue)args["query"]).Value;
        if (Js.Trim(query).Length == 0) throw new InvalidOperationException("query must not be empty");
        var max = args.TryGetValue("limit", out var limit) && limit is JsonNumberValue number ? number.Value : DefaultLimit;
        if (!double.IsFinite(max) || Math.Floor(max) != max || max <= 0) throw new InvalidOperationException("limit must be a positive integer");
        var candidates = (await api.AgentAsync(context).ConfigureAwait(false)).Deferred;
        var documents = candidates.Select(tool => new SearchDocument(tool.Name, SearchText(tool))).ToList();
        var loaded = Search.RankDocuments(query, documents, max >= int.MaxValue ? int.MaxValue : (int)max);
        var text = loaded.Count == 0
            ? "No matching tools found."
            : $"Loaded {loaded.Count} tool{(loaded.Count == 1 ? "" : "s")}. They are available from your next call:\n" + string.Join("\n", loaded.Select(name =>
            {
                var description = candidates.FirstOrDefault(tool => tool.Name == name)?.Description ?? "";
                return $"- {name}: {FirstLine(Js.Trim(description))}";
            }));
        return new ToolExecutionResult
        {
            Output = ToolContent.Text(text),
            Details = JsonValue.Object([KeyValuePair.Create("loaded", (JsonValue)JsonValue.Array(loaded.Select(name => (JsonValue)name)))]),
            Control = loaded.Count == 0 ? null : new ToolControl { AddTools = [.. loaded] },
        };
    }

    /// <summary><c>text.split(/\r?\n/)[0]</c>.</summary>
    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        if (end < 0) return text;
        return end > 0 && text[end - 1] == '\r' ? text[..(end - 1)] : text[..end];
    }

    /// <summary>Schema descriptions and property names, recursively.</summary>
    private static void SchemaText(JsonValue? schema, List<string> parts)
    {
        if (schema is not JsonObjectValue value) return;
        if (value.TryGetValue("description", out var description) && description is JsonStringValue text) parts.Add(text.Value);
        if (value.TryGetValue("properties", out var properties) && properties is JsonObjectValue named)
        {
            foreach (var (name, property) in named)
            {
                parts.Add(name);
                SchemaText(property, parts);
            }
        }
        SchemaText(value.TryGetValue("items", out var items) ? items : null, parts);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (value.TryGetValue(key, out var variants) && variants is JsonArrayValue list)
                foreach (var variant in list) SchemaText(variant, parts);
    }

    /// <summary>Search text of a tool: its name, the name with <c>_</c> as spaces, its description, and its parameters' text.</summary>
    internal static string SearchText(ToolRegistration tool)
    {
        var parts = new List<string> { tool.Name, tool.Name.Replace('_', ' '), tool.Description };
        SchemaText(tool.Parameters, parts);
        return string.Join(" ", parts.Where(part => Js.Trim(part).Length != 0));
    }
}
