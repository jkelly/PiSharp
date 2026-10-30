// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.ModelCatalogMaintainer;

public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var cases = new (string Name, Func<Task> Test)[]
        {
            ("disabled and excluded providers never fetch and preserve data", Disabled),
            ("unknown and missing prices retain known observations", MissingPrices),
            ("unit normalization avoids false changes", Normalization),
            ("increases and decreases include absolute and percentage deltas", PriceDeltas),
            ("currency tier region cache conditions stay distinct", Dimensions),
            ("unknown protocols remain metadata only", UnknownProtocol),
            ("absent and excluded models are retained", Retention),
            ("duplicate and invalid data fail closed", InvalidData),
            ("one failed provider makes scan atomic and incomplete", AtomicFailure),
            ("official pages require unambiguous structured extraction", PricingPage),
            ("timestamps and source reordering do not create diffs", Idempotence),
            ("numeric spelling and rate order retain issue identity", IssueIdentity),
            ("HTTP source validates status, credentials, and boundaries", HttpAdapter),
            ("OpenRouter token cache image and conditional prices normalize", OpenRouter),
            ("OpenRouter explicit discounts normalize to payable rates", OpenRouterDiscount),
            ("unrecognized OpenRouter pricing never silently disappears", OpenRouterUnknown),
            ("publication requires approval and complete changed scan", PublicationGuards),
            ("failed commit and failed API never report success", PublicationFailure),
            ("live GitHub adapter deduplicates linked issues and PRs", GitHubDeduplication),
            ("closed unmerged catalog PR is not duplicated", GitHubClosedPullRequest),
            ("unchanged scans retire stale proposals and later changes reuse them", GitHubNoChangeReconciliation),
            ("unchanged and resumed scans preserve PR ownership and closure guards", GitHubReconciliationGuards),
            ("ready PR is converted to draft before branch writes", GitHubDraftConversion),
            ("failed draft conversion stops branch publication", GitHubDraftConversionFailure),
            ("orphan owned branch is reused to recover PR creation", OrphanBranchRecovery),
            ("changed base price refreshes report for identical proposed overlay", ChangedBaseRefreshesReport),
            ("GitHub duplicate search follows pagination", GitHubPagination),
            ("overlay target cannot overwrite pinned sources", TargetBoundary),
            ("first refresh seeds pinned prices and preserves existing routes", SeedPinned),
            ("shared pinned ids prefer chat and preserve alternate typed definitions", SeedPinnedMultiType),
            ("disabled and excluded providers skip all pinned shard reads", SkipPinnedProviders),
            ("bundled workflow performs real fixture changes", BundledFixture)
        };
        var failures = 0;
        foreach (var test in cases)
            try { await test.Test(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} catalog maintainer checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static ProviderConfig Provider(string id = "test") => new()
    { Id = id, Enabled = true, Endpoint = "https://official.example.test/models", OfficialHosts = ["official.example.test"] };
    private static MaintainerConfig Config(params ProviderConfig[] providers) => new() { Providers = providers.Length == 0 ? [Provider()] : providers };
    private static JsonObject Row(string id = "alpha", decimal? amount = 1m, string unit = "million-tokens") => new()
    {
        ["id"] = id, ["name"] = id, ["api"] = "openai-completions", ["type"] = "chat",
        ["contextWindow"] = 128000, ["input"] = new JsonArray("text"), ["output"] = new JsonArray("text"),
        ["prices"] = new JsonArray(Rate(amount, unit))
    };
    private static JsonObject Rate(decimal? amount, string unit = "million-tokens", string kind = "input", string currency = "USD", string tier = "standard", string region = "us") => new()
    { ["amount"] = amount, ["unit"] = unit, ["kind"] = kind, ["currency"] = currency, ["tier"] = tier, ["region"] = region, ["conditions"] = new JsonObject() };
    private static string Data(params JsonObject[] rows) => new JsonObject { ["data"] = new JsonArray(rows.Select(x => x.DeepClone()).ToArray()) }.ToJsonString();
    private static Task<ScanResult> Scan(JsonObject baseline, params JsonObject[] rows) => CatalogEngine.ScanAsync(Config(), baseline, new FixtureFetcher(new Dictionary<string, string> { ["test"] = Data(rows) }));
    private static JsonObject Model(ScanResult result, string id = "alpha", string provider = "test") => result.Overlay["providers"]![provider]![id]!.AsObject();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> action)
    { try { await action(); } catch { return; } throw new InvalidOperationException("Expected operation to fail."); }

    private static async Task Disabled()
    {
        var initial = await Scan(CatalogJson.EmptyOverlay(), Row());
        var fetcher = new FixtureFetcher(new Dictionary<string, string>());
        var config = new MaintainerConfig { Providers = [Provider() with { Enabled = false, Endpoint = "invalid" }, Provider("excluded") with { Endpoint = "invalid" }], ExcludedProviders = ["excluded"] };
        var result = await CatalogEngine.ScanAsync(config, initial.Overlay, fetcher);
        Check(result.Complete && !result.Changed && fetcher.Requests == 0, "Disabled provider fetched or changed data.");
    }
    private static async Task MissingPrices()
    {
        var initial = await Scan(CatalogJson.EmptyOverlay(), Row());
        var absent = Row(); absent.Remove("prices"); absent.Remove("contextWindow");
        var result = await Scan(initial.Overlay, absent);
        Check(result.Complete && !result.Changed && Model(result)["prices"]![0]!["amount"]!.GetValue<decimal>() == 1m, "Missing price erased previous data.");
        var unknown = await Scan(initial.Overlay, Row(amount: null));
        Check(!unknown.Changed, "Unknown price must not replace a known rate.");
        var fresh = await Scan(CatalogJson.EmptyOverlay(), Row(amount: null));
        Check(fresh.Complete && Model(fresh)["prices"]![0]!["amount"] is null && fresh.PriceChanges.Count == 0, "Unknown became zero or a known price change.");
    }
    private static async Task Normalization()
    {
        var initial = await Scan(CatalogJson.EmptyOverlay(), Row(amount: 0.000002m, unit: "token"));
        var second = await Scan(initial.Overlay, Row(amount: 0.002m, unit: "thousand-tokens"));
        var third = await Scan(second.Overlay, Row(amount: 2m));
        Check(!second.Changed && !third.Changed && second.PriceChanges.Count == 0, "Equivalent units produced a false diff.");
    }
    private static async Task PriceDeltas()
    {
        var old = Row(amount: 1m); old["prices"]!.AsArray().Add(Rate(4m, kind: "output"));
        var initial = await Scan(CatalogJson.EmptyOverlay(), old);
        var next = Row(amount: 2m); next["prices"]!.AsArray().Add(Rate(3m, kind: "output"));
        var result = await Scan(initial.Overlay, next);
        Check(result.PriceChanges.Count == 2 && result.PriceChanges.Any(x => x.Absolute == 1m && x.Percent == 100m)
            && result.PriceChanges.Any(x => x.Absolute == -1m && x.Percent == -25m), "Deltas are incorrect.");
        Check(ChangeReport.Markdown(result).Contains("-25%", StringComparison.Ordinal), "Review is missing percentage delta.");
        var zero = await Scan(CatalogJson.EmptyOverlay(), Row(amount: 0m));
        var nonzero = await Scan(zero.Overlay, Row(amount: 1m));
        Check(nonzero.PriceChanges.Single().Percent is null, "Zero baseline has no finite percentage.");
    }
    private static async Task Dimensions()
    {
        var row = Row(); var prices = row["prices"]!.AsArray();
        prices.Add(Rate(2m, currency: "EUR")); prices.Add(Rate(3m, tier: "batch")); prices.Add(Rate(4m, region: "eu"));
        prices.Add(Rate(0.1m, kind: "cache-read"));
        var conditional = Rate(5m); conditional["conditions"] = new JsonObject { ["minPromptTokens"] = 100000 }; prices.Add(conditional);
        var initial = await Scan(CatalogJson.EmptyOverlay(), row);
        Check(initial.Complete && Model(initial)["prices"]!.AsArray().Count == 6, "Price dimensions were merged.");
        var update = await Scan(initial.Overlay, Row(amount: 1.5m));
        Check(update.PriceChanges.Count == 1 && Model(update)["prices"]!.AsArray().Count == 6, "Missing alternate rates were erased or compared across dimensions.");
    }
    private static async Task UnknownProtocol()
    {
        var row = Row(); row["api"] = "new-wire-v9";
        var result = await Scan(CatalogJson.EmptyOverlay(), row);
        Check(result.Complete && Model(result)["metadata"]!["runtimeCandidate"]!.GetValue<bool>() == false && result.Warnings.Count > 0, "Unknown protocol was promoted to runtime support.");
    }
    private static async Task Retention()
    {
        var initial = await Scan(CatalogJson.EmptyOverlay(), Row(), Row("retired"));
        var result = await Scan(initial.Overlay, Row());
        Check(!result.Changed && Model(result, "retired") is not null, "Absent model was deleted.");
        var config = Config(Provider() with { ExcludedModels = ["alpha"] });
        var excluded = await CatalogEngine.ScanAsync(config, initial.Overlay, new FixtureFetcher(new Dictionary<string, string> { ["test"] = Data(Row(amount: 999m), Row("retired")) }));
        Check(!excluded.Changed, "Excluded model changed.");
    }
    private static async Task InvalidData()
    {
        foreach (var body in new[] { Data(Row(), Row()), Data(Row(amount: -1m)), Data(Row(unit: "guessed-unit")), "{\"data\":[]}", "{\"data\":[],\"data\":[]}", "{\"data\":[],\"has_more\":true}" })
        {
            var result = await CatalogEngine.ScanAsync(Config(), CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["test"] = body }));
            Check(!result.Complete && !result.Changed && result.Errors.Count > 0, "Invalid source was treated as success.");
        }
    }
    private static async Task AtomicFailure()
    {
        var result = await CatalogEngine.ScanAsync(Config(Provider("a"), Provider("z")), CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["a"] = Data(Row()) }));
        Check(!result.Complete && !result.Changed && result.Overlay["providers"]!.AsObject().Count == 0 && result.PriceChanges.Count == 0, "Partial scan escaped atomic failure.");
    }
    private static async Task PricingPage()
    {
        var config = Config(Provider() with { Adapter = "official-page-json", JsonScriptId = "catalog" });
        var valid = "<html><script id=\"catalog\" type=\"application/json\">" + Data(Row()) + "</script></html>";
        var result = await CatalogEngine.ScanAsync(config, CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["test"] = valid }));
        Check(result.Complete && result.Changed, "Structured official page failed.");
        foreach (var html in new[] { "<p>Model alpha: $2 per million maybe</p>", valid + valid })
        {
            var failure = await CatalogEngine.ScanAsync(config, CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["test"] = html }));
            Check(!failure.Complete && !failure.Changed, "Ambiguous HTML was accepted.");
        }
    }
    private static async Task Idempotence()
    {
        var initial = await Scan(CatalogJson.EmptyOverlay(), Row(), Row("beta"));
        var reordered = await Scan(initial.Overlay, Row("beta"), Row());
        Check(!reordered.Changed, "Reordering provider rows changed provenance-only fields.");
        var result = await CatalogEngine.ScanAsync(Config(), initial.Overlay, new TimestampFetcher(Data(Row(), Row("beta"))));
        Check(!result.Changed && CatalogJson.Canonical(initial.Overlay) == CatalogJson.Canonical(result.Overlay), "Retrieval timestamp changed overlay.");
    }
    private sealed class TimestampFetcher(string data) : ISourceFetcher
    { public Task<SourceDocument> FetchAsync(ProviderConfig config, CancellationToken cancellationToken) => Task.FromResult(new SourceDocument(config.Endpoint, data, "2099-01-01T00:00:00Z")); }

    private static async Task IssueIdentity()
    {
        var old = Row(); old["prices"]!.AsArray().Add(Rate(1m, kind: "output"));
        var initial = await Scan(CatalogJson.EmptyOverlay(), old);
        var first = Row(amount: 2m); first["prices"]!.AsArray().Add(Rate(3m, kind: "output"));
        var second = Row(); second["prices"] = new JsonArray(Rate(3.000000m, kind: "output"), Rate(0.0000020000m, "token"));
        var a = await Scan(initial.Overlay, first); var b = await Scan(initial.Overlay, second);
        Check(a.Fingerprint == b.Fingerprint, "Equivalent number spellings/rate order produced duplicate issue identities.");
    }

    private static async Task HttpAdapter()
    {
        var handler = new DelegateHandler(request =>
        {
            Check(request.RequestUri?.Host == "official.example.test", "Wrong endpoint.");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "offline-test-token", "Missing environment credential.");
            return Reply(Data(Row()));
        });
        const string variable = "PISHARP_CATALOG_SELF_TEST_TOKEN";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "offline-test-token");
            using var client = new HttpClient(handler);
            var response = await new HttpSourceFetcher(client).FetchAsync(Provider() with { ApiKeyEnvironmentVariable = variable }, default);
            Check(response.StatusCode == 200 && response.Sha256.Length == 64, "Evidence missing.");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
        using var denied = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)));
        await Throws(() => new HttpSourceFetcher(denied).FetchAsync(Provider(), default));
        await Throws(() => new HttpSourceFetcher(denied).FetchAsync(Provider() with { Endpoint = "https://attacker.example.test/models" }, default));
    }
    private static ProviderConfig OpenRouterConfig() => new() { Id = "openrouter", Enabled = true, Adapter = "openrouter", Endpoint = "https://openrouter.ai/api/v1/models", OfficialHosts = ["openrouter.ai"] };
    private static JsonObject OpenRouterRow() => JsonNode.Parse("""
        {"id":"example/alpha","name":"Alpha","context_length":8192,"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"pricing":{"prompt":"0.000002","completion":"0.000004","input_cache_read":"0.0000001","image":"0.03","input_cache_write_1h":"0.000005","overrides":[{"min_prompt_tokens":200000,"prompt":"0.000003"}]}}
        """)!.AsObject();
    private static async Task OpenRouter()
    {
        var result = await CatalogEngine.ScanAsync(Config(OpenRouterConfig()), CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["openrouter"] = Data(OpenRouterRow()) }));
        Check(result.Complete && result.PriceChanges.Count == 6, "OpenRouter pricing fields not parsed.");
        var prices = Model(result, "example/alpha", "openrouter")["prices"]!.AsArray();
        Check(prices.Any(p => p!["kind"]!.GetValue<string>() == "input" && p["amount"]!.GetValue<decimal>() == 2m), "Input token price not normalized.");
        Check(prices.Any(p => p!["kind"]!.GetValue<string>() == "image-input" && p["amount"]!.GetValue<decimal>() == 0.03m), "Image price treated as tokens.");
        Check(prices.Any(p => p!["tier"]!.GetValue<string>() == "catalog-override" && p["conditions"]!["min_prompt_tokens"]!.GetValue<int>() == 200000), "Conditional tier lost.");
    }
    private static async Task OpenRouterUnknown()
    {
        var row = OpenRouterRow(); row["pricing"]!["new_unrecognized_dimension"] = "0.05";
        var result = await CatalogEngine.ScanAsync(Config(OpenRouterConfig()), CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["openrouter"] = Data(row) }));
        Check(!result.Complete && !result.Changed, "Unknown price field was silently ignored.");
    }
    private static async Task OpenRouterDiscount()
    {
        var row = OpenRouterRow(); row["pricing"]!["discount"] = 0.25m;
        var result = await CatalogEngine.ScanAsync(Config(OpenRouterConfig()), CatalogJson.EmptyOverlay(), new FixtureFetcher(new Dictionary<string, string> { ["openrouter"] = Data(row) }));
        Check(result.Complete && result.PriceChanges.Any(p => p.Dimension["kind"]!.GetValue<string>() == "input" && p.NewAmount == 1.5m), "Explicit discount not applied.");
        row["pricing"]!["discount"] = 1.5m;
        var invalid = await CatalogEngine.ScanAsync(Config(OpenRouterConfig()), result.Overlay, new FixtureFetcher(new Dictionary<string, string> { ["openrouter"] = Data(row) }));
        Check(!invalid.Complete && !invalid.Changed, "Invalid discount was accepted.");
    }
    private static async Task PublicationGuards()
    {
        var result = await Scan(CatalogJson.EmptyOverlay(), Row()); var remote = new FakeRemote(); var checkout = new FakeCheckout(); var config = new GitHubConfig { Repository = "owner/repo" };
        await Throws(() => CatalogPublisher.PublishAsync(result, config, checkout, remote, false));
        await Throws(() => CatalogPublisher.PublishAsync(result with { Complete = false }, config, checkout, remote, true));
        var unchanged = await CatalogPublisher.PublishAsync(result with { Changed = false }, config, checkout, remote, true);
        Check(checkout.Calls == 0 && remote.Calls == 0 && !unchanged.Published, "Guard permitted publication.");
    }
    private static async Task PublicationFailure()
    {
        var result = await Scan(CatalogJson.EmptyOverlay(), Row()); var remote = new FakeRemote();
        await Throws(() => CatalogPublisher.PublishAsync(result, new GitHubConfig(), new FakeCheckout { Fail = true }, remote, true));
        Check(remote.Calls == 0, "Failed push created a remote record.");
        remote.Fail = true;
        await Throws(() => CatalogPublisher.PublishAsync(result, new GitHubConfig(), new FakeCheckout(), remote, true));
    }
    private static async Task GitHubDeduplication()
    {
        var handler = new GitHubHandler(); using var client = new HttpClient(handler); var config = new GitHubConfig { Repository = "owner/repo" };
        var remote = new GitHubRemote(client, config, "fake-offline-token");
        var result = await Scan(CatalogJson.EmptyOverlay(), Row());
        var first = await CatalogPublisher.PublishAsync(result, config, new FakeCheckout(), remote, true);
        var second = await CatalogPublisher.PublishAsync(result, config, new FakeCheckout(), remote, true);
        Check(handler.Issues.Count == 1 && handler.Pulls.Count == 1 && first.IssueUrl == second.IssueUrl && first.PullRequestUrl == second.PullRequestUrl, "Duplicate issue or PR created.");
        Check(handler.Issues[0]["body"]!.GetValue<string>().Contains(first.PullRequestUrl!, StringComparison.Ordinal), "Issue does not link PR.");
        Check(handler.Pulls[0]["body"]!.GetValue<string>().Contains(first.IssueUrl!, StringComparison.Ordinal), "PR does not link issue.");
        Check(handler.Pulls[0]["draft"]!.GetValue<bool>(), "PR must be draft.");
    }
    private static async Task GitHubClosedPullRequest()
    {
        var handler = new GitHubHandler(); using var client = new HttpClient(handler);
        var remote = new GitHubRemote(client, new GitHubConfig { Repository = "owner/repo" }, "fake");
        await remote.EnsurePullRequestAsync("body", default);
        handler.Pulls[0]["state"] = "closed";
        await Throws(() => remote.EnsurePullRequestAsync("body", default));
        Check(handler.Pulls.Count == 1, "Closed unmerged PR was duplicated.");
    }
    private static async Task GitHubNoChangeReconciliation()
    {
        var handler = new GitHubHandler(); using var client = new HttpClient(handler);
        var config = new GitHubConfig { Repository = "owner/repo" };
        var remote = new GitHubRemote(client, config, "fake"); var checkout = new FakeCheckout();
        var baseline = await Scan(CatalogJson.EmptyOverlay(), Row(amount: 1m));
        var unchanged = await Scan(baseline.Overlay, Row(amount: 1m));
        var first = await CatalogPublisher.PublishAsync(unchanged, config, checkout, remote, true);
        Check(!first.Published && handler.Writes == 0 && checkout.Calls == 0, "Unchanged first run performed a write.");
        var proposal = await Scan(baseline.Overlay, Row(amount: 2m));
        await CatalogPublisher.PublishAsync(proposal, config, checkout, remote, true);
        var issueBody = handler.Issues.Single()["body"]!.GetValue<string>();
        handler.Pulls.Single()["draft"] = false;
        var beforeIncomplete = handler.Writes;
        await Throws(() => CatalogPublisher.PublishAsync(unchanged with { Complete = false }, config, checkout, remote, true));
        Check(handler.Writes == beforeIncomplete && handler.Pulls.Single()["state"]!.GetValue<string>() == "open", "Incomplete scan changed the proposal.");
        await CatalogPublisher.PublishAsync(unchanged, config, checkout, remote, true);
        var retired = handler.Pulls.Single();
        Check(retired["state"]!.GetValue<string>() == "closed" && retired["draft"]!.GetValue<bool>() &&
            retired["body"]!.GetValue<string>().Contains("pisharp-model-catalog-superseded:v1", StringComparison.Ordinal), "Stale proposal was not retired as a draft.");
        Check(checkout.Calls == 1 && handler.Issues.Single()["body"]!.GetValue<string>() == issueBody, "Retirement wrote a commit or changed historical price evidence.");
        var afterRetirement = handler.Writes;
        await CatalogPublisher.PublishAsync(unchanged, config, checkout, remote, true);
        Check(handler.Writes == afterRetirement, "Repeated retirement wrote again.");
        var next = await Scan(baseline.Overlay, Row(amount: 3m));
        await CatalogPublisher.PublishAsync(next, config, checkout, remote, true);
        Check(handler.Pulls.Count == 1 && retired["state"]!.GetValue<string>() == "open" && retired["draft"]!.GetValue<bool>() &&
            !retired["body"]!.GetValue<string>().Contains("pisharp-model-catalog-superseded:v1", StringComparison.Ordinal), "Later change did not reuse the retired draft.");
        Check(checkout.Calls == 2 && handler.Issues[0]["body"]!.GetValue<string>() == issueBody, "Resumption changed historical price evidence.");
    }
    private static async Task GitHubReconciliationGuards()
    {
        var handler = new GitHubHandler(); using var client = new HttpClient(handler);
        var config = new GitHubConfig { Repository = "owner/repo" };
        var remote = new GitHubRemote(client, config, "fake"); var checkout = new FakeCheckout();
        var baseline = await Scan(CatalogJson.EmptyOverlay(), Row());
        var unchanged = await Scan(baseline.Overlay, Row());
        var changed = await Scan(baseline.Overlay, Row(amount: 2m));
        await remote.EnsurePullRequestAsync("body", default);
        var markedBody = handler.Pulls.Single()["body"]!.GetValue<string>();
        handler.Pulls.Single()["body"] = "An unmarked human proposal";
        var writes = handler.Writes;
        await Throws(() => CatalogPublisher.PublishAsync(unchanged, config, checkout, remote, true));
        await Throws(() => CatalogPublisher.PublishAsync(changed, config, checkout, remote, true));
        Check(handler.Writes == writes && checkout.Calls == 0 && handler.Pulls.Single()["state"]!.GetValue<string>() == "open", "Unmarked PR was modified.");
        handler.Pulls.Single()["body"] = markedBody;
        handler.Pulls.Single()["state"] = "closed";
        await Throws(() => CatalogPublisher.PublishAsync(changed, config, checkout, remote, true));
        await CatalogPublisher.PublishAsync(unchanged, config, checkout, remote, true);
        Check(handler.Writes == writes && checkout.Calls == 0 && handler.Pulls.Single()["state"]!.GetValue<string>() == "closed", "Human-closed PR was reopened or changed.");
    }
    private static async Task GitHubDraftConversion()
    {
        var handler = new GitHubHandler(); using var client = new HttpClient(handler); var config = new GitHubConfig { Repository = "owner/repo" };
        var remote = new GitHubRemote(client, config, "fake");
        await remote.EnsurePullRequestAsync("body", default); handler.Pulls[0]["draft"] = false;
        var checkout = new FakeCheckout { BeforeCommit = () => Check(handler.Pulls[0]["draft"]!.GetValue<bool>(), "Pushed before establishing draft state.") };
        var scan = await Scan(CatalogJson.EmptyOverlay(), Row());
        await CatalogPublisher.PublishAsync(scan, config, checkout, remote, true);
        Check(handler.DraftConversions == 1 && handler.Pulls.Count == 1 && checkout.Calls == 1, "Ready PR was not converted/reused.");
    }
    private static async Task GitHubDraftConversionFailure()
    {
        var handler = new GitHubHandler { FailDraftConversion = true }; using var client = new HttpClient(handler); var config = new GitHubConfig { Repository = "owner/repo" };
        var remote = new GitHubRemote(client, config, "fake");
        await remote.EnsurePullRequestAsync("body", default); handler.Pulls[0]["draft"] = false;
        var checkout = new FakeCheckout(); var scan = await Scan(CatalogJson.EmptyOverlay(), Row());
        await Throws(() => CatalogPublisher.PublishAsync(scan, config, checkout, remote, true));
        Check(checkout.Calls == 0 && handler.Issues.Count == 0, "Failed conversion allowed branch or issue writes.");
    }
    private static async Task OrphanBranchRecovery()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "pisharp-catalog-orphan-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        GitCheckout? checkout = null;
        try
        {
            const string sha = "1111111111111111111111111111111111111111";
            var scan = await Scan(CatalogJson.EmptyOverlay(), Row()); var calls = new List<string[]>();
            Task<string> Git(string root, string[] args, CancellationToken token)
            {
                calls.Add(args);
                return Task.FromResult(args[0] switch
                {
                    "ls-remote" => sha + "\trefs/heads/automation/model-catalog\n",
                    "rev-parse" => sha + "\n", "log" => "Maintain model catalog\nPiSharp-Catalog-Maintainer: v1\n",
                    "show" => args[1].EndsWith("model-catalog-report.md", StringComparison.Ordinal)
                        ? ChangeReport.Markdown(scan).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
                        : CatalogJson.Canonical(scan.Overlay), _ => ""
                });
            }
            var config = new GitHubConfig { Repository = "owner/repo" };
            checkout = await GitCheckout.CloneAsync(config, "fake", "catalog-maintained/models.overlay.json", workspace, true, default, Git);
            var handler = new GitHubHandler(); using var client = new HttpClient(handler);
            var result = await CatalogPublisher.PublishAsync(scan, config, checkout, new GitHubRemote(client, config, "fake"), true);
            Check(result.Published && result.Commit == sha && handler.Pulls.Count == 1, "Orphan branch did not recover to a PR.");
            Check(calls.Any(x => x[0] == "ls-remote") && calls.Any(x => x[0] == "fetch") && calls.All(x => x[0] is not ("push" or "commit")), "Existing identical owned branch was rewritten.");
        }
        finally { if (checkout is not null) Directory.Delete(checkout.Root); Directory.Delete(workspace); }
    }
    private static async Task ChangedBaseRefreshesReport()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "pisharp-catalog-report-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        GitCheckout? checkout = null;
        try
        {
            const string oldSha = "1111111111111111111111111111111111111111";
            const string newSha = "2222222222222222222222222222222222222222";
            var originalBase = await Scan(CatalogJson.EmptyOverlay(), Row(amount: 1m));
            var oldProposal = await Scan(originalBase.Overlay, Row(amount: 2m));
            var advancedBase = await Scan(CatalogJson.EmptyOverlay(), Row(amount: 1.5m));
            var nextProposal = await Scan(advancedBase.Overlay, Row(amount: 2m));
            Check(CatalogJson.Canonical(oldProposal.Overlay) == CatalogJson.Canonical(nextProposal.Overlay), "Regression setup requires identical proposals.");
            Check(nextProposal.PriceChanges.Single().OldAmount == 1.5m, "Regression setup requires changed baseline price.");
            var calls = new List<string[]>();
            Task<string> Git(string root, string[] args, CancellationToken token)
            {
                calls.Add(args);
                return Task.FromResult(args[0] switch
                {
                    "ls-remote" => oldSha + "\trefs/heads/automation/model-catalog\n",
                    "rev-parse" => (args[1] == "HEAD" ? newSha : oldSha) + "\n",
                    "log" => "Maintain model catalog\nPiSharp-Catalog-Maintainer: v1\n",
                    "show" => args[1].EndsWith("model-catalog-report.md", StringComparison.Ordinal)
                        ? ChangeReport.Markdown(oldProposal) : CatalogJson.Canonical(oldProposal.Overlay),
                    _ => ""
                });
            }
            checkout = await GitCheckout.CloneAsync(new GitHubConfig { Repository = "owner/repo" }, "fake", "catalog-maintained/models.overlay.json", workspace, true, default, Git);
            var commit = await checkout.CommitAndPushAsync(nextProposal.Overlay, ChangeReport.Markdown(nextProposal), default);
            Check(commit == newSha && calls.Any(x => x[0] == "commit") && calls.Any(x => x[0] == "push" && x.Contains("--force-with-lease=refs/heads/automation/model-catalog:" + oldSha)), "Material report update did not commit and push.");
            var saved = await File.ReadAllTextAsync(Path.Combine(checkout.Root, "catalog-maintained", "model-catalog-report.md"));
            Check(saved == ChangeReport.Markdown(nextProposal).Replace("\r\n", "\n", StringComparison.Ordinal) && saved.Contains("| 1.5 | 2 | 0.5 | 33.333333% |", StringComparison.Ordinal), "Committed full report retained stale 1-to-2 delta.");
        }
        finally
        {
            if (checkout is not null)
            {
                var output = Path.Combine(checkout.Root, "catalog-maintained");
                File.Delete(Path.Combine(output, "model-catalog-report.md")); File.Delete(Path.Combine(output, "models.overlay.json"));
                if (Directory.Exists(output)) Directory.Delete(output);
                Directory.Delete(checkout.Root);
            }
            Directory.Delete(workspace);
        }
    }
    private static async Task GitHubPagination()
    {
        var handler = new GitHubHandler();
        for (var i = 1; i <= 100; i++) handler.Issues.Add(new JsonObject { ["number"] = i, ["html_url"] = "https://github.com/owner/repo/issues/" + i, ["body"] = "unrelated" });
        handler.Issues.Add(new JsonObject { ["number"] = 101, ["html_url"] = "https://github.com/owner/repo/issues/101", ["body"] = "<!-- pisharp-model-pricing:known -->\nbody" });
        using var client = new HttpClient(handler); var remote = new GitHubRemote(client, new GitHubConfig { Repository = "owner/repo" }, "fake");
        var found = await remote.EnsurePriceIssueAsync("known", "body", default);
        Check(found.Number == 101 && handler.Issues.Count == 101, "Pagination missed existing issue.");
    }
    private static Task TargetBoundary()
    {
        foreach (var path in new[] { "src/PiSharp.Cli/Models/openrouter.json", "catalog-maintained/../pinned.overlay.json", "catalog-maintained/.git/x.overlay.json", "C:/catalog-maintained/attack.overlay.json" })
        {
            var failed = false; try { (Config() with { OverlayPath = path }).Validate(); } catch (InvalidDataException) { failed = true; }
            Check(failed, "Unsafe output target accepted.");
        }
        return Task.CompletedTask;
    }
    private static async Task BundledFixture()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
        var result = await CatalogEngine.ScanAsync(CatalogJson.Read<MaintainerConfig>(fixture["config"]!.ToJsonString()), fixture["baseline"]!.AsObject(), Workflow.Fixture(fixture));
        Check(result.Complete && result.Changed && result.PriceChanges.Any(x => x.Percent < 0) && result.PriceChanges.Any(x => x.Percent > 0), "Bundled fixture does not exercise changes.");
        var repeated = await CatalogEngine.ScanAsync(CatalogJson.Read<MaintainerConfig>(fixture["config"]!.ToJsonString()), result.Overlay, Workflow.Fixture(fixture));
        Check(!repeated.Changed, "Bundled fixture is not idempotent.");
    }
    private static async Task SeedPinned()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-catalog-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "shards"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "openrouter.json");
        const string pinned = """
            {"anthropic-messages":{"chat:example/alpha":{"id":"example/alpha","name":"Alpha","provider":"openrouter","type":"chat","api":"anthropic-messages","baseUrl":"https://openrouter.ai/api","reasoning":true,"contextWindow":8192,"maxTokens":4096,"input":["text"],"cost":{"input":2,"output":4,"cacheRead":0.1,"cacheWrite":0.2},"compat":{"specialRoute":true}}}}
            """;
        await File.WriteAllTextAsync(path, pinned);
        try
        {
            var config = Config(OpenRouterConfig()) with { BaselineCatalogDirectory = "shards" };
            var notes = new List<string>();
            var baseline = await PinnedBaseline.SeedMissingProvidersAsync(config, CatalogJson.EmptyOverlay(), root, notes);
            var row = OpenRouterRow(); row["pricing"]!["prompt"] = "0.000003"; row["pricing"]!["completion"] = "0.000003";
            var result = await CatalogEngine.ScanAsync(config, baseline, new FixtureFetcher(new Dictionary<string, string> { ["openrouter"] = Data(row) }));
            Check(result.Complete && result.PriceChanges.Any(x => x.OldAmount == 2m && x.NewAmount == 3m && x.Percent == 50m)
                && result.PriceChanges.Any(x => x.OldAmount == 4m && x.NewAmount == 3m && x.Percent == -25m), "Initial refresh missed pinned price deltas.");
            var metadata = Model(result, "example/alpha", "openrouter")["metadata"]!;
            Check(metadata["api"]!.GetValue<string>() == "anthropic-messages" && metadata["baseUrl"]!.GetValue<string>() == "https://openrouter.ai/api"
                && metadata["compat"]!["specialRoute"]!.GetValue<bool>(), "Existing route or compatibility metadata changed.");
            Check(await File.ReadAllTextAsync(path) == pinned && notes.Count == 1, "Seed modified pinned source.");
            await File.WriteAllTextAsync(path, "invalid");
            var reused = await PinnedBaseline.SeedMissingProvidersAsync(config, baseline, root, []);
            Check(CatalogJson.Canonical(reused) == CatalogJson.Canonical(baseline), "Existing maintained provider was reseeded.");
            await Throws(() => PinnedBaseline.SeedMissingProvidersAsync(config, CatalogJson.EmptyOverlay(), root, []));
        }
        finally { File.Delete(path); Directory.Delete(directory); Directory.Delete(root); }
    }
    private static async Task SeedPinnedMultiType()
    {
        // These chat/image definitions share an id in the repository's real OpenRouter shard.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "pinned-multi-type.json"));
        var catalog = PiSharp.AI.Catalogs.FrozenModelCatalog.ReadProviderJson("openrouter", bytes);
        var notes = new List<string>();
        var projected = PinnedBaseline.Project(OpenRouterConfig(), catalog, "shards/openrouter.json", notes: notes);
        var model = projected["google/gemini-3-pro-image"]!.AsObject();
        var metadata = model["metadata"]!.AsObject();
        Check(projected.Count == 1 && metadata["type"]!.GetValue<string>() == "chat" &&
            metadata["api"]!.GetValue<string>() == "openai-completions" && metadata["runtimeCandidate"]!.GetValue<bool>(),
            "Shared id did not retain the chat route.");
        Check(metadata["pinnedAlternates"] is JsonArray { Count: 1 } alternates &&
            JsonNode.DeepEquals(alternates[0], JsonNode.Parse(bytes)!["openrouter-images"]!["image:google/gemini-3-pro-image"]),
            "Alternate image definition was not preserved exactly.");
        Check(notes.Count == 1 && notes[0].Contains("metadata.pinnedAlternates", StringComparison.Ordinal), "Missing collision review note.");
        var reordered = JsonNode.Parse(bytes)!.AsObject();
        var image = reordered["openrouter-images"]!.DeepClone(); reordered.Remove("openrouter-images"); reordered["openrouter-images"] = image;
        var reorderedCatalog = PiSharp.AI.Catalogs.FrozenModelCatalog.ReadProviderJson("openrouter", System.Text.Encoding.UTF8.GetBytes(reordered.ToJsonString()));
        Check(CatalogJson.Canonical(projected["google/gemini-3-pro-image"]!["metadata"]) ==
            CatalogJson.Canonical(PinnedBaseline.Project(OpenRouterConfig(), reorderedCatalog, "shards/openrouter.json")["google/gemini-3-pro-image"]!["metadata"]),
            "Source group order changed the chosen route or retained evidence.");
        var row = OpenRouterRow(); row["id"] = "google/gemini-3-pro-image";
        var baseline = CatalogJson.EmptyOverlay(); baseline["providers"]!["openrouter"] = projected;
        var scan = await CatalogEngine.ScanAsync(Config(OpenRouterConfig()), baseline,
            new FixtureFetcher(new Dictionary<string, string> { ["openrouter"] = Data(row) }));
        Check(scan.Complete && JsonNode.DeepEquals(Model(scan, "google/gemini-3-pro-image", "openrouter")["metadata"]!["pinnedAlternates"], metadata["pinnedAlternates"]),
            "Refreshing a shared id lost its alternate pinned evidence.");
    }
    private static async Task SkipPinnedProviders()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-catalog-skipped-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "shards"); Directory.CreateDirectory(directory);
        var disabled = Path.Combine(directory, "disabled.json"); var excluded = Path.Combine(directory, "excluded.json");
        await File.WriteAllTextAsync(disabled, "broken disabled shard"); await File.WriteAllTextAsync(excluded, "broken excluded shard");
        try
        {
            var config = Config(Provider("disabled") with { Enabled = false }, Provider("excluded")) with
            { BaselineCatalogDirectory = "shards", ExcludedProviders = ["excluded"] };
            var notes = new List<string>(); var result = await PinnedBaseline.SeedMissingProvidersAsync(config, CatalogJson.EmptyOverlay(), root, notes);
            Check(result["providers"]!.AsObject().Count == 0 && notes.Count == 0, "Skipped providers were read or seeded.");
        }
        finally { File.Delete(disabled); File.Delete(excluded); Directory.Delete(directory); Directory.Delete(root); }
    }
    private sealed class FakeCheckout : ICatalogCheckout
    {
        public int Calls { get; private set; } public bool Fail { get; init; } public Action? BeforeCommit { get; init; }
        public Task<string> CommitAndPushAsync(JsonObject overlay, string markdown, CancellationToken token)
        { BeforeCommit?.Invoke(); Calls++; if (Fail) throw new InvalidOperationException("Simulated push failure."); return Task.FromResult("fake-commit"); }
    }
    private sealed class FakeRemote : ICatalogRemote
    {
        public int Calls { get; private set; } public bool Fail { get; set; }
        public Task<RemoteItem> EnsurePriceIssueAsync(string identity, string body, CancellationToken token) => Next();
        public Task<RemoteItem> EnsurePullRequestAsync(string body, CancellationToken token) => Next();
        public Task LinkIssueAsync(RemoteItem issue, string body, CancellationToken token) { Calls++; return Task.CompletedTask; }
        private Task<RemoteItem> Next() { Calls++; if (Fail) throw new InvalidOperationException("Simulated API failure."); return Task.FromResult(new RemoteItem(1, "https://github.com/owner/repo/issues/1")); }
    }
    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class GitHubHandler : HttpMessageHandler
    {
        public List<JsonObject> Issues { get; } = []; public List<JsonObject> Pulls { get; } = [];
        public int Writes { get; private set; }
        public bool FailDraftConversion { get; init; } public int DraftConversions { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.Host == "api.github.com" && request.Headers.Authorization?.Scheme == "Bearer", "GitHub auth or host missing.");
            var path = request.RequestUri.AbsolutePath; var list = path.Contains("/pulls", StringComparison.Ordinal) ? Pulls : Issues;
            var last = path.Split('/')[^1];
            if (request.Method == HttpMethod.Get && int.TryParse(last, out var number)) return Reply(list.Single(x => x["number"]!.GetValue<int>() == number).ToJsonString());
            if (request.Method == HttpMethod.Get)
            {
                var pageText = request.RequestUri.Query.Split('&').Single(x => x.StartsWith("page=", StringComparison.Ordinal)).Split('=')[1];
                var page = int.Parse(pageText, System.Globalization.CultureInfo.InvariantCulture);
                return Reply(new JsonArray(list.Skip((page - 1) * 100).Take(100).Select(x => x.DeepClone()).ToArray()).ToJsonString());
            }
            Writes++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (path == "/graphql")
            {
                DraftConversions++;
                if (FailDraftConversion) return Reply("{\"errors\":[{\"message\":\"not allowed\"}]}");
                var id = body["variables"]!["id"]!.GetValue<string>();
                Pulls.Single(x => x["node_id"]!.GetValue<string>() == id)["draft"] = true;
                return Reply("{\"data\":{\"convertPullRequestToDraft\":{\"pullRequest\":{\"isDraft\":true}}}}");
            }
            if (request.Method == HttpMethod.Post)
            {
                body["state"] = "open";
                body["node_id"] = (list == Pulls ? "PR_" : "I_") + (list.Count + 1);
                body["number"] = list.Count + 1; body["html_url"] = "https://github.com/owner/repo/" + (list == Pulls ? "pull/" : "issues/") + (list.Count + 1);
                list.Add(body); return Reply(body.ToJsonString());
            }
            if (request.Method == HttpMethod.Patch)
            {
                var item = list.Single(x => x["number"]!.GetValue<int>() == int.Parse(last, System.Globalization.CultureInfo.InvariantCulture));
                foreach (var (key, value) in body) item[key] = value?.DeepClone(); return Reply(item.ToJsonString());
            }
            throw new InvalidOperationException("Unexpected GitHub operation.");
        }
    }
}
