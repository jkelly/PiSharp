using System.Text.Json;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;

internal static class NativeTodoSchemaTests
{
    internal const string Schema = "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"list\",\"add\",\"toggle\",\"clear\"]},\"text\":{\"type\":\"string\",\"description\":\"Todo text (for add)\"},\"id\":{\"type\":\"number\",\"description\":\"Todo ID (for toggle)\"}},\"required\":[\"action\"]}";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("stateful-todo.schema preserves source enum, finite numbers and optional fields", Valid),
        ("stateful-todo.schema refuses conversion, unknown enum and ambiguous fields", Invalid),
        ("stateful-todo.schema rejects unsupported validation and keeps legacy profile", Unsupported)
    ];
    private static Task Valid()
    {
        var schema = NativeToolObjectSchema.Read(JsonData.Parse(Schema));
        foreach (var raw in new[] { "{\"action\":\"list\"}", "{\"action\":\"add\",\"text\":\"\"}",
            "{\"action\":\"toggle\",\"id\":1.5}", "{\"action\":\"toggle\",\"id\":-0}",
            "{\"action\":\"clear\",\"future\":null}", "{\"action\":\"list\",\"text\":\"\\u6587\\n\\u0000\"}" })
        { var input = JsonData.Parse(raw); Check(schema.Validate(input), "Valid source input rejected."); Check(input.ToString() == raw, "Input was rewritten."); }
        return Task.CompletedTask;
    }
    private static Task Invalid()
    {
        var schema = NativeToolObjectSchema.Read(JsonData.Parse(Schema));
        foreach (var raw in new[] { "{}", "{\"action\":\"unknown\"}", "{\"action\":1}", "{\"action\":\"toggle\",\"id\":\"1\"}",
            "{\"action\":\"toggle\",\"id\":null}", "{\"action\":\"list\",\"text\":false}", "{\"action\":\"toggle\",\"id\":1e999}",
            "[]", "null" }) Check(!schema.Validate(JsonData.Parse(raw)), "Invalid input admitted.");
        var duplicateRejected = false;
        try { _ = JsonData.Parse("{\"action\":\"list\",\"action\":\"clear\"}"); }
        catch (JsonException) { duplicateRejected = true; }
        Check(duplicateRejected, "Duplicate keys bypassed owned JSON admission before schema validation.");
        return Task.CompletedTask;
    }
    private static Task Unsupported()
    {
        foreach (var raw in new[] { Schema.Replace("\"type\":\"number\"", "\"type\":\"number\",\"minimum\":0", StringComparison.Ordinal),
            Schema.Replace("\"enum\":[\"list\",\"add\",\"toggle\",\"clear\"]", "\"enum\":[]", StringComparison.Ordinal),
            Schema.Replace("\"type\":\"number\"", "\"type\":\"array\"", StringComparison.Ordinal) })
        {
            var rejected = false;
            try { _ = NativeToolObjectSchema.Read(JsonData.Parse(raw)); }
            catch (NativeExtensionException error) when (error.Failure == NativeExtensionFailure.UnsupportedSchema) { rejected = true; }
            Check(rejected, "Unsupported validation was ignored.");
        }
        var legacy = NativeToolObjectSchema.Read(JsonData.Parse(NativeStringSchemaTests.HelloParameters));
        Check(legacy.Validate(JsonData.Parse("{\"name\":\"Ada\",\"future\":null}")), "Legacy annotations/additional properties changed.");
        Check(!legacy.Validate(JsonData.Parse("{\"name\":1}")), "Legacy conversion was widened.");
        return Task.CompletedTask;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
