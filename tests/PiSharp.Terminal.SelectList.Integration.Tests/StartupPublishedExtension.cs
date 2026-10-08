using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace StartupPublishedFixture;

// Public test-only entry in the already admitted consumer assembly. No module initializer,
// environment access, provider call or static shared signal substitutes for the real RPC route.
public sealed class Entry : IPiSharpExtension
{
    private string selected = "not-selected";
    private string outcome = "not-started";
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.Observe(new("startup", "session_start", Start));
        registry.RegisterTool(new("result", "fixture.startup.result", "Return the actual startup selector outcome",
            JsonData.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"), Result));
        return ValueTask.CompletedTask;
    }
    private async ValueTask Start(JsonData observation, IExtensionContext context, CancellationToken token)
    {
        var ui = ((IExtensionUiContext)context).Ui;
        // Session creation uses the ordinary no-UI profile. Only the terminal RPC startup is interactive.
        if (!ui.Capabilities.Supports(ExtensionUiFeature.Select)) return;
        var result = await ui.SelectAsync("STARTUP choose 文", ["", "startup-approved"], new(0), token);
        outcome = result.Kind.ToString();
        selected = result.Kind == ExtensionUiOutcomeKind.Value ? result.Value : "no-value";
    }
    private ValueTask<JsonData> Result(JsonData arguments, IExtensionToolContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "startup:" + selected } },
            details = new { selected, outcome }, structuredContent = new { selected, outcome }
        })));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
