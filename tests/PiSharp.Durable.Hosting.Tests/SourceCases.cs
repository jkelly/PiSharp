using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

// Decision 0010, M3: sources, safe retrieval, parsers and the source check job, against fixture HTTP on 127.0.0.1.
internal static partial class Program
{
    private static RetrievalOptions Local(RetrievalLimits? limits = null, IReadOnlyList<IRetrievalProvider>? fallbacks = null,
        IReadOnlyDictionary<string, string>? credentials = null, string? github = null) => new()
    {
        AllowedPrivateHosts = ["127.0.0.1"],
        Limits = limits ?? new RetrievalLimits(),
        Fallbacks = fallbacks ?? [],
        Credentials = credentials ?? new Dictionary<string, string>(),
        GitHubApi = github ?? "https://api.github.com",
    };

    private static string Rss(params (string Guid, string Title, string Text)[] items) =>
        "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>Fixture</title>" +
        string.Concat(items.Select(item => $"<item><guid isPermaLink=\"false\">{item.Guid}</guid><title>{SecurityElement.Escape(item.Title)}</title>" +
            $"<link>https://example.com/{item.Guid}</link><description>{SecurityElement.Escape(item.Text)}</description></item>")) + "</channel></rss>";

    private static async Task<JsonObjectValue> CheckSourcesAsync(HostedSession session, JsonObjectValue input, string status = "completed", JsonObjectValue? budget = null)
    {
        var start = await session.StartExecutionAsync(1, "pisharp-source-check", input, budget, null);
        return await WaitForRunAsync(session, start.TaskId.Value, status);
    }

    private static JsonObjectValue Checked(params string[] ids) => JsonValue.Object([new("sourceIds", JsonValue.Array(ids.Select(id => (JsonValue)id)))]);

    private static JsonObjectValue SourceResult(JsonObjectValue run, string id) =>
        Jobs.Items((JsonObjectValue)run["result"], "sources").OfType<JsonObjectValue>().Single(result => Jobs.String(result, "sourceId") == id);

    private static JsonObjectValue Feed(string id, string url, string extra = "") => Json($$"""{"id":"{{id}}","kind":"feed","url":"{{url}}"{{extra}}}""");

    /// <summary>A provider the test controls; it costs a credit, so its interrupted calls are uncertain.</summary>
    private sealed class TestProvider(string id, Func<RetrievalRequest, CancellationToken, Task<RetrievalResponse>> fetch) : IRetrievalProvider
    {
        public int Calls;
        public string Id => id;
        public IReadOnlyDictionary<string, long> Estimate(RetrievalRequest request) => new Dictionary<string, long> { ["requests"] = 1, ["credits:test"] = 1 };
        public Task<RetrievalResponse> FetchAsync(RetrievalRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return fetch(request, cancellationToken);
        }
    }

    internal static IEnumerable<(string, Func<Task>)> SourceCases()
    {
        yield return Case("sources.address-policy-refuses-non-public-addresses", () =>
        {
            foreach (var address in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0",
                "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "fc00::1", "fd12:3456::1", "fec0::1", "ff02::1", "::ffff:127.0.0.1", "::ffff:10.0.0.1",
                "64:ff9b::a00:1", "2002:a00:1::", "::7f00:1", "2001::1", "198.18.0.1" })
                Check(AddressPolicy.NonPublic(IPAddress.Parse(address)) is not null, address + " is not public");
            foreach (var address in new[] { "8.8.8.8", "1.1.1.1", "172.32.0.1", "100.128.0.1", "2606:4700:4700::1111", "::ffff:8.8.8.8", "2002:808:808::" })
                Equal(null, AddressPolicy.NonPublic(IPAddress.Parse(address)), address + " is public");
            foreach (var address in new[] { "169.254.169.254", "::ffff:169.254.169.254", "fd00:ec2::254", "100.100.100.200" })
                Check(AddressPolicy.IsMetadata(IPAddress.Parse(address)), address + " is a metadata address");
        });

