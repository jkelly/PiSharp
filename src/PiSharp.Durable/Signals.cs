// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/src/signals.ts. Signals: typed JSON values delivered to a live task
// from outside, kept in its memos until a wake uses them.
//
// Mappings: upstream types a token by its value (SignalToken<T>, SignalKey<T>, a phantom type); values here are JsonValue, so tokens and
// keys are not generic. A schema is a JSON Schema object (JsonObjectValue), compiled with pi-schema (PiSharp.Contracts.Schema) as a tool's
// structured output schema is; upstream's WeakMap of token internals is the token's own internal fields, and its "was not built by
// defineSignal()" check has no counterpart, as only DefineSignal builds a token. A value is immutable strict JSON, so upstream's isJsonValue
// trust-boundary check of delivered and migrated values has nothing to reject beyond the schema. SignalState is one record with Value or
// Expired. The memo of a key is upstream's JSON: {version, value, consumed?: true} or {expired: true}, under `pi.signal.<name>/<key>`.
using System.Collections.Immutable;
using PiSharp.Contracts.Schema;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;
using PiSharp.Durable.Internal.PiAi.Validation;

namespace PiSharp.Durable;

/// <summary>The key of one wait for a signal: JSON <c>{signal, key}</c>, used in sleeps and parks (upstream <c>SignalKey</c>).</summary>
/// <param name="Signal">The signal's name.</param>
/// <param name="Key">The key.</param>
public sealed record SignalKey(string Signal, string Key)
{
    /// <summary><c>{signal, key}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("signal", Signal).Add("key", Key).Build();

    /// <summary>Decodes <see cref="ToJson"/>; other fields are ignored, as upstream's phantom <c>~value</c> would be.</summary>
    public static SignalKey FromJson(JsonValue json)
    {
        const string what = "signal key";
        var value = RecordJson.Object(json, what);
        return new(RecordJson.String(value, "signal", what), RecordJson.String(value, "key", what));
    }
}

/// <summary>A value stored by an older version of a signal, to the current shape (upstream <c>migrate</c>).</summary>
public delegate JsonValue SignalMigration(JsonValue value, int fromVersion);

/// <summary>What <see cref="Signals.DefineSignal"/> takes (upstream <c>SignalDefinition</c>).</summary>
public sealed record SignalDefinition
{
    /// <summary>Unique like a task or document kind; must not contain <c>/</c>.</summary>
    public required string Name { get; init; }
    /// <summary>Positive integer version of the value shape.</summary>
    public required int Version { get; init; }
    /// <summary>A JSON Schema of the value, checked with pi-schema.</summary>
    public required JsonObjectValue Schema { get; init; }
    /// <summary>A value stored by an older version, to the current shape.</summary>
    public SignalMigration? Migrate { get; init; }
}

/// <summary>A signal kind, built by <see cref="Signals.DefineSignal"/>: values delivered under keys (upstream <c>SignalToken</c>).</summary>
public sealed class SignalToken
{
    private readonly JsonSchemaValidator validator;

    internal SignalToken(string name, int version, JsonSchemaValidator validator, SignalMigration? migrate)
    {
        Name = name;
        Version = version;
        this.validator = validator;
        Migrate = migrate;
    }

    /// <summary>The signal's name.</summary>
    public string Name { get; }
    /// <summary>The version of the value shape.</summary>
    public int Version { get; }
    internal SignalMigration? Migrate { get; }

