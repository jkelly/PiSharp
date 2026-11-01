// PiSharp-only (no upstream Pi source): bounded XHTML audit and approved workspace fixes.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PiSharp.Samples.AccessibilityAuditAssistant;

public static class Workflow
{
    public sealed record Document(string Id, string Html);
    public sealed record Fix(string DocumentId, string ElementId, string Rule, string Value);
    public sealed record Finding(string DocumentId, string ElementId, string Rule, string Detail);
    public sealed record Plan(string Fingerprint, string[] DocumentIds, Fix[] Fixes, Finding[] Findings);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const string DataPath = "accessibility-documents.json";

    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("""{"documentIds":{"type":"array","items":{"type":"string"},"minItems":1}}""", "documentIds");
        var ids = Read<Document[]>(c.Input["documents"]).Select(d => d.Id).ToArray();
        return new("53", "Accessibility Audit Assistant",
            "Inspect all requested documentIds, propose_accessibility_fixes, then apply_accessibility_fixes. Apply only supplied fix values and approved workspace changes. Report the rescan and unresolved issues. This limited XHTML checker is never an accessibility certification.",
            [new("inspect_accessibility", "Scan well-formed supplied XHTML for missing image alternatives, form/control names and document language.", schema,
                (a, _) => Task.FromResult<JsonNode?>(Node(Build(c, Ids(a)).Findings))),
             new("propose_accessibility_fixes", "Match supplied exact fixes to current findings; freeze source content and selected IDs.", schema, async (a, t) =>
             {
                 var plan = Build(c, Ids(a)); c.State[Key(plan.DocumentIds)] = Node(plan);
                 await c.WriteJsonAsync("accessibility-plan.json", Node(plan), t); return Node(plan);
             }),
             new("apply_accessibility_fixes", "Apply the exact proposed local XHTML attributes after approval and rescan every selected document.", schema,
                (a, t) => Apply(c, Ids(a), t), RequiresApproval: true)],
            [new("inspect_accessibility", Args(ids)), new("propose_accessibility_fixes", Args(ids)), new("apply_accessibility_fixes", Args(ids))]);
    }

    public static JsonObject Args(params string[] ids) => new() { ["documentIds"] = Node(ids) };
    private static string[] Ids(JsonObject a) => Read<string[]>(a["documentIds"]);
    private static T Read<T>(JsonNode? n) where T : class => n?.Deserialize<T>(Options) ?? throw new InvalidDataException("Required fixture or argument data is missing.");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value)!;
    private static string Key(string[] ids) => "plan:" + string.Join("|", ids.Order(StringComparer.Ordinal));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Document[] Current(SampleContext c) => File.Exists(c.ResolvePath(DataPath))
        ? Read<Document[]>(JsonNode.Parse(File.ReadAllText(c.ResolvePath(DataPath)))) : Read<Document[]>(c.Input["documents"]);

    private static Plan Build(SampleContext c, string[] ids)
    {
        var all = Current(c);
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || all.Select(x => x.Id).Distinct().Count() != all.Length)
            throw new InvalidDataException("Document IDs must be nonempty and unique.");
        var selected = ids.Order(StringComparer.Ordinal).Select(id => all.SingleOrDefault(d => d.Id == id) ?? throw new InvalidDataException("Unknown document: " + id)).ToArray();
        var findings = selected.SelectMany(Scan).ToArray();
        var configured = Read<Fix[]>(c.Input["fixes"]);
        if (configured.GroupBy(f => (f.DocumentId, f.ElementId, f.Rule)).Any(g => g.Count() > 1)) throw new InvalidDataException("Conflicting duplicate fixes.");
        var fixes = configured.Where(f => findings.Any(i => i.DocumentId == f.DocumentId && i.ElementId == f.ElementId && i.Rule == f.Rule) &&
            f.Rule != "malformed-xhtml" && f.ElementId.Length > 0 && !string.IsNullOrWhiteSpace(f.Value)).ToArray();
        return new(Hash(Node(new { selected, fixes }).ToJsonString()), selected.Select(d => d.Id).ToArray(), fixes, findings);
    }

    public static Finding[] Scan(Document source)
    {
        XDocument document;
        try { document = XDocument.Parse(source.Html, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException ex) { return [new(source.Id, "", "malformed-xhtml", "Cannot audit: " + ex.Message)]; }
        var root = document.Root;
        if (root is null || root.Name.LocalName != "html") return [new(source.Id, "", "malformed-xhtml", "A single html root is required.")];
        var elements = root.DescendantsAndSelf().ToArray();
        var duplicateIds = elements.Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).Where(g => g.Count() > 1).ToArray();
        if (duplicateIds.Length > 0) return [new(source.Id, "", "malformed-xhtml", "Duplicate element IDs make exact fixes ambiguous.")];
        var findings = new List<Finding>();
        void Add(XElement e, string rule, string detail) => findings.Add(new(source.Id, (string?)e.Attribute("id") ?? "", rule, detail));
        if (string.IsNullOrWhiteSpace((string?)root.Attribute("lang"))) Add(root, "document-language", "html lacks a language.");
        foreach (var element in elements)
        {
            var tag = element.Name.LocalName;
            if (tag == "img" && element.Attribute("alt") is null) Add(element, "image-alt", "Image has no alt attribute; decorative intent cannot be inferred.");
            if (tag is not ("input" or "select" or "textarea" or "button")) continue;
            if (tag == "input" && string.Equals((string?)element.Attribute("type"), "hidden", StringComparison.OrdinalIgnoreCase)) continue;
            var id = (string?)element.Attribute("id");
            var ariaName = !string.IsNullOrWhiteSpace((string?)element.Attribute("aria-label"));
            var refs = ((string?)element.Attribute("aria-labelledby") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ariaName |= refs.Length > 0 && refs.All(r => elements.Any(e => (string?)e.Attribute("id") == r && !string.IsNullOrWhiteSpace(e.Value)));
            var label = element.Ancestors().Any(e => e.Name.LocalName == "label" && !string.IsNullOrWhiteSpace(e.Value)) ||
                id is not null && elements.Any(e => e.Name.LocalName == "label" && (string?)e.Attribute("for") == id && !string.IsNullOrWhiteSpace(e.Value));
            var buttonName = tag == "button" && !string.IsNullOrWhiteSpace(element.Value) || tag == "input" &&
                (string?)element.Attribute("type") is "submit" or "reset" or "button" && !string.IsNullOrWhiteSpace((string?)element.Attribute("value"));
            if (!ariaName && !label && !buttonName) Add(element, "control-name", "Control has no supported accessible name.");
        }
        return findings.ToArray();
    }

    private static async Task<JsonNode?> Apply(SampleContext c, string[] ids, CancellationToken token)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval is required for local HTML edits.");
        var key = Key(ids);
        var plan = Read<Plan>(c.State[key]);
        var current = Current(c);
        var receiptKey = key + ":receipt";
        if (c.State[receiptKey] is JsonObject receipt && receipt.Text("afterHash") == Hash(Node(current).ToJsonString())) return receipt.DeepClone();
        var fresh = Build(c, ids);
        if (plan.Fingerprint != fresh.Fingerprint) throw new InvalidOperationException("Source or configured fixes changed; propose again.");
        var updated = current.Select(d =>
        {
            var fixes = plan.Fixes.Where(f => f.DocumentId == d.Id).ToArray();
            if (fixes.Length == 0) return d;
            var xml = XDocument.Parse(d.Html, LoadOptions.PreserveWhitespace);
            foreach (var fix in fixes)
            {
                var element = xml.Descendants().Single(e => (string?)e.Attribute("id") == fix.ElementId);
                element.SetAttributeValue(fix.Rule switch { "image-alt" => "alt", "control-name" => "aria-label", "document-language" => "lang", _ => throw new InvalidOperationException("Unsupported fix.") }, fix.Value);
            }
            return d with { Html = xml.ToString(SaveOptions.DisableFormatting) };
        }).ToArray();
        var result = new JsonObject { ["fixedCount"] = plan.Fixes.Length, ["remaining"] = Node(updated.Where(d => ids.Contains(d.Id)).SelectMany(Scan).ToArray()),
            ["afterHash"] = Hash(Node(updated).ToJsonString()), ["scope"] = "workspace XHTML only; limited checker, not certification" };
        await c.WriteJsonAsync(DataPath, Node(updated), token);
        await c.WriteJsonAsync("accessibility-rescan.json", result, token);
        c.State[receiptKey] = result.DeepClone(); return result;
    }
}
