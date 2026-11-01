using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace AgentsExtensionFixture;

/// <summary>A native extension with a sequential replay-safe tool that returns details, a model-only tool that returns an error, and a
/// command (which agents do not support).</summary>
public sealed class Extension : IPiSharpExtension
{
    private static readonly JsonData TextSchema =
        JsonData.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterTool(new ExtensionToolDescriptor("upper", "ext_upper", "Uppercases text", TextSchema, (arguments, _, _) =>
        {
            var text = arguments.Value.GetProperty("text").GetString() ?? "";
            return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = text.ToUpperInvariant() } },
                details = new { length = text.Length },
            })));
        }) { SequentialExecution = true });
        registry.RegisterTool(new ExtensionToolDescriptor("fail", "ext_fail", "Always fails", TextSchema, (_, _, _) =>
            ValueTask.FromResult(JsonData.Parse("""{"content":[{"type":"text","text":"nope"}],"isError":true}""")))
        { Exposure = ToolExposure.ModelOnly });
        registry.RegisterCommand(new ExtensionCommandDescriptor("hello", "hello", "Says hello", (_, _, _) => ValueTask.CompletedTask));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