    /// <summary>The key of one wait; throws for an empty key.</summary>
    public SignalKey Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentException($"Signal {Name} key must be non-empty", nameof(key));
        return new SignalKey(Name, key);
    }

    /// <summary>Whether <paramref name="value"/> matches the signal's schema.</summary>
    internal bool Check(JsonValue value) => validator.Check(SchemaValues.ToNode(value));

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>A key's state: the value delivered under it, or expired because its sleep's deadline came first (upstream
/// <c>SignalState</c>).</summary>
public sealed record SignalState
{
    private SignalState(JsonValue? value, bool expired) { Value = value; Expired = expired; }

    /// <summary><c>{value}</c>.</summary>
    public static SignalState Of(JsonValue value) => new(value ?? throw new ArgumentNullException(nameof(value)), false);
    /// <summary><c>{expired: true}</c>.</summary>
    public static SignalState ExpiredKey { get; } = new(null, true);

    /// <summary>The value delivered under the key, migrated to the token's version; null when the key expired.</summary>
    public JsonValue? Value { get; }
    /// <summary>Whether the key expired: its sleep's deadline came before a value.</summary>
    public bool Expired { get; }
}

/// <summary>
/// What a delivery did (upstream <c>SignalResult</c>): <c>delivered</c> (durably accepted, not necessarily consumed), <c>duplicate</c> (the
/// key holds a value), <c>invalid</c> (not matching the schema), <c>closed</c> (the task ended, holds its outcome, is abort-marked, or the
/// key expired), <c>not_found</c> (no such task).
/// </summary>
public enum SignalResult
{
    /// <summary><c>delivered</c>.</summary>
    Delivered,
    /// <summary><c>duplicate</c>.</summary>
    Duplicate,
    /// <summary><c>invalid</c>.</summary>
    Invalid,
    /// <summary><c>closed</c>.</summary>
    Closed,
    /// <summary><c>not_found</c>.</summary>
    NotFound,
}

/// <summary>Signal definitions and the built-in signal (upstream <c>signals.ts</c>).</summary>
public static class Signals
{
    /// <summary>Keywords whose value is a schema or an array of schemas. Annotations (<c>examples</c>, <c>default</c>) are not walked.</summary>
    private static readonly string[] SchemaKeywords =
    [
        "items", "prefixItems", "additionalItems", "unevaluatedItems", "contains", "anyOf", "oneOf", "allOf", "not", "if", "then", "else",
        "additionalProperties", "unevaluatedProperties", "propertyNames",
    ];

    /// <summary>Keywords whose value maps names to schemas; draft 7's <c>dependencies</c> may also map to name arrays, which hold none.</summary>
    private static readonly string[] SchemaMapKeywords = ["properties", "patternProperties", "dependentSchemas", "dependencies", "$defs", "definitions"];

    /// <summary>Defines a signal kind (upstream <c>defineSignal</c>). Throws for a name with <c>/</c>, a version that is not a positive
    /// integer, or a schema that admits non-JSON values.</summary>
    public static SignalToken DefineSignal(SignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = definition.Name;
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definition.Schema);
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
            throw new ArgumentException($"Signal name {((JsonValue)name).ToJson()} must be non-empty, without \"/\"");
        if (definition.Version < 1) throw new ArgumentException($"Signal {name} version must be a positive integer");
        if (NonJsonType(definition.Schema) is { } nonJson) throw new ArgumentException($"Signal {name} schema admits non-JSON values ({nonJson})");
        return new SignalToken(name, definition.Version, JsonSchemaValidator.Compile(SchemaValues.ToNode(definition.Schema)), definition.Migrate);
    }

    /// <summary>A non-JSON type a schema accepts anywhere (pi-schema's <c>bytes</c>, <c>bigint</c>, TypeBox's <c>undefined</c>), if any.</summary>
    private static string? NonJsonType(JsonValue? schema)
    {
        if (schema is not JsonObjectValue node) return null;
        IEnumerable<JsonValue> types = node.TryGetValue("type", out var type) ? type is JsonArrayValue listed ? listed : [type] : [];
        foreach (var each in types)
            if (each is JsonStringValue { Value: "bytes" or "bigint" or "undefined" } found) return found.Value;
        var children = new List<JsonValue>();
        foreach (var keyword in SchemaKeywords)
        {
            if (!node.TryGetValue(keyword, out var value)) continue;
            if (value is JsonArrayValue array) children.AddRange(array);
            else children.Add(value);
        }
        foreach (var keyword in SchemaMapKeywords)
            if (node.TryGetValue(keyword, out var value) && value is JsonObjectValue map) children.AddRange(map.Select(entry => entry.Value));
        foreach (var child in children)
            if (NonJsonType(child) is { } nested) return nested;
        return null;
    }

    /// <summary>
    /// Built in: delivered to <see cref="SubmissionDraft.Notify"/> when the submission settles, in the settling commit (upstream
    /// <c>SubmissionSettled</c>, <c>pi.submission-settled</c>). Its value is the settled record (<see cref="SubmissionRecord.ToJson"/>), what
    /// <c>Submission.WaitAsync()</c> resolves to; its schema checks each variant's required fields, so another delivery cannot take the key
    /// with something else.
    /// </summary>
    public static SignalToken SubmissionSettled { get; } = DefineSignal(new SignalDefinition
    {
        Name = "pi.submission-settled",
        Version = 1,
        Schema = JsonValue.Object([KeyValuePair.Create("anyOf", (JsonValue)JsonValue.Array(
            Settled("input", "done", ("entry", true), ("answer", true)),
            Settled("write", "done", ("entry", true)),
            Settled("continue", "done", ("answer", true)),
            Settled("input", "unanswered", ("entry", false), ("reason", true)),
            Settled("write", "unanswered", ("reason", true)),
            Settled("continue", "unanswered", ("reason", true))))]),
    });

    /// <summary>A settled record of <paramref name="type"/> and <paramref name="status"/> with <paramref name="fields"/> besides the common
    /// ones; other fields are allowed (upstream <c>settled</c>, an open pi-schema object).</summary>
    private static JsonObjectValue Settled(string type, string status, params (string Name, bool Required)[] fields)
    {
        static JsonObjectValue Of(string type) => JsonValue.Object([KeyValuePair.Create("type", (JsonValue)type)]);
        static JsonObjectValue Const(string value) => JsonValue.Object([KeyValuePair.Create("const", (JsonValue)value)]);
        var properties = new List<KeyValuePair<string, JsonValue>>
        {
            KeyValuePair.Create("id", (JsonValue)Of("integer")),
            KeyValuePair.Create("conversationId", (JsonValue)Of("integer")),
            KeyValuePair.Create("requestId", (JsonValue)Of("string")),
            KeyValuePair.Create("notify", (JsonValue)JsonValue.Object([
                KeyValuePair.Create("type", (JsonValue)"object"),
                KeyValuePair.Create("properties", (JsonValue)JsonValue.Object([
                    KeyValuePair.Create("taskId", (JsonValue)Of("integer")),
                    KeyValuePair.Create("key", (JsonValue)Of("string")),
                ])),
                KeyValuePair.Create("required", (JsonValue)JsonValue.Array("taskId", "key")),
                KeyValuePair.Create("additionalProperties", (JsonValue)false),
            ])),
            KeyValuePair.Create("type", (JsonValue)Const(type)),
            KeyValuePair.Create("status", (JsonValue)Const(status)),
        };
        var required = new List<JsonValue> { "id", "conversationId", "type", "status" };
        foreach (var (name, needed) in fields)
        {
            properties.Add(KeyValuePair.Create(name, (JsonValue)Of(name == "reason" ? "string" : "integer")));
            if (needed) required.Add(name);
        }
        return JsonValue.Object([
            KeyValuePair.Create("type", (JsonValue)"object"),
            KeyValuePair.Create("properties", (JsonValue)JsonValue.Object(properties)),
            KeyValuePair.Create("required", (JsonValue)JsonValue.Array(required)),
        ]);
    }

    /// <summary>Whether a submission settled: answered (<c>done</c>) or not (<c>unanswered</c>) (upstream <c>isSettled</c>).</summary>
    internal static bool IsSettled(SubmissionRecord record) => record.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered;

    /// <summary>The memo holding a key's state. Signal names contain no <c>/</c>, so names never collide (upstream <c>signalMemoName</c>).</summary>
    internal static string MemoName(string signal, string key) => $"pi.signal.{signal}/{key}";

    /// <summary>
    /// Decides a delivery to <paramref name="record"/> (spec suspendable §3.2; upstream <c>deliverSignal</c>): the result, and the record to
    /// write when it changes, with the value stored, or the key marked expired when the task sleeps on it past its deadline.
    /// </summary>
    internal static (SignalResult Result, TaskRecord? Record) Deliver(TaskRecord? record, SignalToken token, string key, JsonValue? value, double now)
    {
        if (record is null) return (SignalResult.NotFound, null);
        var state = record.State;
        if (state.Status is DurableTaskStatus.Terminal or DurableTaskStatus.Completing || record.AbortRequested) return (SignalResult.Closed, null);
        // Values come from outside: the trust boundary of the signal's schema.
        if (string.IsNullOrEmpty(key) || value is null || !token.Check(value)) return (SignalResult.Invalid, null);
        var name = MemoName(token.Name, key);
        if (MemoOf(record.Memos, name) is { } stored)
            return (stored.ContainsKey("expired") ? SignalResult.Closed : SignalResult.Duplicate, null);
        if (state is SleepingTaskState { Until: { } until, Signals: { } listed } && until <= now &&
            listed.Any(each => each.Signal == token.Name && each.Key == key))
            return (SignalResult.Closed, WithMemo(record, name, ExpiredMemo));
        var memo = new RecordJson.Writer().Number("version", token.Version).Add("value", value).Build();
        return (SignalResult.Delivered, WithMemo(record, name, memo));
    }

    /// <summary>
    /// A key's state in <paramref name="memos"/>, the value migrated to the token's version; null while nothing came (upstream
    /// <c>readSignal</c>). Throws for a stored version newer than the token's, a missing or throwing migration, or a migrated value the
    /// schema rejects.
    /// </summary>
    internal static SignalState? Read(JsonObjectValue? memos, SignalToken token, string key)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(key);
        if (MemoOf(memos, MemoName(token.Name, key)) is not { } stored) return null;
        if (stored.ContainsKey("expired")) return SignalState.ExpiredKey;
        var version = (int)((JsonNumberValue)stored["version"]).Value;
        var value = stored["value"];
        if (version == token.Version) return SignalState.Of(value);
        if (version > token.Version)
            throw new InvalidOperationException($"Signal {token.Name} value has version {version}, newer than {token.Version}");
        if (token.Migrate is null) throw new InvalidOperationException($"Signal {token.Name} has no migration from version {version}");
        var migrated = token.Migrate(value, version);
        if (migrated is null || !token.Check(migrated))
            throw new InvalidOperationException($"Signal {token.Name} migration from version {version} returned an invalid value");
        return SignalState.Of(migrated);
    }

    /// <summary>Throws unless a sleep waits for something (<c>until</c> or a non-empty <c>signals</c>) and none of its keys served a wait
    /// (upstream <c>checkSleep</c>).</summary>
    internal static void CheckSleep(SleepingTaskState state, JsonObjectValue? memos, TaskId task)
    {
        if (state.Until is null && state.Signals is not { Length: > 0 })
            throw new InvalidOperationException($"Task {task} cannot sleep without until or signals");
        foreach (var (signal, key) in state.Signals ?? [])
        {
            if (MemoOf(memos, MemoName(signal, key)) is not { } stored) continue;
            if (stored.ContainsKey("expired"))
                throw new InvalidOperationException($"Task {task} cannot sleep on signal {signal} key {key}: it expired");
            if (Consumed(stored))
                throw new InvalidOperationException($"Task {task} cannot sleep on signal {signal} key {key}: it already woke a wait");
        }
    }

    /// <summary>Whether a sleep can wake now: a listed key holds a value no wake used, or its deadline came (upstream <c>sleepDue</c>).</summary>
    internal static bool SleepDue(SleepingTaskState state, JsonObjectValue? memos, double now) =>
        ReadyKey(state, memos) is not null || (state.Until is { } until && until <= now);

    /// <summary>
    /// The wake of a sleep that can wake now, and the memos the waking reservation writes (upstream <c>sleepWake</c>): the first listed key
    /// holding a value no wake used, marked consumed; otherwise the deadline, with every listed key that holds nothing marked expired. Null
    /// when the sleep cannot wake yet.
    /// </summary>
    internal static (WakeReason Wake, ImmutableArray<KeyValuePair<string, JsonValue>> Memos)? SleepWake(SleepingTaskState state,
        JsonObjectValue? memos, double now)
    {
        if (ReadyKey(state, memos) is { } ready)
        {
            var name = MemoName(ready.Signal, ready.Key);
            var consumed = MemoOf(memos, name)!.SetItem("consumed", true);
            return (WakeReason.OfSignal(ready.Signal, ready.Key), [KeyValuePair.Create(name, (JsonValue)consumed)]);
        }
        if (state.Until is not { } until || until > now) return null;
        if (state.Signals is not { Length: > 0 } listed) return (WakeReason.Time, []);
        var expired = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        foreach (var (signal, key) in listed)
        {
            var name = MemoName(signal, key);
            if (MemoOf(memos, name) is null && !expired.Any(entry => entry.Key == name)) expired.Add(KeyValuePair.Create(name, (JsonValue)ExpiredMemo));
        }
        return (WakeReason.Expired, expired.ToImmutable());
    }

    /// <summary>The first listed key, in <c>signals</c> order, holding a value no wake used (upstream <c>readyKey</c>).</summary>
    private static SignalKey? ReadyKey(SleepingTaskState state, JsonObjectValue? memos)
    {
        foreach (var listed in state.Signals ?? [])
            if (MemoOf(memos, MemoName(listed.Signal, listed.Key)) is { } stored && !stored.ContainsKey("expired") && !Consumed(stored)) return listed;
        return null;
    }

    private static JsonObjectValue ExpiredMemo { get; } = JsonValue.Object([KeyValuePair.Create("expired", (JsonValue)true)]);

    private static bool Consumed(JsonObjectValue memo) => memo.TryGetValue("consumed", out var consumed) && consumed is JsonBooleanValue { Value: true };

    /// <summary><paramref name="record"/> with a memo added; signal memos are written only to live records (upstream <c>withMemos</c>).</summary>
    private static TaskRecord WithMemo(TaskRecord record, string name, JsonValue memo) =>
        record with { Memos = (record.Memos ?? JsonObjectValue.Empty).SetItem(name, memo) };

    private static JsonObjectValue? MemoOf(JsonObjectValue? memos, string name) =>
        memos is not null && memos.TryGetValue(name, out var memo) ? memo as JsonObjectValue : null;
}
