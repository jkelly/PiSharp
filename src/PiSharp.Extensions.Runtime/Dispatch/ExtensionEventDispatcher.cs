using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime.Dispatch;

public sealed record ExtensionEventDispatchOptions(int MaximumTextCharacters = 65_536,
    int MaximumJsonCharacters = 8 * 1024 * 1024, int MaximumJsonBytes = 32 * 1024 * 1024,
    int MaximumJsonDepth = 32, int MaximumImages = 128, int MaximumConcurrentDispatches = 32,
    int MaximumDispatchDepth = 8);

/// <summary>Typed event reducers. Complete tool-result admission is supplied by the host's pure codec.
/// This standalone dispatcher does not activate extensions, invoke tools, authorize calls, or persist entries.</summary>
public sealed class ExtensionEventDispatcher
{
    private readonly Action<JsonData> admitToolResult;
    private readonly ExtensionEventDispatchOptions options;
    private int activeDispatches;
    private readonly Func<ImmutableArray<TranscriptEntry>, CancellationToken, TranscriptEntry?>? restoreSystemMessage;
    public ExtensionReducerHandlerSet<ExtensionBeforeAgentStartEvent, ExtensionBeforeAgentStartPatch> BeforeAgentStartHandlers { get; } = new();
    public ExtensionReducerHandlerSet<ExtensionContextEvent, ExtensionContextMessagesPatch> ContextHandlers { get; } = new();
    private readonly Action<ImmutableArray<TranscriptEntry>>? admitContextMessages;
    public ExtensionReducerHandlerSet<ExtensionContextWithSystemEvent, ExtensionContextMessagesPatch> ContextWithSystemHandlers { get; } = new();
    public ExtensionReducerHandlerSet<ExtensionInputEvent, ExtensionInputPatch> InputHandlers { get; } = new();
    public ExtensionReducerHandlerSet<ExtensionToolCallEvent, ExtensionToolCallPatch> ToolCallHandlers { get; } = new();
    public ExtensionReducerHandlerSet<ExtensionToolResultEvent, ExtensionToolResultPatch> ToolResultHandlers { get; } = new();

