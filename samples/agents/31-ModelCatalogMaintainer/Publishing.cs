// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public sealed record RemoteItem(int Number, string Url);
public sealed record PublishResult(bool Published, string? PullRequestUrl, string? IssueUrl, string? Commit);
public interface ICatalogRemote
{
    Task PrepareAsync(CancellationToken token) => Task.CompletedTask;
    Task RetireObsoletePullRequestAsync(string body, CancellationToken token) => Task.CompletedTask;
    Task<RemoteItem> EnsurePriceIssueAsync(string identity, string body, CancellationToken token);
    Task<RemoteItem> EnsurePullRequestAsync(string body, CancellationToken token);
    Task LinkIssueAsync(RemoteItem issue, string body, CancellationToken token);
}
public interface ICatalogCheckout
{
    Task<string> CommitAndPushAsync(JsonObject overlay, string markdown, CancellationToken token);
}

public static class CatalogPublisher
{
    public static async Task<PublishResult> PublishAsync(ScanResult scan, GitHubConfig config, ICatalogCheckout checkout, ICatalogRemote remote,
        bool approved, CancellationToken token = default)
    {
        if (!approved) throw new InvalidOperationException("Live publication requires --approve.");
        if (!scan.Complete) throw new InvalidOperationException("An incomplete scan cannot be published.");
        if (!scan.Changed)
        {
            await remote.RetireObsoletePullRequestAsync(ChangeReport.Markdown(scan), token);
            return new PublishResult(false, null, null, null);
        }
        var markdown = ChangeReport.Markdown(scan);
        var reportUrl = $"https://github.com/{config.Repository}/blob/{config.Branch}/catalog-maintained/model-catalog-report.md";
        var details = $"Model catalog refresh: {scan.Changes.Count} model changes, {scan.PriceChanges.Count} price observations.\n\n[Full changes, old/new amounts, absolute/percentage differences, and source evidence]({reportUrl}).\n\n";
        // GitHub bodies have a size cap; the complete table is always committed to the branch.
        if (markdown.Length < 45000) details += markdown;
        else details += "The full price table exceeds the issue body limit; use the versioned report linked above.\n";
        // Establish ownership and draft state before any branch update.
        await remote.PrepareAsync(token);
        // Commit/push must succeed before reporting a publication or creating review records.
        var commit = await checkout.CommitAndPushAsync(scan.Overlay, markdown, token);
        RemoteItem? issue = null;
        if (scan.PriceChanges.Count != 0) issue = await remote.EnsurePriceIssueAsync(scan.Fingerprint, details, token);
        var pr = await remote.EnsurePullRequestAsync(details + (issue is null ? "" : $"\nPrice review: {issue.Url}\n"), token);
        if (issue is not null) await remote.LinkIssueAsync(issue, details + $"\nProposed catalog update: {pr.Url}\n", token);
        return new PublishResult(true, pr.Url, issue?.Url, commit);
    }
}

