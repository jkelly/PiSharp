// Decision 0012: a workflow action in JavaScript. The script sees the Data as `data` (JSON, by the workflow's serializer) and the visit as
// `ctx` ({ workflow, action, step, attempt, visit, isReplay }), and returns what changes: `updates`, an RFC 7386 merge patch of the Data (a
// null removes a property), and the decision, `next` (an action or router name, or "#end"), `stop: true` or `fail: "<reason>"`; nothing
// returned continues with the next action unchanged. `output` and whatever the script prints go to the run's log. The patched Data is read
// back as TData by the engine, inside the same transition commit as the decision, so both happen exactly once. The script itself runs at
// least once, like any code action, and has no side effects of its own (it runs pure). A script that throws counts as a failed attempt
// (the action's retry policy applies).
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.JavaScript;

/// <summary>A JavaScript action failed: the script threw or returned something that is not a decision.</summary>
public sealed class JsActionException(string message) : Exception(message);

/// <summary>JavaScript steps for workflows.</summary>
public static class JsWorkflowExtensions
{
    /// <summary>Adds an action named <paramref name="name"/> that runs <paramref name="code"/> (an async function body over <c>data</c> and
    /// <c>ctx</c>, returning <c>{ updates?, next?, stop?, fail?, output? }</c>) on <paramref name="runtime"/> (default
    /// <see cref="JsRuntime.Shared"/>).</summary>
    public static WorkflowBuilder<TData> Script<TData>(this WorkflowBuilder<TData> builder, string name, string code, JsRuntime? runtime = null,
        ActionOptions? options = null) where TData : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (JsRuntime.CheckSyntax(code) is { } syntax)
            throw new WorkflowDefinitionException($"The script of action '{name}' does not parse: {syntax}");
        return builder.Add(new ActionStep<TData>(name, "script", new JsAction<TData>(name, code, runtime ?? JsRuntime.Shared), options ?? new ActionOptions()));
    }
}

/// <summary>Runs one JavaScript action.</summary>
internal sealed class JsAction<TData>(string name, string code, JsRuntime runtime) : IAction<TData> where TData : class
{
    public string Name => name;

    public async Task<ActionResult> ExecuteAsync(TData data, ActionContext context, CancellationToken cancellationToken)
    {
        var json = context.Scope.Codec.Serialize(data);
        var visit = JsonValue.Object([
            KeyValuePair.Create("workflow", (JsonValue)context.Workflow),
            KeyValuePair.Create("action", (JsonValue)context.Action),
            KeyValuePair.Create("step", (JsonValue)context.Step),
            KeyValuePair.Create("attempt", (JsonValue)context.Attempt),
            KeyValuePair.Create("visit", (JsonValue)context.Visit),
            KeyValuePair.Create("isReplay", (JsonValue)context.IsReplay),
        ]);
        var result = await runtime.RunAsync(code, [KeyValuePair.Create("data", json), KeyValuePair.Create("ctx", (JsonValue)visit)], cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok) throw new JsActionException($"The script of action '{name}' failed: {result.Error}");
        var decision = result.Value switch
        {
            null or JsonNullValue => JsonObjectValue.Empty,
            JsonObjectValue fields => fields,
            _ => throw new JsActionException($"The script of action '{name}' returned {result.ValueText}; return an object such as {{ updates, next }}."),
        };
        var log = string.Join("\n", new[] { result.Output, Text(decision, "output") }.Where(part => part.Length > 0));
        if (log.Length > 0) await context.LogAsync(log, cancellationToken).ConfigureAwait(false);
        var next = Decide(decision);
        return decision.TryGetValue("updates", out var updates) && updates is not JsonNullValue
            ? next with { ReplacedData = updates is JsonObjectValue ? MergePatch(json, updates) : throw Invalid("updates", "an object") }
            : next;
    }

    private ActionResult Decide(JsonObjectValue decision)
    {
        foreach (var key in decision.Keys)
            if (key is not ("updates" or "next" or "stop" or "fail" or "output"))
                throw new JsActionException($"The script of action '{name}' returned an unknown field '{key}'; use updates, next, stop, fail or output.");
        if (decision.TryGetValue("fail", out var fail) && fail is not JsonNullValue)
            return fail is JsonStringValue { Value.Length: > 0 } reason ? ActionResult.Fail(reason.Value) : throw Invalid("fail", "a reason");
        if (decision.TryGetValue("stop", out var stop) && stop is not JsonNullValue)
            return stop is JsonBooleanValue flag ? flag.Value ? ActionResult.Stop() : Next(decision) : throw Invalid("stop", "true or false");
        return Next(decision);
    }

    private ActionResult Next(JsonObjectValue decision) =>
        !decision.TryGetValue("next", out var next) || next is JsonNullValue ? ActionResult.Next()
        : next is JsonStringValue { Value.Length: > 0 } target ? ActionResult.GoTo(target.Value)
        : throw Invalid("next", "an action name");

    private JsActionException Invalid(string field, string expected) =>
        new($"The script of action '{name}' returned a '{field}' that is not {expected}.");

    private static string Text(JsonObjectValue decision, string key) => decision.TryGetValue(key, out var value) ? value switch
    {
        JsonNullValue => "",
        JsonStringValue text => text.Value,
        var other => other.ToJson(),
    } : "";

    /// <summary>RFC 7386: <paramref name="patch"/> applied to <paramref name="target"/>.</summary>
    internal static JsonValue MergePatch(JsonValue target, JsonValue patch)
    {
        if (patch is not JsonObjectValue fields) return patch;
        var merged = target is JsonObjectValue existing ? existing.ToList() : [];
        foreach (var (key, value) in fields)
        {
            var index = merged.FindIndex(property => property.Key == key);
            if (value is JsonNullValue)
            {
                if (index >= 0) merged.RemoveAt(index);
                continue;
            }
            var updated = MergePatch(index >= 0 ? merged[index].Value : JsonValue.Null, value);
            if (index >= 0) merged[index] = KeyValuePair.Create(key, updated);
            else merged.Add(KeyValuePair.Create(key, updated));
        }
        return JsonValue.Object(merged);
    }
}
