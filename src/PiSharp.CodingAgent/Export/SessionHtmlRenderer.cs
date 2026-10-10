using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;

namespace PiSharp.CodingAgent.Export;

/// <summary>Captured immutable data only. Null LeafId means no selected branch, not the physical leaf.</summary>
public sealed record SessionHtmlSnapshot(SessionEntry Header, ImmutableArray<SessionEntry> Entries,
    string? LeafId, string? SystemPrompt = null, JsonData? Tools = null);
public sealed record SessionHtmlRendererOptions(int MaximumEntries = 100_000,
    int MaximumInputCharacters = 16_777_216, int MaximumOutputBytes = 67_108_864);
public enum SessionHtmlRenderFailure { InvalidSnapshot, MissingLeaf, ResourceLimit }
public sealed class SessionHtmlRenderException(SessionHtmlRenderFailure failure) : Exception(failure switch
{
    SessionHtmlRenderFailure.MissingLeaf => "HTML export selected leaf does not exist.",
    SessionHtmlRenderFailure.ResourceLimit => "HTML export exceeds configured limits.",
    _ => "HTML export requires a complete validated session snapshot."
}) { public SessionHtmlRenderFailure Failure { get; } = failure; }
public sealed record SessionHtmlRenderResult(string Html, ImmutableArray<byte> Utf8Bytes,
    string? LeafId, int EntryCount);

