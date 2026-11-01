// Decision 0011 (run W4, R7): the Tool Bench's templates, after the mockup's Templates menu: three examples (Weather and Calculator are the
// Playground's own, Create ticket is the bench's) and PiSharp's built-in tools (read, grep, find, ls, bash, edit, write) with their real
// declarations, read from PiSharp.Tools. A built-in is offered as a definition only: the bench never runs it, and its schema is read-only.
// No template carries a sample mock: mocks are what the user saves.
using System.Text.Json.Nodes;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Bench;

/// <summary>A template of the bench's Templates menu.</summary>
/// <param name="Id">The id (the template's data attribute).</param>
/// <param name="Label">What the menu says.</param>
/// <param name="Icon">The Bootstrap Icons class.</param>
/// <param name="Tool">The definition.</param>
/// <param name="Builtin">Whether it is PiSharp's own tool.</param>
public sealed record BenchTemplate(string Id, string Label, string Icon, RunTool Tool, bool Builtin)
{
    /// <summary>The template as the page reads it (<c>data-bench-template</c>): name, description, parameters, builtin.</summary>
    public string Json => new JsonObject
    {
        ["name"] = Tool.Name,
        ["description"] = Tool.Description ?? "",
        ["parameters"] = Tool.Parameters?.DeepClone(),
        ["builtin"] = Builtin,
    }.ToJsonString();
}

/// <summary>The bench's templates.</summary>
public static class BenchTemplates
{
    private static readonly Lazy<IReadOnlyList<BenchTemplate>> all = new(Load);

    /// <summary>Every template: the examples, then the built-ins.</summary>
    public static IReadOnlyList<BenchTemplate> All => all.Value;

    /// <summary>The examples.</summary>
    public static IReadOnlyList<BenchTemplate> Examples => [.. All.Where(template => !template.Builtin)];

    /// <summary>PiSharp's built-in tools.</summary>
    public static IReadOnlyList<BenchTemplate> Builtins => [.. All.Where(template => template.Builtin)];

    /// <summary>The names of the built-in tools.</summary>
    public static IReadOnlyList<string> BuiltinNames => [.. Builtins.Select(template => template.Tool.Name)];

    /// <summary>The templates the shared tool editor offers as radios (as the mockup: the examples, read and grep).</summary>
    public static IReadOnlyList<ToolTemplate> EditorTemplates =>
    [
        .. All.Where(template => !template.Builtin || template.Tool.Name is "read" or "grep")
            .Select(template => new ToolTemplate(template.Id, template.Builtin ? "PiSharp " + template.Label : template.Label, template.Icon, template.Tool)),
    ];

    /// <summary>The declaration of built-in tool <paramref name="name"/>, or null.</summary>
    public static RunTool? Builtin(string name) => Builtins.FirstOrDefault(template => template.Tool.Name == name)?.Tool;

    /// <summary>The icon of a tool by its name (a template's, else a wrench).</summary>
    public static string Icon(string name) => All.FirstOrDefault(template => template.Tool.Name == name)?.Icon ?? "bi-wrench";

    private static List<BenchTemplate> Load()
    {
        var templates = new List<BenchTemplate>();
        foreach (var template in PlaygroundPages.Templates())
        {
            if (template.Id == "weather") templates.Add(new("weather", "Weather", template.Icon, template.Tool, false));
            else if (template.Id == "calc") templates.Add(new("calc", "Calculator", template.Icon, template.Tool, false));
        }
        templates.Add(new("ticket", "Create ticket", "bi-ticket-perforated", new RunTool("create_ticket",
            "Open a ticket in the operations queue and return its id.",
            JsonNode.Parse("""{"type":"object","properties":{"title":{"type":"string","description":"A one-line summary"},"priority":{"type":"string","enum":["low","normal","high","urgent"]},"body":{"type":"string","description":"What happened and what to do"},"labels":{"type":"array","items":{"type":"string"}}},"required":["title","priority"],"additionalProperties":false}""")!.AsObject()), false));
        var root = Path.GetTempPath();
        Add(templates, "read", "bi-file-earmark-code", () => ReadWriteTools.ReadDeclaration.ToString());
        Add(templates, "grep", "bi-search", () => new PiGrepTool(root, root, _ => ValueTask.FromResult<string?>(null)).Declaration.ToString());
        Add(templates, "find", "bi-folder2-open", () => new PiFindTool(root, root, _ => ValueTask.FromResult<string?>(null)).Declaration.ToString());
        Add(templates, "ls", "bi-list-ul", () => new LsTool(root, root, null, pi: true).Declaration.ToString());
        Add(templates, "bash", "bi-terminal", () => BashTool.SourceDeclaration.ToString());
        Add(templates, "edit", "bi-pencil-square", () => EditTool.SourceDeclaration.ToString());
        Add(templates, "write", "bi-file-earmark-plus", () => ReadWriteTools.WriteDeclaration.ToString());
        return templates;
    }

    private static void Add(List<BenchTemplate> templates, string id, string icon, Func<string> declaration)
    {
        try
        {
            if (JsonNode.Parse(declaration()) is not JsonObject json || json["name"] is not JsonValue name || !name.TryGetValue<string>(out var text)) return;
            var description = json["description"] is JsonValue value && value.TryGetValue<string>(out var said) ? said : null;
            templates.Add(new(id, text, icon, new RunTool(text, description, json["parameters"]?.DeepClone() as JsonObject), true));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A built-in whose declaration cannot be read is left out of the menu.
        }
    }
}
