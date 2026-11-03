// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/validation.ts (validateToolArguments) and
// packages/agent/src/agent-loop.ts (prepareToolCall). Goldens recorded from the real upstream code by
// ToolValidation/gen-tool-validation-goldens.mjs (pi-ai 1.1.0, typebox 1.3.27).
//
// Pi v1.5.0 sync (schema area): validateToolArguments now runs pi-schema, so the 1.1.0 goldens no longer hold its verdicts and are not
// recaptured. Their inputs stay, under authored checks of the 1.5.0 behaviour (packages/ai/src/utils/validation.ts at
// f9a26365568127461c47a07a37dcb42c140a3ac5): the message shape, coerced arguments that validate again unchanged, and TypeBox metadata
// that changes no result. The messages below are pi-schema's, authored from its explainer (packages/schema/src/explain.ts).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class ToolValidationTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("validation built-in declarations are the upstream TypeBox schemas and validate as they do", BuiltinDeclarations),
        ("validation invoker passes coerced arguments and keeps the model's call", InvokerCoerces),
        ("validation invoker failure is the upstream error result without running the tool", InvokerFailure),
        ("validation edit prepareArguments runs before the schema check", EditPrepareArguments),
        ("validation thrown prepareArguments error is the error result text", PreparationError),
        ("validation non-object final arguments get the upstream root error", NonObjectArguments),
    ];

    internal static JsonElement Goldens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ToolValidation", "tool-validation-goldens.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    internal static ToolSchemaOrigin Origin(string name) => name switch
    {
        "typebox" => ToolSchemaOrigin.TypeBox, "legacy" => ToolSchemaOrigin.LegacyTypeBoxKindSymbol, _ => ToolSchemaOrigin.JsonSchema
    };

    internal static string Outcome(ToolArgumentValidationResult result) => result.IsValid ? "ok " + result.ArgumentsJson : "error " + result.ErrorMessage;

    /// <summary>PiSharp's built-in declarations carry no TypeBox metadata: they must equal the upstream schemas without it, and validate
    /// the golden inputs exactly as the upstream schemas with their TypeBox metadata do (pi-schema ignores it).</summary>
    private static Task BuiltinDeclarations()
    {
        using var temp = new Temp();
        var readWrite = new ReadWriteTools(temp.Root, temp.Root);
        var declarations = new Dictionary<string, JsonData>(StringComparer.Ordinal)
        {
            ["builtin_read"] = ReadWriteTools.ReadDeclaration, ["builtin_write"] = ReadWriteTools.WriteDeclaration,
            ["builtin_edit"] = EditTool.SourceDeclaration, ["builtin_bash"] = BashTool.SourceDeclaration,
            ["builtin_grep"] = new PiGrepTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>("rg")).Declaration,
            ["builtin_find"] = new PiFindTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>("fd")).Declaration,
            ["builtin_ls"] = new LsTool(temp.Root, temp.Root).Declaration,
        };
        var root = Goldens(); var schemas = root.GetProperty("schemas"); var failures = new List<string>(); var replayed = 0;
        foreach (var (name, declaration) in declarations)
        {
            var parameters = declaration.Value.GetProperty("parameters");
            var upstream = Strip(JsonNode.Parse(schemas.GetProperty(name).GetProperty("schema").GetRawText()));
            Check(JsonNode.DeepEquals(upstream, JsonNode.Parse(parameters.GetRawText())), name + " declaration differs from the upstream schema.");
            foreach (var item in root.GetProperty("cases").EnumerateArray().Where(item => item.GetProperty("schema").GetString() == name))
            {
                var args = item.GetProperty("argsJson").GetString()!;
                var declared = Outcome(ToolArgumentValidation.ValidateJson(name, parameters.GetRawText(), args, ToolSchemaOrigin.TypeBox));
                var annotated = Outcome(ToolArgumentValidation.ValidateJson(name, schemas.GetProperty(name).GetProperty("schema").GetRawText(), args, ToolSchemaOrigin.TypeBox));
                if (declared != annotated) failures.Add($"{name} {args} => {declared} / with TypeBox metadata: {annotated}");
                replayed++;
            }
            var schema = (readWrite.Adapters.Concat([(IPreparedToolAdapter)new EditTool(temp.Root, temp.Root, new((path, _) => ValueTask.FromResult(path))),
                new BashTool(new NoRunner(), new BashToolOptions(Environment.ProcessPath!, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Root)),
                new PiGrepTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>("rg")), new PiFindTool(temp.Root, temp.Root, _ => ValueTask.FromResult<string?>("fd")), new LsTool(temp.Root, temp.Root).Adapter])
                .Single(adapter => "builtin_" + adapter.Name == name) as IToolArgumentSchemaAdapter)?.ArgumentSchema;
            Check(schema is { Origin: ToolSchemaOrigin.TypeBox } && JsonElement.DeepEquals(schema.Parameters.Value, parameters),
                name + " adapter does not declare its TypeBox schema.");
        }
        Check(replayed >= 40, "Too few built-in golden cases: " + replayed);
        Check(failures.Count == 0, failures.Count + " built-in golden mismatches:\n" + string.Join("\n", failures.Take(5)));
        return Task.CompletedTask;

        static JsonNode? Strip(JsonNode? node) => node switch
        {
            JsonObject value => new JsonObject(value.Where(property => !property.Key.StartsWith('~'))
                .Select(property => KeyValuePair.Create(property.Key, Strip(property.Value)))),
            JsonArray value => new JsonArray([.. value.Select(Strip)]),
            _ => node?.DeepClone()
        };
    }

    private static async Task InvokerCoerces()
    {
        using var temp = new Temp();
        await File.WriteAllTextAsync(temp.File("lines.txt"), "one\ntwo\nthree\nfour");
        var tools = new ReadWriteTools(temp.Root, temp.Root); var policy = new Recording();
        var invocation = Invocation("read", """{"path":"lines.txt","offset":"2","limit":"2","extra":null}""");
        var result = await tools.CreateInvoker(policy).ExecuteAsync(invocation, default);
        Check(!result.IsError, "Coerced read failed: " + Text(result));
        Equal("two\nthree\n\n[1 more lines in file. Use offset=4 to continue.]", Text(result));
        // The tool saw the coerced numbers; the model's call (and so the committed message and events) keeps the strings.
        var action = policy.Actions.Single().Arguments.Value;
        Equal(JsonValueKind.Number, action.GetProperty("offset").ValueKind); Equal(2, action.GetProperty("limit").GetInt32());
        Equal("\"2\"", invocation.Call.Arguments.Value.GetProperty("limit").GetRawText());
        // Optional nulls are dropped before the tool runs (pi-schema coercion, pass 1).
        var nulls = await tools.CreateInvoker(policy).ExecuteAsync(Invocation("read", """{"path":"lines.txt","limit":null}"""), default);
        Equal("one\ntwo\nthree\nfour", Text(nulls));
        // write: content number -> string, extra properties pass through the schema and are ignored by execute.
        var write = await tools.CreateInvoker(policy).ExecuteAsync(Invocation("write", """{"path":"n.txt","content":42,"mode":"x"}"""), default);
        Check(!write.IsError, "Coerced write failed: " + Text(write)); Equal("42", await File.ReadAllTextAsync(temp.File("n.txt")));
    }

    private static async Task InvokerFailure()
    {
        using var temp = new Temp();
        var tools = new ReadWriteTools(temp.Root, temp.Root); var policy = new Recording();
        var missing = await tools.CreateInvoker(policy).ExecuteAsync(Invocation("read", """{"offset":"x"}"""), default);
        Equal("Validation failed for tool \"read\":\n  - path: missing\n  - offset: expected number, got string\n\nReceived arguments:\n{\n  \"offset\": \"x\"\n}", Text(missing));
        Check(missing.IsError && missing.Failure?.Kind == ToolFailureKind.InvalidArguments, "Validation failure was not an error result.");
        Equal("{}", missing.Details.ToString()); Equal(0, policy.Actions.Count);
        var bash = new BashTool(new NoRunner(), new BashToolOptions(Environment.ProcessPath!, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Root));
        var failed = await new ToolInvoker([bash], policy).ExecuteAsync(Invocation("bash", """{"command":["ls"],"timeout":true}"""), default);
        Equal("Validation failed for tool \"bash\":\n  - command: expected string, got array\n\nReceived arguments:\n{\n  \"command\": [\n    \"ls\"\n  ],\n  \"timeout\": true\n}", Text(failed));
        Equal(0, policy.Actions.Count);
    }

    private static async Task EditPrepareArguments()
    {
        using var temp = new Temp();
        await File.WriteAllTextAsync(temp.File("e.txt"), "alpha beta");
        var tools = new BuiltinTools(temp.Root); var policy = new Recording();
        // Legacy top-level oldText/newText: prepareEditArguments turns them into edits before the schema requires edits.
        var legacy = await tools.Edit.CreateInvoker(policy).ExecuteAsync(Invocation("edit", """{"path":"e.txt","oldText":"alpha","newText":"gamma"}"""), default);
        Check(!legacy.IsError, "Legacy edit failed: " + Text(legacy)); Equal("gamma beta", await File.ReadAllTextAsync(temp.File("e.txt")));
        // edits as a JSON string and as a single object, with an extra property.
        var encoded = await tools.Edit.CreateInvoker(policy).ExecuteAsync(Invocation("edit",
            """{"path":"e.txt","edits":"{\"oldText\":\"beta\",\"newText\":\"delta\"}","note":1}"""), default);
        Check(!encoded.IsError, "Encoded edit failed: " + Text(encoded)); Equal("gamma delta", await File.ReadAllTextAsync(temp.File("e.txt")));
        Equal("{\"path\":\"e.txt\",\"edits\":[{\"oldText\":\"a\",\"newText\":\"b\"}]}",
            EditTool.PrepareEditArguments(JsonData.Parse("""{"path":"e.txt","edits":{"oldText":"a","newText":"b"}}""")).ToString());
        var invalid = await tools.Edit.CreateInvoker(policy).ExecuteAsync(Invocation("edit", """{"path":"e.txt","edits":[{"oldText":"a"}]}"""), default);
        Equal("Validation failed for tool \"edit\":\n  - edits.0.newText: missing\n\nReceived arguments:\n{\n  \"path\": \"e.txt\",\n  \"edits\": [\n    {\n      \"oldText\": \"a\"\n    }\n  ]\n}", Text(invalid));
    }

    private static async Task PreparationError()
    {
        var policy = new Recording();
        var result = await new ToolInvoker([new ThrowingPreparation()], policy).ExecuteAsync(Invocation("prep", "{}"), default);
        Equal("prepare refused", Text(result)); Check(result.IsError && result.Failure?.Kind == ToolFailureKind.InvalidArguments, "Not an error result.");
        Equal("{}", result.Details.ToString()); Equal(0, policy.Actions.Count);
    }

    private sealed class ThrowingPreparation : IInitialToolArgumentPreparationAdapter
    {
        public string Name => "prep";
        public ValueTask<JsonData> PrepareInitialArgumentsAsync(ToolInvocation invocation, CancellationToken token) =>
            throw new ToolArgumentPreparationException("prepare refused");
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => throw new InvalidOperationException("The tool ran.");
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("The tool ran.");
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("The tool ran.");
    }

    // parseStreamingJson can finalize any JSON value; agent-loop prepareToolCall runs prepareArguments and validateToolArguments on it.
    // Authored from Pi v1.5.0 validateToolArguments (pi-schema) with the pi-coding-agent read, edit and bash tools.
    private static async Task NonObjectArguments()
    {
        using var temp = new Temp();
        var policy = new Recording();
        var read = new ReadWriteTools(temp.Root, temp.Root).CreateInvoker(policy);
        var edit = new BuiltinTools(temp.Root).Edit.CreateInvoker(policy);
        var bash = new ToolInvoker([new BashTool(new NoRunner(), new BashToolOptions(Environment.ProcessPath!, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Root))], policy);
        foreach (var (name, invoker) in new[] { ("read", read), ("edit", edit), ("bash", bash) })
            foreach (var (arguments, received, type) in new[]
            {
                ("[1,2]", "[\n  1,\n  2\n]", "array"), ("\"s\"", "\"s\"", "string"), ("5", "5", "integer"), ("null", "null", "null"), ("true", "true", "boolean"), ("[]", "[]", "array")
            })
            {
                var result = await invoker.ExecuteAsync(Invocation(name, arguments), default);
                Check(result.IsError && result.Failure?.Kind == ToolFailureKind.InvalidArguments, name + " " + arguments + " was not a validation error.");
                Equal("Validation failed for tool \"" + name + "\":\n  - root: expected object, got " + type + "\n\nReceived arguments:\n" + received, Text(result));
            }
        Equal(0, policy.Actions.Count);
    }

    private sealed class BuiltinTools(string root)
    {
        public EditTool Edit { get; } = new(root, root, new((path, _) => ValueTask.FromResult(path)));
    }

    private static ToolInvocation Invocation(string name, string arguments)
    {
        var call = new ToolCallContent(name + "-call", name, JsonData.Parse(arguments));
        return new(new("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private static string Text(ToolResult result) => string.Concat(result.Content.Select(content => content.Text));
    private sealed class Recording : IToolActionPolicy
    {
        public List<PreparedToolAction> Actions { get; } = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token)
        { Actions.Add(finalAction); return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class NoRunner : IProcessRunner
    {
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The tool ran.");
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("pisharp-validation-").FullName;
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
    }
}
