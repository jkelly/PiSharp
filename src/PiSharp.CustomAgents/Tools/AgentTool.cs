// Decision 0012: typed tools. AgentTool.Create builds a durable ToolRegistration from a C# handler over a typed arguments record: the
// parameters schema comes from System.Text.Json's schema exporter (strict numbers, [Description] attributes), the Harness validates the
// model's arguments against it before the handler runs, and the arguments are then read with the same serializer options. A handler that
// throws returns an error result to the model instead of faulting the tool task.
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PiSharp.CustomAgents.Internal;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Tools;

/// <summary>Options of a typed tool.</summary>
public sealed record AgentToolOptions
{
    /// <summary>Whether an execution interrupted by a crash may rerun on recovery. Default <see cref="ToolReplay.Unsafe"/>: the model gets an
    /// <c>interrupted</c> error instead. Mark a tool <see cref="ToolReplay.Safe"/> when rerunning it is harmless (reads, or work made
    /// idempotent with <see cref="AgentToolContext.OnceAsync{T}"/>).</summary>
    public ToolReplay Replay { get; init; } = ToolReplay.Unsafe;
    /// <summary>Not offered to the model until loaded (<c>ToolControl.AddTools</c>); other tools can call it any time.</summary>
    public bool Deferred { get; init; }
    /// <summary>Run this tool's round one call at a time.</summary>
    public bool Sequential { get; init; }
    /// <summary>Who may call the tool; default the model and other tools.</summary>
    public ImmutableArray<ToolCaller>? Callers { get; init; }
    /// <summary>Serializer options for the arguments and results; default the Web defaults.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }
    /// <summary>Output bounds.</summary>
    public ToolOutputLimits? OutputLimits { get; init; }
    /// <summary>Services for <see cref="AgentToolContext.GetService{T}"/>.</summary>
    public IServiceProvider? Services { get; init; }
}

/// <summary>What a typed tool returns: text for the model, and optionally a value for programs, details for UIs, and controls.</summary>
public sealed record AgentToolResult
{
    /// <summary>The text the model sees.</summary>
    public required string Text { get; init; }
    /// <summary>Whether the call failed.</summary>
    public bool IsError { get; init; }
    /// <summary>Details for UIs (not shown to the model).</summary>
    public JsonValue? Details { get; init; }
    /// <summary>Post-tools controls (load deferred tools, end the run, hand off).</summary>
    public ToolControl? Control { get; init; }
    internal JsonValue? StructuredOutput { get; init; }

    /// <summary>A successful result.</summary>
    public static AgentToolResult Ok(string text) => new() { Text = text ?? "" };

    /// <summary>An error result; the model sees <paramref name="message"/>.</summary>
    public static AgentToolResult Error(string message) => new() { Text = message ?? "", IsError = true };

    /// <summary>A successful result with <paramref name="text"/>.</summary>
    public static implicit operator AgentToolResult(string text) => Ok(text);
}

/// <summary>What a typed tool's handler can use during one call.</summary>
public sealed class AgentToolContext
{
    private readonly JsonSerializerOptions options;
    private readonly IServiceProvider? services;

    internal AgentToolContext(string tool, IToolExecutionApi api, DurableContext context, JsonSerializerOptions options, IServiceProvider? services)
    {
        Tool = tool;
        Api = api;
        Context = context;
        this.options = options;
        this.services = services;
    }

    /// <summary>The tool's name.</summary>
    public string Tool { get; }
    /// <summary>The durable tool API (documents, nested tool calls, child tasks, the calling agent).</summary>
    public IToolExecutionApi Api { get; }
    /// <summary>The durable context of the call.</summary>
    public DurableContext Context { get; }
    /// <summary>Cancelled when the call is aborted or the Harness closes.</summary>
    public CancellationToken CancellationToken => Context.CancellationToken;
    /// <summary>The tool task (stable across a rerun of a replay-safe tool).</summary>
    public TaskId TaskId => Api.TaskId;
    /// <summary>The calling conversation.</summary>
    public ConversationId ConversationId => Api.ConversationId;
    /// <summary>The model's tool call ID.</summary>
    public string CallId => Api.CallId;

    /// <summary>A key for external calls that is the same on every rerun of this call: <c>tool:&lt;task&gt;:&lt;key&gt;</c>.</summary>
    public string IdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return "tool:" + TaskId.Value.ToString(CultureInfo.InvariantCulture) + ":" + key;
    }

    /// <summary>Appends running output the model sees when the call ends (and UIs see while it runs).</summary>
    public void Output(string text) => Api.Output(text);

    /// <summary>
    /// Runs <paramref name="work"/> and records its result on the tool task, first writer wins: a rerun after a crash returns the recorded
    /// result without running <paramref name="work"/> again. The work itself runs at least once (a crash before the record reruns it).
    /// </summary>
    public async Task<T> OnceAsync<T>(string key, Func<CancellationToken, Task<T>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);
        var name = "once:" + key;
        if (await Api.MemoAsync(name, Context).ConfigureAwait(false) is JsonObjectValue recorded) return Read<T>(recorded);
        var result = await work(CancellationToken).ConfigureAwait(false);
        var candidate = JsonValue.Object([KeyValuePair.Create("value", ToolJson.ToChord(result, options))]);
        return Read<T>((JsonObjectValue)await Api.MemoAsync(name, candidate, Context).ConfigureAwait(false));
    }

    /// <summary>A service from <see cref="AgentToolOptions.Services"/>; null when absent.</summary>
    public T? GetService<T>() where T : class => services?.GetService(typeof(T)) as T;

    private T Read<T>(JsonObjectValue recorded) => JsonSerializer.Deserialize<T>(recorded["value"].ToJson(), options)!;
}

