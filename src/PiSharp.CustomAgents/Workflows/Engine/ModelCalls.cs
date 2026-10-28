// Decision 0012: model calls from workflow actions. A call runs in a child conversation the run's task owns, created once per visit and
// call key and recorded in the run document, in two commits: the child and its agent first, then the submission. The request ID
// wf:<task>:<step>:<key>:<attempt>[:r<repair>] is deterministic, and durable returns the existing submission for a known request ID, so
// a rerun after a crash waits for the same answer instead of asking again. A typed call selects a per-type extension whose submit_result
// tool terminates the run; the answer entry then carries the validated arguments.
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.CustomAgents.Internal;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows.Engine;

/// <summary>A model call that ended without an answer (no model, a provider error, an abort).</summary>
public sealed class ModelCallException(string message, string? reason = null) : Exception(message)
{
    /// <summary>Durable's reason for the unanswered submission (<c>model_error</c>, <c>no_model</c>, …), when known.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>The answer of a model call.</summary>
/// <param name="Text">The answer's text blocks, joined.</param>
/// <param name="Message">The answer's assistant message (pi-ai wire JSON).</param>
/// <param name="ConversationId">The child conversation that answered; it stays, so a host can show or continue it.</param>
public sealed record ModelAnswer(string Text, JsonObjectValue Message, ConversationId ConversationId);

/// <summary>The submit_result tool of one result type.</summary>
internal sealed partial class ResultTool
{
    public const string ToolName = "submit_result";

    private ResultTool(Type type, TypeSchema schema, Extension extension, ToolRegistration tool)
    {
        Type = type;
        Schema = schema;
        Extension = extension;
        Tool = tool;
    }

    public Type Type { get; }
    public TypeSchema Schema { get; }
    public Extension Extension { get; }
    public ToolRegistration Tool { get; }

    public static ResultTool Create(string workflow, Type type, JsonSerializerOptions options)
    {
        var schema = Schemas.Export(type, options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(type.FullName ?? type.Name)))[..8];
        var tool = new ToolRegistration
        {
            Name = ToolName,
            Description = "Submit your final result. Call this once, with arguments that match its schema, when you have the answer; do not " +
                "answer in plain text.",
            Parameters = schema.Schema,
            ParametersOrigin = PiSharp.Contracts.ToolSchemaOrigin.JsonSchema,
            Replay = ToolReplay.Safe,
            Callers = [ToolCaller.Model],
            Deferred = true,
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Output = JsonValue.Array(JsonValue.Object([new("type", "text"), new("text", "Result recorded.")])),
                Control = new ToolControl { Terminate = true },
            }),
        };
        var name = "pisharp.workflow." + workflow + ".result." + Unsafe().Replace(type.Name, "_").ToLowerInvariant() + "-" + hash;
        return new ResultTool(type, schema, new Extension { Name = name, Tools = [tool] }, tool);
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex Unsafe();
}

