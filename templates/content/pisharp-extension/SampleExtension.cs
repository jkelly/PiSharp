using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace MyPiSharpExtension;

/// <summary>
/// Starter native PiSharp extension. Load the build output directory with <c>pisharp -e path/to/pisharp-extension.json</c>
/// or place it in <c>~/.pi/agent/extensions/&lt;name&gt;/</c>.
/// </summary>
public sealed class SampleExtension : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterCommand(new(
            "hello",
            "hello",
            "Prints a greeting from this native extension.",
            async (arguments, context, token) =>
            {
                var raw = arguments.Value.ValueKind == JsonValueKind.String ? arguments.Value.GetString() : null;
                var target = string.IsNullOrWhiteSpace(raw) ? "world" : raw.Trim();
                if (context is IExtensionUiContext ui)
                    await ui.Ui.PublishAsync(new ExtensionUiNotify($"Hello, {target}! (from MyPiSharpExtension)", ExtensionUiNotifyKind.Info), token);
            }));

        registry.RegisterTool(new(
            "echo",
            "echo",
            "Echoes the supplied text back to the model.",
            JsonData.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "text": { "type": "string", "description": "Text to echo." }
                  },
                  "required": ["text"]
                }
                """),
            (arguments, context, token) =>
            {
                var text = arguments.Value.GetProperty("text").GetString() ?? string.Empty;
                return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
                {
                    content = new[] { new { type = "text", text = "echo: " + text } },
                    details = new { }
                })));
            }));

        registry.Observe(new(
            "on-session-start",
            "session_start",
            (observation, context, token) => ValueTask.CompletedTask));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
