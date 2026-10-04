using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Context;

/// <summary>Null replacement omits a contribution. An owned object carries exact content and wrapper fields.</summary>
public sealed record SessionContextEditDraft(string TargetId, JsonData? Replacement);
public enum SessionContextEditFailure
{
    InvalidReplacement, TargetNotFound, TargetNotActive, TargetNotEditable, UnsupportedReplacement, ResourceLimit
}
public sealed class SessionContextEditException : Exception
{
    public SessionContextEditFailure Failure { get; }
    public SessionContextEditException(SessionContextEditFailure failure) : base(failure switch
    {
        SessionContextEditFailure.InvalidReplacement => "Context edit replacement must be null or contain string/array content.",
        SessionContextEditFailure.TargetNotFound => "Context edit target was not found.",
        SessionContextEditFailure.TargetNotActive => "Context edit target is not on the active branch.",
        SessionContextEditFailure.TargetNotEditable => "Context edit target does not contribute editable model content.",
        SessionContextEditFailure.ResourceLimit => "Context edit exceeds configured session bounds.",
        _ => "Context edit replacement is outside the supported native record profile."
    }) => Failure = failure;
}

/// <summary>Active manager admission only. Imported stored-edit replay deliberately has different rules.</summary>
public static class SessionContextEditValidator
{
    public static JsonData Normalize(SessionContextProjection current, SessionContextEditDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        if (draft is null) throw new SessionContextEditException(SessionContextEditFailure.InvalidReplacement);
        var replacement = draft.Replacement ?? JsonData.Null;
        var body = replacement.Value;
        if (body.ValueKind != JsonValueKind.Null && (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("content", out var content) || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Array)))
            throw new SessionContextEditException(SessionContextEditFailure.InvalidReplacement);
        if (draft.TargetId is null || !current.ById.TryGetValue(draft.TargetId, out var target))
            throw new SessionContextEditException(SessionContextEditFailure.TargetNotFound);
        if (!current.Ancestry.Any(entry => entry.Id == draft.TargetId))
            throw new SessionContextEditException(SessionContextEditFailure.TargetNotActive);
        var role = target.Kind == SessionEntryKind.CustomMessage ? "custom" :
            target.Kind == SessionEntryKind.Message ? target.WireBody.Value.GetProperty("message").GetProperty("role").GetString() : null;
        if (role is not ("user" or "assistant" or "toolResult" or "custom") ||
            target.Kind == SessionEntryKind.Message && role == "custom")
            throw new SessionContextEditException(SessionContextEditFailure.TargetNotEditable);
        cancellationToken.ThrowIfCancellationRequested();
        // The pinned manager creates a NEW content-only wrapper for assistant/tool-result strings.
        // User/custom strings and all content arrays retain the supplied owned replacement object.
        if (body.ValueKind != JsonValueKind.Null && role is ("assistant" or "toolResult") &&
            body.GetProperty("content").ValueKind == JsonValueKind.String)
            return JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":" + body.GetProperty("content").GetRawText() + "}]}");
        return replacement;
    }
}
