using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Events;

/// <summary>Experimental typed reducer profile; not the approved extension ABI.</summary>
public static class ExperimentalEventContract
{
    public const string Profile = "experimental-input-tool-reducers-0";
}

/// <summary>Immutable full request context. Replacement affects only the outgoing request, never history.</summary>
public sealed record ExtensionContextEvent(ImmutableArray<TranscriptEntry> Messages);
public sealed record ExtensionContextWithSystemEvent(ImmutableArray<TranscriptEntry> Messages);
/// <summary>Null leaves context unchanged; an empty initialized array explicitly removes all messages.</summary>
public sealed record ExtensionContextMessagesPatch(ImmutableArray<TranscriptEntry>? Messages = null);
public sealed record ExtensionContextReduction(ImmutableArray<TranscriptEntry> Messages,
    ImmutableArray<ExtensionEventDiagnostic> Diagnostics);

/// <summary>Immutable native prompt snapshot. Rich mutable BuildSystemPromptOptions are not exposed.</summary>
public sealed record ExtensionBeforeAgentStartEvent(string Prompt, string SystemPrompt, JsonData? Images = null);
public sealed record ExtensionCustomMessage(string CustomType, bool Display, JsonData? Content = null, JsonData? Details = null);
/// <summary>Null SystemPrompt retains the prior prompt; an empty string is an explicit override.</summary>
public sealed record ExtensionBeforeAgentStartPatch(ExtensionCustomMessage? Message = null, string? SystemPrompt = null);
public sealed record ExtensionBeforeAgentStartReduction(ImmutableArray<ExtensionCustomMessage> Messages,
    string? ForcedSystemPrompt, ImmutableArray<ExtensionEventDiagnostic> Diagnostics);

public enum ExtensionInputSource { Interactive, Rpc, Extension }
public enum ExtensionInputAction { Continue, Transform, Handled }

/// <summary>Null Images means absent; JsonData.Null is explicit JSON null. Values are immutable.</summary>
public sealed record ExtensionInputEvent(string Text, ExtensionInputSource Source,
    JsonData? Images = null, string? StreamingBehavior = null);

/// <summary>Transform images use the source nullish rule: missing/null retains current images.</summary>
public sealed record ExtensionInputPatch(ExtensionInputAction Action, string? Text = null, JsonData? Images = null);
public sealed record ExtensionInputReduction(ExtensionInputAction Action, ExtensionInputEvent Event,
    ImmutableArray<ExtensionEventDiagnostic> Diagnostics);

public sealed record ExtensionToolCallEvent(string ToolName, string ToolCallId, JsonData Arguments,
    string? ParentToolCallId = null);

/// <summary>Arguments is an explicit replacement for Pi's in-place input mutation.
/// Decision is a source-shaped raw result object; absence is a null CLR reference.</summary>
public sealed record ExtensionToolCallPatch(JsonData? Arguments = null, JsonData? Decision = null);
public sealed record ExtensionToolCallReduction(ExtensionToolCallEvent Event, JsonData? Decision,
    bool ArgumentsReplaced)
{
    public bool Blocked => Decision is not null && Decision.Value.TryGetProperty("block", out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.True;
}

/// <summary>Result retains the complete tool result, including unknown properties and raw numeric spelling.</summary>
public sealed record ExtensionToolResultEvent(string ToolName, string ToolCallId, JsonData Arguments,
    JsonData Result, string? ParentToolCallId = null, bool OutcomeIsError = false);

/// <summary>Raw patch presence matters. Only content/details/structuredContent/isError/usage reduce state.</summary>
public sealed record ExtensionToolResultPatch(JsonData Fields);
public sealed record ExtensionToolResultReduction(ExtensionToolResultEvent Event, bool Modified,
    ImmutableArray<JsonData> ReturnedPatches, ImmutableArray<ExtensionEventDiagnostic> Diagnostics);

public delegate ValueTask<TPatch?> ExtensionReducerCallback<TEvent, TPatch>(
    TEvent snapshot, IExtensionContext context, CancellationToken cancellationToken) where TPatch : class;

public enum ExtensionEventFailure { HandlerFailed, InvalidResult, LimitExceeded, ReentrantLimit, LeadingSystemRemoved }

/// <summary>Host-authored diagnostic codes; no plugin exception text or payload is copied into diagnostics.</summary>
public sealed record ExtensionEventDiagnostic(string EventName, string OwnerId, long OwnerGeneration,
    string RegistrationId, ExtensionEventFailure Failure);

/// <summary>Tool-call hook failure blocks the call. A requested cancellation remains cancellation.</summary>
public sealed class ExtensionEventDispatchException : Exception
{
    public ExtensionEventDiagnostic Diagnostic { get; }
    public ExtensionEventDispatchException(ExtensionEventDiagnostic diagnostic, Exception? innerException = null)
        : base($"Extension event '{diagnostic.EventName}' failed: {diagnostic.Failure}.", innerException) =>
        Diagnostic = diagnostic;
}
