// Decision 0011 (run W4, R8): Presets. In process: a preset never keeps a credential (a header named like one, a value that looks like a
// key, a provider option or a field the page types do not know), each page's capture round-trips through the store and its JSON back into
// the same page, a preset loads converted into another page with notes for what is left out (never silently), the JSON files parse whole
// (invalid JSON or one invalid preset imports nothing) and carry no use counts, the store keeps ids clear of the routes' words, and R9's
// "presets" export part plans and imports by id. End to end over the faux app: /presets in its empty and default states with nothing the
// CSP would block, save from the Playground, Compare and the Tool Bench, edit, duplicate, count a use, load into each page, export one and
// all (attachments with no secret), import (refused, skipped, renamed) and delete; the three pages host the shared modals and presets-host.js.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Presets;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    private static readonly string[] PresetScripts = ["presets.js", "presets-host.js", "presets-modals.js"];

    internal static IEnumerable<(string, Func<Task>)> PresetCases() =>
    [
        Case("preset.rules.never-keep-a-secret", () =>
        {
            var basic = new Preset { Name = "Secrets", Kind = PresetPages.Playground, Provider = "faux", Model = "echo" };
            Equal("on", PresetRules.Normalize(basic with { Headers = new Dictionary<string, string> { ["X-Trace"] = "on" } }).Headers?["X-Trace"], "a plain header is kept");
            var planted = Planted("sk-");
            foreach (var (name, value) in new[] { ("Authorization", "Basic abc"), ("X-Api-Key", "abc"), ("Cookie", "a=b"), ("X-Note", "Bearer abc.def"), ("X-Note", planted) })
            {
                var error = Throws<ArgumentException>(() => PresetRules.Normalize(basic with { Headers = new Dictionary<string, string> { [name] = value } }), "the header " + name + " is refused");
                Check(!error.Message.Contains(value, StringComparison.Ordinal) || value.Length < 4, "the refusal does not repeat the value: " + error.Message);
            }
            foreach (var extra in new[] { """{"auth":{"apiKey":"x"}}""", "{\"metadata\":{\"note\":\"" + Planted("ghp_") + "\"}}", "{\"list\":[\"" + Planted("AKIA") + "\"]}" })
            {
                var error = Throws<ArgumentException>(() => PresetRules.Normalize(basic with { Options = new PresetOptions { ProviderOptions = JsonNode.Parse(extra)!.AsObject() } }),
                    "provider options " + extra[..12] + " are refused");
                Check(!error.Message.Contains("PLANTED", StringComparison.Ordinal), "the refusal does not repeat the value");
            }
            Check(PresetRules.Normalize(basic with { Options = new PresetOptions { ProviderOptions = JsonNode.Parse("""{"metadata":{"user":"me"}}""")!.AsObject() } }).Options?.ProviderOptions is not null,
                "plain provider options are kept");
            Check(PresetRules.LooksSecret("Bearer x") && PresetRules.LooksSecret(planted) && !PresetRules.LooksSecret("sk") && !PresetRules.LooksSecret("on"), "what looks like a key");

            // A field the page's own types do not know (an API key typed into the spec or the setup) is never kept.
            var capture = PresetPlaygroundCapture();
            capture["spec"]!["apiKey"] = planted;
            capture["apiKey"] = planted;
            NoPlanted(() => PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, capture) with { Name = "k" }), planted, "the Playground's apiKey");
            var bench = PresetBenchCapture();
            bench["setup"]!["apiKey"] = planted;
            NoPlanted(() => PresetRules.Normalize(PresetCapture.From(PresetPages.Bench, bench) with { Name = "k" }), planted, "the bench's apiKey");
            var compare = PresetCompareCapture();
            compare["draft"]!["columns"]![0]!["apiKey"] = planted;
            NoPlanted(() => PresetRules.Normalize(PresetCapture.From(PresetPages.Compare, compare) with { Name = "k" }), planted, "a column's apiKey");
        }),

        Case("preset.capture.round-trips-each-page", () =>
        {
            // The Playground: model, parameters, system prompt, messages and tools (with their mock, on/off and the execution mode).
            var playground = PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, PresetPlaygroundCapture()) with { Name = "SQL explainer" });
            Equal("model,parameters,systemPrompt,messages,tools", string.Join(",", playground.Parts), "the Playground's parts");
            Check(PresetJson.Same(playground, PresetJson.Read(JsonNode.Parse(PresetJson.Export(playground)))), "the file reads back the same preset");
            var plan = PresetLoad.For(playground, PresetPages.Playground);
            Equal(0, plan.Notes.Count, "nothing left out: " + string.Join(" | ", plan.Notes));
            Equal("model,parameters,systemPrompt,messages,tools", string.Join(",", plan.Parts), "every part loads");
            var spec = plan.Payload["spec"]!;
            Equal(("faux", "echo", 0.2, 512, 0.9, "low"), (spec["provider"]!.GetValue<string>(), spec["model"]!.GetValue<string>(), spec["temperature"]!.GetValue<double>(),
                spec["maxTokens"]!.GetValue<int>(), spec["topP"]!.GetValue<double>(), spec["thinking"]!.GetValue<string>()), "the model and options");
            Equal("You explain SQL.", spec["systemPrompt"]!.GetValue<string>(), "the system prompt");
            Equal("Explain this join.", spec["messages"]![0]!["text"]!.GetValue<string>(), "the messages");
            Equal("get_weather", spec["toolChoice"]!.GetValue<string>(), "the tool choice");
            var tools = plan.Payload["tools"]!.AsArray();
            Equal("get_weather,lookup", string.Join(",", tools.Select(tool => tool!["name"]!.GetValue<string>())), "the tools");
            Check(tools[0]!["mock"]!.GetValue<string>() == """{"temp":4}""" && tools[0]!["mockError"]!.GetValue<bool>() == false && tools[0]!["enabled"]!.GetValue<bool>(),
                "the first tool's mock: " + tools[0]!.ToJsonString());
            Check(tools[1]!["enabled"]!.GetValue<bool>() == false && tools[1]!["mock"] is null, "the second is switched off, without a mock");
            Equal(false, plan.Payload["parallel"]!.GetValue<bool>(), "sequential");
            Check(spec["headers"] is null, "no headers were set");

            // Compare: the columns (with their overrides), the shared options, the system prompt and the prompt.
            var compare = PresetRules.Normalize(PresetCapture.From(PresetPages.Compare, PresetCompareCapture()) with { Name = "Speed check" });
            Equal("model,parameters,systemPrompt,messages", string.Join(",", compare.Parts), "Compare's parts");
            var draft = PresetLoad.For(compare, PresetPages.Compare);
            Equal(0, draft.Notes.Count, "nothing left out: " + string.Join(" | ", draft.Notes));
            var shared = draft.Payload["draft"]!;
            Equal("faux/echo,faux/echo-small,faux/echo-thinking", string.Join(",", shared["columns"]!.AsArray().Select(column => column!["provider"] + "/" + column!["model"])), "the columns");
            Equal(0.9, shared["columns"]![1]!["temperature"]!.GetValue<double>(), "a column's temperature");
            Equal("high", shared["columns"]![2]!["thinking"]!.GetValue<string>(), "a column's thinking");
            Equal(("Which is faster?", "Be brief.", 256, 0.5), (shared["prompt"]!.GetValue<string>(), shared["systemPrompt"]!.GetValue<string>(), shared["maxTokens"]!.GetValue<int>(),
                shared["temperature"]!.GetValue<double>()), "the shared request");

            // The Tool Bench: model, thinking and the rules, the system prompt, the tools with their mocks, and the user messages.
            var bench = PresetRules.Normalize(PresetCapture.From(PresetPages.Bench, PresetBenchCapture()) with { Name = "Weather bench" });
            Equal("model,parameters,systemPrompt,messages,tools", string.Join(",", bench.Parts), "the bench's parts");
            var setup = PresetLoad.For(bench, PresetPages.Bench);
            Equal(0, setup.Notes.Count, "nothing left out: " + string.Join(" | ", setup.Notes));
            var loaded = setup.Payload["setup"]!;
            Equal(("faux", "echo", "low", "any", false), (loaded["provider"]!.GetValue<string>(), loaded["model"]!.GetValue<string>(), loaded["thinking"]!.GetValue<string>(),
                loaded["toolChoice"]!.GetValue<string>(), loaded["rules"]!["autoRespond"]!.GetValue<bool>()), "the setup");
            Equal("Oslo", loaded["tools"]![0]!["mocks"]![0]!["name"]!.GetValue<string>(), "the tool's mock");
            Equal("""tool: get_weather {"location":"Oslo"}|and then Bergen""", string.Join("|", setup.Payload["messages"]!.AsArray().Select(item => item!.GetValue<string>())), "the user messages");

            // The execution mode goes with the tools: without tools, a preset keeps no tools part.
            var bare = PresetPlaygroundCapture();
            bare["tools"] = new JsonArray();
            bare["spec"]!.AsObject().Remove("toolChoice");
            Check(!PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, bare) with { Name = "bare" }).Has(PresetParts.Tools), "no tools, no tools part");
        }),

        Case("preset.load.converts-for-another-page", () =>
        {
            var bench = PresetRules.Normalize(PresetCapture.From(PresetPages.Bench, PresetBenchCapture()) with { Name = "Weather bench" });
            var intoPlayground = PresetLoad.For(bench, PresetPages.Playground);
            Equal("required", intoPlayground.Payload["spec"]!["toolChoice"]!.GetValue<string>(), "any is required in the Playground");
            Check(intoPlayground.Notes.Any(note => note.Contains("\"any\"", StringComparison.Ordinal)) && intoPlayground.Notes.Any(note => note.Contains("auto-respond", StringComparison.Ordinal))
                && intoPlayground.Notes.Any(note => note.Contains("one mock response per tool", StringComparison.Ordinal)), "and says so, with the rules and the mocks: " + string.Join(" | ", intoPlayground.Notes));
            Equal("""{"temp":4}""", intoPlayground.Payload["tools"]![0]!["mock"]!.GetValue<string>(), "the matching mock is the one kept (no fallback)");
            var benchIntoCompare = PresetLoad.For(bench, PresetPages.Compare);
            Check(benchIntoCompare.Notes.Any(note => note.Contains("no tools", StringComparison.Ordinal)) && benchIntoCompare.Notes.Any(note => note.Contains("first column", StringComparison.Ordinal)),
                "Compare names what it leaves out: " + string.Join(" | ", benchIntoCompare.Notes));
            Equal(1, benchIntoCompare.Payload["draft"]!["columns"]!.AsArray().Count, "one column, the others stay");
            Equal("and then Bergen", benchIntoCompare.Payload["draft"]!["prompt"]!.GetValue<string>(), "the last user message is the prompt");

            var compare = PresetRules.Normalize(PresetCapture.From(PresetPages.Compare, PresetCompareCapture()) with { Name = "Speed check" });
            var compareIntoPlayground = PresetLoad.For(compare, PresetPages.Playground);
            Equal("echo", compareIntoPlayground.Payload["spec"]!["model"]!.GetValue<string>(), "the first column's model");
            Check(compareIntoPlayground.Notes.Any(note => note.Contains("2 other models are left out", StringComparison.Ordinal)), "the others are named: " + string.Join(" | ", compareIntoPlayground.Notes));
            var compareIntoBench = PresetLoad.For(compare, PresetPages.Bench);
            Check(compareIntoBench.Notes.Any(note => note.Contains("thinking only", StringComparison.Ordinal)), "the bench keeps thinking only: " + string.Join(" | ", compareIntoBench.Notes));

            var playground = PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, PresetPlaygroundCapture(headers: new JsonObject { ["X-Trace"] = "on" })) with { Name = "SQL explainer" });
            var playgroundIntoCompare = PresetLoad.For(playground, PresetPages.Compare);
            var notes = string.Join(" | ", playgroundIntoCompare.Notes);
            Check(notes.Contains("topP", StringComparison.Ordinal) && notes.Contains("no extra headers", StringComparison.Ordinal) && notes.Contains("no tools", StringComparison.Ordinal)
                && notes.Contains("first column and the other columns stay", StringComparison.Ordinal), "Compare names each part it leaves out: " + notes);
            Check(playgroundIntoCompare.Payload["draft"]!["topP"] is null && playgroundIntoCompare.Payload["draft"]!["headers"] is null, "and sends none of them");
            var playgroundIntoBench = PresetLoad.For(playground, PresetPages.Bench);
            Check(string.Join(" | ", playgroundIntoBench.Notes).Contains("no extra headers", StringComparison.Ordinal), "the bench too");

            // The account is an id: the bench selects it; the Playground and Compare say they use the active account.
            var withAccount = playground with { Account = "acc-1" };
            Equal("acc-1", PresetLoad.For(withAccount, PresetPages.Bench).Payload["setup"]!["account"]!.GetValue<string>(), "the bench's account");
            Check(PresetLoad.For(withAccount, PresetPages.Playground).Notes.Any(note => note.Contains("active faux account", StringComparison.Ordinal)), "the Playground's note");
            Check(PresetLoad.For(withAccount, PresetPages.Compare).Notes.Any(note => note.Contains("active faux account", StringComparison.Ordinal)), "Compare's note");
            Throws<ArgumentException>(() => PresetLoad.For(playground, "chat"), "only the three pages");
        }),

        Case("preset.json.export-and-parse", () =>
        {
            var preset = PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, PresetPlaygroundCapture()) with { Name = "SQL explainer", Id = "sql-explainer" })
                with { Uses = 7, LastUsed = DateTimeOffset.UtcNow, UsedIn = [PresetPages.Playground] };
            var file = PresetJson.Export(preset);
            foreach (var key in PresetJson.LocalKeys) Check(!file.Contains($"\"{key}\"", StringComparison.Ordinal), "a file has no " + key);
            Equal("sql-explainer.preset.json", PresetJson.FileName(preset), "its file name");
            var all = PresetJson.ExportAll([preset], new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
            var envelope = JsonNode.Parse(all)!;
            Equal((PresetJson.Format, PresetJson.Version, 1), (envelope["format"]!.GetValue<string>(), envelope["version"]!.GetValue<int>(), envelope["presets"]!.AsArray().Count), "the envelope");
            Equal(1, PresetJson.Parse(all).Count, "an export reads back");
            Equal(1, PresetJson.Parse(file).Count, "one preset reads back");
            Equal(1, PresetJson.Parse("[" + file + "]").Count, "a list reads back");
            Equal(0, PresetJson.Parse(file).Single().Uses, "a file's use count is never taken");
            Check(Throws<ArgumentException>(() => PresetJson.Parse("{ not json"), "invalid JSON").Message.StartsWith("The file is not valid JSON", StringComparison.Ordinal), "invalid JSON is named");
            Throws<ArgumentException>(() => PresetJson.Parse(""), "an empty file");
            Throws<ArgumentException>(() => PresetJson.Parse("""{"hello":1}"""), "not a presets file");
            Throws<ArgumentException>(() => PresetJson.Parse("""{"format":"other","presets":[]}"""), "another format");
            Throws<ArgumentException>(() => PresetJson.Parse("""{"format":"pisharp-presets","version":99,"presets":[]}"""), "a newer version");
            var bad = JsonNode.Parse(all)!;
            bad["presets"]!.AsArray().Add(JsonNode.Parse("""{"name":"Bad","kind":"playground","provider":"faux","model":"echo","headers":{"Authorization":"Bearer x"}}"""));
            Check(Throws<ArgumentException>(() => PresetJson.Parse(bad.ToJsonString()), "one invalid preset").Message.Contains("Nothing was imported", StringComparison.Ordinal),
                "one invalid preset refuses the whole file");
            Throws<ArgumentException>(() => PresetJson.Parse("""{"name":"Bad id","id":"Not An Id","kind":"playground","provider":"faux","model":"echo"}"""), "a bad id");
            Equal("export-preset", PresetJson.Parse("""{"name":"x","id":"export","kind":"playground","provider":"faux","model":"echo"}""").Single().Id, "a route word is never an id");
            Check(PresetJson.Changes(null, preset).Any(change => change.Key == "name") && PresetJson.Changes(preset, preset with { Name = "Other" }).Single().Key == "name", "the changed keys");
        }),

        Case("preset.store.round-trip", () =>
        {
            var root = Temp("preset-store");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, PresetStore.Migration]);
                db.Migrate();
                Equal("presets", MigrationArea.Of(PresetStore.Migration.Id)?.Name, "migration 400 is in the presets range");
                var store = new PresetStore(db);
                var changed = 0;
                store.Changed += (_, _) => changed++;
                var draft = PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, PresetPlaygroundCapture()) with { Name = "SQL Explainer · low temp" });
                var first = store.Add(draft);
                var second = store.Add(draft);
                Equal(("sql-explainer-low-temp", "sql-explainer-low-temp-2"), (first.Id, second.Id), "ids from the name, never the same");
                Check(first.Created != default && first.Updated >= first.Created, "when it was saved");
                Check(PresetJson.Same(first, store.Get(first.Id)!), "it reads back");
                Equal(2, store.Count(), "two presets");
                Equal("grid-preset", store.Add(draft with { Name = "Grid" }).Id, "a route word is never an id");
                var used = store.Update(first.Id, preset => preset with { Uses = preset.Uses + 1 }, touch: false)!;
                Equal((1, first.Updated), (used.Uses, used.Updated), "a use does not count as a change");
                Check(store.Delete(second.Id) && !store.Delete(second.Id) && store.Get(second.Id) is null, "deleted once");
                Check(store.Update("nope", preset => preset) is null, "an unknown preset");
                var imported = store.AddAll([draft with { Id = first.Id, Name = "Clash" }, draft with { Name = "Fresh" }]);
                Equal(first.Id + "-2", imported[0].Id, "an import never replaces a preset");
                Equal(4, store.Count(), "both imported");
                Check(changed >= 6, "every change is announced: " + changed);
                Equal(PresetStore.Usable("export"), PresetStore.Slug("Export"), "slugs avoid the route words");
                Equal("preset", PresetStore.Slug("!!!"), "a name without letters");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("preset.data-part.plans-and-imports", async () =>
        {
            var root = Temp("preset-part");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, PresetStore.Migration]);
                db.Migrate();
                var store = new PresetStore(db);
                var part = new PresetsExportPart(store);
                Equal("presets", part.Name, "the part's name");
                Check(part.Export() is null, "nothing to export yet");
                var draft = PresetRules.Normalize(PresetCapture.From(PresetPages.Playground, PresetPlaygroundCapture()) with { Name = "SQL explainer" });
                var kept = store.Add(draft);
                store.Update(kept.Id, preset => preset with { Uses = 5 }, touch: false);
                var exported = part.Export()!;
                Check(!exported.ToJsonString().Contains("\"uses\"", StringComparison.Ordinal), "an export has no use counts");
                Equal(0, part.Plan(exported).Items.Count, "the same presets: nothing to import");
                Equal(1, part.Plan(exported).Unchanged, "one unchanged");

                var file = JsonNode.Parse(exported.ToJsonString())!.AsArray();
                file[0]!["name"] = "SQL explainer v2";
                file.Add(JsonNode.Parse("""{"id":"new-one","name":"New one","kind":"playground","provider":"faux","model":"echo","systemPrompt":"Hi."}"""));
                file.Add(JsonNode.Parse("""{"id":"bad","name":"Bad","kind":"playground","provider":"faux","model":"echo","headers":{"X-Api-Key":"x"}}"""));
                var plan = part.Plan(file);
                Equal("presets/sql-explainer,presets/new-one,presets/bad", string.Join(",", plan.Items.Select(item => item.Id)), "one item each");
                Check(plan.Items[0].Changes.Any(change => change is { Key: "name", After: "SQL explainer v2" }), "the changed key");
                Check(plan.Items[2].Problem is { Length: > 0 }, "an invalid preset is listed with its problem");
                Check(plan.Notes.Count == 1, "the note about replacing a preset of the same id");
                var result = await part.ImportAsync(file, new HashSet<string>(["presets/sql-explainer", "presets/new-one", "presets/bad"]), CancellationToken.None);
                Equal("presets/sql-explainer,presets/new-one", string.Join(",", result.Applied), "the valid ones are imported");
                var replaced = store.Get("sql-explainer")!;
                Equal(("SQL explainer v2", 5), (replaced.Name, replaced.Uses), "replaced by id, keeping the use count");
                Check(store.Get("bad") is null, "the invalid one is not");
                var skipped = await part.ImportAsync(file, new HashSet<string>(), CancellationToken.None);
                Equal(0, skipped.Applied.Count, "only accepted items");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("preset.http.save-edit-use-load-delete", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("preset-http");
            Equal("[]", (await app.Get("/api/presets")).Body?["presets"]?.ToJsonString(), "no presets yet");

            // The save modal's content for the Playground's request.
            var preview = await app.SendAsync(HttpMethod.Post, "/api/presets/preview", new JsonObject { ["page"] = "playground", ["capture"] = PresetPlaygroundCapture(headers: new JsonObject { ["X-Trace"] = "on" }) }, origin: app.Origin);
            Equal(200, preview.Status, "the preview: " + preview.Text);
            var modal = preview.Body!["html"]!.GetValue<string>();
            Check(preview.Body["valid"]!.GetValue<bool>() && modal.Contains("data-preset-save-form", StringComparison.Ordinal) && modal.Contains("Captured settings", StringComparison.Ordinal),
                "the new pane: " + Cut(modal));
            Check(Regex.IsMatch(modal, "id=\"sp-inc-model\"[^>]*checked") && !Regex.IsMatch(modal, "id=\"sp-inc-headers\"[^>]*checked"), "headers are left out unless ticked");
            PlaygroundNoInline("the save modal", modal);
            var refused = await app.SendAsync(HttpMethod.Post, "/api/presets/preview", new JsonObject { ["page"] = "playground", ["capture"] = new JsonObject { ["spec"] = new JsonObject { ["provider"] = "faux" } } }, origin: app.Origin);
            Check(refused.Status == 200 && refused.Body?["valid"]?.GetValue<bool>() == false && refused.Body["html"]!.GetValue<string>().Contains("data-preset-capture-error", StringComparison.Ordinal),
                "a request that cannot be saved says why: " + refused.Text);

            // Save from each page.
            var saved = await PresetSaveAsync(app, "playground", PresetPlaygroundCapture(headers: new JsonObject { ["X-Trace"] = "on" }), "SQL explainer", ["review", "cheap"],
                include: ["model", "parameters", "systemPrompt", "messages", "tools"]);
            Equal("sql-explainer", saved["id"]!.GetValue<string>(), "the id");
            Check(saved["headers"] is null && saved["uses"] is null, "the headers were left out, and the answer is the file's form: " + saved.ToJsonString());
            var compare = await PresetSaveAsync(app, "compare", PresetCompareCapture(), "Speed check", ["review"]);
            var bench = await PresetSaveAsync(app, "bench", PresetBenchCapture(), "Weather bench", []);
            Equal(("compare", "bench"), (compare["kind"]!.GetValue<string>(), bench["kind"]!.GetValue<string>()), "the kinds");
            var blank = await app.SendAsync(HttpMethod.Post, "/api/presets", new JsonObject
            {
                ["page"] = "presets", ["capture"] = new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["systemPrompt"] = "Be kind." }, ["name"] = "Kind echo",
            }, origin: app.Origin);
            Check(blank.Status == 200 && blank.Body?["preset"]?["systemPrompt"]?.GetValue<string>() == "Be kind.", "New preset on the Presets page: " + blank.Text);
            Equal(4, (await app.Get("/api/presets")).Body!["presets"]!.AsArray().Count, "four presets");

            // A secret is refused, and not repeated.
            var planted = Planted("sk-");
            var secret = await app.SendAsync(HttpMethod.Post, "/api/presets", new JsonObject
            {
                ["page"] = "playground", ["capture"] = PresetPlaygroundCapture(headers: new JsonObject { ["X-Note"] = planted }), ["name"] = "Leaky",
            }, origin: app.Origin);
            Check(secret.Status == 400 && secret.Body?["error"]?["code"]?.GetValue<string>() == "invalid" && !secret.Text.Contains(planted, StringComparison.Ordinal),
                "a header that looks like a key is refused: " + secret.Text);
            Equal(4, (await app.Get("/api/presets")).Body!["presets"]!.AsArray().Count, "nothing saved");
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/presets", new JsonObject { ["page"] = "presets" }, origin: "http://evil.example")).Status, "a cross-origin write is refused");
            Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/presets/import", text: "{}", contentType: "text/plain", origin: app.Origin)).Status, "writes are JSON");

            // Update from the page (keeps the id and the uses), edit, duplicate, count a use.
            var used = await app.SendAsync(HttpMethod.Post, "/api/presets/sql-explainer/used", new JsonObject { ["page"] = "compare" }, origin: app.Origin);
            Equal(1, used.Body?["uses"]?.GetValue<int>(), "a use: " + used.Text);
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/presets/sql-explainer/used", new JsonObject { ["page"] = "chat" }, origin: app.Origin)).Status, "only the three pages");
            var update = PresetPlaygroundCapture();
            update["spec"]!["temperature"] = 0.7;
            var updated = await app.SendAsync(HttpMethod.Post, "/api/presets", new JsonObject
            {
                ["page"] = "playground", ["capture"] = update, ["name"] = "SQL explainer", ["tags"] = new JsonArray("review"), ["update"] = "sql-explainer",
            }, origin: app.Origin);
            Check(updated.Status == 200 && updated.Body?["created"]?.GetValue<bool>() == false, "updated: " + updated.Text);
            Equal(("sql-explainer", 0.7, 1), (updated.Body!["preset"]!["id"]!.GetValue<string>(), updated.Body["preset"]!["options"]!["temperature"]!.GetValue<double>(),
                (await app.Get("/api/presets/sql-explainer")).Body!["preset"]!["uses"]!.GetValue<int>()), "same id, new temperature, uses kept");
            var crossKind = await app.SendAsync(HttpMethod.Post, "/api/presets", new JsonObject
            {
                ["page"] = "bench", ["capture"] = PresetBenchCapture(), ["name"] = "Bench over it", ["update"] = "sql-explainer",
            }, origin: app.Origin);
            Check(crossKind.Status == 400 && crossKind.Text.Contains("as a new preset", StringComparison.Ordinal), "a Playground preset is not updated from the bench: " + crossKind.Text);
            var sameOrigin = await app.SendAsync(HttpMethod.Post, "/api/presets/preview", new JsonObject { ["page"] = "playground", ["capture"] = PresetPlaygroundCapture(), ["origin"] = "sql-explainer" }, origin: app.Origin);
            var otherOrigin = await app.SendAsync(HttpMethod.Post, "/api/presets/preview", new JsonObject { ["page"] = "bench", ["capture"] = PresetBenchCapture(), ["origin"] = "sql-explainer" }, origin: app.Origin);
            Check(sameOrigin.Body!["html"]!.GetValue<string>().Contains("id=\"sp-target-update\"", StringComparison.Ordinal)
                && !otherOrigin.Body!["html"]!.GetValue<string>().Contains("id=\"sp-target-update\"", StringComparison.Ordinal), "Update is offered on the preset's own page only");
            var edited = await app.SendAsync(HttpMethod.Post, "/api/presets/sql-explainer", new JsonObject
            {
                ["name"] = "SQL explainer · low temp", ["description"] = "Explains joins.", ["tags"] = "review, sql", ["pinned"] = true, ["include"] = new JsonArray("model", "parameters", "systemPrompt", "tools"),
            }, origin: app.Origin);
            Equal(200, edited.Status, "the edit pane: " + edited.Text);
            var after = edited.Body!["preset"]!;
            Check(after["name"]!.GetValue<string>() == "SQL explainer · low temp" && after["pinned"]!.GetValue<bool>() && after["messages"] is null
                && after["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()).SequenceEqual(["review", "sql"]), "renamed, pinned, tagged, without messages: " + after.ToJsonString());
            Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/presets/sql-explainer", new JsonObject { ["name"] = "" }, origin: app.Origin)).Status, "a name is required");
            var copy = await app.SendAsync(HttpMethod.Post, "/api/presets/sql-explainer/duplicate", new JsonObject(), origin: app.Origin);
            Check(copy.Status == 200 && copy.Body?["preset"]?["name"]?.GetValue<string>() == "SQL explainer · low temp (copy)" && copy.Body["preset"]!["id"]!.GetValue<string>() != "sql-explainer",
                "a copy: " + copy.Text);

            // Load into each page: the payload, the parts and the notes; nothing counted.
            foreach (var page in PresetPages.All)
            {
                var load = await app.Get($"/api/presets/weather-bench/load?page={page}");
                Equal(200, load.Status, "load into " + page + ": " + load.Text);
                Check(load.Body!["available"]!.GetValue<bool>() && load.Body["page"]!.GetValue<string>() == page && load.Body["parts"]!.AsArray().Count > 0, "a plan for " + page);
                Check(page == "bench" ? load.Body["notes"]!.AsArray().Count == 0 : load.Body["notes"]!.AsArray().Count > 0, page + "'s notes: " + load.Body["notes"]!.ToJsonString());
                Check(!load.Text.Contains("\"uses\"", StringComparison.Ordinal), "the plan carries no counts");
            }
            Equal(0, (await app.Get("/api/presets/weather-bench")).Body!["preset"]!["uses"]?.GetValue<int>() ?? 0, "loading is not counted until the page loads it");
            Equal(400, (await app.Get("/api/presets/weather-bench/load?page=chat")).Status, "only the three pages");
            Equal(404, (await app.Get("/api/presets/nope/load?page=playground")).Status, "an unknown preset");
            var unknown = await PresetSaveAsync(app, "presets", new JsonObject { ["provider"] = "faux", ["model"] = "gone-model" }, "Gone", []);
            var gone = await app.Get($"/api/presets/{unknown["id"]}/load?page=playground");
            Check(gone.Body?["available"]?.GetValue<bool>() == false && gone.Body["problem"]!.GetValue<string>().Contains("faux/gone-model", StringComparison.Ordinal),
                "a model this app does not know cannot load: " + gone.Text);

            // Delete.
            Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/presets/{unknown["id"]}/delete", new JsonObject(), origin: app.Origin)).Status, "deleted");
            Equal(404, (await app.SendAsync(HttpMethod.Post, $"/api/presets/{unknown["id"]}/delete", new JsonObject(), origin: app.Origin)).Status, "once");
            Equal(404, (await app.Get($"/api/presets/{unknown["id"]}")).Status, "gone");
            var list = await app.Get("/api/presets");
            Equal("no-store", list.Response.Headers.CacheControl?.ToString(), "answers are not cached");
            Check(!app.Log.Contains(planted, StringComparison.Ordinal), "the planted key never reaches the log");
        }),

        Case("preset.http.export-and-import", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("preset-files");
            await PresetSaveAsync(app, "playground", PresetPlaygroundCapture(), "SQL explainer", ["review"]);
            await PresetSaveAsync(app, "bench", PresetBenchCapture(), "Weather bench", ["tools"]);
            await app.SendAsync(HttpMethod.Post, "/api/presets/sql-explainer/used", new JsonObject { ["page"] = "playground" }, origin: app.Origin);

            var one = await app.Get("/api/presets/sql-explainer/export");
            Equal(200, one.Status, "one preset's file");
            Check(one.Response.Content.Headers.ContentDisposition?.DispositionType == "attachment" && one.Response.Content.Headers.ContentDisposition.FileName?.Trim('"') == "sql-explainer.preset.json",
                "a download: " + one.Response.Content.Headers.ContentDisposition);
            Check(!one.Text.Contains("\"uses\"", StringComparison.Ordinal) && !one.Text.Contains("apiKey", StringComparison.OrdinalIgnoreCase), "no use count, no key: " + Cut(one.Text));
            var all = await app.Get("/api/presets/export");
            Check(all.Status == 200 && Regex.IsMatch(all.Response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "", "^pisharp-presets-\\d{4}-\\d{2}-\\d{2}\\.json$"), "every preset's file");
            Equal(2, all.Body!["presets"]!.AsArray().Count, "both presets");
            Equal(404, (await app.Get("/api/presets/nope/export")).Status, "an unknown preset");

            // Import: invalid JSON or one invalid preset saves nothing; the same presets are skipped; a taken id is renamed.
            var invalid = await PresetImportAsync(app, "{ not json");
            Check(invalid.Status == 400 && invalid.Body!["error"]!["message"]!.GetValue<string>().StartsWith("The file is not valid JSON", StringComparison.Ordinal), "invalid JSON: " + invalid.Text);
            var mixed = JsonNode.Parse(all.Text)!;
            var leaked = Planted("ghp_");
            mixed["presets"]!.AsArray().Add(JsonNode.Parse("""{"id":"leaky","name":"Leaky","kind":"playground","provider":"faux","model":"echo","options":{"providerOptions":{"note":"LEAKED"}}}""".Replace("LEAKED", leaked, StringComparison.Ordinal)));
            mixed["presets"]!.AsArray().Add(JsonNode.Parse("""{"id":"fine","name":"Fine","kind":"playground","provider":"faux","model":"echo"}"""));
            var refused = await PresetImportAsync(app, mixed.ToJsonString());
            Check(refused.Status == 400 && refused.Text.Contains("Nothing was imported", StringComparison.Ordinal) && !refused.Text.Contains(leaked, StringComparison.Ordinal),
                "a file with a secret is refused whole: " + refused.Text);
            Equal(2, (await app.Get("/api/presets")).Body!["presets"]!.AsArray().Count, "nothing saved");
            var same = await PresetImportAsync(app, all.Text);
            Check(same.Status == 200 && same.Body!["imported"]!.AsArray().Count == 0 && same.Body["skipped"]!.AsArray().Count == 2, "the same presets are skipped: " + same.Text);
            var changed = JsonNode.Parse(one.Text)!;
            changed["description"] = "Changed elsewhere.";
            var renamed = await PresetImportAsync(app, changed.ToJsonString());
            Check(renamed.Status == 200 && renamed.Body!["renamed"]!.AsArray().Single()!["to"]!.GetValue<string>() == "sql-explainer-2", "a taken id is saved under a new one: " + renamed.Text);
            Equal(1, (await app.Get("/api/presets/sql-explainer")).Body!["preset"]!["uses"]!.GetValue<int>(), "the preset here is never replaced");
            Equal(0, (await app.Get("/api/presets/sql-explainer-2")).Body!["preset"]!["uses"]?.GetValue<int>() ?? 0, "an imported preset starts unused");
        }),

        Case("preset.pages.render-and-host-the-modals", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("preset-pages");
            var empty = await app.Get("/presets");
            Equal(200, empty.Status, "GET /presets");
            CheckShell("/presets", empty.Text);
            PlaygroundNoInline("/presets", empty.Text);
            Check(!empty.Text.Contains("Coming in a later milestone", StringComparison.Ordinal), "no longer the later-milestone page");
            Check(empty.Text.Contains("data-presets data-state=\"empty\"", StringComparison.Ordinal) && empty.Text.Contains("data-presets-empty", StringComparison.Ordinal)
                && empty.Text.Contains("No presets <em>yet</em>", StringComparison.Ordinal), "the empty state");
            Check(Regex.IsMatch(empty.Text, "title=\"No presets to export yet\"") && empty.Text.Contains("data-preset-import", StringComparison.Ordinal), "Import, and Export all disabled");
            Check(Regex.IsMatch(empty.Text, "id=\"modal-save-preset\"[^>]*data-page=\"presets\"") && empty.Text.Contains("data-mode=\"new\"", StringComparison.Ordinal)
                && empty.Text.Contains("data-provider-model=\"sp\"", StringComparison.Ordinal), "New preset: a model and a system prompt");
            Check(Regex.IsMatch(empty.Text, "<script type=\"module\" src=\"/js/presets\\.js\\?v=") && Regex.IsMatch(empty.Text, "<link href=\"/css/presets\\.css\\?v="), "its module and stylesheet");
            foreach (var script in PresetScripts)
            {
                var asset = await app.Get("/js/" + script);
                Equal(200, asset.Status, script);
                Check(!Regex.IsMatch(asset.Text, "\\.innerHTML\\s*=\\s*[^;\\n]*\\b(name|text|message|description|notes|body|title)\\b"), script + " never writes user text as HTML");
                Check(!Regex.IsMatch(asset.Text, "\\.insertAdjacentHTML|document\\.write|\\beval\\(|new Function\\("), script + " writes no other HTML and runs no text");
                Check(!asset.Text.Contains("?state=", StringComparison.Ordinal) && !asset.Text.Contains("?modal=", StringComparison.Ordinal), script + " has none of the mockups' machinery");
            }
            Equal(200, (await app.Get("/css/presets.css")).Status, "presets.css");

            await PresetSaveAsync(app, "playground", PresetPlaygroundCapture(), "SQL explainer", ["review", "cheap"]);
            await PresetSaveAsync(app, "compare", PresetCompareCapture(), "Speed check", ["review"]);
            await PresetSaveAsync(app, "bench", PresetBenchCapture(), "Weather bench", ["tools"]);
            await PresetSaveAsync(app, "presets", new JsonObject { ["provider"] = "faux", ["model"] = "gone-model" }, "Gone", []);
            var page = await app.Get("/presets");
            CheckShell("/presets", page.Text);
            PlaygroundNoInline("/presets default", page.Text);
            Check(page.Text.Contains("data-presets data-state=\"default\" data-total=\"4\"", StringComparison.Ordinal), "the default state");
            Equal(4, Regex.Matches(page.Text, "data-preset-card=\"").Count, "a card for each preset");
            Check(page.Text.Contains("href=\"/playground?preset=sql-explainer&amp;provider=faux&amp;model=echo\"", StringComparison.Ordinal), "Open in Playground");
            Check(page.Text.Contains("href=\"/compare?preset=speed-check\"", StringComparison.Ordinal) && page.Text.Contains("href=\"/bench?preset=weather-bench\"", StringComparison.Ordinal),
                "Open in Compare and in the Tool Bench");
            Check(Regex.IsMatch(page.Text, "data-preset-card=\"gone\"[^>]*") && page.Text.Contains("preset-card p-3 h-100 d-flex flex-column is-warn", StringComparison.Ordinal),
                "a preset that cannot load is marked");
            Check(Regex.IsMatch(page.Text, "4 presets (·|&#xB7;) 1 compare (·|&#xB7;) 1 bench"), "the count: " + Regex.Match(page.Text, "data-preset-count>([^<]*)<").Groups[1].Value);
            Check(page.Text.Contains("href=\"/api/presets/export\" download data-preset-export-all", StringComparison.Ordinal), "Export all");
            var usesAt = page.Text.IndexOf("\"uses\":", StringComparison.Ordinal);
            Check(usesAt < 0, "no JSON of the presets on the page: " + (usesAt < 0 ? "" : page.Text.Substring(Math.Max(0, usesAt - 300), 400)));

            var grid = await app.Get("/presets/grid?q=speed");
            Check(grid.Status == 200 && Regex.Matches(grid.Text, "data-preset-card=\"").Count == 1 && grid.Text.Contains("data-count=\"1 of 4 presets\"", StringComparison.Ordinal),
                "the grid for a search: " + Cut(grid.Text));
            var tagged = await app.Get("/presets/grid?tag=review&in=compare");
            Equal(1, Regex.Matches(tagged.Text, "data-preset-card=\"").Count, "a tag and a page");
            var none = await app.Get("/presets/grid?q=nothing-like-this");
            Check(none.Text.Contains("data-presets-none", StringComparison.Ordinal) && none.Text.Contains("data-preset-clear", StringComparison.Ordinal), "none match");
            var bench = await app.Get("/presets?type=bench");
            Equal(1, Regex.Matches(bench.Text, "data-preset-card=\"").Count, "the bench's link lists bench presets");

            var edit = await app.Get("/presets/sql-explainer/edit");
            Check(edit.Status == 200 && edit.Text.Contains("data-mode=\"edit\"", StringComparison.Ordinal) && edit.Text.Contains("Preset JSON", StringComparison.Ordinal)
                && edit.Text.Contains("data-preset-delete", StringComparison.Ordinal), "the edit pane");
            Equal(404, (await app.Get("/presets/nope/edit")).Status, "an unknown preset");
            var picker = await app.Get("/presets/picker?page=bench");
            Check(picker.Status == 200 && Regex.Matches(picker.Text, "data-lp-item=\"").Count == 4 && picker.Text.Contains("data-lp-ok=\"false\"", StringComparison.Ordinal)
                && picker.Text.Contains("Manage all 4 presets", StringComparison.Ordinal), "the load modal's list: " + Cut(picker.Text));
            PlaygroundNoInline("the load modal", picker.Text);

            // Each page hosts the modals and presets-host.js; its own buttons stay as the page renders them (presets-host.js turns them on).
            foreach (var (href, title) in new[] { ("/playground?preset=sql-explainer", "Playground"), ("/compare?preset=speed-check", "Compare"), ("/bench?preset=weather-bench", "Tool Bench") })
            {
                var host = await app.Get(href);
                Equal(200, host.Status, href);
                CheckShell(href, host.Text);
                PlaygroundNoInline(href, host.Text);
                Check(host.Text.Contains($"<h1 class=\"app-title\">{title}", StringComparison.Ordinal), href + ": the page");
                Check(Regex.IsMatch(host.Text, "id=\"modal-save-preset\"[^>]*data-page=\"(playground|compare|bench)\"") && host.Text.Contains("data-mode=\"host\"", StringComparison.Ordinal)
                    && host.Text.Contains("id=\"modal-load-preset\"", StringComparison.Ordinal) && host.Text.Contains("data-hosted=\"true\"", StringComparison.Ordinal), href + ": the modals' shells");
                Check(Regex.IsMatch(host.Text, "<script type=\"module\" src=\"/js/presets-host\\.js\\?v=") && Regex.IsMatch(host.Text, "<link href=\"/css/presets\\.css\\?v="), href + ": presets-host.js and presets.css");
                Check(host.Text.Contains("data-preset-save", StringComparison.Ordinal) && host.Text.Contains("data-preset-load", StringComparison.Ordinal), href + ": the buttons");
                Check(Regex.IsMatch(host.Text, "<link href=\"/css/(playground|compare|bench)\\.css\\?v="), href + ": the page's own stylesheet is still versioned");
                Equal(1, Regex.Matches(host.Text, "id=\"modal-save-preset\"").Count, href + ": one save modal");
            }
            var agent = await app.Get("/compare?mode=agent");
            Check(agent.Status == 200 && !agent.Text.Contains("presets-host.js", StringComparison.Ordinal), "agent comparisons have no presets");
            Equal(200, (await app.Get("/presets/picker?page=compare")).Status, "Compare's list");
        }),
    ];

    private static void NoPlanted(Func<Preset> make, string planted, string what)
    {
        try
        {
            var preset = make();
            Check(!JsonSerializer.Serialize(preset, SettingsStore.JsonOptions).Contains(planted, StringComparison.Ordinal) && !PresetJson.Export(preset).Contains(planted, StringComparison.Ordinal),
                what + " is never kept");
        }
        catch (ArgumentException error)
        {
            Check(!error.Message.Contains(planted, StringComparison.Ordinal), what + ": the refusal does not repeat it");
        }
    }

    /// <summary>The Playground's request as presets-host.js captures it: faux/echo, options, a system prompt, a message and two tools.</summary>
    private static JsonObject PresetPlaygroundCapture(JsonObject? headers = null)
    {
        var spec = new JsonObject
        {
            ["provider"] = "faux",
            ["model"] = "echo",
            ["source"] = "playground",
            ["thinking"] = "low",
            ["maxTokens"] = 512,
            ["temperature"] = 0.2,
            ["topP"] = 0.9,
            ["systemPrompt"] = "You explain SQL.",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Explain this join." }),
            ["toolChoice"] = "get_weather",
        };
        if (headers is not null) spec["headers"] = headers;
        return new JsonObject
        {
            ["spec"] = spec,
            ["tools"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "get_weather",
                    ["description"] = "Current weather for a place.",
                    ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}"""),
                    ["enabled"] = true,
                    ["mock"] = """{"temp":4}""",
                    ["mockError"] = false,
                },
                new JsonObject
                {
                    ["name"] = "lookup",
                    ["description"] = "",
                    ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
                    ["enabled"] = false,
                }),
            ["parallel"] = false,
        };
    }

    /// <summary>Compare's request as presets-host.js captures it: three faux columns (two with overrides) and the shared options.</summary>
    private static JsonObject PresetCompareCapture() => new()
    {
        ["draft"] = new JsonObject
        {
            ["systemPrompt"] = "Be brief.",
            ["prompt"] = "Which is faster?",
            ["thinking"] = null,
            ["maxTokens"] = 256,
            ["temperature"] = 0.5,
            ["columns"] = new JsonArray(
                new JsonObject { ["provider"] = "faux", ["model"] = "echo" },
                new JsonObject { ["provider"] = "faux", ["model"] = "echo-small", ["temperature"] = 0.9 },
                new JsonObject { ["provider"] = "faux", ["model"] = "echo-thinking", ["thinking"] = "high" }),
        },
    };

    /// <summary>The Tool Bench's setup and messages as bench-setup.js reads them.</summary>
    private static JsonObject PresetBenchCapture()
    {
        var setup = BenchSetupJson(autoRespond: false);
        setup["thinking"] = "low";
        setup["systemPrompt"] = "Use the tools.";
        setup["toolChoice"] = "any";
        setup["parallel"] = false;
        return new JsonObject
        {
            ["setup"] = setup,
            ["messages"] = new JsonArray("""tool: get_weather {"location":"Oslo"}""", "and then Bergen"),
        };
    }

    private static async Task<JsonNode> PresetSaveAsync(WebAgentApp app, string page, JsonObject capture, string name, string[] tags, string[]? include = null)
    {
        var body = new JsonObject { ["page"] = page, ["capture"] = capture, ["name"] = name, ["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode)JsonValue.Create(tag)!)]) };
        if (include is not null) body["include"] = new JsonArray([.. include.Select(part => (JsonNode)JsonValue.Create(part)!)]);
        var saved = await app.SendAsync(HttpMethod.Post, "/api/presets", body, origin: app.Origin);
        Check(saved.Status == 200 && saved.Body?["created"]?.GetValue<bool>() == true, $"saved from {page}: {saved.Text}");
        return saved.Body!["preset"]!;
    }

    private static Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> PresetImportAsync(WebAgentApp app, string text) =>
        app.SendAsync(HttpMethod.Post, "/api/presets/import", new JsonObject { ["text"] = text }, origin: app.Origin);
}