    public ExtensionEventDispatcher(Action<JsonData> admitToolResult, ExtensionEventDispatchOptions? options = null,
        Action<ImmutableArray<TranscriptEntry>>? admitContextMessages = null,
        Func<ImmutableArray<TranscriptEntry>, CancellationToken, TranscriptEntry?>? restoreSystemMessage = null)
    {
        ArgumentNullException.ThrowIfNull(admitToolResult);
        this.admitToolResult = admitToolResult;
        this.admitContextMessages = admitContextMessages;
        this.restoreSystemMessage = restoreSystemMessage;
        this.options = options ?? new();
        if (this.options.MaximumTextCharacters <= 0 || this.options.MaximumJsonCharacters <= 0 ||
            this.options.MaximumJsonBytes <= 0 || this.options.MaximumJsonDepth is < 1 or > 64 ||
            this.options.MaximumImages <= 0 || this.options.MaximumConcurrentDispatches <= 0 ||
            this.options.MaximumDispatchDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async ValueTask<ExtensionBeforeAgentStartReduction> DispatchBeforeAgentStartAsync(
        ExtensionReducerSnapshot<ExtensionBeforeAgentStartEvent, ExtensionBeforeAgentStartPatch> snapshot,
        ExtensionBeforeAgentStartEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default)
    {
        BeforeAgentStartHandlers.Validate(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        using var dispatch = Enter("before_agent_start", operationCancellationToken, sessionCancellationToken);
        if (!Text(input.Prompt) || !Text(input.SystemPrompt)) throw new ArgumentException("Invalid prompt snapshot.");
        var current = input with { Images = Images(input.Images) };
        string? forced = null;
        var messages = ImmutableArray.CreateBuilder<ExtensionCustomMessage>();
        var admittedMessages = ImmutableArray<TranscriptEntry>.Empty;
        var diagnostics = ImmutableArray.CreateBuilder<ExtensionEventDiagnostic>();
        foreach (var entry in snapshot.Entries)
        {
            using var linked = Linked(entry.LifetimeCancellationToken, operationCancellationToken, sessionCancellationToken);
            ExtensionBeforeAgentStartPatch? patch;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                patch = await entry.Callback(current, Context(entry, operationCancellationToken, sessionCancellationToken), linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                linked.Token.ThrowIfCancellationRequested();
                diagnostics.Add(entry.Diagnostic("before_agent_start", ExtensionEventFailure.HandlerFailed)); continue;
            }
            if (patch is null) continue;
            try
            {
                if (patch.SystemPrompt is { } prompt && !Text(prompt)) throw new ArgumentException("Invalid forced prompt.");
                var nextMessages = admittedMessages;
                if (patch.Message is { } message)
                {
                    if (!Text(message.CustomType) || string.IsNullOrEmpty(message.CustomType)) throw new ArgumentException("Invalid custom type.");
                    var body = JsonData.Parse(JsonSerializer.Serialize(new
                    {
                        role = "custom", customType = message.CustomType, display = message.Display,
                        content = message.Content is null || message.Content.Value.ValueKind == JsonValueKind.Null ? JsonData.Parse("[]").Value : message.Content.Value,
                        details = message.Details?.Value, timestamp = 0
                    }));
                    nextMessages = AdmitMessages(admittedMessages.Add(new("custom", body)));
                }
                linked.Token.ThrowIfCancellationRequested();
                admittedMessages = nextMessages;
                if (patch.Message is { } accepted) messages.Add(accepted);
                if (patch.SystemPrompt is { } replacement)
                { forced = replacement; current = current with { SystemPrompt = replacement }; }
            }
            catch (Exception)
            {
                linked.Token.ThrowIfCancellationRequested();
                diagnostics.Add(entry.Diagnostic("before_agent_start", ExtensionEventFailure.InvalidResult));
            }
        }
        dispatch.ThrowIfCanceled();
        return new(messages.ToImmutable(), forced, diagnostics.ToImmutable());
    }

    public async ValueTask<ExtensionContextReduction> DispatchContextAsync(
        ExtensionReducerSnapshot<ExtensionContextEvent, ExtensionContextMessagesPatch> snapshot,
        ExtensionContextEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default)
    {
        ContextHandlers.Validate(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        using var dispatch = Enter("context", operationCancellationToken, sessionCancellationToken);
        var current = AdmitMessages(input.Messages);
        if (!snapshot.Entries.IsEmpty && restoreSystemMessage is null)
            throw new InvalidOperationException("Conversation context hooks require host system-state replay.");
        var diagnostics = ImmutableArray.CreateBuilder<ExtensionEventDiagnostic>();
        foreach (var entry in snapshot.Entries)
        {
            using var linked = Linked(entry.LifetimeCancellationToken, operationCancellationToken, sessionCancellationToken);
            var visible = current.Where(message => message.Role != "system").ToImmutableArray();
            ExtensionContextMessagesPatch? patch;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                patch = await entry.Callback(new(visible), Context(entry, operationCancellationToken, sessionCancellationToken), linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                linked.Token.ThrowIfCancellationRequested();
                diagnostics.Add(entry.Diagnostic("context", ExtensionEventFailure.HandlerFailed)); continue;
            }
            if (patch?.Messages is not { } replacement) continue;
            try
            {
                var admitted = AdmitMessages(replacement);
                // Source compares message identities, not serialized equality. An unchanged list keeps mid-turn system positions.
                if (admitted.Length == visible.Length && admitted.Zip(visible).All(pair => ReferenceEquals(pair.First, pair.Second))) continue;
                var head = restoreSystemMessage!(current, linked.Token);
                var restored = head is null ? admitted : admitted.Insert(0, head);
                restored = AdmitMessages(restored);
                linked.Token.ThrowIfCancellationRequested();
                current = restored;
            }
            catch (Exception)
            {
                linked.Token.ThrowIfCancellationRequested();
                diagnostics.Add(entry.Diagnostic("context", ExtensionEventFailure.InvalidResult));
            }
        }
        dispatch.ThrowIfCanceled();
        return new(current, diagnostics.ToImmutable());
    }

    public async ValueTask<ExtensionContextReduction> DispatchContextWithSystemAsync(
        ExtensionReducerSnapshot<ExtensionContextWithSystemEvent, ExtensionContextMessagesPatch> snapshot,
        ExtensionContextWithSystemEvent input, CancellationToken operationCancellationToken = default,
        CancellationToken sessionCancellationToken = default)
    {
        ContextWithSystemHandlers.Validate(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        using var dispatch = Enter("context_with_system", operationCancellationToken, sessionCancellationToken);
        var current = AdmitMessages(input.Messages);
        var diagnostics = ImmutableArray.CreateBuilder<ExtensionEventDiagnostic>();
        foreach (var entry in snapshot.Entries)
        {
            using var linked = Linked(entry.LifetimeCancellationToken, operationCancellationToken, sessionCancellationToken);
            ExtensionContextMessagesPatch? patch;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                patch = await entry.Callback(new(current), Context(entry, operationCancellationToken, sessionCancellationToken), linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                linked.Token.ThrowIfCancellationRequested();
                diagnostics.Add(entry.Diagnostic("context_with_system", ExtensionEventFailure.HandlerFailed)); continue;
            }
            if (patch?.Messages is not { } replacement) continue;
            try
            {
                var admitted = AdmitMessages(replacement);
                linked.Token.ThrowIfCancellationRequested();
                var hadSystem = !current.IsEmpty && current[0].Role == "system";
                current = admitted;
                if (hadSystem && (current.IsEmpty || current[0].Role != "system"))
                    diagnostics.Add(entry.Diagnostic("context_with_system", ExtensionEventFailure.LeadingSystemRemoved));
            }
            catch (Exception)
            {
                linked.Token.ThrowIfCancellationRequested();
                diagnostics.Add(entry.Diagnostic("context_with_system", ExtensionEventFailure.InvalidResult));
            }
        }
        dispatch.ThrowIfCanceled();
        return new(current, diagnostics.ToImmutable());
    }

    private ImmutableArray<TranscriptEntry> AdmitMessages(ImmutableArray<TranscriptEntry> messages)
    {
        if (admitContextMessages is null) throw new InvalidOperationException("Context message admission requires a host validator.");
        if (messages.IsDefault || messages.Length > 1024) throw new ArgumentException("Invalid context message count.");
        long characters = 0, bytes = 0;
        foreach (var message in messages)
        {
            if (message?.WireBody is null) throw new ArgumentException("Invalid context message.");
            var raw = message.WireBody.ToString();
            characters += raw.Length; bytes += Encoding.UTF8.GetByteCount(raw);
            if (characters > options.MaximumJsonCharacters || bytes > options.MaximumJsonBytes)
                throw new ArgumentException("Context messages exceed aggregate limits.");
            _ = Strict(message.WireBody, requireObject: true);
        }
        admitContextMessages(messages);
        return messages;
    }

    public async ValueTask<ExtensionInputReduction> DispatchInputAsync(
        ExtensionReducerSnapshot<ExtensionInputEvent, ExtensionInputPatch> snapshot, ExtensionInputEvent input,
        CancellationToken operationCancellationToken = default, CancellationToken sessionCancellationToken = default)
    {
        InputHandlers.Validate(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        using var dispatch = Enter("input", operationCancellationToken, sessionCancellationToken);
        if (!Enum.IsDefined(input.Source) || !Text(input.Text) ||
            input.StreamingBehavior is not (null or "steer" or "followUp")) throw new ArgumentException("Invalid input event.");
        var original = input with { Images = Images(input.Images) };
        var current = original;
        var diagnostics = ImmutableArray.CreateBuilder<ExtensionEventDiagnostic>();
        foreach (var entry in snapshot.Entries)
        {
            using var linked = Linked(entry.LifetimeCancellationToken, operationCancellationToken, sessionCancellationToken);
            var context = Context(entry, operationCancellationToken, sessionCancellationToken);
            ExtensionInputPatch? patch;
            try { linked.Token.ThrowIfCancellationRequested(); patch = await entry.Callback(current, context, linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); }
            catch (Exception) { linked.Token.ThrowIfCancellationRequested(); diagnostics.Add(entry.Diagnostic("input", ExtensionEventFailure.HandlerFailed)); continue; }
            if (patch is null) continue;
            try
            {
                if (!Enum.IsDefined(patch.Action)) throw new ArgumentException("Invalid input action.");
                if (patch.Action == ExtensionInputAction.Handled)
                    return new(ExtensionInputAction.Handled, current, diagnostics.ToImmutable());
                if (patch.Action != ExtensionInputAction.Transform) continue;
                if (!Text(patch.Text)) throw new ArgumentException("Invalid input transform.");
                var images = patch.Images is null || patch.Images.Value.ValueKind == JsonValueKind.Null ? current.Images :
                    ReferenceEquals(patch.Images, current.Images) ? current.Images : Images(patch.Images);
                current = current with { Text = patch.Text!, Images = images };
            }
            catch (Exception) { linked.Token.ThrowIfCancellationRequested(); diagnostics.Add(entry.Diagnostic("input", ExtensionEventFailure.InvalidResult)); }
        }
        dispatch.ThrowIfCanceled();
        return new(current.Text != original.Text || !ReferenceEquals(current.Images, original.Images)
            ? ExtensionInputAction.Transform : ExtensionInputAction.Continue, current, diagnostics.ToImmutable());
    }

    public async ValueTask<ExtensionToolCallReduction> DispatchToolCallAsync(
        ExtensionReducerSnapshot<ExtensionToolCallEvent, ExtensionToolCallPatch> snapshot, ExtensionToolCallEvent input,
        CancellationToken operationCancellationToken = default, CancellationToken sessionCancellationToken = default)
    {
        ToolCallHandlers.Validate(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        using var dispatch = Enter("tool_call", operationCancellationToken, sessionCancellationToken);
        CallIdentity(input.ToolName, input.ToolCallId, input.ParentToolCallId);
        var current = input with { Arguments = Strict(input.Arguments, requireObject: true) };
        JsonData? decision = null;
        var replaced = false;
        foreach (var entry in snapshot.Entries)
        {
            using var linked = Linked(entry.LifetimeCancellationToken, operationCancellationToken, sessionCancellationToken);
            ExtensionToolCallPatch? patch;
            try { linked.Token.ThrowIfCancellationRequested(); patch = await entry.Callback(current, Context(entry, operationCancellationToken, sessionCancellationToken), linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); }
            catch (Exception error) { linked.Token.ThrowIfCancellationRequested(); throw new ExtensionEventDispatchException(entry.Diagnostic("tool_call", ExtensionEventFailure.HandlerFailed), error); }
            if (patch is null) continue;
            try
            {
                // Validate the whole proposed patch before committing either replacement or decision.
                var arguments = patch.Arguments is null ? current.Arguments : Strict(patch.Arguments, requireObject: true);
                var proposed = patch.Decision is null ? null : Decision(patch.Decision);
                linked.Token.ThrowIfCancellationRequested();
                if (patch.Arguments is not null) { current = current with { Arguments = arguments }; replaced = true; }
                if (proposed is not null) decision = proposed;
                if (decision is not null && decision.Value.TryGetProperty("block", out var block) && block.ValueKind == JsonValueKind.True)
                    return new(current, decision, replaced);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception error) { linked.Token.ThrowIfCancellationRequested(); throw new ExtensionEventDispatchException(entry.Diagnostic("tool_call", ExtensionEventFailure.InvalidResult), error); }
        }
        dispatch.ThrowIfCanceled();
        return new(current, decision, replaced);
    }

    public async ValueTask<ExtensionToolResultReduction> DispatchToolResultAsync(
        ExtensionReducerSnapshot<ExtensionToolResultEvent, ExtensionToolResultPatch> snapshot, ExtensionToolResultEvent input,
        CancellationToken operationCancellationToken = default, CancellationToken sessionCancellationToken = default)
    {
        ToolResultHandlers.Validate(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        using var dispatch = Enter("tool_result", operationCancellationToken, sessionCancellationToken);
        CallIdentity(input.ToolName, input.ToolCallId, input.ParentToolCallId);
        var owned = Strict(input.Result, requireObject: true);
        admitToolResult(owned);
        var current = input with { Arguments = Strict(input.Arguments, requireObject: true), Result = owned };
        var modified = false;
        var patches = ImmutableArray.CreateBuilder<JsonData>();
        var diagnostics = ImmutableArray.CreateBuilder<ExtensionEventDiagnostic>();
        foreach (var entry in snapshot.Entries)
        {
            using var linked = Linked(entry.LifetimeCancellationToken, operationCancellationToken, sessionCancellationToken);
            ExtensionToolResultPatch? patch;
            try { linked.Token.ThrowIfCancellationRequested(); patch = await entry.Callback(current, Context(entry, operationCancellationToken, sessionCancellationToken), linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); }
            catch (Exception) { linked.Token.ThrowIfCancellationRequested(); diagnostics.Add(entry.Diagnostic("tool_result", ExtensionEventFailure.HandlerFailed)); continue; }
            if (patch is null) continue;
            try
            {
                var fields = Strict(patch.Fields, requireObject: true);
                var next = ApplyResult(current.Result, fields, out var changed);
                admitToolResult(next);
                linked.Token.ThrowIfCancellationRequested();
                patches.Add(fields);
                if (changed) { current = current with { Result = next }; modified = true; }
            }
            catch (Exception) { linked.Token.ThrowIfCancellationRequested(); diagnostics.Add(entry.Diagnostic("tool_result", ExtensionEventFailure.InvalidResult)); }
        }
        dispatch.ThrowIfCanceled();
        return new(current, modified, patches.ToImmutable(), diagnostics.ToImmutable());
    }

    private JsonData ApplyResult(JsonData current, JsonData patch, out bool changed)
    {
        string[] consumed = ["content", "details", "structuredContent", "isError", "usage"];
        var replacements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in consumed) if (patch.Value.TryGetProperty(name, out var value)) replacements.Add(name, value);
        changed = replacements.Count != 0;
        if (!changed) return current;
        if (replacements.TryGetValue("isError", out var error) && error.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw new ArgumentException("Invalid error patch.");
        var clearStructured = replacements.ContainsKey("content") && !replacements.ContainsKey("structuredContent");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in current.Value.EnumerateObject())
            {
                if (clearStructured && property.Name == "structuredContent") continue;
                writer.WritePropertyName(property.Name);
                writer.WriteRawValue(replacements.Remove(property.Name, out var replacement) ? replacement.GetRawText() : property.Value.GetRawText());
            }
            foreach (var name in consumed) if (replacements.TryGetValue(name, out var value))
            { writer.WritePropertyName(name); writer.WriteRawValue(value.GetRawText()); }
            writer.WriteEndObject();
        }
        return Strict(JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray())), requireObject: true);
    }
    private JsonData Decision(JsonData value)
    {
        var owned = Strict(value, requireObject: true);
        foreach (var name in new[] { "block", "terminate" })
            if (owned.Value.TryGetProperty(name, out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new ArgumentException("Invalid tool-call flag.");
        if (owned.Value.TryGetProperty("reason", out var reason) && reason.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException("Invalid tool-call reason.");
        return owned;
    }
    private JsonData? Images(JsonData? value)
    {
        if (value is null) return null;
        var owned = Strict(value);
        if (owned.Value.ValueKind == JsonValueKind.Null) return owned;
        if (owned.Value.ValueKind != JsonValueKind.Array || owned.Value.GetArrayLength() > options.MaximumImages)
            throw new ArgumentException("Invalid input images.");
        foreach (var image in owned.Value.EnumerateArray())
            if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "image" ||
                !image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String || !image.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Invalid input image envelope.");
        return owned;
    }
    private JsonData Strict(JsonData value, bool requireObject = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var raw = value.ToString();
        if (raw.Length > options.MaximumJsonCharacters || Encoding.UTF8.GetByteCount(raw) > options.MaximumJsonBytes)
            throw new ArgumentException("JSON limit exceeded.");
        using var strict = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = options.MaximumJsonDepth });
        var result = JsonData.Parse(raw);
        if (requireObject && result.Value.ValueKind != JsonValueKind.Object || !JsonScalars(result.Value))
            throw new ArgumentException("Invalid JSON value.");
        return result;
    }
    private static bool JsonScalars(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property => Scalars(property.Name) && JsonScalars(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(JsonScalars),
        JsonValueKind.String => Scalars(value.GetString()),
        JsonValueKind.Number => value.TryGetDouble(out var number) && double.IsFinite(number),
        _ => value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
    };
    private bool Text(string? value) => value is not null && value.Length <= options.MaximumTextCharacters && Scalars(value);
    private static bool Scalars(string? value)
    {
        if (value is null) return false;
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }
    private static void CallIdentity(string name, string id, string? parent)
    {
        foreach (var value in new[] { name, id, parent })
            if (value is not null && (value.Length is 0 or > 256 || value.Contains('\0') || !Scalars(value)))
                throw new ArgumentException("Invalid tool call identity.");
        if (name is null || id is null) throw new ArgumentException("Missing tool call identity.");
    }
    private static CancellationTokenSource Linked(CancellationToken lifetime, CancellationToken operation, CancellationToken session) =>
        CancellationTokenSource.CreateLinkedTokenSource(lifetime, operation, session);
    private static ReducerContext Context<TEvent, TPatch>(ReducerRegistration<TEvent, TPatch> entry,
        CancellationToken operation, CancellationToken session) where TPatch : class =>
        new(entry.OwnerId, entry.OwnerGeneration, operation, session, entry.LifetimeCancellationToken);
    private DispatchLease Enter(string name, CancellationToken operation, CancellationToken session)
    {
        operation.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested();
        var depth = DispatchLease.Depth(this) + 1;
        if (depth > options.MaximumDispatchDepth) throw DispatchLimit(name, ExtensionEventFailure.ReentrantLimit);
        if (Interlocked.Increment(ref activeDispatches) > options.MaximumConcurrentDispatches)
        { Interlocked.Decrement(ref activeDispatches); throw DispatchLimit(name, ExtensionEventFailure.LimitExceeded); }
        return new(this, operation, session);
    }
    private static ExtensionEventDispatchException DispatchLimit(string name, ExtensionEventFailure failure) =>
        new(new(name, "host", 0, "dispatch", failure));
    private sealed class DispatchLease : IDisposable
    {
        private static readonly AsyncLocal<DispatchLease?> current = new();
        private readonly ExtensionEventDispatcher dispatcher;
        private readonly DispatchLease? parent;
        private readonly CancellationToken operation, session;
        private bool active = true;
        internal DispatchLease(ExtensionEventDispatcher dispatcher, CancellationToken operation, CancellationToken session)
        { this.dispatcher = dispatcher; this.operation = operation; this.session = session; parent = current.Value; current.Value = this; }
        internal static int Depth(ExtensionEventDispatcher dispatcher)
        {
            var depth = 0;
            for (var frame = current.Value; frame is not null; frame = frame.parent)
                if (Volatile.Read(ref frame.active) && ReferenceEquals(frame.dispatcher, dispatcher)) depth++;
            return depth;
        }
        internal void ThrowIfCanceled() { operation.ThrowIfCancellationRequested(); session.ThrowIfCancellationRequested(); }
        public void Dispose() { Volatile.Write(ref active, false); current.Value = parent; Interlocked.Decrement(ref dispatcher.activeDispatches); }
    }
}
