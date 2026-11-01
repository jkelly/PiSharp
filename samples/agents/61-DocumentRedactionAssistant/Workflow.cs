// PiSharp-only (no upstream Pi source): bounded text redaction with explicit export approval.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.DocumentRedactionAssistant;

public static class Workflow
{
    internal sealed record Span(int Start, int Length, string Kind, string Value);
    internal sealed record Redaction(string Id, string Hash, string Text, Span[] Matches, Span[] Merged);
    private static readonly (string Kind, Regex Pattern)[] Patterns = [
        ("email", new(@"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+\.[\p{L}]{2,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))),
        ("ssn", new(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))),
        ("phone", new(@"(?<!\d)(?:\+1[ -]?)?\(?\d{3}\)?[ -]\d{3}[ -]\d{4}(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
    ];
    private static JsonObject Schema() => SampleJson.Schema("{\"documentId\":{\"type\":\"string\"}}", "documentId");
    internal static JsonObject Args(string id) => new() { ["documentId"] = id };
    public static WorkflowSpec Create(SampleContext c) => new("61", "Document redaction assistant",
        "Detect supported sensitive patterns, preview sanitized text, then save a redaction review for letter-1. When the host grants approval, also call export_redacted with the current preview expectedHash as requested by the fixture. Source text is plain text only: no OCR, document binaries, metadata or embedded images are processed. Never claim exhaustive PII removal. Without approval, stop at the saved review.",
        [new("detect_sensitive", "Detect email, SSN, phone and supplied literal spans without returning their values", Schema(), (a, _) => Task.FromResult<JsonNode?>(Detect(c, a.Text("documentId")))),
         new("preview_redaction", "Merge overlapping sensitive spans and preview sanitized plain text", Schema(), (a, _) => Task.FromResult<JsonNode?>(Preview(c, a.Text("documentId")))),
         new("save_redaction_review", "Persist the sanitized preview and detection metadata for human review", Schema(), async (a, ct) =>
         {
             var id = a.Text("documentId"); var report = Preview(c, id); var path = "redaction-review-" + id + ".json";
             await c.WriteJsonAsync(path, report, ct); return new JsonObject { ["path"] = path, ["review"] = report };
         }),
         new("export_redacted", "Export approved sanitized text locally and verify every detected value is absent",
             SampleJson.Schema("{\"documentId\":{\"type\":\"string\"},\"expectedHash\":{\"type\":\"string\"}}", "documentId", "expectedHash"),
             (a, ct) => Export(c, a, ct), RequiresApproval: true)],
        Demo(c));
    private static IReadOnlyList<PlannedCall> Demo(SampleContext c)
    {
        var calls = new List<PlannedCall> { new("detect_sensitive", Args("letter-1")), new("preview_redaction", Args("letter-1")), new("save_redaction_review", Args("letter-1")) };
        if (c.Approved) calls.Add(new("export_redacted", new JsonObject { ["documentId"] = "letter-1", ["expectedHash"] = Redact(c, "letter-1").Hash }));
        return calls;
    }
    internal static Redaction Redact(SampleContext c, string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Invalid document ID.");
        if (c.Input["documents"] is not JsonArray documents || documents.Any(d => d is not JsonObject))
            throw new ArgumentException("documents must contain object rows.");
        var docs = documents.Select(d => d!.AsObject()).Where(d => d.Text("id") == id).ToArray();
        if (docs.Length != 1) throw new ArgumentException("Unknown or conflicting document ID.");
        var doc = docs[0];
        if (doc.Text("format") != "text/plain") throw new ArgumentException("Only plain text is supported; no binary documents, OCR or metadata scanning.");
        if (doc["text"] is not JsonValue textValue || !textValue.TryGetValue<string>(out var source))
            throw new ArgumentException("Document text must be explicitly supplied as a string; missing, null or nonstring text cannot be redacted.");
        // An explicitly supplied empty string is a valid empty document, unlike missing source text.
        if (source.Length > 1_000_000) throw new ArgumentException("Text exceeds sample limit.");
        var spans = new List<Span>();
        foreach (var (kind, pattern) in Patterns)
            foreach (Match m in pattern.Matches(source)) spans.Add(new(m.Index, m.Length, kind, m.Value));
        foreach (var value in doc["sensitiveLiterals"]!.AsArray().Select(x => x!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (value.Length < 2) throw new ArgumentException("Sensitive literals must contain at least two UTF-16 code units.");
            for (var index = 0; index <= source.Length - value.Length;)
            {
                var found = source.IndexOf(value, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                spans.Add(new(found, value.Length, "literal", source.Substring(found, value.Length)));
                index = found + 1; // Include overlapping literal matches before merging.
            }
        }
        var merged = new List<Span>();
        foreach (var span in spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length))
        {
            if (merged.Count == 0 || span.Start >= merged[^1].Start + merged[^1].Length) { merged.Add(span); continue; }
            var previous = merged[^1]; var end = Math.Max(previous.Start + previous.Length, span.Start + span.Length);
            merged[^1] = new(previous.Start, end - previous.Start, "merged", source.Substring(previous.Start, end - previous.Start));
        }
        var marker = "[REDACTED]";
        string Substitute(string replacement)
        {
            var builder = new StringBuilder(); var offset = 0;
            foreach (var span in merged) { builder.Append(source, offset, span.Start - offset); builder.Append(replacement); offset = span.Start + span.Length; }
            builder.Append(source, offset, source.Length - offset);
            return builder.ToString();
        }
        var sanitized = Substitute(marker);
        if (spans.Any(s => sanitized.Contains(s.Value, StringComparison.OrdinalIgnoreCase)))
        {
            // Use a single character absent from the source so it cannot contain a detected
            // value or reconstruct one across a replacement boundary. Prefer a visible block.
            marker = new[] { "█" }.Concat(Enumerable.Range(0xE000, 0x1900).Select(n => ((char)n).ToString()))
                .FirstOrDefault(candidate => !source.Contains(candidate, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("No safe replacement marker is available for this source.");
            sanitized = Substitute(marker);
        }
        Verify(sanitized, spans);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.ToJsonString())));
        return new(id, hash, sanitized, spans.ToArray(), merged.ToArray());
    }
    private static void Verify(string sanitized, IEnumerable<Span> spans)
    {
        if (spans.Any(s => sanitized.Contains(s.Value, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Sanitization verification failed; a detected value remains.");
    }
    internal static JsonObject Detect(SampleContext c, string id)
    {
        var result = Redact(c, id);
        return new() { ["documentId"] = id, ["expectedHash"] = result.Hash, ["offsetUnit"] = "UTF-16", ["detections"] = new JsonArray(result.Matches.OrderBy(s => s.Start).ThenBy(s => s.Kind, StringComparer.Ordinal).Select(s => (JsonNode?)new JsonObject { ["start"] = s.Start, ["length"] = s.Length, ["kind"] = s.Kind }).ToArray()), ["mergedSpanCount"] = result.Merged.Length, ["scope"] = "Supported patterns and supplied literals in plain text only." };
    }
    internal static JsonObject Preview(SampleContext c, string id)
    {
        var result = Redact(c, id); var report = Detect(c, id);
        report["sanitizedText"] = result.Text; report["detectedValuesAbsent"] = true; report["status"] = "preview-needs-human-review";
        return report;
    }
    internal static async Task<JsonNode?> Export(SampleContext c, JsonObject args, CancellationToken ct)
    {
        if (!c.Approved) throw new InvalidOperationException("Explicit host approval required.");
        var result = Redact(c, args.Text("documentId"));
        if (result.Hash != args.Text("expectedHash")) throw new InvalidOperationException("Source or policy changed; preview again.");
        var path = "redacted-" + result.Id + ".txt"; var fullPath = c.ResolvePath(path);
        if (File.Exists(fullPath))
        {
            if (await File.ReadAllTextAsync(fullPath, ct) != result.Text) throw new InvalidOperationException("Existing export differs; use a fresh workspace.");
        }
        else await File.WriteAllTextAsync(fullPath, result.Text, new UTF8Encoding(false), ct);
        Verify(await File.ReadAllTextAsync(fullPath, ct), result.Matches);
        return new JsonObject { ["documentId"] = result.Id, ["expectedHash"] = result.Hash, ["path"] = path, ["detectedValuesAbsent"] = true, ["status"] = "sanitized-local-text-export", ["scope"] = "Plain text only; human review required for unsupported sensitive content." };
    }
}