/// <summary>Pure standalone, script-free HTML. Owns no writer, file, theme discovery, extension or session lifetime.</summary>
public sealed class SessionHtmlRenderer
{
    private readonly SessionHtmlRendererOptions options;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public SessionHtmlRenderer(SessionHtmlRendererOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MaximumEntries is < 1 or > 100_000 ||
            this.options.MaximumInputCharacters is < 1 or > 67_108_864 ||
            this.options.MaximumOutputBytes is < 1 or > 268_435_456)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public SessionHtmlRenderResult Render(SessionHtmlSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Header is null || !snapshot.Header.IsHeader || snapshot.Header.Id.Length == 0 || snapshot.Entries.IsDefault)
            throw Error(SessionHtmlRenderFailure.InvalidSnapshot);
        if (snapshot.Entries.Length > options.MaximumEntries) throw Error(SessionHtmlRenderFailure.ResourceLimit);
        if (snapshot.Tools is { } tools && tools.Value.ValueKind != JsonValueKind.Array)
            throw Error(SessionHtmlRenderFailure.InvalidSnapshot);
        long inputCharacters = snapshot.Header.WireBody.ToString().Length + (long)(snapshot.SystemPrompt?.Length ?? 0) +
            (snapshot.Tools?.ToString().Length ?? 0) + (snapshot.LeafId?.Length ?? 0);
        if (inputCharacters > options.MaximumInputCharacters) throw Error(SessionHtmlRenderFailure.ResourceLimit);
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null) throw Error(SessionHtmlRenderFailure.InvalidSnapshot);
            inputCharacters += entry.WireBody.ToString().Length;
            if (inputCharacters > options.MaximumInputCharacters) throw Error(SessionHtmlRenderFailure.ResourceLimit);
        }
        if (snapshot.SystemPrompt is { } prompt)
        {
            try { _ = Utf8.GetByteCount(prompt); }
            catch (EncoderFallbackException) { throw Error(SessionHtmlRenderFailure.InvalidSnapshot); }
        }
        SessionTreeSnapshot tree; ImmutableArray<SessionEntry> branch;
        try
        {
            tree = new SessionTreeQueries(new(new(MaximumEntries: options.MaximumEntries,
                MaximumAncestorSteps: options.MaximumEntries, MaximumInputCharacters: options.MaximumInputCharacters),
                MaximumQueryEntries: options.MaximumEntries)).Build(snapshot.Entries, cancellationToken);
            branch = tree.GetBranch(snapshot.LeafId, cancellationToken);
        }
        catch (SessionContextProjectionException error)
        { throw Error(error.Failure == SessionContextProjectionFailure.ResourceLimit ? SessionHtmlRenderFailure.ResourceLimit : SessionHtmlRenderFailure.InvalidSnapshot); }
        catch (SessionTreeQueryException error)
        { throw Error(error.Failure == SessionTreeQueryFailure.MissingEntry ? SessionHtmlRenderFailure.MissingLeaf : SessionHtmlRenderFailure.ResourceLimit); }

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < snapshot.Entries.Length; index++) positions.Add(snapshot.Entries[index].Id, index);
        var selected = branch.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var html = new StringBuilder(); long outputBytes = 0;
        Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>");
        Text(tree.SessionNameAvailable && tree.SessionName is { } name ? name : "Pi session " + snapshot.Header.Id);
        Append("</title><style>" + Css + "</style></head><body><header><h1>Pi session export</h1><dl>");
        Field("Session", snapshot.Header.Id); Field("Created", snapshot.Header.Timestamp);
        Field("Working directory", snapshot.Header.WireBody.Value.GetProperty("cwd").GetString());
        if (snapshot.Header.WireBody.Value.TryGetProperty("parentSession", out var parentSession)) Field("Parent session", Display(parentSession));
        Field("Selected leaf", snapshot.LeafId); Field("Physical leaf", tree.PhysicalLeafId);
        if (tree.SessionNameAvailable) Field("Name", tree.SessionName);
        Append("</dl><p>Static export. Text and Markdown are displayed verbatim. Images and custom renderer HTML are not activated.</p></header>");
        if (snapshot.SystemPrompt is not null) { Append("<details><summary>Captured system prompt</summary>"); Pre(snapshot.SystemPrompt); Append("</details>"); }
        if (snapshot.Tools is not null) { Append("<details><summary>Captured tool definitions</summary>"); Pre(snapshot.Tools.ToString()); Append("</details>"); }
        Append("<nav aria-label=\"Session branches\"><h2>Entries and branches</h2><ol>");
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested(); Append("<li");
            if (selected.Contains(entry.Id)) Append(" class=\"selected\"");
            Append("><a href=\"#" + Anchor(entry.Id) + "\">"); Text(entry.Id); Append("</a> "); Text(entry.Type);
            if (entry.ParentId is { } parent) { Append("; parent <a href=\"#" + Anchor(parent) + "\">"); Text(parent); Append("</a>"); }
            else Append("; root");
            if (tree.LabelsAvailable && tree.GetLabel(entry.Id) is { } label) { Append("; label "); Text(label.Label); }
            if (entry.Id == snapshot.LeafId) Append("; selected leaf");
            Append("</li>");
        }
        Append("</ol></nav><main><section id=\"selected-transcript\"><h2>Selected branch transcript</h2>");
        if (branch.IsEmpty) Append("<p>No selected entries.</p>");
        foreach (var entry in branch) RenderEntry(entry, true);
        Append("</section><section id=\"other-branches\"><h2>Other branch entries</h2>");
        foreach (var entry in snapshot.Entries) if (!selected.Contains(entry.Id)) RenderEntry(entry, false);
        Append("</section><details><summary>Complete captured JSONL archive</summary><pre id=\"session-jsonl\">");
        var codec = new SessionEntryCodec(new(MaximumRecordCharacters: options.MaximumInputCharacters, MaximumJsonDepth: PiSharp.Contracts.JsonData.MaximumDepth));
        Text(codec.Serialize(snapshot.Header)); Append("\n");
        foreach (var entry in snapshot.Entries) { cancellationToken.ThrowIfCancellationRequested(); Text(codec.Serialize(entry)); Append("\n"); }
        Append("</pre></details></main></body></html>\n");
        cancellationToken.ThrowIfCancellationRequested();
        var result = html.ToString(); var bytes = Utf8.GetBytes(result).ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(result, bytes, snapshot.LeafId, snapshot.Entries.Length);

        string Anchor(string id) => "entry-" + positions[id].ToString(CultureInfo.InvariantCulture);
        void Append(string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputBytes += Utf8.GetByteCount(value);
            if (outputBytes > options.MaximumOutputBytes) throw Error(SessionHtmlRenderFailure.ResourceLimit);
            html.Append(value);
        }
        void Text(string? value) => Append(WebUtility.HtmlEncode(value ?? "(none)")!.Replace("\r", "&#13;", StringComparison.Ordinal));
        void Pre(string? value) { Append("<pre><code>"); Text(value); Append("</code></pre>"); }
        void Field(string label, string? value) { Append("<dt>"); Text(label); Append("</dt><dd>"); Text(value); Append("</dd>"); }
        void RenderEntry(SessionEntry entry, bool onSelectedBranch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append("<article id=\"" + Anchor(entry.Id) + "\" class=\"entry" + (onSelectedBranch ? " selected" : "") + "\"><h3>");
            var body = entry.WireBody.Value;
            Text(entry.Kind == SessionEntryKind.Message ? body.GetProperty("message").GetProperty("role").GetString() : entry.Type);
            Append("</h3><dl>"); Field("Entry", entry.Id); Field("Parent", entry.ParentId); Field("Timestamp", entry.Timestamp); Append("</dl>");
            switch (entry.Kind)
            {
                case SessionEntryKind.Message: RenderMessage(body.GetProperty("message")); break;
                case SessionEntryKind.CustomMessage:
                    Append("<p>Custom message: "); Text(body.GetProperty("customType").GetString());
                    if (!body.GetProperty("display").GetBoolean()) Append(" (hidden in terminal)");
                    Append("</p>"); Content(body.GetProperty("content")); break;
                case SessionEntryKind.Compaction:
                    Append("<dl>"); Field("Tokens before compaction", Display(body.GetProperty("tokensBefore")));
                    Field("First kept entry", body.GetProperty("firstKeptEntryId").GetString()); Append("</dl>");
                    Pre(body.GetProperty("summary").GetString()); break;
                case SessionEntryKind.BranchSummary:
                    Append("<dl>"); Field("Abandoned branch", body.GetProperty("fromId").GetString()); Append("</dl>");
                    Pre(body.GetProperty("summary").GetString()); break;
                default: Pre(body.GetRawText()); break;
            }
            Append("</article>");
        }
        void RenderMessage(JsonElement message)
        {
            var role = message.GetProperty("role").GetString();
            if (role is not ("system" or "user" or "assistant" or "toolResult" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary"))
            { Pre(message.GetRawText()); return; }
            Append("<dl>");
            foreach (var field in new[] { "toolName", "toolCallId", "isError", "model", "stopReason", "exitCode", "cancelled", "truncated" })
                if (message.TryGetProperty(field, out var value)) Field(field, Display(value));
            Append("</dl>");
            if (role is "system" or "user" or "assistant" or "toolResult" or "custom") Content(message.GetProperty("content"));
            else if (role == "bashExecution")
            {
                Append("<h4>Command</h4>"); Pre(message.GetProperty("command").GetString());
                Append("<h4>Output</h4>"); Pre(message.GetProperty("output").GetString());
            }
            else if (message.TryGetProperty("summary", out var summary)) Pre(Display(summary));
            else Pre(message.GetRawText());
            if (message.TryGetProperty("errorMessage", out var error)) { Append("<h4>Error</h4>"); Pre(Display(error)); }
        }
        void Content(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) { Pre(content.GetString()); return; }
            if (content.ValueKind != JsonValueKind.Array) { Pre(content.GetRawText()); return; }
            foreach (var block in content.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!block.TryGetProperty("type", out var type)) { Pre(block.GetRawText()); continue; }
                switch (type.GetString())
                {
                    case "text": Pre(block.GetProperty("text").GetString()); break;
                    case "thinking":
                        Append("<details><summary>Thinking</summary>"); Pre(block.GetProperty("thinking").GetString()); Append("</details>"); break;
                    case "toolCall":
                        Append("<details open><summary>Tool call: "); Text(block.GetProperty("name").GetString());
                        Append(" (call "); Text(block.GetProperty("id").GetString()); Append(")</summary>");
                        Pre(block.GetProperty("arguments").GetRawText()); Append("</details>"); break;
                    case "image":
                        Append("<p>Image omitted from display: "); Text(block.GetProperty("mimeType").GetString());
                        Append(". Data retained in the captured archive.</p>"); break;
                    default: Pre(block.GetRawText()); break;
                }
            }
        }
    }

    private static string Display(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private static SessionHtmlRenderException Error(SessionHtmlRenderFailure failure) => new(failure);
    // Pinned index.ts fallback: #343541 userMessageBg, factors .7/.85, and RGB offsets +20/+15/+0.
    // This fixed fallback profile does not perform ambient named/system theme discovery.
    private const string Css = """
        :root{--exportPageBg:rgb(36,37,46);--exportCardBg:rgb(44,45,55);--exportInfoBg:rgb(72,68,65)}
        *{box-sizing:border-box}body{margin:0 auto;max-width:1100px;padding:24px;font:14px/1.5 ui-monospace,monospace;color:#e5e7eb;background:var(--exportPageBg)}
        header,nav,article,details{padding:16px;margin:12px 0;background:var(--exportCardBg);border:1px solid #626773;border-radius:6px}
        h1,h2,h3,h4{line-height:1.2}a{color:#a7c7ff}pre{white-space:pre-wrap;overflow-wrap:anywhere;margin:12px 0}dl{display:grid;grid-template-columns:minmax(120px,1fr) 4fr;gap:4px}dd{margin:0;overflow-wrap:anywhere}
        .selected{border-left:3px solid #b9a0ed}li.selected{background:var(--exportInfoBg)}:target{outline:2px solid #b9a0ed}summary{cursor:pointer}p,li{overflow-wrap:anywhere}
        """;
}