internal static partial class ModelCalls
{
    /// <summary>Runs one model call keyed <paramref name="key"/> in this visit, with the agent <paramref name="agent"/>.</summary>
    public static async Task<ModelAnswer> CallAsync(ActionContext context, string key, AgentChange agent, string text, TimeSpan? timeout,
        string? requestSuffix, CancellationToken cancellationToken)
    {
        var runtime = context.Runtime;
        var task = context.Task;
        var durable = context.Durable.WithCancellation(cancellationToken);
        var callKey = context.Step.ToString(CultureInfo.InvariantCulture) + ":" + key;
        ConversationId child = default;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            if (!doc.ContainsKey("calls") || doc.Get("calls") is not JsonObjectValue) doc.Set("calls", JsonObjectValue.Empty);
            var calls = doc.GetObject("calls");
            if (calls.Get(callKey) is JsonObjectValue known && known.TryGetValue("conversationId", out var id) && id is JsonNumberValue number)
            {
                child = new ConversationId((long)number.Value);
                return;
            }
            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(task.Id)).ConfigureAwait(false);
            await Agents.ConfigureAsync(tx, created.Id, agent).ConfigureAwait(false);
            child = created.Id;
            calls.Set(callKey, RunDocs.Object(("conversationId", created.Id.Value), ("action", context.Action), ("at", runtime.Now())));
            while (calls.Count > 200) calls.Delete(calls.Keys[0]);
        }, durable).ConfigureAwait(false);
        var handle = await runtime.ConversationAsync(child, durable).ConfigureAwait(false)
            ?? throw new ModelCallException($"The model call conversation {child.Value} is missing.");
        var requestId = context.IdempotencyKey(key) + ":" + context.Attempt.ToString(CultureInfo.InvariantCulture) + (requestSuffix ?? "");
        var submission = await handle.SubmitAsync(SubmissionDraft.Input(text, requestId: requestId), durable).ConfigureAwait(false);
        SubmissionRecord settled;
        using (var deadline = timeout is { } limit ? new CancellationTokenSource(limit, context.Scope.Catalog.Options.TimeProvider ?? TimeProvider.System) : null)
        {
            var waiting = deadline is null ? durable : durable.WithCancellation(deadline.Token);
            try { settled = await submission.WaitAsync(waiting).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline is { IsCancellationRequested: true } && !durable.IsCancellationRequested)
            {
                // A placed input keeps running after a submission abort; the child conversation belongs to this call alone, so abort it.
                await handle.AbortAsync(durable).ConfigureAwait(false);
                throw new TimeoutException($"The model did not answer within {timeout}.");
            }
        }
        if (settled.Status != SubmissionStatus.Done || settled.Type != SubmissionType.Input || settled.Answer is not { } answer)
        {
            var reason = settled.Reason;
            throw new ModelCallException($"The model call '{key}' ended without an answer ({settled.Status.ToString().ToLowerInvariant()}" +
                (reason is null ? "" : ": " + reason) + ").", reason);
        }
        JsonObjectValue? message = null;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var entry = await tx.GetEntryAsync(Entries.AssistantEntry, answer).ConfigureAwait(false);
            message = entry?.Model is { Count: > 0 } messages ? messages[0] as JsonObjectValue : null;
        }, durable).ConfigureAwait(false);
        if (message is null) throw new ModelCallException($"The answer of model call '{key}' is missing.");
        return new ModelAnswer(Text(message), message, child);
    }

    /// <summary>A typed call: asks, then repairs up to <paramref name="maxRepairs"/> times, and returns the validated result.</summary>
    public static async Task<T> CallTypedAsync<T>(ActionContext context, string key, AgentChange agent, ResultTool result, string text,
        TimeSpan? timeout, int maxRepairs, CancellationToken cancellationToken)
    {
        var options = context.Scope.Codec.Options;
        var prompt = text;
        for (var repair = 0; ; repair++)
        {
            var answer = await CallAsync(context, key, agent, prompt, timeout, repair == 0 ? null : ":r" + repair.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            if (TryRead<T>(answer.Message, result, options, out var value, out var error)) return value;
            if (repair >= maxRepairs)
                throw new ModelCallException($"The model did not return a valid {typeof(T).Name} for '{key}' after {repair + 1} answers: {error}");
            prompt = $"Your answer could not be used: {error}\nCall the {ResultTool.ToolName} tool with arguments that match its schema.";
        }
    }

    /// <summary>The answer's text blocks, joined.</summary>
    public static string Text(JsonObjectValue message) =>
        message.TryGetValue("content", out var content) && content is JsonArrayValue blocks
            ? string.Concat(blocks.OfType<JsonObjectValue>().Select(block =>
                Str(block, "type") == "text" && block.TryGetValue("text", out var text) && text is JsonStringValue value ? value.Value : ""))
            : content is JsonStringValue plain ? plain.Value : "";

    /// <summary>Reads a typed result: the last submit_result call's arguments, else JSON in the text (fenced or bare).</summary>
    internal static bool TryRead<T>(JsonObjectValue message, ResultTool result, JsonSerializerOptions options, out T value, out string error)
    {
        value = default!;
        JsonValue? candidate = null;
        if (message.TryGetValue("content", out var content) && content is JsonArrayValue blocks)
            candidate = blocks.OfType<JsonObjectValue>().LastOrDefault(block => Str(block, "type") == "toolCall" && Str(block, "name") == ResultTool.ToolName)
                is { } call && call.TryGetValue("arguments", out var arguments) ? arguments : null;
        var fromTool = candidate is not null;
        if (candidate is null)
        {
            var text = Text(message);
            var json = Fenced().Match(text) is { Success: true } fence ? fence.Groups[1].Value : Bare(text);
            if (json is null)
            {
                error = $"no {ResultTool.ToolName} call and no JSON in the answer";
                return false;
            }
            try { candidate = JsonValue.Parse(json); }
            catch (Exception parse) when (parse is JsonException or FormatException or ArgumentException)
            {
                error = "the JSON in the answer does not parse: " + parse.Message;
                return false;
            }
            // Text JSON may be the bare value of a wrapped type, or its {value} object.
            if (result.Schema.Wrapped && !(candidate is JsonObjectValue only && only.ContainsKey("value")))
                candidate = JsonValue.Object([new("value", candidate)]);
        }
        if (result.Schema.Wrapped)
        {
            if (candidate is not JsonObjectValue wrapper || !wrapper.TryGetValue("value", out var inner))
            {
                error = "the result has no 'value'";
                return false;
            }
            candidate = inner;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(candidate.ToJson(), options)!;
            if (value is null)
            {
                error = "the result is null";
                return false;
            }
            error = "";
            return true;
        }
        catch (JsonException parse)
        {
            error = (fromTool ? "the submitted result" : "the JSON in the answer") + " does not fit " + typeof(T).Name + ": " + parse.Message;
            return false;
        }
    }

    private static string? Bare(string text)
    {
        var start = text.IndexOfAny(['{', '[']);
        var end = text.LastIndexOfAny(['}', ']']);
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string? Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var item) && item is JsonStringValue text ? text.Value : null;

    [GeneratedRegex("```(?:json)?\\s*\\n([\\s\\S]*?)\\n?```")]
    private static partial Regex Fenced();

    /// <summary>The agent of a prompt child: every field set, because a task-owned conversation starts as a copy of its owner's agent.</summary>
    public static AgentChange PromptAgent(ModelRef? model, string? thinking, string? instructions, ResultTool? result) => new()
    {
        Model = model is null ? throw new ModelCallException("No model is configured for the prompt; set PromptOptions.Model, Workflow.WithModel or the catalog's DefaultModel.", "no_model") : Patch<ModelRef>.Set(model),
        ThinkingLevel = Patch<string>.Set(thinking ?? "off"),
        Instructions = instructions is null ? Patch<string>.Clear : Patch<string>.Set(instructions),
        Extensions = Patch<ExtensionSelection>.Set(ExtensionSelection.Only(result is null ? [] : [result.Extension])),
        Tools = Patch<ToolSelection>.Clear,
        ModelTools = Patch<ModelToolSelection>.Set(ModelToolSelection.Only(result is null ? [] : [result.Tool])),
    };

    public static string? Join(params string?[] parts) =>
        parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToImmutableArray() is { Length: > 0 } present ? string.Join("\n\n", present) : null;
}
