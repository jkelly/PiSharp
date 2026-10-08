using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PublishedCliFixture;

/// <summary>Authored C# consumer of Hello's public JSON shape; this does not execute the TypeScript source.</summary>
public sealed class HelloSchemaEntry : IPiSharpExtension
{
    public HelloSchemaEntry() => Signals.Mark("constructor");
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Signals.Mark("initialize");
        registry.RegisterTool(new("hello", "hello", "A simple greeting tool",
            JsonData.Parse("{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name to greet\"}}}"),
            (arguments, context, token) =>
            {
                token.ThrowIfCancellationRequested();
                var actual = context as IExtensionToolInvocationContext ?? throw new InvalidOperationException("Actual invocation identity is required by this authored fixture.");
                Signals.Mark("hello-execute:" + JsonSerializer.Serialize(new { id = actual.ToolCallId, arguments = arguments.Value }));
                try
                {
                    var name = arguments.Value.GetProperty("name").GetString()!;
                    return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new
                    {
                        content = new[] { new { type = "text", text = "Hello, " + name + "!" } }, details = new { greeted = name }
                    })));
                }
                finally { Signals.Mark("hello-execute-closed"); }
            }));
        // Separate authored adversarial hook: proves that Hello's final schema remains authoritative.
        registry.RegisterToolCallHandler(new("hello-schema-call", (call, context, token) =>
        {
            token.ThrowIfCancellationRequested(); Signals.Mark("hello-call:" + call.ToolName);
            return ValueTask.FromResult<ExtensionToolCallPatch?>(call.ToolName == "hello" &&
                call.Arguments.Value.GetProperty("name").GetString() == "fixture-invalid-replacement"
                ? new(JsonData.Parse("{\"name\":7}")) : null);
        }));
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { Signals.Mark("dispose"); return ValueTask.CompletedTask; }
}
