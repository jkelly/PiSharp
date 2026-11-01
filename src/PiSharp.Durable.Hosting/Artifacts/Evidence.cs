// PiSharp-only (decision 0010): evidence references. An evidence record is an immutable pointer to retrieved content: where it came from,
// when, its content hash and parser, a bounded excerpt, and the blob holding the full body when there is one. Judgments and artifacts
// cite evidence by ID, and an ID only resolves within its own session.
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>Content to keep as evidence.</summary>
/// <param name="Url">An HTTP(S) URL, or null for content without one.</param>
/// <param name="Text">The full normalized text; kept in the blob store when it is longer than the excerpt.</param>
public sealed record EvidenceDraft(string Title, string? Url, string Text, string ContentType = "text/plain", string Parser = "text",
    string ParserVersion = "1", double? RetrievedAt = null, string? SourceId = null, string? ItemId = null);

/// <summary>Evidence records of a session.</summary>
public static class Evidence
{
    /// <summary>The longest excerpt kept in the record.</summary>
    public const int MaxExcerpt = 2000;

    /// <summary>The longest evidence text.</summary>
    public const int MaxText = 256 * 1024;

    /// <summary>The evidence ID of content: the same URL and content in a session is one record.</summary>
    public static string IdOf(string sessionId, string? url, string contentHash) => CanonicalJson.Id("ev_", sessionId, url ?? "", contentHash);

    /// <summary>Validates a draft; field errors use draft property names.</summary>
    internal static void Check(EvidenceDraft draft)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 300) errors.Add(new FieldError("/title", "must have 1 to 300 characters"));
        if (draft.Url is { } url && !Artifacts.IsSafeUrl(url)) errors.Add(new FieldError("/url", "must be an absolute http(s) URL of at most 2000 characters"));
        if (draft.Text.Length > MaxText) errors.Add(new FieldError("/text", $"may have at most {MaxText} characters"));
        if (draft.ContentType.Length is 0 or > 100) errors.Add(new FieldError("/contentType", "must have 1 to 100 characters"));
        if (draft.Parser.Length is 0 or > 50 || draft.ParserVersion.Length is 0 or > 20) errors.Add(new FieldError("/parser", "must name a parser and its version"));
        if (errors.Count > 0) throw HostValidationException.Of("Invalid evidence", errors);
    }

    /// <summary>Writes the evidence record in <paramref name="tx"/> unless it exists, and returns it. The blob, when the text needs one,
    /// must already be stored (<paramref name="blobHash"/>).</summary>
    internal static async Task<JsonObjectValue> WriteAsync(ITx tx, string sessionId, long? runTaskId, EvidenceDraft draft, string contentHash, string? blobHash,
        double now)
    {
        var id = IdOf(sessionId, draft.Url, contentHash);
        var doc = await tx.DocAsync(SessionRecords.EvidenceDoc, id, JsonValue.Null).ConfigureAwait(false);
        if (doc.Get("evidence") is JsonObjectValue existing) return existing;
        var record = JsonValue.Object(
        [
            new("id", id),
            new("sessionId", sessionId),
            new("runTaskId", runTaskId is { } task ? task : JsonValue.Null),
            new("title", draft.Title),
            new("url", draft.Url is { } url ? url : JsonValue.Null),
            new("retrievedAt", draft.RetrievedAt ?? now),
            new("contentType", draft.ContentType),
            new("contentHash", contentHash),
            new("parser", draft.Parser),
            new("parserVersion", draft.ParserVersion),
            new("excerpt", Excerpt(draft.Text)),
            new("length", draft.Text.Length),
            new("blobHash", blobHash is null ? JsonValue.Null : blobHash),
            new("sourceId", draft.SourceId is { } source ? source : JsonValue.Null),
            new("itemId", draft.ItemId is { } item ? item : JsonValue.Null),
            new("createdAt", now),
        ]);
        doc.Set("evidence", record);
        await SessionRecords.IndexAsync(tx, "evidence", id).ConfigureAwait(false);
        return record;
    }

    /// <summary>The content hash of evidence text and its parser.</summary>
    internal static string HashOf(EvidenceDraft draft) => CanonicalJson.HashText(draft.Parser + "/" + draft.ParserVersion + "\n" + draft.Text);

    /// <summary>Stores the text as a blob when it does not fit the excerpt; returns the blob hash or null.</summary>
    internal static string? StoreBody(DurableDeviceHost host, string sessionId, EvidenceDraft draft, string contentHash) =>
        draft.Text.Length <= MaxExcerpt ? null
            : host.Blobs.Put(Encoding.UTF8.GetBytes(draft.Text), draft.ContentType, $"evidence:{sessionId}:{IdOf(sessionId, draft.Url, contentHash)}").Hash;

    private static string Excerpt(string text) => text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "…";
}
