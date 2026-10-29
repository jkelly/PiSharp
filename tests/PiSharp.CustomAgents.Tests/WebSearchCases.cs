using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;
using PiSharp.Durable.Storage;

// Decision 0012: the web_search tool and its backends. The backends run against a fake HTTP handler, so no case reaches the network.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> WebSearchCases()
    {
        yield return Case("agents.web.tool-shape", () =>
        {
            var tool = WebSearchTool.Create(new CannedSearch());
            Equal("web_search", tool.Name, "default name");
            Equal(ToolReplay.Safe, tool.Replay, "a search is replay-safe");
            var parameters = tool.Parameters.ToJson();
            Check(parameters.Contains("\"required\":[\"query\"]", StringComparison.Ordinal) && parameters.Contains("\"maximum\":10", StringComparison.Ordinal),
                "query is required and count is capped: " + parameters);
            Check(tool.StructuredOutputSchema?.ToJson().Contains("\"results\"", StringComparison.Ordinal) == true, "programs get the response's schema");
            var custom = WebSearchTool.Create(new CannedSearch(), new WebSearchToolOptions { Name = "search", MaxCount = 3, DefaultCount = 2 });
            Check(custom.Name == "search" && custom.Parameters.ToJson().Contains("\"maximum\":3", StringComparison.Ordinal), "options reach the schema");
            Throws<ArgumentException>(() => WebSearchTool.Create(new CannedSearch(), new WebSearchToolOptions { MaxCount = 0 }), "MaxCount 0");
            Throws<ArgumentException>(() => WebSearchTool.Create(new CannedSearch(), new WebSearchToolOptions { MaxCount = 21 }), "MaxCount 21");
            Throws<ArgumentException>(() => WebSearchTool.Create(new CannedSearch(), new WebSearchToolOptions { DefaultCount = 11 }), "DefaultCount over MaxCount");
            Throws<ArgumentException>(() => WebSearchTool.Create(new CannedSearch(), new WebSearchToolOptions { Timeout = TimeSpan.Zero }), "zero timeout");
        });

        yield return Case("agents.web.brave-request-and-results", async () =>
        {
            var http = new FakeHttp(_ => Reply("""
                {"web":{"results":[
                  {"title":"USB <strong>Power Delivery</strong>","url":"https://www.usb.org/usb-charger-pd","description":"PD 3.1 adds &quot;Extended Power Range&quot; up to <strong>240 W</strong>."},
                  {"title":"No address","description":"skipped by the tool, kept by the backend"}]}}
                """));
            var brave = new BraveWebSearch("brave-test-key", new HttpClient(http));
            var found = await brave.SearchAsync(new WebSearchRequest("usb pd 240 W", 3), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("GET", sent.Method, "method");
            Equal("https://api.search.brave.com/res/v1/web/search?q=usb%20pd%20240%20W&count=3", sent.Uri, "query and count in the URL");
            Equal("brave-test-key", sent.Header("X-Subscription-Token"), "subscription token");
            Check(sent.Header("Accept")?.Contains("application/json", StringComparison.Ordinal) == true, "asks for JSON");
            Equal("Brave Search", found.Provider, "provider");
            Equal(2, found.Results.Count, "both results parsed");
            Equal("https://www.usb.org/usb-charger-pd", found.Results[0].Url, "url");
            var empty = await new BraveWebSearch("k", new HttpClient(new FakeHttp(_ => Reply("""{"query":{"original":"x"}}""")))).SearchAsync(new WebSearchRequest("x", 50), CancellationToken.None);
            Equal(0, empty.Results.Count, "no web section: no results");
        });

        yield return Case("agents.web.tavily-request-and-results", async () =>
        {
            var http = new FakeHttp(_ => Reply("""
                {"query":"usb pd","answer":"USB PD 3.1 (2021) raised the limit to 240 W.","results":[
                  {"title":"USB PD 3.1","url":"https://example.org/pd31","content":"Extended Power Range: 28, 36 and 48 V.","score":0.93}]}
                """));
            var tavily = new TavilyWebSearch("tvly-test", new HttpClient(http)) { IncludeAnswer = true, SearchDepth = "advanced" };
            var found = await tavily.SearchAsync(new WebSearchRequest("usb pd", 40), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("POST", sent.Method, "method");
            Equal("https://api.tavily.com/search", sent.Uri, "endpoint");
            Equal("Bearer tvly-test", sent.Header("Authorization"), "bearer key");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("usb pd", (string?)body["query"], "query");
            Equal(20, (int?)body["max_results"], "count capped at Tavily's 20");
            Equal("advanced", (string?)body["search_depth"], "depth");
            Equal(true, (bool?)body["include_answer"], "answer asked for");
            Equal("USB PD 3.1 (2021) raised the limit to 240 W.", found.Answer, "answer");
            Equal("Extended Power Range: 28, 36 and 48 V.", found.Results.Single().Snippet, "content is the snippet");
        });

        yield return Case("agents.web.firecrawl-request-and-results", async () =>
        {
            var http = new FakeHttp(_ => Reply("""
                {"success":true,"data":{
                  "web":[
                    {"title":"USB Charger (USB Power Delivery) | USB-IF","description":"USB PD 3.1 adds Extended Power Range: up to 240 W.","url":"https://www.usb.org/usb-charger-pd","position":1},
                    {"title":"USB hardware","url":"https://en.wikipedia.org/wiki/USB_hardware","metadata":{"description":"Revision 3.1 (2021) raised the maximum to 240 W."}},
                    {"title":"No address","description":"skipped"}],
                  "news":[
                    {"title":"USB-IF publishes PD 3.1","snippet":"240 W over USB-C.","url":"https://example.org/pd31","date":"May 26, 2021"},
                    {"title":"Duplicate","snippet":"again","url":"https://www.usb.org/usb-charger-pd"}]},
                 "warning":null,"id":"search-1","creditsUsed":2}
                """));
            var firecrawl = new FirecrawlWebSearch("fc-test", new HttpClient(http)) { Sources = ["web", "news"], Country = "PT", TimeFilter = "qdr:y" };
            var found = await firecrawl.SearchAsync(new WebSearchRequest("usb pd 240 W", 4), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("POST", sent.Method, "method");
            Equal("https://api.firecrawl.dev/v2/search", sent.Uri, "v2 search endpoint");
            Equal("Bearer fc-test", sent.Header("Authorization"), "bearer key");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("usb pd 240 W", (string?)body["query"], "query");
            Equal(4, (int?)body["limit"], "limit");
            Equal("""[{"type":"web"},{"type":"news"}]""", body["sources"]!.ToJsonString(), "sources");
            Equal("PT", (string?)body["country"], "country");
            Equal("qdr:y", (string?)body["tbs"], "time filter");
            Check(body["scrapeOptions"] is null, "the pages are not scraped");
            Equal("Firecrawl", found.Provider, "provider");
            Equal("https://www.usb.org/usb-charger-pd,https://en.wikipedia.org/wiki/USB_hardware,https://example.org/pd31",
                string.Join(",", found.Results.Select(result => result.Url)), "web results, then news, each page once");
            Equal("Revision 3.1 (2021) raised the maximum to 240 W.", found.Results[1].Snippet, "the page's metadata description when there is no description");
            Equal("May 26, 2021: 240 W over USB-C.", found.Results[2].Snippet, "a news snippet starts with its date");
            Check(found.Answer is null, "no answer");
            var plain = new FakeHttp(_ => Reply("""{"success":true,"data":{"web":[]}}"""));
            await new FirecrawlWebSearch("k", new HttpClient(plain)).SearchAsync(new WebSearchRequest("x", 500), CancellationToken.None);
            var defaults = JsonNode.Parse(plain.Requests.Single().Body!)!;
            Check(defaults["sources"]!.ToJsonString() == """[{"type":"web"}]""" && (int?)defaults["limit"] == 100 && defaults["country"] is null && defaults["tbs"] is null,
                "web only, the limit capped at 100, no country or time filter: " + defaults.ToJsonString());
            var failed = await ThrowsAsync<WebSearchException>(() => new FirecrawlWebSearch("k", new HttpClient(new FakeHttp(_ =>
                Reply("""{"success":false,"error":"Search engine unavailable"}""")))).SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "200 failure");
            Equal("Firecrawl failed: Search engine unavailable", failed.Message, "success: false in a 200 reply");
            var credits = await ThrowsAsync<WebSearchException>(() => new FirecrawlWebSearch("k", new HttpClient(new FakeHttp(_ =>
                Reply("""{"success":false,"error":"Insufficient credits to perform this request."}""", HttpStatusCode.PaymentRequired))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "402");
            Equal("Firecrawl returned HTTP 402: Insufficient credits to perform this request.", credits.Message, "Firecrawl's error");
        });

        yield return Case("agents.web.openai-request-and-results", async () =>
        {
            const string Text = "USB PD 3.1 added 240 W charging in 2021. It defines Extended Power Range.";
            var http = new FakeHttp(_ => Reply($$$"""
                {"output":[
                  {"type":"web_search_call","status":"completed","action":{"type":"search","query":"usb pd 240 W","sources":[
                    {"type":"url","url":"https://www.usb.org/usb-charger-pd"},{"type":"url","url":"https://en.wikipedia.org/wiki/USB_hardware"}]}},
                  {"type":"message","role":"assistant","content":[{"type":"output_text","text":"{{{Text}}}","annotations":[
                    {"type":"url_citation","start_index":0,"end_index":40,"url":"https://www.usb.org/usb-charger-pd","title":"USB Charger (USB Power Delivery)"},
                    {"type":"url_citation","start_index":41,"end_index":73,"url":"https://www.usb.org/usb-charger-pd","title":"duplicate"},
                    {"type":"url_citation","start_index":41,"end_index":73,"url":"https://example.org/epr","title":"EPR explained"}]}]}]}
                """));
            var openai = new OpenAIWebSearch("sk-test", "gpt-test", new HttpClient(http));
            var found = await openai.SearchAsync(new WebSearchRequest("usb pd 240 W", 5), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("https://api.openai.com/v1/responses", sent.Uri, "Responses endpoint");
            Equal("Bearer sk-test", sent.Header("Authorization"), "bearer key");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("gpt-test", (string?)body["model"], "model");
            Equal("web_search", (string?)body["tools"]![0]!["type"], "hosted web_search tool");
            Equal("required", (string?)body["tool_choice"], "the search must run");
            Equal("web_search_call.action.sources", (string?)body["include"]![0], "sources asked for");
            Check(((string?)body["input"])!.Contains("usb pd 240 W", StringComparison.Ordinal), "the query is in the input");
            Equal(Text, found.Answer, "the reply is the answer");
            Equal("https://www.usb.org/usb-charger-pd,https://example.org/epr,https://en.wikipedia.org/wiki/USB_hardware",
                string.Join(",", found.Results.Select(result => result.Url)), "cited pages first, once each, then the other sources");
            Equal("USB PD 3.1 added 240 W charging in 2021.", found.Results[0].Snippet, "the cited span is the snippet");
            Equal("en.wikipedia.org", found.Results[2].Title, "an uncited source is titled by its host");
            Equal("OpenAI web search, gpt-test", found.Provider, "provider");
        });

        yield return Case("agents.web.http-errors", async () =>
        {
            var refused = await ThrowsAsync<WebSearchException>(() => new BraveWebSearch("secret-key-123", new HttpClient(new FakeHttp(_ =>
                Reply("""{"error":{"detail":"invalid token secret-key-123"}}""", HttpStatusCode.Unauthorized))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "401");
            Check(refused.Message.Contains("HTTP 401", StringComparison.Ordinal) && refused.Message.Contains("BRAVE_API_KEY", StringComparison.Ordinal) &&
                !refused.Message.Contains("secret-key-123", StringComparison.Ordinal), "a refused key names its variable, never the key: " + refused.Message);
            var limited = await ThrowsAsync<WebSearchException>(() => new OpenAIWebSearch("sk", http: new HttpClient(new FakeHttp(_ =>
                Reply("""{"error":{"message":"Rate limit reached for requests","type":"requests"}}""", HttpStatusCode.TooManyRequests))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "429");
            Equal("OpenAI web search, gpt-5.4-mini returned HTTP 429: Rate limit reached for requests.", limited.Message, "the backend's error message");
            Equal(TimeSpan.FromSeconds(10), limited.RetryAfter, "a 429 without Retry-After waits 10 s");
            var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"success":false,"error":"Rate limit exceeded."}""") };
            retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            var asked = await ThrowsAsync<WebSearchException>(() => new FirecrawlWebSearch("k", new HttpClient(new FakeHttp(_ => retry)))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "429 with Retry-After");
            Equal(TimeSpan.FromSeconds(7), asked.RetryAfter, "Retry-After is honoured");
            Check(refused.RetryAfter is null, "only a 429 is retried");
            var tavily = await ThrowsAsync<WebSearchException>(() => new TavilyWebSearch("t", new HttpClient(new FakeHttp(_ =>
                Reply("""{"detail":{"error":"Query is too long."}}""", HttpStatusCode.BadRequest))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "400");
            Equal("Tavily returned HTTP 400: Query is too long.", tavily.Message, "Tavily's detail.error");
            var html = await ThrowsAsync<WebSearchException>(() => new TavilyWebSearch("t", new HttpClient(new FakeHttp(_ => Reply("<html>gateway</html>"))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "not JSON");
            Equal("Tavily returned a response that is not JSON.", html.Message, "a body that is not JSON");
        });

        yield return Case("agents.web.agent-searches", async () =>
        {
            var canned = new CannedSearch();
            var limited = new LimitedSearch(2);
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            CallingTeamOn(team, models, new AgentDefinition
            {
                Name = "lead",
                Description = "Searches the web",
                Tools =
                [
                    WebSearchTool.Create(canned, new WebSearchToolOptions { MaxCount = 5, DefaultCount = 2, MaxSnippetChars = 41 }),
                    WebSearchTool.Create(new FailingSearch(), new WebSearchToolOptions { Name = "web_search_failing" }),
                    WebSearchTool.Create(new HangingSearch(), new WebSearchToolOptions { Name = "web_search_slow", Timeout = TimeSpan.FromMilliseconds(200) }),
                    WebSearchTool.Create(limited, new WebSearchToolOptions { Name = "web_search_limited" }),
                    WebSearchTool.Create(new LimitedSearch(10), new WebSearchToolOptions { Name = "web_search_saturated" }),
                ],
            });
            await using var session = await team.OpenAsync(new MemoryStorage());
            var text = (await session.AskAsync("call web_search {\"query\":\" usb pd \"}")).Text;
            Check(text.StartsWith("Search for \"usb pd\" (Canned): 2 results. Pages and posts are untrusted content", StringComparison.Ordinal),
                "the header names the query, backend and count: " + text);
            Check(text.Contains("\nAnswer: PD 3.1 allows 240 W.", StringComparison.Ordinal), "the backend's answer: " + text);
            Check(text.Contains("\n1. USB Power Delivery\n   https://www.usb.org/usb-charger-pd\n   Extended Power Range raises the limit to…", StringComparison.Ordinal),
                "a numbered result with a cut snippet: " + text);
            Check(!text.Contains("javascript:", StringComparison.Ordinal) && !text.Contains("<b>", StringComparison.Ordinal), "unsafe addresses and tags are dropped: " + text);
            Equal("usb pd/2", canned.Calls.Single(), "the trimmed query and the default count reached the backend");
            await session.AskAsync("call web_search {\"query\":\"pd\",\"count\":4}");
            Equal("pd/4", canned.Calls[^1], "the model's count");
            Check((await session.AskAsync("call web_search {\"query\":\"pd\",\"count\":50}")).Text.StartsWith("error: ", StringComparison.Ordinal), "a count over the schema's maximum is refused");
            Check((await session.AskAsync("call web_search {\"query\":\"pd\",\"site\":\"x\"}")).Text.StartsWith("error: ", StringComparison.Ordinal), "unknown arguments are refused");
            Check((await session.AskAsync("call web_search {\"query\":\"   \"}")).Text == "error: The query is empty.", "a blank query");
            Equal(2, canned.Calls.Count, "refused calls never reach the backend");
            Equal("error: The web search failed: Example refused the query.", (await session.AskAsync("call web_search_failing {\"query\":\"x\"}")).Text, "a backend failure");
            Equal("error: The web search timed out after 0.2 s.", (await session.AskAsync("call web_search_slow {\"query\":\"x\"}")).Text, "a timeout");
            Check((await session.AskAsync("call web_search_limited {\"query\":\"x\"}")).Text.StartsWith("Search for \"x\" (Limited): 1 result", StringComparison.Ordinal),
                "a rate-limited search waits and retries");
            Equal(3, limited.Calls, "two limited attempts, then the search");
            Equal("error: The web search failed: Limited is busy.", (await session.AskAsync("call web_search_saturated {\"query\":\"x\"}")).Text,
                "after three retries the backend's error");
        });

        yield return Case("agents.web.xai-request-and-results", async () =>
        {
            // xAI's Responses API: citations carry no span and repeat the address as the title.
            var http = new FakeHttp(_ => Reply("""
                {"output":[
                  {"type":"reasoning","summary":[]},
                  {"type":"web_search_call","status":"completed","action":{"type":"search","query":"usb pd","sources":[
                    {"type":"url","url":"https://www.usb.org/usb-charger-pd"},{"type":"url","url":"https://www.ti.com/lit/SSZTD49"}]}},
                  {"type":"message","role":"assistant","content":[{"type":"output_text","text":"PD 3.1 (2021) allows 240 W.","annotations":[
                    {"type":"url_citation","url":"https://www.usb.org/usb-charger-pd","start_index":0,"end_index":0,"title":"https://www.usb.org/usb-charger-pd"}]}]}]}
                """));
            var found = await new XaiWebSearch("xai-test", http: new HttpClient(http)).SearchAsync(new WebSearchRequest("usb pd", 5), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("https://api.x.ai/v1/responses", sent.Uri, "xAI's Responses endpoint");
            Equal("Bearer xai-test", sent.Header("Authorization"), "bearer key");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("grok-4.7", (string?)body["model"], "default model");
            Equal("web_search", (string?)body["tools"]![0]!["type"], "hosted web_search tool");
            Equal("PD 3.1 (2021) allows 240 W.", found.Answer, "the reply is the answer");
            Equal("https://www.usb.org/usb-charger-pd,https://www.ti.com/lit/SSZTD49", string.Join(",", found.Results.Select(result => result.Url)), "cited, then consulted");
            Equal("www.usb.org", found.Results[0].Title, "a title that repeats the address becomes the host");
            Check(found.Results[0].Snippet is null, "no span, no snippet");
            Equal("xAI web search, grok-4.7", found.Provider, "provider");
            Check(((string?)body["input"])!.StartsWith("Search the web for: usb pd", StringComparison.Ordinal), "a web search prompt");
            Equal("web_search_call.action.sources", (string?)body["include"]![0], "the consulted pages asked for");
        });

        yield return Case("agents.web.xai-x-search", async () =>
        {
            // X search: posts come back as x.com/i/status addresses, cited with a number for a title, and in the response's citations list.
            var http = new FakeHttp(_ => Reply("""
                {"output":[
                  {"type":"custom_tool_call","name":"x_keyword_search","input":"{\"query\":\"usb pd 240W\"}"},
                  {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Posters call 240 W EPR a big step [[1]](https://x.com/i/status/111).","annotations":[
                    {"type":"url_citation","url":"https://x.com/i/status/111","start_index":0,"end_index":0,"title":"1"}]}]}],
                 "citations":["https://x.com/i/status/111","https://x.com/i/status/222",{"url":"https://x.com/i/status/333"}]}
                """));
            var x = new XaiWebSearch("xai-test", http: new HttpClient(http))
            {
                Sources = XaiSearchSources.X,
                XFromDate = new DateOnly(2026, 9, 1),
                XToDate = new DateOnly(2026, 10, 29),
                XHandles = ["@usbif", "TexasInstruments"],
            };
            var found = await x.SearchAsync(new WebSearchRequest("usb pd 240W", 5), CancellationToken.None);
            var body = JsonNode.Parse(http.Requests.Single().Body!)!;
            Equal("""[{"type":"x_search","from_date":"2026-09-01","to_date":"2026-10-29","allowed_x_handles":["usbif","TexasInstruments"]}]""",
                body["tools"]!.ToJsonString(), "only x_search, with the dates and handles (no @)");
            Check(body["include"] is null, "X search asks for no consulted-pages list");
            Check(((string?)body["input"])!.StartsWith("Search posts on X for: usb pd 240W", StringComparison.Ordinal), "an X prompt");
            Equal("xAI X search, grok-4.7", found.Provider, "provider");
            Equal("https://x.com/i/status/111,https://x.com/i/status/222,https://x.com/i/status/333",
                string.Join(",", found.Results.Select(result => result.Url)), "cited posts, then the other citations, once each");
            Equal("x.com", found.Results[0].Title, "a citation number is not a title");
            var both = new FakeHttp(_ => Reply("""{"output":[]}"""));
            var mixed = new XaiWebSearch("k", http: new HttpClient(both)) { Sources = XaiSearchSources.WebAndX, XExcludedHandles = ["spam"] };
            await mixed.SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None);
            var sent = JsonNode.Parse(both.Requests.Single().Body!)!;
            Equal("""[{"type":"web_search"},{"type":"x_search","excluded_x_handles":["spam"]}]""", sent["tools"]!.ToJsonString(), "both tools");
            Equal("xAI web and X search, grok-4.7", mixed.Name, "both: the name");
            var conflict = await ThrowsAsync<WebSearchException>(() => new XaiWebSearch("k", http: new HttpClient(both))
                { Sources = XaiSearchSources.X, XHandles = ["a"], XExcludedHandles = ["b"] }.SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "both handle lists");
            Check(conflict.Message.Contains("cannot both be set", StringComparison.Ordinal), "handles and excluded handles are refused: " + conflict.Message);
        });

        yield return Case("agents.web.anthropic-request-and-results", async () =>
        {
            var http = new FakeHttp(_ => Reply("""
                {"type":"message","role":"assistant","stop_reason":"end_turn","content":[
                  {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"usb pd 240 W"}},
                  {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
                    {"type":"web_search_result","url":"https://www.usb.org/usb-charger-pd","title":"USB Charger (USB Power Delivery)","encrypted_content":"x"},
                    {"type":"web_search_result","url":"https://en.wikipedia.org/wiki/USB_hardware","title":"USB hardware","encrypted_content":"y"}]},
                  {"type":"text","text":"USB PD 3.1 "},
                  {"type":"text","text":"added 240 W in 2021.","citations":[{"type":"web_search_result_location","url":"https://www.usb.org/usb-charger-pd",
                    "title":"USB Charger (USB Power Delivery)","cited_text":"USB PD Revision 3.1 ... delivering up to 240W","encrypted_index":"z"}]}]}
                """));
            var found = await new AnthropicWebSearch("ant-test", http: new HttpClient(http)).SearchAsync(new WebSearchRequest("usb pd 240 W", 5), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("https://api.anthropic.com/v1/messages", sent.Uri, "Messages endpoint");
            Equal("ant-test", sent.Header("x-api-key"), "x-api-key");
            Equal("2026-10-21", sent.Header("anthropic-version"), "API version");
            Equal("server-side-fallback-2026-07-01", sent.Header("anthropic-beta"), "the refusal fallback beta");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("claude-opus-5-5", (string?)body["model"], "default model");
            Equal("web_search_20260209", (string?)body["tools"]![0]!["type"], "the dynamic-filtering web search");
            Equal("web_search", (string?)body["tools"]![0]!["name"], "tool name");
            Equal(3, (int?)body["tools"]![0]!["max_uses"], "max_uses");
            Equal("low", (string?)body["output_config"]!["effort"], "low effort for a search");
            Equal("default", (string?)body["fallbacks"], "refusal fallback");
            Equal("user", (string?)body["messages"]![0]!["role"], "one user message");
            Check(body["temperature"] is null && body["thinking"] is null, "no sampling or thinking parameters");
            Equal("USB PD 3.1 added 240 W in 2021.", found.Answer, "the text blocks joined");
            Equal("https://www.usb.org/usb-charger-pd,https://en.wikipedia.org/wiki/USB_hardware", string.Join(",", found.Results.Select(result => result.Url)),
                "cited, then the other results");
            Equal("USB PD Revision 3.1 ... delivering up to 240W", found.Results[0].Snippet, "cited_text is the snippet");
            Equal("Claude web search, claude-opus-5-5", found.Provider, "provider");

            var older = new FakeHttp(_ => Reply("""{"stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}"""));
            await new AnthropicWebSearch("k", "claude-haiku-4-5", new HttpClient(older)).SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None);
            var olderBody = JsonNode.Parse(older.Requests.Single().Body!)!;
            Equal("web_search_20250305", (string?)olderBody["tools"]![0]!["type"], "the basic web search on older models");
            Check(olderBody["output_config"] is null && olderBody["fallbacks"] is null && older.Requests.Single().Header("anthropic-beta") is null,
                "no effort or fallback where the model does not take them");
        });

        yield return Case("agents.web.anthropic-pause-refusal-and-errors", async () =>
        {
            var replies = new Queue<string>([
                """{"stop_reason":"pause_turn","content":[{"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"q"}}]}""",
                """{"stop_reason":"end_turn","content":[{"type":"web_search_tool_result","tool_use_id":"s1","content":[{"type":"web_search_result","url":"https://example.org/a","title":"A"}]},{"type":"text","text":"Found A."}]}""",
            ]);
            var http = new FakeHttp(_ => Reply(replies.Dequeue()));
            var found = await new AnthropicWebSearch("k", http: new HttpClient(http)).SearchAsync(new WebSearchRequest("q", 3), CancellationToken.None);
            Equal(2, http.Requests.Count, "a paused turn is resumed");
            var resumed = JsonNode.Parse(http.Requests[1].Body!)!["messages"]!.AsArray();
            Equal("user,assistant", string.Join(",", resumed.Select(item => (string?)item!["role"])), "the question and the paused turn, with no extra user message");
            Equal("server_tool_use", (string?)resumed[1]!["content"]![0]!["type"], "the paused turn is sent back unchanged");
            Equal("Found A.", found.Answer, "the resumed answer");
            Equal("https://example.org/a", found.Results.Single().Url, "the resumed results");

            var refused = await ThrowsAsync<WebSearchException>(() => new AnthropicWebSearch("k", http: new HttpClient(new FakeHttp(_ =>
                Reply("""{"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"},"content":[]}"""))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "refusal");
            Equal("Claude web search, claude-opus-5-5 declined the search (cyber).", refused.Message, "a refusal names its category");
            var limited = await ThrowsAsync<WebSearchException>(() => new AnthropicWebSearch("k", http: new HttpClient(new FakeHttp(_ => Reply("""
                {"stop_reason":"end_turn","content":[{"type":"web_search_tool_result","tool_use_id":"s1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}]}
                """)))).SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "tool error");
            Equal("Claude web search, claude-opus-5-5 could not search: max_uses_exceeded.", limited.Message, "a server tool error, which arrives in a 200 reply");
            var overloaded = await ThrowsAsync<WebSearchException>(() => new AnthropicWebSearch("k", http: new HttpClient(new FakeHttp(_ =>
                Reply("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", (HttpStatusCode)529))))
                .SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "529");
            Equal("Claude web search, claude-opus-5-5 returned HTTP 529: Overloaded.", overloaded.Message, "Anthropic's error message");
        });

        yield return Case("agents.web.openrouter-request-and-results", async () =>
        {
            // OpenRouter may send whitespace before the JSON while the model works.
            var http = new FakeHttp(_ => Reply("""

                {"choices":[{"message":{"role":"assistant","content":"USB PD 3.1 (2021) raised the limit to 240 W.","annotations":[
                  {"type":"url_citation","url_citation":{"url":"https://www.usb.org/usb-charger-pd","title":"USB Charger (USB Power Delivery)","start_index":0,"end_index":0,
                    "content":"The USB PD Revision 3.1 specification ... up to 240W"}},
                  {"type":"url_citation","url_citation":{"url":"https://www.usb.org/usb-charger-pd","title":"again"}},
                  {"type":"url_citation","url_citation":{"url":"https://example.org/epr"}}]}}]}
                """));
            var openrouter = new OpenRouterWebSearch("or-test", "anthropic/claude-sonnet-5.5", new HttpClient(http)) { Engine = "exa" };
            var found = await openrouter.SearchAsync(new WebSearchRequest("usb pd", 30), CancellationToken.None);
            var sent = http.Requests.Single();
            Equal("https://openrouter.ai/api/v1/chat/completions", sent.Uri, "chat completions endpoint");
            Equal("Bearer or-test", sent.Header("Authorization"), "bearer key");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("anthropic/claude-sonnet-5.5", (string?)body["model"], "the model slug");
            Equal("web", (string?)body["plugins"]![0]!["id"], "the web plugin");
            Equal(10, (int?)body["plugins"]![0]!["max_results"], "results capped at 10");
            Equal("exa", (string?)body["plugins"]![0]!["engine"], "engine");
            Equal("USB PD 3.1 (2021) raised the limit to 240 W.", found.Answer, "the reply is the answer");
            Equal("https://www.usb.org/usb-charger-pd,https://example.org/epr", string.Join(",", found.Results.Select(result => result.Url)), "each cited page once");
            Equal("The USB PD Revision 3.1 specification ... up to 240W", found.Results[0].Snippet, "the citation's content is the snippet");
            Equal("example.org", found.Results[1].Title, "an untitled citation is titled by its host");
            var noEngine = new FakeHttp(_ => Reply("""{"choices":[{"message":{"content":"x"}}]}"""));
            await new OpenRouterWebSearch("k", http: new HttpClient(noEngine)).SearchAsync(new WebSearchRequest("x", 2), CancellationToken.None);
            var plain = JsonNode.Parse(noEngine.Requests.Single().Body!)!;
            Check(plain["plugins"]![0]!["engine"] is null && (string?)plain["model"] == "openai/gpt-5.4-mini", "no engine by default, the default model");
            var failed = await ThrowsAsync<WebSearchException>(() => new OpenRouterWebSearch("k", http: new HttpClient(new FakeHttp(_ =>
                Reply("""{"error":{"code":502,"message":"Provider returned error"}}""")))).SearchAsync(new WebSearchRequest("x", 1), CancellationToken.None), "200 error");
            Equal("OpenRouter web search, openai/gpt-5.4-mini failed: Provider returned error", failed.Message, "an error inside a 200 reply");
        });

        yield return Case("agents.web.provider-from-environment", () =>
        {
            static Func<string, string?> Env(params string[] names) => name => names.Contains(name) ? "value" : null;
            Check(WebSearchTool.ProviderFromEnvironment(Env()) is null, "no key: no backend");
            Check(WebSearchTool.ProviderFromEnvironment(Env("OPENROUTER_API_KEY")) is OpenRouterWebSearch, "OpenRouter as the last resort");
            Check(WebSearchTool.ProviderFromEnvironment(Env("OPENROUTER_API_KEY", "XAI_API_KEY")) is XaiWebSearch, "xAI before OpenRouter");
            Check(WebSearchTool.ProviderFromEnvironment(Env("XAI_API_KEY", "ANTHROPIC_API_KEY")) is AnthropicWebSearch, "Anthropic before xAI");
            Check(WebSearchTool.ProviderFromEnvironment(Env("ANTHROPIC_API_KEY", "OPENAI_API_KEY")) is OpenAIWebSearch, "OpenAI before Anthropic");
            Check(WebSearchTool.ProviderFromEnvironment(Env("OPENAI_API_KEY", "FireCrawl_API_KEY")) is FirecrawlWebSearch, "Firecrawl before OpenAI");
            Check(WebSearchTool.ProviderFromEnvironment(Env("FireCrawl_API_KEY", "TAVILY_API_KEY")) is TavilyWebSearch, "Tavily before Firecrawl");
            Check(WebSearchTool.ProviderFromEnvironment(Env("OPENAI_API_KEY", "TAVILY_API_KEY", "BRAVE_API_KEY")) is BraveWebSearch, "Brave first");
            Check(BraveWebSearch.FromEnvironment(name => name == "BRAVE_API_KEY" ? "" : null) is null, "an empty value is unset");
            Equal("gpt-other", OpenAIWebSearch.FromEnvironment(Env("OPENAI_API_KEY"), "gpt-other")!.Model, "the model");
            Check(WebSearchTool.ProviderFromEnvironment("xai", Env("XAI_API_KEY", "BRAVE_API_KEY")) is XaiWebSearch, "a backend by name");
            Check(WebSearchTool.ProviderFromEnvironment("anthropic", Env("XAI_API_KEY")) is null, "a named backend without its key");
            Equal("brave,tavily,firecrawl,openai,anthropic,xai,openrouter", string.Join(",", WebSearchTool.Backends), "the backends");
            Equal("FireCrawl_API_KEY", WebSearchTool.ApiKeyVariable("firecrawl"), "Firecrawl's variable");
            Equal("OPENROUTER_API_KEY", WebSearchTool.ApiKeyVariable("openrouter"), "a backend's variable");
            Throws<ArgumentException>(() => WebSearchTool.ProviderFromEnvironment("bing", Env()), "an unknown backend");
        });
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A recorded request.</summary>
    private sealed record SentRequest(string Method, string Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>An HTTP handler that records each request and answers it.</summary>
    private sealed class FakeHttp(Func<SentRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
            var sent = new SentRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, headers,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (Requests) Requests.Add(sent);
            return respond(sent);
        }
    }

    /// <summary>A backend with fixed results that records each query and count.</summary>
    private sealed class CannedSearch : IWebSearchProvider
    {
        public List<string> Calls { get; } = [];
        public string Name => "Canned";

        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(request.Query + "/" + request.Count);
            return Task.FromResult(new WebSearchResponse
            {
                Provider = Name,
                Query = request.Query,
                Answer = "PD 3.1 allows 240 W.",
                Results =
                [
                    new("USB <b>Power</b> Delivery", "https://www.usb.org/usb-charger-pd", "Extended Power Range raises the limit to 240 W at 48 V and 5 A."),
                    new("Bad", "javascript:alert(1)", "dropped"),
                    new("Wikipedia", "https://en.wikipedia.org/wiki/USB_hardware", null),
                    new("Third", "https://example.org/3", "three"),
                ],
            });
        }
    }

    private sealed class FailingSearch : IWebSearchProvider
    {
        public string Name => "Example";
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken) =>
            throw new WebSearchException("Example refused the query.");
    }

    /// <summary>Refuses the first searches as rate-limited (wait 50 ms), then answers.</summary>
    private sealed class LimitedSearch(int refusals) : IWebSearchProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public string Name => "Limited";

        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref calls) <= refusals
                ? throw new WebSearchException("Limited is busy.") { RetryAfter = TimeSpan.FromMilliseconds(50) }
                : Task.FromResult(new WebSearchResponse { Provider = Name, Query = request.Query, Results = [new("A page", "https://example.org/a", null)] });
    }

    private sealed class HangingSearch : IWebSearchProvider
    {
        public string Name => "Hanging";
        public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