        yield return Case("sources.crud-revisions-validation-and-tombstones", async () =>
        {
            await using var device = await TestDevice.OpenAsync("sources-crud", new DeviceSetup
            {
                Retrieval = Local(new RetrievalLimits { MaxPages = 3 }, credentials: new Dictionary<string, string> { ["gh"] = "Bearer x" }),
            });
            var sources = device.Host.Sources;
            var invalid = Throws<HostValidationException>(() => sources.Create(Json("""
                {"id":"Bad Id","kind":"rss","maxPages":9,"credentialRef":"missing","extra":1}
                """)), "an invalid source");
            var paths = invalid.Fields.Select(field => field.Path).ToHashSet();
            foreach (var path in new[] { "/id", "/kind", "/maxPages", "/credentialRef", "/extra" }) Check(paths.Contains(path), "field error at " + path + ": " + invalid.Message);
            Check(Throws<HostValidationException>(() => sources.Create(Json("""{"id":"x","kind":"feed"}""")), "a feed without a URL")
                .Fields.Any(field => field.Path == "/url"), "feeds need a URL");
            Check(Throws<HostValidationException>(() => sources.Create(Json("""{"id":"x","kind":"github-releases","url":"https://example.com"}""")), "releases with a URL")
                .Fields.Any(field => field.Path == "/repository"), "releases need a repository");
            Throws<HostValidationException>(() => sources.Create(Json("""{"id":"x","kind":"feed","url":"file:///etc/passwd"}""")), "a file URL");

            var (created, replayed) = sources.Create(Feed("news", "https://example.com/feed.xml"), "create-news");
            Check(!replayed, "first create");
            Equal(1L, created.Revision, "revision 1");
            Equal(100L, Jobs.Long(created.Spec, "maxItems"), "default maxItems");
            Equal("news", Jobs.String(created.Spec, "title"), "the title defaults to the ID");
            Check(created.Enabled, "enabled by default");
            Equal("new", Jobs.String(created.ToJson()["state"] as JsonObjectValue ?? JsonObjectValue.Empty, "status"), "never checked");
            Check(sources.Create(Feed("news", "https://example.com/feed.xml"), "create-news").Replayed, "an idempotent replay");
            Throws<HostConflictException>(() => sources.Create(Feed("news", "https://example.com/other.xml"), "create-news"), "the key with another body");
            Throws<HostConflictException>(() => sources.Create(Feed("news", "https://example.com/feed.xml")), "a duplicate ID");

            Throws<HostConflictException>(() => sources.Update("news", Json("""{"title":"News"}"""), 7), "a stale revision");
            var updated = sources.Update("news", Json("""{"title":"News","enabled":false,"maxItems":5}"""), 1);
            Equal(2L, updated.Revision, "revision 2");
            Check(!updated.Enabled && Jobs.Long(updated.Spec, "maxItems") == 5, "changes merged");
            Check(Throws<HostValidationException>(() => sources.Update("news", Json("""{"kind":"page"}"""), 2), "a kind change").Fields.Any(field => field.Path == "/kind"),
                "the kind cannot change");
            var withCredential = sources.Create(Json("""{"id":"gh","kind":"github-releases","repository":"owner/repo","credentialRef":"gh"}""")).Source;
            var cleared = sources.Update("gh", Json("""{"credentialRef":null}"""), withCredential.Revision);
            Check(!cleared.Spec.ContainsKey("credentialRef"), "a null removes an optional field");

            Throws<HostConflictException>(() => sources.Delete("news", 1), "a stale delete");
            sources.Delete("news", 2);
            Equal(null, sources.Get("news"), "deleted");
            Check(sources.List().All(source => source.Id != "news"), "not listed");
            var revived = sources.Create(Feed("news", "https://example.com/feed.xml")).Source;
            Equal(4L, revived.Revision, "a tombstone comes back at its next revision");
        });