/// <summary>Builds durable tools from typed C# handlers.</summary>
public static class AgentTool
{
    /// <summary>A tool whose arguments are a <typeparamref name="TArgs"/> object.</summary>
    public static ToolRegistration Create<TArgs>(string name, string description, Func<TArgs, AgentToolContext, Task<AgentToolResult>> handler,
        AgentToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new AgentToolOptions();
        var json = Json(options);
        return Build(name, description, ParametersOf(typeof(TArgs), json), null, options,
            (arguments, context) => handler(arguments.Deserialize<TArgs>(json)!, context));
    }

    /// <summary>A tool whose arguments are read with <paramref name="typeInfo"/> (source-generated serialization, trimming-safe).</summary>
    public static ToolRegistration Create<TArgs>(string name, string description, JsonTypeInfo<TArgs> typeInfo,
        Func<TArgs, AgentToolContext, Task<AgentToolResult>> handler, AgentToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new AgentToolOptions();
        var schema = Schemas.Export(typeInfo);
        if (schema.Wrapped) throw new ArgumentException($"Tool '{name}' needs an object type for its arguments, not {typeof(TArgs).Name}.", nameof(typeInfo));
        return Build(name, description, schema.Schema, null, options, (arguments, context) => handler(arguments.Deserialize(typeInfo)!, context));
    }

    /// <summary>A tool returning a <typeparamref name="TResult"/>: programs that call it (other tools, code mode) receive the value, validated
    /// against its schema, and the model sees its JSON.</summary>
    public static ToolRegistration Create<TArgs, TResult>(string name, string description, Func<TArgs, AgentToolContext, Task<TResult>> handler,
        AgentToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new AgentToolOptions();
        var json = Json(options);
        var output = Schemas.Export(typeof(TResult), json);
        return Build(name, description, ParametersOf(typeof(TArgs), json), output.Schema, options, async (arguments, context) =>
        {
            var value = await handler(arguments.Deserialize<TArgs>(json)!, context).ConfigureAwait(false);
            var chord = ToolJson.ToChord(value, json);
            var structured = output.Wrapped ? JsonValue.Object([KeyValuePair.Create("value", chord)]) : chord;
            return new AgentToolResult { Text = JsonSerializer.Serialize(value, json), StructuredOutput = structured };
        });
    }

    /// <summary>A tool without arguments.</summary>
    public static ToolRegistration Create(string name, string description, Func<AgentToolContext, Task<AgentToolResult>> handler,
        AgentToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Build(name, description, ToolRegistration.EmptyParameters, null, options ?? new AgentToolOptions(), (_, context) => handler(context));
    }

    /// <summary>The Web defaults, plus required constructor parameters and nullable annotations, so a tool schema lists its required
    /// properties and the arguments read back as declared.</summary>
    internal static JsonSerializerOptions DefaultJson { get; } = Defaults();

    internal static JsonSerializerOptions Json(AgentToolOptions options) => options.JsonOptions ?? DefaultJson;

    private static JsonSerializerOptions Defaults()
    {
        var options = new JsonSerializerOptions(DataCodec.DefaultOptions) { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static JsonObjectValue ParametersOf(Type type, JsonSerializerOptions json)
    {
        var schema = Schemas.Export(type, json);
        if (schema.Wrapped) throw new ArgumentException($"A tool's arguments must be an object type, not {type.Name}.", nameof(type));
        return schema.Schema;
    }

    internal static ToolRegistration Build(string name, string description, JsonObjectValue parameters, JsonObjectValue? outputSchema,
        AgentToolOptions options, Func<JsonElement, AgentToolContext, Task<AgentToolResult>> run)
    {
        ToolNames.Validate(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var json = Json(options);
        return new ToolRegistration
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            ParametersOrigin = PiSharp.Contracts.ToolSchemaOrigin.JsonSchema,
            StructuredOutputSchema = outputSchema,
            Replay = options.Replay,
            Deferred = options.Deferred,
            ExecutionMode = options.Sequential ? ToolExecutionMode.Sequential : null,
            Callers = options.Callers,
            OutputLimits = options.OutputLimits,
            Execute = async (arguments, api, context) =>
            {
                var tool = new AgentToolContext(name, api, context, json, options.Services);
                AgentToolResult result;
                try
                {
                    using var document = JsonDocument.Parse(arguments.ToJson());
                    result = await run(document.RootElement, tool).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !context.IsCancellationRequested)
                {
                    result = AgentToolResult.Error(error is JsonException ? $"Invalid arguments for '{name}': {error.Message}" : $"Tool '{name}' failed: {error.Message}");
                }
                return ToExecution(result);
            },
        };
    }

    internal static ToolExecutionResult ToExecution(AgentToolResult result) => new()
    {
        Output = ToolJson.Text(result.Text),
        StructuredOutput = result.IsError ? null : result.StructuredOutput,
        IsError = result.IsError ? true : null,
        Details = result.Details,
        Control = result.Control,
    };
}

/// <summary>Tool name rules and JSON helpers shared by tool sources.</summary>
internal static partial class ToolNames
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    public static void Validate(string name)
    {
        if (name is null || !Pattern().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a tool name (a letter, then up to 63 letters, digits, '_' or '-').", nameof(name));
    }
}

internal static class ToolJson
{
    public static JsonArrayValue Text(string text) => JsonValue.Array(JsonValue.Object([new("type", "text"), new("text", text)]));

    /// <summary>A value as Chord JSON (numbers that do not fit a double exactly are refused, as in the Data codec).</summary>
    public static JsonValue ToChord<T>(T value, JsonSerializerOptions options) => new DataCodec(options, int.MaxValue).Serialize(value, "tool value");
}