public sealed class GitHubRemote : ICatalogRemote
{
    private const string Marker = "<!-- pisharp-model-catalog-maintainer:v1 -->";
    private const string SupersededMarker = "<!-- pisharp-model-catalog-superseded:v1 -->";
    private readonly HttpClient client;
    private readonly GitHubConfig config;
    public GitHubRemote(HttpClient client, GitHubConfig config, string token)
    {
        config.Validate(); this.client = client; this.config = config;
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PiSharp-CatalogMaintainer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    public async Task<RemoteItem> EnsurePriceIssueAsync(string identity, string body, CancellationToken token)
    {
        var marker = $"<!-- pisharp-model-pricing:{identity} -->";
        body = marker + "\n" + body;
        var candidates = await ListAsync($"repos/{config.Repository}/issues?state=all&per_page=100", token);
        var matches = candidates.Where(x => x["pull_request"] is null && x["body"]?.GetValue<string>().Contains(marker, StringComparison.Ordinal) == true).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Duplicate marked price issues already exist; manual reconciliation required.");
        if (matches.Length == 1)
        {
            var existing = matches[0];
            var priorBody = existing["body"]?.GetValue<string>();
            if (priorBody != body && priorBody?.StartsWith(body + "\nProposed catalog update:", StringComparison.Ordinal) != true)
                await SendAsync(HttpMethod.Patch, $"repos/{config.Repository}/issues/{existing["number"]}", new JsonObject { ["body"] = body }, token);
            return Item(existing);
        }
        return Item(await SendAsync(HttpMethod.Post, $"repos/{config.Repository}/issues", new JsonObject
        { ["title"] = "Review model catalog price changes", ["body"] = body }, token));
    }
    public async Task<RemoteItem> EnsurePullRequestAsync(string body, CancellationToken token)
    {
        body = Marker + "\n" + body;
        var history = await PullRequestHistoryAsync(token);
        var candidates = history.Where(pr => pr["state"]?.GetValue<string>() == "open").ToList();
        if (candidates.Count > 1) throw new InvalidOperationException("Multiple open PRs use the stable catalog branch.");
        if (candidates.Count == 1)
        {
            var existing = candidates[0];
            if (existing["body"]?.GetValue<string>().Contains(Marker, StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Stable branch has an unmarked PR; refusing to take ownership.");
            await EnsureDraftAsync(existing, token);
            if (existing["body"]?.GetValue<string>() != body)
                await SendAsync(HttpMethod.Patch, $"repos/{config.Repository}/pulls/{existing["number"]}", new JsonObject { ["body"] = body }, token);
            return Item(existing);
        }
        if (ReusableSupersededPullRequest(history) is { } retired)
        {
            RequireRetiredDraft(retired);
            return Item(await SendAsync(HttpMethod.Patch, $"repos/{config.Repository}/pulls/{retired["number"]}",
                new JsonObject { ["body"] = body, ["state"] = "open" }, token));
        }
        return Item(await SendAsync(HttpMethod.Post, $"repos/{config.Repository}/pulls", new JsonObject
        {
            ["title"] = "Maintain model catalog and pricing", ["body"] = body, ["head"] = config.Branch,
            ["base"] = config.BaseBranch, ["draft"] = true
        }, token));
    }
    public async Task PrepareAsync(CancellationToken token)
    {
        var history = await PullRequestHistoryAsync(token);
        var open = history.Where(pr => pr["state"]?.GetValue<string>() == "open").ToArray();
        if (open.Length > 1) throw new InvalidOperationException("Multiple open PRs use the stable catalog branch.");
        if (open.Length == 1)
        {
            if (open[0]["body"]?.GetValue<string>().Contains(Marker, StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Stable branch has an unmarked PR; refusing to take ownership.");
            await EnsureDraftAsync(open[0], token);
        }
        else if (ReusableSupersededPullRequest(history) is { } retired) RequireRetiredDraft(retired);
    }
    public async Task RetireObsoletePullRequestAsync(string body, CancellationToken token)
    {
        var history = await PullRequestHistoryAsync(token);
        var open = history.Where(pr => pr["state"]?.GetValue<string>() == "open").ToArray();
        if (open.Length > 1) throw new InvalidOperationException("Multiple open PRs use the stable catalog branch.");
        if (open.Length == 0) return;
        var existing = open[0];
        if (existing["body"]?.GetValue<string>().Contains(Marker, StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Stable branch has an unmarked PR; refusing to take ownership.");
        await EnsureDraftAsync(existing, token);
        await SendAsync(HttpMethod.Patch, $"repos/{config.Repository}/pulls/{existing["number"]}", new JsonObject
        {
            ["state"] = "closed",
            ["body"] = Marker + "\n" + SupersededMarker + "\nThis proposal is superseded: the latest complete scan has no changes from the base catalog. Historical price review issues remain available. A later catalog change may reopen this draft.\n\n" + body
        }, token);
    }
    private static JsonObject? ReusableSupersededPullRequest(IEnumerable<JsonObject> history)
    {
        var closed = history.Where(pr => pr["state"]?.GetValue<string>() == "closed" && pr["merged_at"] is null &&
            pr["body"]?.GetValue<string>().Contains(Marker, StringComparison.Ordinal) == true).ToArray();
        if (closed.Any(pr => pr["body"]?.GetValue<string>().Contains(SupersededMarker, StringComparison.Ordinal) != true))
            throw new InvalidOperationException("The marked catalog PR was closed without merging. Review/reopen it or choose a new explicitly configured branch; no duplicate PR created.");
        return closed.OrderByDescending(pr => pr["number"]!.GetValue<int>()).FirstOrDefault();
    }
    private static void RequireRetiredDraft(JsonObject retired)
    {
        if (retired["draft"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The superseded catalog PR is no longer a draft; manual review is required before publication.");
    }
    private Task<List<JsonObject>> PullRequestHistoryAsync(CancellationToken token)
    {
        var owner = config.Repository.Split('/')[0];
        return ListAsync($"repos/{config.Repository}/pulls?state=all&head={Uri.EscapeDataString(owner + ":" + config.Branch)}&base={Uri.EscapeDataString(config.BaseBranch)}&per_page=100", token);
    }
    private async Task EnsureDraftAsync(JsonObject pullRequest, CancellationToken token)
    {
        if (pullRequest["draft"]?.GetValue<bool>() == true) return;
        var nodeId = pullRequest["node_id"]?.GetValue<string>() ?? throw new InvalidOperationException("Cannot establish the existing PR's draft state; no publication permitted.");
        var converted = await SendAsync(HttpMethod.Post, "graphql", new JsonObject
        {
            ["query"] = "mutation($id:ID!){convertPullRequestToDraft(input:{pullRequestId:$id}){pullRequest{isDraft}}}",
            ["variables"] = new JsonObject { ["id"] = nodeId }
        }, token);
        if (converted["errors"] is JsonArray errors && errors.Count > 0 ||
            converted["data"]?["convertPullRequestToDraft"]?["pullRequest"]?["isDraft"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("GitHub did not confirm draft conversion; no publication permitted.");
    }
    public async Task LinkIssueAsync(RemoteItem issue, string body, CancellationToken token)
    {
        var current = await SendAsync(HttpMethod.Get, $"repos/{config.Repository}/issues/{issue.Number}", null, token);
        var firstLine = current["body"]?.GetValue<string>().Split('\n')[0] ?? "";
        if (!firstLine.StartsWith("<!-- pisharp-model-pricing:", StringComparison.Ordinal)) throw new InvalidOperationException("Issue marker changed during publication.");
        var updated = firstLine + "\n" + body;
        if (current["body"]?.GetValue<string>() != updated)
            await SendAsync(HttpMethod.Patch, $"repos/{config.Repository}/issues/{issue.Number}", new JsonObject { ["body"] = updated }, token);
    }
    private async Task<List<JsonObject>> ListAsync(string path, CancellationToken token)
    {
        var list = new List<JsonObject>();
        for (var page = 1; page <= 1000; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path + "&page=" + page);
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub list failed: HTTP {(int)response.StatusCode}.");
            var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonArray ?? throw new InvalidDataException("Expected GitHub list response.");
            list.AddRange(rows.Select(row => row?.AsObject() ?? throw new InvalidDataException("Invalid GitHub item.")));
            if (rows.Count < 100) return list;
        }
        throw new InvalidOperationException("GitHub pagination limit reached; refusing an incomplete duplicate check.");
    }
    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub {method} failed: HTTP {(int)response.StatusCode}. Rerun to reconcile stable markers; writes are never blindly retried.");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsObject() ?? throw new InvalidDataException("Empty GitHub response.");
    }
    private static RemoteItem Item(JsonObject value) => new(value["number"]!.GetValue<int>(), value["html_url"]!.GetValue<string>());
}

public sealed class GitCheckout : ICatalogCheckout
{
    private readonly GitHubConfig config;
    private readonly string credential;
    private readonly string overlayPath;
    private readonly Func<string, string[], CancellationToken, Task<string>>? gitRunner;
    private string? expectedBranchSha;
    public string Root { get; }
    private GitCheckout(GitHubConfig config, string credential, string overlayPath, string root, Func<string, string[], CancellationToken, Task<string>>? gitRunner)
    { this.config = config; this.credential = credential; this.overlayPath = overlayPath; Root = root; this.gitRunner = gitRunner; }

    public static async Task<GitCheckout> CloneAsync(GitHubConfig config, string credential, string overlayPath, string workspace, bool approved, CancellationToken token,
        Func<string, string[], CancellationToken, Task<string>>? gitRunner = null)
    {
        if (!approved) throw new InvalidOperationException("Live Git operations require --approve.");
        config.Validate(); Paths.ValidateRelative(overlayPath);
        var root = Paths.Under(workspace, "checkout-" + Guid.NewGuid().ToString("N"));
        var checkout = new GitCheckout(config, credential, overlayPath, root, gitRunner);
        Directory.CreateDirectory(root);
        await checkout.GitAsync(["clone", "--no-local", "--single-branch", "--branch", config.BaseBranch, "--", $"https://github.com/{config.Repository}.git", root], token);
        var remote = await checkout.GitAsync(["ls-remote", "--heads", "origin", "refs/heads/" + config.Branch], token);
        checkout.expectedBranchSha = string.IsNullOrWhiteSpace(remote) ? null : remote.Split('\t')[0].Trim();
        if (checkout.expectedBranchSha is not null)
        {
            await checkout.GitAsync(["fetch", "--no-tags", "origin", $"refs/heads/{config.Branch}:refs/remotes/origin/catalog-maintained"], token);
            var fetched = (await checkout.GitAsync(["rev-parse", "refs/remotes/origin/catalog-maintained"], token)).Trim();
            if (fetched != checkout.expectedBranchSha) throw new InvalidOperationException("Catalog branch changed during clone; rerun.");
            // An existing branch must be owned by this maintainer before any overwrite.
            var message = await checkout.GitAsync(["log", "-1", "--format=%B", fetched], token);
            if (!message.Contains("PiSharp-Catalog-Maintainer: v1", StringComparison.Ordinal))
                throw new InvalidOperationException("Stable branch is not marked as maintainer-owned.");
        }
        await checkout.GitAsync(["checkout", "-b", config.Branch], token);
        return checkout;
    }
    public async Task<string> CommitAndPushAsync(JsonObject overlay, string markdown, CancellationToken token)
    {
        var candidate = CatalogJson.Canonical(overlay);
        var candidateReport = NormalizeReport(markdown);
        if (expectedBranchSha is not null)
        {
            var oldJson = await GitAsync(["show", expectedBranchSha + ":" + overlayPath], token);
            var previous = JsonNode.Parse(oldJson)?.AsObject() ?? throw new InvalidDataException("Existing branch overlay is invalid.");
            var previousReport = await GitAsync(["show", expectedBranchSha + ":catalog-maintained/model-catalog-report.md"], token);
            // The base can advance while the proposed overlay stays the same. Keep its old/new price review current too.
            // Reports exclude retrieval/commit timestamps; normalize line endings to avoid host-only differences.
            if (CatalogJson.Canonical(WithoutProvenance(previous)) == CatalogJson.Canonical(WithoutProvenance(overlay)) &&
                NormalizeReport(previousReport) == candidateReport) return expectedBranchSha;
        }
        var target = Paths.Under(Root, overlayPath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, candidate, token);
        var report = Paths.Under(Root, "catalog-maintained/model-catalog-report.md");
        await File.WriteAllTextAsync(report, candidateReport, token);
        await GitAsync(["add", "--", overlayPath, "catalog-maintained/model-catalog-report.md"], token);
        await GitAsync(["commit", "-m", "Maintain model catalog and pricing\n\nPiSharp-Catalog-Maintainer: v1"], token);
        var commit = (await GitAsync(["rev-parse", "HEAD"], token)).Trim();
        var lease = $"--force-with-lease=refs/heads/{config.Branch}:{expectedBranchSha ?? ""}";
        await GitAsync(["push", lease, "origin", $"HEAD:refs/heads/{config.Branch}"], token);
        expectedBranchSha = commit;
        return commit;
    }
    private static string NormalizeReport(string report) => report.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
    private static JsonNode? WithoutProvenance(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Where(x => x.Key != "provenance").Select(x => KeyValuePair.Create(x.Key, WithoutProvenance(x.Value)))),
        JsonArray array => new JsonArray(array.Select(WithoutProvenance).ToArray()),
        _ => node?.DeepClone()
    };
    private async Task<string> GitAsync(string[] arguments, CancellationToken token)
    {
        if (gitRunner is not null) return await gitRunner(Root, arguments, token);
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Credentials stay out of URLs, arguments, disk, stdout and committed files.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_COUNT"] = "3";
        start.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
        start.Environment["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + credential));
        start.Environment["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        start.Environment["GIT_CONFIG_VALUE_1"] = Path.Combine(Path.GetDirectoryName(Root)!, "disabled-hooks-" + Path.GetFileName(Root));
        start.Environment["GIT_CONFIG_KEY_2"] = "credential.helper";
        start.Environment["GIT_CONFIG_VALUE_2"] = "";
        start.Environment["GIT_AUTHOR_NAME"] = "PiSharp Catalog Maintainer";
        start.Environment["GIT_AUTHOR_EMAIL"] = "catalog-maintainer@users.noreply.github.com";
        start.Environment["GIT_COMMITTER_NAME"] = "PiSharp Catalog Maintainer";
        start.Environment["GIT_COMMITTER_EMAIL"] = "catalog-maintainer@users.noreply.github.com";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var output = await stdout; _ = await stderr;
        // Error output can include server diagnostics or auth configuration; do not echo it.
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} failed (exit {process.ExitCode}); isolated checkout preserved at {Root}.");
        return output;
    }
}
