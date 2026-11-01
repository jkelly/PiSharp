// PiSharp-only (no upstream Pi source): deterministic recorded-page monitoring with durable local baselines.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.WebsiteChangeMonitor;

public static class Workflow
{
    public sealed record Page(string Id, string Html, int Status, string RequiredText, int MinimumChangedWords);
    public sealed record Change(string Id, string? Before, string After, string[] AddedWords, string[] RemovedWords, bool Alert, string Reason);
    public sealed record Plan(string SourceHash, string BaselineHash, string[] PageIds, Change[] Changes);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const string StorePath = "monitor-state.json";

    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("""{"pageIds":{"type":"array","items":{"type":"string"},"minItems":1}}""", "pageIds");
        var ids = Read<Page[]>(c.Input["pages"]).Select(p => p.Id).ToArray();
        return new("54", "Website Change Monitor",
            "Inspect recorded pages for every requested pageId, compare_page_content against persistent baselines using the configured text/word conditions, then commit_monitor_results. Report local alerts and baseline updates. Never claim a live fetch or delivered notification.",
            [new("inspect_recorded_pages", "Read supplied HTTP status, configured alert conditions and normalized visible text without network access.", schema,
                (a, _) => Task.FromResult<JsonNode?>(Node(Pages(c, Ids(a)).Select(p => new { p.Id, p.Status, p.RequiredText, p.MinimumChangedWords, content = Normalize(p.Html) }).ToArray()))),
             new("compare_page_content", "Compute content and word differences against saved baselines; suppress markup/whitespace-only changes.", schema, async (a, t) =>
             {
                 var plan = Build(c, Ids(a)); c.State[Key(plan.PageIds)] = Node(plan);
                 await c.WriteJsonAsync("monitor-comparison.json", Node(plan), t); return Node(plan);
             }),
             new("commit_monitor_results", "Persist successful baselines and deduplicated local alert records; approval required.", schema,
                 (a, t) => Commit(c, Ids(a), t), RequiresApproval: true)],
            [new("inspect_recorded_pages", Args(ids)), new("compare_page_content", Args(ids)), new("commit_monitor_results", Args(ids))]);
    }

    public static JsonObject Args(params string[] ids) => new() { ["pageIds"] = Node(ids) };
    private static T Read<T>(JsonNode? value) where T : class => value?.Deserialize<T>(Options) ?? throw new InvalidDataException("Required monitor data is missing.");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value)!;
    private static string[] Ids(JsonObject args) => Read<string[]>(args["pageIds"]);
    private static string Key(string[] ids) => "plan:" + string.Join("|", ids.Order(StringComparer.Ordinal));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Page[] Pages(SampleContext c, string[] ids)
    {
        var all = Read<Page[]>(c.Input["pages"]);
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || all.Select(p => p.Id).Distinct().Count() != all.Length)
            throw new InvalidDataException("Page IDs must be unique and nonempty.");
        var selected = ids.Order(StringComparer.Ordinal).Select(id => all.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Unknown page: " + id)).ToArray();
        if (selected.Any(p => p.MinimumChangedWords < 0 || p.Html is null || p.RequiredText is null)) throw new InvalidDataException("Invalid page conditions.");
        return selected;
    }

    public static string Normalize(string html)
    {
        // Recorded HTML text extraction, not browser rendering: scripts, style and comments do not trigger alerts.
        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>|<!--.*?-->", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]*>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
    }

    private static JsonObject Store(SampleContext c)
    {
        if (File.Exists(c.ResolvePath(StorePath))) return JsonNode.Parse(File.ReadAllText(c.ResolvePath(StorePath)))!.AsObject();
        var baseline = Read<Dictionary<string, string>>(c.Input["baselines"]);
        return new JsonObject { ["baselines"] = Node(baseline.ToDictionary(p => p.Key, p => Normalize(p.Value))), ["alerts"] = new JsonObject(), ["commits"] = new JsonObject() };
    }
    private static Plan Build(SampleContext c, string[] ids)
    {
        var pages = Pages(c, ids); var store = Store(c); var baseline = store["baselines"]!.AsObject();
        var changes = pages.Select(p =>
        {
            var before = baseline[p.Id]?.GetValue<string>(); var after = Normalize(p.Html);
            var previousWords = Words(before ?? ""); var currentWords = Words(after);
            var added = currentWords.Except(previousWords, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var removed = previousWords.Except(currentWords, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var reason = p.Status != 200 ? "recorded HTTP failure: baseline retained" : before is null ? "initial baseline: no alert" :
                before == after ? "normalized content unchanged" : !after.Contains(p.RequiredText, StringComparison.OrdinalIgnoreCase) ? "required text absent" :
                added.Length + removed.Length < p.MinimumChangedWords ? "below changed-word threshold" : "configured condition matched";
            return new Change(p.Id, before, after, added, removed, reason == "configured condition matched", reason);
        }).ToArray();
        return new(Hash(Node(pages).ToJsonString()), BaselineHash(baseline, pages), pages.Select(p => p.Id).ToArray(), changes);
    }
    private static string[] Words(string value) => Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).Distinct().ToArray();
    private static string BaselineHash(JsonObject baseline, Page[] pages) => Hash(Node(pages.Select(p => new { p.Id, value = baseline[p.Id]?.GetValue<string>() }).ToArray()).ToJsonString());

    private static async Task<JsonNode?> Commit(SampleContext c, string[] ids, CancellationToken token)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval is required to persist monitor baselines and alerts.");
        var plan = Read<Plan>(c.State[Key(ids)]); var pages = Pages(c, ids);
        if (plan.SourceHash != Hash(Node(pages).ToJsonString())) throw new InvalidOperationException("Recorded pages or conditions changed; compare again.");
        var store = Store(c); var baseline = store["baselines"]!.AsObject(); var alerts = store["alerts"]!.AsObject(); var created = 0;
        store["commits"] ??= new JsonObject(); var commits = store["commits"]!.AsObject();
        var planId = Hash(Node(plan).ToJsonString()); var currentBaselineHash = BaselineHash(baseline, pages);
        if (plan.BaselineHash != currentBaselineHash && commits[planId]?.GetValue<string>() != currentBaselineHash)
            throw new InvalidOperationException("Baseline changed concurrently; compare again.");
        foreach (var change in plan.Changes)
        {
            if (pages.Single(p => p.Id == change.Id).Status != 200) continue;
            if (change.Alert)
            {
                var eventId = Hash(change.Id + "\n" + change.Before + "\n" + change.After + "\n" + plan.SourceHash);
                if (!alerts.ContainsKey(eventId)) { alerts[eventId] = Node(change); created++; }
            }
            baseline[change.Id] = change.After;
        }
        commits[planId] = BaselineHash(baseline, pages);
        await c.WriteJsonAsync(StorePath, store, token);
        var result = new JsonObject { ["newLocalAlerts"] = created, ["totalLocalAlerts"] = alerts.Count,
            ["successfulPageIds"] = Node(pages.Where(p => p.Status == 200).Select(p => p.Id).ToArray()), ["statePath"] = StorePath, ["delivery"] = "local records only" };
        await c.WriteJsonAsync("monitor-report.json", result, token); return result;
    }
}
