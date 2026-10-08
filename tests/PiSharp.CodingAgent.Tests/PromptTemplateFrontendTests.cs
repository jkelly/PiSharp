using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using static PromptTemplateDiscoveryTests;

internal static class PromptTemplateFrontendTests
{
    public const string Prefix = "prompt template frontend ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "catalog accepts prompt names and submits their original slash text", Submission),
        (Prefix + "prompt name completion stays local and extension completion retains ownership", Completion)
    ];
    private static async Task Submission()
    {
        using var output = new StringWriter(); using var frontend = new InteractiveSessionFrontend(output);
        var sent = new List<JsonData>(); frontend.Bind((record, _) => { sent.Add(record); return Task.CompletedTask; });
        await Catalog(frontend);
        foreach (var text in new[] { "/review arg", "/review\targ", "/日本語 'arg'" })
        {
            await frontend.LineAsync(text, default); Equal("prompt", sent[^1].Value.GetProperty("type").GetString());
            Equal(text, sent[^1].Value.GetProperty("message").GetString());
        }
        Equal(3, sent.Count);
        await frontend.LineAsync("/unknown arg", default); Equal(3, sent.Count);
        await frontend.LineAsync("/complete /name", default); Equal(3, sent.Count);
        Equal(true, output.ToString().Contains("/name with spaces", StringComparison.Ordinal));
    }
    private static async Task Completion()
    {
        using var output = new StringWriter(); using var frontend = new InteractiveSessionFrontend(output);
        var sent = new List<JsonData>(); frontend.Bind((record, _) => { sent.Add(record); return Task.CompletedTask; });
        await Catalog(frontend);
        await frontend.LineAsync("/complete /rev", default); Equal(0, sent.Count);
        Equal(true, output.ToString().Contains("/review", StringComparison.Ordinal)); Equal(false, output.ToString().Contains('\u001b'));
        await frontend.LineAsync("/complete 日本語", default); Equal(0, sent.Count);
        await frontend.LineAsync("/complete review prefix", default);
        Equal("pisharp_complete_extension_command", sent.Single().Value.GetProperty("type").GetString());
        Equal("review", sent[0].Value.GetProperty("command").GetString()); Equal("prefix", sent[0].Value.GetProperty("prefix").GetString());
    }
    private static ValueTask Catalog(InteractiveSessionFrontend frontend) => frontend.ObserveAsync(JsonData.Parse(JsonSerializer.Serialize(new
    {
        type = "response", id = "chat-commands", command = "get_commands", success = true,
        data = new { commands = new object[] {
            new { name = "review", source = "extension" },
            new { name = "review", source = "prompt", description = "Review\u001b[31m inert" },
            new { name = "日本語", source = "prompt", description = "Unicode" },
            new { name = "name with spaces", source = "prompt", description = "Accepted catalog row" }
        } }
    })), default);
}