        yield return Case("sources.fetch-blocks-private-targets-redirects-and-oversized-bodies", async () =>
        {
            await using var other = new FixtureServer(request => FixtureResponse.Text("landed: " + (request.Header("Authorization") ?? "anonymous"), "text/plain"));
            var big = new byte[3 * 1024 * 1024];
            using var bomb = new MemoryStream();
            using (var gzip = new GZipStream(bomb, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(big);
            await using var server = new FixtureServer(request => request.Path switch
            {
                "/ok" => FixtureResponse.Text("fine", "text/plain"),
                var path when path.StartsWith("/loop/", StringComparison.Ordinal) =>
                    FixtureResponse.Code(302, new Dictionary<string, string> { ["Location"] = "/loop/" + (int.Parse(path[6..]) + 1) }),
                "/to-localhost" => FixtureResponse.Code(302, new Dictionary<string, string> { ["Location"] = $"http://localhost:{request.Header("Host")!.Split(':')[1]}/ok" }),
                "/to-other" => FixtureResponse.Code(307, new Dictionary<string, string> { ["Location"] = other.Url("/landing") }),
                "/large" => new FixtureResponse(200, new byte[3 * 1024 * 1024], new Dictionary<string, string> { ["Content-Type"] = "text/plain" }),
                "/bomb" => new FixtureResponse(200, bomb.ToArray(), new Dictionary<string, string> { ["Content-Type"] = "text/plain", ["Content-Encoding"] = "gzip" }),
                "/limited" => FixtureResponse.Code(429, new Dictionary<string, string> { ["Retry-After"] = "120" }),
                _ => FixtureResponse.Code(404),
            });
            using var open = new DirectRetrievalProvider(Local(), TimeProvider.System);
            using var closed = new DirectRetrievalProvider(new RetrievalOptions(), TimeProvider.System);
            Task<RetrievalResponse> Get(DirectRetrievalProvider provider, string url, string? authorization = null) =>
                provider.FetchAsync(new RetrievalRequest(new Uri(url), null, authorization, null, null, new RetrievalLimits()), CancellationToken.None);
            async Task<RetrievalException> Fails(DirectRetrievalProvider provider, string url, string failure)
            {
                var error = await ThrowsAsync<RetrievalException>(() => Get(provider, url), url);
                Equal(failure, error.Failure, url + ": " + error.Message);
                return error;
            }

            await Fails(closed, server.Url("/ok"), "blocked");
            await Fails(closed, $"http://localhost:{server.Port}/ok", "blocked");
            await Fails(open, "http://169.254.169.254/latest/meta-data/", "blocked");
            Equal(0, server.Count("/"), "nothing blocked reached the server");
            Equal("fine", Encoding.UTF8.GetString((await Get(open, server.Url("/ok"))).Body), "an allowed private host");
            await Fails(open, server.Url("/loop/0"), "redirects");
            Equal(6, server.Count("/loop/"), "five redirects followed, then refused");
            await Fails(open, server.Url("/to-localhost"), "blocked");
            var landed = await Get(open, server.Url("/to-other"), "Bearer secret");
            Equal("landed: anonymous", Encoding.UTF8.GetString(landed.Body), "authorization never crosses origins");
            Equal("Bearer secret", server.Requests.Single(request => request.Path == "/to-other").Header("Authorization"), "the first origin got it");
            await Fails(open, server.Url("/large"), "too_large");
            await Fails(open, server.Url("/bomb"), "too_large");
            var limited = await Fails(open, server.Url("/limited"), "rate_limited");
            Equal(TimeSpan.FromSeconds(120), limited.RetryAfter, "Retry-After is read");
            Check(limited.Transient, "429 is transient");
        });

        yield return Case("sources.parsers-handle-namespaces-malformed-missing-ids-and-duplicates", () =>
        {
            var baseUrl = new Uri("https://example.com/blog/feed.xml");
            var rss = SourceParsers.Feed(Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?>
                <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:dc="http://purl.org/dc/elements/1.1/"
                     xmlns:atom="http://www.w3.org/2005/Atom">
                  <channel><title>Blog</title><atom:link rel="next" href="/blog/feed.xml?page=2"/>
                    <item><guid isPermaLink="false">a-1</guid><title>First</title><link>/blog/first?id=42&amp;utm_source=x#top</link>
                      <content:encoded><![CDATA[<p>Hello <b>world</b></p><script>alert(1)</script>]]></content:encoded><pubDate>Tue, 10 Jun 2003 04:00:00 GMT</pubDate></item>
                    <item><title>Linked</title><link>https://Example.com:443/blog/linked</link><dc:date>2003-06-10T06:00:00+02:00</dc:date><description>Two</description></item>
                    <item><title>Bare</title><description>No ID or link</description></item>
                    <item><guid isPermaLink="false">a-1</guid><title>Duplicate</title></item>
                  </channel>
                </rss>
                """), baseUrl, 10_000);
            Equal(3, rss.Items.Count, "duplicates keep the first");
            Equal("id:a-1", rss.Items[0].ItemId, "guid identity");
            Equal("https://example.com/blog/first?id=42", rss.Items[0].Url, "resolved, tracking dropped, identity kept, fragment dropped");
            Equal("Hello world", rss.Items[0].Text, "HTML reduced to text, scripts dropped");
            Equal(1055217600000.0, rss.Items[0].PublishedAt, "RFC 822 date");
            Equal("url:https://example.com/blog/linked", rss.Items[1].ItemId, "link identity, canonical");
            Equal(1055217600000.0, rss.Items[1].PublishedAt, "dc:date with an offset");
            Check(rss.Items[2].ItemId.StartsWith("hash:", StringComparison.Ordinal) && rss.Items[2].PublishedAt is null, "hash identity, no date");
            Equal("https://example.com/blog/feed.xml?page=2", rss.NextPage?.ToString(), "RSS next page");

            var atom = SourceParsers.Feed(Encoding.UTF8.GetBytes("""
                <feed xmlns="http://www.w3.org/2005/Atom"><title>Atom</title><link rel="next" href="https://example.com/atom?page=2"/>
                  <entry><id>urn:uuid:1</id><title type="html">One &amp;lt;b&amp;gt;</title><link rel="alternate" href="https://example.com/one"/>
                    <updated>2024-01-02T03:04:05Z</updated><content type="html">&lt;p&gt;Body&lt;/p&gt;</content></entry>
                  <entry><title>No id</title><link href="https://example.com/two"/><summary type="text">Plain &lt;kept&gt;</summary></entry>
                </feed>
                """), baseUrl, 10_000);
            Equal(2, atom.Items.Count, "Atom entries");
            Equal("id:urn:uuid:1", atom.Items[0].ItemId, "Atom id");
            Equal("Body", atom.Items[0].Text, "Atom HTML content");
            Equal("Plain <kept>", atom.Items[1].Text, "Atom text content stays text");
            Equal("https://example.com/atom?page=2", atom.NextPage?.ToString(), "Atom next page");

            var rdf = SourceParsers.Feed(Encoding.UTF8.GetBytes("""
                <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/">
                  <channel rdf:about="https://example.com/"><title>RDF</title></channel>
                  <item rdf:about="https://example.com/r1"><title>R1</title><link>https://example.com/r1</link></item>
                </rdf:RDF>
                """), baseUrl, 10_000);
            Equal("id:https://example.com/r1", rdf.Items.Single().ItemId, "RSS 1.0 rdf:about");

            Equal("parse", Throws<RetrievalException>(() => SourceParsers.Feed(Encoding.UTF8.GetBytes("<rss><channel><item></channel></rss>"), baseUrl, 100),
                "malformed XML").Failure, "malformed XML is a parse failure");
            Equal("parse", Throws<RetrievalException>(() => SourceParsers.Feed(Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?><!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><rss><channel><title>&xxe;</title></channel></rss>
                """), baseUrl, 100), "a DTD").Failure, "a DTD is refused");
            Equal("parse", Throws<RetrievalException>(() => SourceParsers.Feed(Encoding.UTF8.GetBytes("<html/>"), baseUrl, 100), "not a feed").Failure, "not a feed");

            var releases = SourceParsers.GitHubReleases(Encoding.UTF8.GetBytes("""
                [{"id":3,"tag_name":"v3.0.0-rc1","name":"","prerelease":true,"draft":false,"html_url":"https://github.com/o/r/releases/v3","body":"rc"},
                 {"id":2,"tag_name":"v2.0.0","name":"Two","prerelease":false,"draft":false,"html_url":"https://github.com/o/r/releases/v2","body":"Notes","published_at":"2024-05-01T00:00:00Z"},
                 {"id":1,"tag_name":"v1.9.0","draft":true}]
                """), "<https://api.github.com/repos/o/r/releases?page=2>; rel=\"next\", <https://api.github.com/repos/o/r/releases?page=5>; rel=\"last\"", false, 10_000);
            Equal("release:2", releases.Items.Single().ItemId, "drafts and prereleases skipped");
            Equal("Two", releases.Items[0].Title, "the release name");
            Equal("v2.0.0\n\nNotes", releases.Items[0].Text, "tag and notes");
            Equal("https://api.github.com/repos/o/r/releases?page=2", releases.NextPage?.ToString(), "the Link header's next page");
            Equal(2, SourceParsers.GitHubReleases(Encoding.UTF8.GetBytes("""[{"id":3,"tag_name":"v3","prerelease":true},{"id":2,"tag_name":"v2"}]"""), null, true, 100)
                .Items.Count, "prereleases when asked for");

            var page = SourceParsers.Page(Encoding.UTF8.GetBytes("""
                <html><head><title> Status &amp; news </title><style>body{}</style></head>
                <body><h1>Status</h1><p>All systems<br>operational</p><script>document.write('x')</script><!-- hidden --></body></html>
                """), "text/html; charset=utf-8", new Uri("https://Example.com/status?utm_medium=a"), 10_000);
            Equal("Status & news", page.Items.Single().Title, "the page title");
            Equal("page:https://example.com/status", page.Items[0].ItemId, "the page identity");
            Equal("Status\nAll systems\noperational", page.Items[0].Text, "page text without scripts or comments");
        });

        yield return Case("sources.check-conditional-304-suppression-deletions-and-tombstones", async () =>
        {
            var body = Rss(("a", "Alpha", "first"), ("b", "Beta", "second"), ("c", "Gamma", "third"));
            string? etag = "\"v1\"";
            await using var server = new FixtureServer(request =>
                etag is not null && request.Header("If-None-Match") == etag ? FixtureResponse.Code(304)
                    : FixtureResponse.Text(body, headers: etag is null ? null : new Dictionary<string, string> { ["ETag"] = etag }));
            await using var device = await TestDevice.OpenAsync("sources-check", new DeviceSetup { Retrieval = Local() });
            var session = await device.CreateAsync();
            device.Host.Sources.Create(Feed("blog", server.Url("/feed.xml"), ""","title":"Blog" """));

            var first = await CheckSourcesAsync(session, Checked("blog"));
            var result = SourceResult(first, "blog");
            Equal("changed", Jobs.String(result, "status"), "first check");
            Equal(3L, Jobs.Long(result, "new"), "three new items");
            var artifactId = Jobs.String((JsonObjectValue)first["result"], "artifactId")!;
            var artifact = await session.ArtifactAsync(artifactId);
            Equal("evidence-list", Jobs.String(artifact, "kind"), "an evidence list");
            var listed = Jobs.Items((JsonObjectValue)artifact["props"], "items").OfType<JsonObjectValue>().ToList();
            Equal(3, listed.Count, "every new item listed");
            Check(Jobs.String(listed[0], "note")!.Contains("new · Blog: Alpha", StringComparison.Ordinal), "the note: " + listed[0].ToJson());
            var evidence = await session.EvidenceAsync(Jobs.String(listed[0], "evidenceId")!);
            Equal("blog", Jobs.String(evidence, "sourceId"), "evidence names its source");
            Equal("first", await session.EvidenceTextAsync(Jobs.String(listed[0], "evidenceId")!), "the item text is the evidence");
            Equal(3, device.Host.Sources.Get("blog")!.State.Items, "three items kept");

            var second = await CheckSourcesAsync(session, Checked("blog"));
            Check(Jobs.Items((JsonObjectValue)second["result"], "sources").Count() == 1, "one source");
            Equal("unchanged", Jobs.String(SourceResult(second, "blog"), "status"), "a 304 is unchanged");
            Check(SourceResult(second, "blog")["notModified"] is JsonBooleanValue { Value: true }, "not modified");
            Equal("\"v1\"", server.Requests.Last().Header("If-None-Match"), "the validator was sent");
            Equal(JsonValue.Null, ((JsonObjectValue)second["result"])["artifactId"], "nothing is published when nothing changed");

            etag = null;
            body = Rss(("a", "Alpha", "first"), ("b", "Beta", "second, edited"), ("d", "Delta", "fourth"));
            var third = SourceResult(await CheckSourcesAsync(session, Checked("blog")), "blog");
            Equal((1L, 1L, 1L), (Jobs.Long(third, "new"), Jobs.Long(third, "changed"), Jobs.Long(third, "removed")), "new, changed and removed: " + third.ToJson());

            var fourth = await CheckSourcesAsync(session, Checked("blog"));
            Equal("unchanged", Jobs.String(SourceResult(fourth, "blog"), "status"), "the same content without validators is unchanged");
            Equal(JsonValue.Null, ((JsonObjectValue)fourth["result"])["artifactId"], "and publishes nothing");

            body = Rss(("a", "Alpha", "first"), ("b", "Beta", "second, edited"), ("c", "Gamma", "third"), ("d", "Delta", "fourth"));
            Equal("unchanged", Jobs.String(SourceResult(await CheckSourcesAsync(session, Checked("blog")), "blog"), "status"),
                "a tombstoned item that comes back unchanged is not announced again");

            device.Host.Sources.Delete("blog", device.Host.Sources.Get("blog")!.Revision);
            device.Host.Sources.Create(Feed("blog", server.Url("/feed.xml")));
            Equal("unchanged", Jobs.String(SourceResult(await CheckSourcesAsync(session, Checked("blog")), "blog"), "status"),
                "a recreated source does not announce its old items");
            var items = device.Host.Sources.Items("blog", 2);
            Equal(2, Jobs.Items(items, "items").Count(), "a page of items");
            Check(items["next"] is JsonStringValue, "with a next cursor");
            Check(device.Host.Sources.Receipts("blog").Count >= 6, "fetch receipts kept");
        });

        yield return Case("sources.check-retries-after-cooldown-and-keeps-retry-after-across-restart", async () =>
        {
            var clock = new JumpClock();
            var failNext = 1;
            var retryAfter = false;
            await using var server = new FixtureServer(request =>
            {
                if (retryAfter) return FixtureResponse.Code(429, new Dictionary<string, string> { ["Retry-After"] = "3600" });
                return Interlocked.Decrement(ref failNext) >= 0 ? FixtureResponse.Code(503) : FixtureResponse.Text(Rss(("a", "Alpha", "one")));
            });
            var setup = new DeviceSetup { Time = clock, Retrieval = Local() };
            await using var device = await TestDevice.OpenAsync("sources-retry", setup);
            var session = await device.CreateAsync();
            device.Host.Sources.Create(Feed("flaky", server.Url("/feed.xml")));
            var start = await session.StartExecutionAsync(1, "pisharp-source-check", Checked("flaky"), null, null);
            await WaitUntilAsync(async () => (await RunAsync(session, start.TaskId.Value))?.ToJson().Contains("Retrying ", StringComparison.Ordinal) == true,
                "the check waits for the cooldown");
            Equal(1, server.Count("/"), "one failed request");
            var health = device.Host.Sources.Health().Single();
            var cooldown = Jobs.Long(health, "cooldownUntil")!.Value - clock.GetUtcNow().ToUnixTimeMilliseconds();
            Check(cooldown is > 100_000 and <= 132_000, "about two minutes with jitter: " + cooldown);
            Equal("new", Jobs.String((JsonObjectValue)device.Host.Sources.Get("flaky")!.ToJson()["state"], "status"), "a retry in progress is not a failed check");
            // The run says "Retrying" before its next iteration arms the sleep until the cooldown ends.
            await WaitUntilArmedAsync(clock, () => Task.FromResult<double?>(Jobs.Long(health, "cooldownUntil")), "the retry's timer is armed");
            clock.Advance(TimeSpan.FromMinutes(3));
            var run = await WaitForRunAsync(session, start.TaskId.Value, "completed");
            Equal("changed", Jobs.String(SourceResult(run, "flaky"), "status"), "the retry succeeded");
            Equal(2L, Jobs.Long(SourceResult(run, "flaky"), "attempts"), "two attempts");
            Equal(0, device.Host.Sources.Health().Count, "success clears the origin's health");

            retryAfter = true;
            var deferred = await CheckSourcesAsync(session, Checked("flaky"));
            var outcome = SourceResult(deferred, "flaky");
            Equal("deferred", Jobs.String(outcome, "status"), "a Retry-After beyond the retry wait defers");
            var eligible = Jobs.Long(outcome, "nextEligibleAt")!.Value - clock.GetUtcNow().ToUnixTimeMilliseconds();
            Check(eligible is > 3_500_000 and <= 3_600_000, "the next attempt waits for Retry-After: " + eligible);
            var state = (JsonObjectValue)device.Host.Sources.Get("flaky")!.ToJson()["state"];
            Equal("degraded", Jobs.String(state, "status"), "the source shows degraded");
            Check(device.Host.Sources.Get("flaky")!.Enabled, "and stays enabled");
            var requests = server.Count("/");

            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            Equal("deferred", Jobs.String(SourceResult(await CheckSourcesAsync(session, Checked("flaky")), "flaky"), "status"), "the cooldown survives a restart");
            var forced = Checked("flaky").SetItem("force", true);
            Equal("deferred", Jobs.String(SourceResult(await CheckSourcesAsync(session, forced), "flaky"), "status"), "a forced check still honors Retry-After");
            Equal(requests, server.Count("/"), "no request during the cooldown");
            retryAfter = false;
            clock.Advance(TimeSpan.FromMinutes(61));
            Equal("unchanged", Jobs.String(SourceResult(await CheckSourcesAsync(session, Checked("flaky")), "flaky"), "status"), "checked again after Retry-After");
            Equal("ok", Jobs.String((JsonObjectValue)device.Host.Sources.Get("flaky")!.ToJson()["state"], "status"), "healthy again");
        });

        yield return Case("sources.check-isolates-authenticated-cache-and-gates-fallback", async () =>
        {
            await using var server = new FixtureServer(request =>
            {
                if (request.Path == "/down") return FixtureResponse.Code(503);
                var authenticated = request.Header("Authorization") == "Bearer secret";
                var etag = authenticated ? "\"p1\"" : "\"a1\"";
                if (request.Header("If-None-Match") == etag) return FixtureResponse.Code(304);
                return FixtureResponse.Text(authenticated ? Rss(("p", "Private", "members only")) : Rss(("q", "Public", "for everyone")),
                    headers: new Dictionary<string, string> { ["ETag"] = etag });
            });
            var fallback = new TestProvider("test-extract", (request, _) => Task.FromResult(new RetrievalResponse(200, request.Url,
                Encoding.UTF8.GetBytes(Rss(("f", "Fallback", "extracted"))), "application/rss+xml", null, null)));
            await using var device = await TestDevice.OpenAsync("sources-auth", new DeviceSetup
            {
                Retrieval = Local(new RetrievalLimits { MaxRetryWait = TimeSpan.Zero }, [fallback], new Dictionary<string, string> { ["fixture"] = "Bearer secret" }),
            });
            var session = await device.CreateAsync();
            var sources = device.Host.Sources;
            sources.Create(Feed("private", server.Url("/feed.xml"), ""","credentialRef":"fixture","allowFallback":true"""));
            sources.Create(Feed("public", server.Url("/feed.xml")));
            var run = await CheckSourcesAsync(session, Checked("private", "public"));
            Equal(("changed", "changed"), (Jobs.String(SourceResult(run, "private"), "status"), Jobs.String(SourceResult(run, "public"), "status")), "both read");
            var texts = new List<string>();
            foreach (var item in Jobs.Items((JsonObjectValue)(await session.ArtifactAsync(Jobs.String((JsonObjectValue)run["result"], "artifactId")!))["props"], "items")
                .OfType<JsonObjectValue>())
                texts.Add(await session.EvidenceTextAsync(Jobs.String(item, "evidenceId")!));
            Check(texts.Contains("members only") && texts.Contains("for everyone"), "each source has its own content: " + string.Join(", ", texts));
            Check(server.Requests.Where(request => request.Header("Authorization") is null).All(request => request.Header("If-None-Match") is null),
                "the anonymous request did not reuse the authenticated validator");

            var again = await CheckSourcesAsync(session, Checked("private", "public"));
            Check(Jobs.String(SourceResult(again, "private"), "status") == "unchanged" && Jobs.String(SourceResult(again, "public"), "status") == "unchanged", "both unchanged");
            var last = server.Requests.TakeLast(2).ToList();
            Check(last.Any(request => request.Header("Authorization") == "Bearer secret" && request.Header("If-None-Match") == "\"p1\""), "the private validator");
            Check(last.Any(request => request.Header("Authorization") is null && request.Header("If-None-Match") == "\"a1\""), "the public validator");

            sources.Create(Feed("private-down", server.Url("/down"), ""","credentialRef":"fixture","allowFallback":true"""));
            sources.Create(Feed("public-down", server.Url("/down"), ""","allowFallback":true"""));
            sources.Create(Feed("strict-down", server.Url("/down?strict")));
            var down = await CheckSourcesAsync(session, Checked("private-down", "public-down", "strict-down"));
            Equal("deferred", Jobs.String(SourceResult(down, "private-down"), "status"), "a credentialed source never falls back");
            Equal("changed", Jobs.String(SourceResult(down, "public-down"), "status"), "an allowed fallback reads the source");
            Equal("test-extract", Jobs.String(SourceResult(down, "public-down"), "provider"), "through the fallback");
            Equal("deferred", Jobs.String(SourceResult(down, "strict-down"), "status"), "a source that does not allow fallback");
            Equal(1, fallback.Calls, "the fallback was called once");
            var usage = device.Host.Budgets.Usage(session.Id + "/" + Jobs.Long(down, "id"));
            Equal(1L, Jobs.Long((JsonObjectValue)usage["totals"], "credits:test"), "the fallback's credit is counted");
        });

        yield return Case("sources.check-recovers-interrupted-fetches-without-losing-charges", async () =>
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = true;
            await using var server = new FixtureServer(async request =>
            {
                if (request.Path == "/flaky") return FixtureResponse.Code(503);
                if (hold && request.Path == "/slow")
                {
                    arrived.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                return FixtureResponse.Text(Rss(("s", "Slow", "eventually")));
            });
            var paidStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var paidHold = true;
            var paid = new TestProvider("test-paid", async (request, cancellation) =>
            {
                if (paidHold)
                {
                    paidStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                return new RetrievalResponse(200, request.Url, Encoding.UTF8.GetBytes(Rss(("p", "Paid", "extracted"))), "application/rss+xml", null, null);
            });
            var setup = new DeviceSetup { Retrieval = Local(fallbacks: [paid]) };
            await using var device = await TestDevice.OpenAsync("sources-crash", setup);
            var session = await device.CreateAsync();
            device.Host.Sources.Create(Feed("slow", server.Url("/slow")));
            device.Host.Sources.Create(Feed("flaky", server.Url("/flaky"), ""","allowFallback":true"""));

            // A free call interrupted by a restart is made again under the same operation and reservation.
            var slow = await session.StartExecutionAsync(1, "pisharp-source-check", Checked("slow"), null, null);
            await arrived.Task.WaitAsync(Answer);
            await device.Host.DisposeAsync();
            hold = false;
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            var run = await WaitForRunAsync(session, slow.TaskId.Value, "completed");
            Equal("changed", Jobs.String(SourceResult(run, "slow"), "status"), "the check finished after the restart");
            var runKey = session.Id + "/" + slow.TaskId.Value;
            var reservations = device.Host.Budgets.ForRun(runKey);
            Equal(1, reservations.Count, "one reservation for the interrupted operation");
            Equal("settled", reservations[0].Status, "settled once");
            Equal(2, server.Count("/slow"), "the call was made again");
            Equal(1, device.Host.Sources.Receipts("slow").Count, "one receipt");

            // A paid call interrupted by a restart stays counted as uncertain.
            var flaky = await session.StartExecutionAsync(1, "pisharp-source-check", Checked("flaky"), null, null);
            await paidStarted.Task.WaitAsync(Answer);
            await device.Host.DisposeAsync();
            paidHold = false;
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            run = await WaitForRunAsync(session, flaky.TaskId.Value, "completed");
            Equal("changed", Jobs.String(SourceResult(run, "flaky"), "status"), "the fallback answered on its next attempt");
            var usage = device.Host.Budgets.Usage(session.Id + "/" + flaky.TaskId.Value);
            Equal(1L, Jobs.Long(usage, "uncertain"), "the interrupted paid call is uncertain");
            Check(usage["costKnown"] is JsonBooleanValue { Value: false }, "so the run's cost is not known");
            Equal(2L, Jobs.Long((JsonObjectValue)usage["totals"], "credits:test"), "both paid calls are counted");
            Check(device.Host.Sources.Receipts("flaky").Any(receipt => Jobs.String(receipt, "status") == "uncertain"), "an uncertain receipt");
        });

        yield return Case("sources.burst-checks-stay-within-day-and-run-caps", async () =>
        {
            await using var server = new FixtureServer(request => FixtureResponse.Text(Rss((request.Path.Trim('/'), "Item", "text"))));
            await using var device = await TestDevice.OpenAsync("sources-burst", new DeviceSetup { Retrieval = Local() });
            var session = await device.CreateAsync();
            foreach (var index in Enumerable.Range(0, 5)) device.Host.Sources.Create(Feed("s" + index, server.Url("/s" + index)));
            var limits = device.Host.Budgets.Limits();
            device.Host.Budgets.SetLimits(new Dictionary<string, long?> { ["requests"] = 3 }, limits.Revision);
            var starts = await Task.WhenAll(Enumerable.Range(0, 5).Select(index => session.StartExecutionAsync(1, "pisharp-source-check", Checked("s" + index), null, null)));
            var runs = new List<JsonObjectValue>();
            foreach (var start in starts)
            {
                await WaitUntilAsync(async () => Jobs.String((await RunAsync(session, start.TaskId.Value))!, "status") is "completed" or "failed", "the check ended");
                runs.Add((await RunAsync(session, start.TaskId.Value))!);
            }
            Equal(3, runs.Count(run => Jobs.String(run, "status") == "completed"), "three checks fit the day's cap");
            Equal(3, server.Requests.Count, "three requests were made");
            Check(runs.Where(run => Jobs.String(run, "status") == "failed").All(run => run.ToJson().Contains("limit", StringComparison.Ordinal)), "the others hit the limit");
            Equal(3L, Jobs.Long((JsonObjectValue)device.Host.Budgets.Today()["used"], "requests"), "the day's use");

            device.Host.Budgets.SetLimits(new Dictionary<string, long?> { ["requests"] = null }, device.Host.Budgets.Limits().Revision);
            foreach (var index in Enumerable.Range(5, 3)) device.Host.Sources.Create(Feed("s" + index, server.Url("/s" + index)));
            var capped = await CheckSourcesAsync(session, Checked("s5", "s6", "s7"), budget: JsonValue.Object([new("requests", 2)]));
            Equal(2, Jobs.Items((JsonObjectValue)capped["result"], "sources").OfType<JsonObjectValue>().Count(result => Jobs.String(result, "status") == "changed"),
                "two sources within the run's cap");
            Equal("limited", Jobs.String(SourceResult(capped, "s7"), "status"), "the third is limited");
        });

        yield return Case("sources.check-follows-pages-github-releases-and-reevaluates-under-a-policy", async () =>
        {
            var clock = new JumpClock();
            var pageTwoFailures = 1;
            FixtureServer? self = null;
            await using var server = new FixtureServer(request => request.Path switch
            {
                "/atom" => FixtureResponse.Text($"""
                    <feed xmlns="http://www.w3.org/2005/Atom"><title>Paged</title><link rel="next" href="/atom2"/>
                      <entry><id>e1</id><title>One</title></entry><entry><id>e2</id><title>Two</title></entry></feed>
                    """),
                "/atom2" => Interlocked.Decrement(ref pageTwoFailures) >= 0 ? FixtureResponse.Code(503)
                    : FixtureResponse.Text("""<feed xmlns="http://www.w3.org/2005/Atom"><entry><id>e3</id><title>Three</title></entry></feed>"""),
                var path when path.StartsWith("/repos/owner/tool/releases", StringComparison.Ordinal) && path.Contains("page=2", StringComparison.Ordinal) =>
                    FixtureResponse.Text("""[{"id":1,"tag_name":"v1.0.0","name":"One","body":"First"}]""", "application/json"),
                var path when path.StartsWith("/repos/owner/tool/releases", StringComparison.Ordinal) =>
                    FixtureResponse.Text("""[{"id":2,"tag_name":"v2.0.0","name":"Two","body":"Second","html_url":"https://github.com/owner/tool/releases/v2"}]""",
                        "application/json", new Dictionary<string, string> { ["Link"] = $"<{self!.Url("/repos/owner/tool/releases?per_page=100&page=2")}>; rel=\"next\"" }),
                _ => FixtureResponse.Code(404),
            });
            self = server;
            var models = new FauxDurableModels
            {
                Classify = (_, _) => Task.FromResult(JsonValue.Object([new("label", JsonValue.Object([new("type", "choice"), new("choice", "relevant"),
                    new("probabilities", JsonValue.Object([new("relevant", 0.97), new("ignore", 0.03)])), new("confidence", 0.97)]))])),
            };
            var setup = new DeviceSetup { Time = clock, Models = models, Retrieval = Local(github: server.Base.ToString().TrimEnd('/')) };
            await using var device = await TestDevice.OpenAsync("sources-pages", setup);
            var session = await device.CreateAsync();
            device.Host.Sources.Create(Feed("paged", server.Url("/atom"), ""","maxPages":2"""));
            device.Host.Sources.Create(Json("""{"id":"tool","kind":"github-releases","repository":"owner/tool","maxPages":2}"""));
            var start = await session.StartExecutionAsync(1, "pisharp-source-check", Checked("paged", "tool"), null, null);
            try
            {
                await WaitUntilAsync(async () => (await RunAsync(session, start.TaskId.Value))?.ToJson().Contains("Retrying ", StringComparison.Ordinal) == true,
                    "page two waits for its cooldown");
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("No retry: " + Cut((await RunAsync(session, start.TaskId.Value))!.ToJson()) + " requests: " +
                    string.Join(", ", server.Requests.Select(request => request.Path)));
            }
            // The run says "Retrying" before its next iteration arms the sleep until the earliest cooldown ends.
            await WaitUntilArmedAsync(clock, () => Task.FromResult<double?>(device.Host.Sources.Health().Min(row => Jobs.Long(row, "cooldownUntil"))),
                "the retry's timer is armed");
            clock.Advance(TimeSpan.FromMinutes(3));
            var run = await WaitForRunAsync(session, start.TaskId.Value, "completed");
            Equal(3L, Jobs.Long(SourceResult(run, "paged"), "new"), "both pages");
            Equal(1, server.Requests.Count(request => request.Path == "/atom"), "page one was fetched once");
            Equal(2, server.Requests.Count(request => request.Path == "/atom2"), "page two was retried");
            Equal(2L, Jobs.Long(SourceResult(run, "tool"), "new"), "both release pages");

            // A new policy version may judge the cached items again even though nothing changed.
            device.Host.Policies.Publish("watch", Json("""
                {"title":"Watch","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Does it matter?",
                 "inputSchema":{"type":"object","additionalProperties":false,"required":["title"],"properties":{
                   "title":{"type":"string","maxLength":300},"summary":{"type":"string","maxLength":3000}}},
                 "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}]}
                """));
            var unchanged = await CheckSourcesAsync(session, Checked("tool").SetItem("policyId", "watch"));
            Equal("unchanged", Jobs.String(SourceResult(unchanged, "tool"), "status"), "nothing changed");
            Equal(0L, Jobs.Long((JsonObjectValue)unchanged["result"], "judged"), "so nothing is judged");
            Equal(JsonValue.Null, ((JsonObjectValue)unchanged["result"])["artifactId"], "or published");
            var reevaluated = await CheckSourcesAsync(session, Checked("tool").SetItem("policyId", "watch").SetItem("reevaluate", true));
            Equal(2L, Jobs.Long((JsonObjectValue)reevaluated["result"], "judged"), "reevaluation judges the current items: " + Cut(reevaluated.ToJson()));
            var listed = Jobs.Items((JsonObjectValue)(await session.ArtifactAsync(Jobs.String((JsonObjectValue)reevaluated["result"], "artifactId")!))["props"], "items")
                .OfType<JsonObjectValue>().ToList();
            Check(listed.Count == 2 && listed.All(item => Jobs.String(item, "note")!.Contains("relevant", StringComparison.Ordinal)), "labels listed: " + listed[0].ToJson());
        });
    }

    private static string Cut(string text) => text.Length <= 3000 ? text : text[..3000] + "…";
}
