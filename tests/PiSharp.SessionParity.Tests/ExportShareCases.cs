using System.Net;
using System.Security.Cryptography;
using System.Text;
using PiSharp.CodingAgent.Export;

// HTML export, JSONL branch export and session sharing (Pi v1.1.0 core/export-html/**, core/session-export.ts,
// modes/interactive/session-share.ts). Expected digests were computed by running the unmodified upstream modules (index.ts
// generateHtml/generateThemeVars, theme.ts, system-theme.ts, pi-tui colors.ts/oklab.ts, session-export.ts serializeSessionBranch)
// under Node 22 against the same authored fixture bytes; only module imports were redirected to local stubs (paths, config dirs).
internal static partial class Program
{
    // Authored fixture session (tricky JSON: replacement patterns, escapes, lone surrogate, number forms, index keys, duplicates,
    // a malformed line) and an authored custom theme, as base64 of their exact bytes.
    private const string FixtureBase64 = "eyJ0eXBlIjoic2Vzc2lvbiIsInZlcnNpb24iOjMsImlkIjoiMDE5OWFhYWEtYmJiYi03Y2NjLThkZGQtZWVlZWZmZmYwMDAwIiwidGltZXN0YW1wIjoiMjAyNi0xMC0wMVQwMDowMDowMC4wMDBaIiwiY3dkIjoiL3dvcmsvcHJvaiJ9CnsidHlwZSI6Im1vZGVsX2NoYW5nZSIsImlkIjoiYTEiLCJwYXJlbnRJZCI6bnVsbCwidGltZXN0YW1wIjoiMjAyNi0xMC0wMVQwMDowMDowMS4wMDBaIiwicHJvdmlkZXIiOiJhbnRocm9waWMiLCJtb2RlbElkIjoiY2xhdWRlLXgifQp7InR5cGUiOiJtZXNzYWdlIiwiaWQiOiJiMiIsInBhcmVudElkIjoiYTEiLCJ0aW1lc3RhbXAiOiIyMDI2LTEwLTAxVDAwOjAwOjAyLjAwMFoiLCJtZXNzYWdlIjp7InJvbGUiOiJ1c2VyIiwiY29udGVudCI6IkhpIDwvc2NyaXB0PiAkJiAkJyAkYCAkJCB7e0pTfX0gw6kg8J+nrSBcLyB0YWJcdGVuZCBcdTAwMDEgbG9uZSBcdWRjMDAgeCIsInRpbWVzdGFtcCI6MS4wZTN9fQp7InR5cGUiOiJtZXNzYWdlIiwiaWQiOiJjMyIsInBhcmVudElkIjoiYjIiLCJ0aW1lc3RhbXAiOiIyMDI2LTEwLTAxVDAwOjAwOjAzLjAwMFoiLCJtZXNzYWdlIjp7InJvbGUiOiJhc3Npc3RhbnQiLCJjb250ZW50IjpbeyJ0eXBlIjoidGV4dCIsInRleHQiOiJvayJ9LHsidHlwZSI6InRvb2xDYWxsIiwiaWQiOiJjYWxsXzEiLCJuYW1lIjoibG9va3VwIiwiYXJndW1lbnRzIjp7ImIiOjEsIjIiOiJ0d28iLCIxIjoib25lIiwibiI6MUUyMSwibSI6LTAsInMiOjFlLTcsImJpZyI6MTIzNDU2Nzg5MDEyMzQ1Njc4OTAsImYiOjAuMSwiZyI6MTAwLjUwLCJoIjoxLjVlMzAwfX0seyJ0eXBlIjoidG9vbENhbGwiLCJpZCI6ImNhbGxfMiIsIm5hbWUiOiJiYXNoIiwiYXJndW1lbnRzIjp7ImNvbW1hbmQiOiJscyJ9fV0sImFwaSI6IngiLCJwcm92aWRlciI6InAiLCJtb2RlbCI6Im0iLCJ1c2FnZSI6eyJpbnB1dCI6MX0sInN0b3BSZWFzb24iOiJ0b29sVXNlIiwidGltZXN0YW1wIjoxNzAwMDAwMDAwMDAwfX0KeyJ0eXBlIjoibWVzc2FnZSIsImlkIjoiZDQiLCJwYXJlbnRJZCI6ImMzIiwidGltZXN0YW1wIjoiMjAyNi0xMC0wMVQwMDowMDowNC4wMDBaIiwibWVzc2FnZSI6eyJyb2xlIjoidG9vbFJlc3VsdCIsInRvb2xDYWxsSWQiOiJjYWxsXzEiLCJ0b29sTmFtZSI6Imxvb2t1cCIsImNvbnRlbnQiOlt7InR5cGUiOiJ0ZXh0IiwidGV4dCI6InJlc3VsdCJ9XSwiZGV0YWlscyI6eyJrIjoidiJ9LCJpc0Vycm9yIjpmYWxzZSwidGltZXN0YW1wIjoxNzAwMDAwMDAwMDAxfX0KeyJ0eXBlIjoibWVzc2FnZSIsImlkIjoiZTUiLCJwYXJlbnRJZCI6ImMzIiwidGltZXN0YW1wIjoiMjAyNi0xMC0wMVQwMDowMDowNS4wMDBaIiwibWVzc2FnZSI6eyJyb2xlIjoidXNlciIsImNvbnRlbnQiOiJicmFuY2giLCJ0aW1lc3RhbXAiOjJ9LCAgImR1cCI6MSwgImR1cCI6Mn0Kbm90IGpzb24gYXQgYWxsCnsidHlwZSI6Im1lc3NhZ2UiLCJpZCI6Imc3IiwicGFyZW50SWQiOiJjMyIsInRpbWVzdGFtcCI6IjIwMjYtMTAtMDFUMDA6MDA6MDUuNTAwWiIsIm1lc3NhZ2UiOnsicm9sZSI6InRvb2xSZXN1bHQiLCJ0b29sQ2FsbElkIjoiY2FsbF8yIiwidG9vbE5hbWUiOiJiYXNoIiwiY29udGVudCI6W3sidHlwZSI6InRleHQiLCJ0ZXh0IjoiYS50eHQifV0sImlzRXJyb3IiOnRydWUsInRpbWVzdGFtcCI6M319CnsidHlwZSI6ImxhYmVsIiwiaWQiOiJmNiIsInBhcmVudElkIjoiZDQiLCJ0aW1lc3RhbXAiOiIyMDI2LTEwLTAxVDAwOjAwOjA2LjAwMFoiLCJ0YXJnZXRJZCI6ImIyIiwibGFiZWwiOiJtYXJrIn0K";
    private const string FixtureSha256 = "1acdf661f0c220fe41604371c5a6a432456e68eca6395ec72a199fc0bab1c0f1";
    private const string MyThemeBase64 = "ewoJIm5hbWUiOiAibXl0aGVtZSIsCgkidmFycyI6IHsgImJhc2UiOiAiI2FiYyIsICJyZWYiOiAiYmFzZSIsICJpZHgiOiAyMDIsICJsY2giOiAib2tsY2goNzAlIDAuMSAyMDBkZWcpIiwgImhzbCI6ICJva2hzbCgxMjAgNTAlIDQwJSkiIH0sCgkiY29sb3JzIjogewoJCSJhY2NlbnQiOiAicmVmIiwgImJvcmRlciI6IDMzLCAiYm9yZGVyQWNjZW50IjogImxjaCIsICJib3JkZXJNdXRlZCI6ICJoc2wiLCAic3VjY2VzcyI6ICIjMDBmZjAwIiwgImVycm9yIjogIm9rbGNoKDAuNiAwLjI1IDI1KSIsCgkJIndhcm5pbmciOiAib2toc2woODBkZWcgOTAlIDcwJSkiLCAibXV0ZWQiOiAyNDQsICJkaW0iOiAiIiwgInRleHQiOiAiIiwgInRoaW5raW5nVGV4dCI6ICIjODA4MDgwIiwgInNlbGVjdGVkQmciOiAiIzIwMjAzMCIsCgkJInVzZXJNZXNzYWdlQmciOiAiI2YwZjBmMCIsICJ1c2VyTWVzc2FnZVRleHQiOiAiIzExMTExMSIsICJjdXN0b21NZXNzYWdlQmciOiAxNywgImN1c3RvbU1lc3NhZ2VUZXh0IjogImlkeCIsCgkJImN1c3RvbU1lc3NhZ2VMYWJlbCI6ICIjYzBjIiwgInRvb2xQZW5kaW5nQmciOiAiIiwgInRvb2xTdWNjZXNzQmciOiAiIzAwMjIwMCIsICJ0b29sRXJyb3JCZyI6ICIjMjIwMDAwIiwgInRvb2xUaXRsZSI6ICIjZmZmZmZmIiwKCQkidG9vbE91dHB1dCI6ICIjYWFhYWFhIiwgIm1kSGVhZGluZyI6ICIjZmZhYTAwIiwgIm1kTGluayI6ICIjMDA4OGZmIiwgIm1kTGlua1VybCI6ICIjNjY2NjY2IiwgIm1kQ29kZSI6ICIjZmYwMGZmIiwKCQkibWRDb2RlQmxvY2siOiAiIzAwZmZhYSIsICJtZENvZGVCbG9ja0JvcmRlciI6ICIjNDQ0NDQ0IiwgIm1kUXVvdGUiOiAiIzk5OTk5OSIsICJtZFF1b3RlQm9yZGVyIjogIiM1NTU1NTUiLCAibWRIciI6ICIjMzMzMzMzIiwKCQkibWRMaXN0QnVsbGV0IjogIiNmZjg4MDAiLCAidG9vbERpZmZBZGRlZCI6ICIjMDBhYTAwIiwgInRvb2xEaWZmUmVtb3ZlZCI6ICIjYWEwMDAwIiwgInRvb2xEaWZmQ29udGV4dCI6ICIjNzc3Nzc3IiwKCQkic3ludGF4Q29tbWVudCI6ICIjNmE5OTU1IiwgInN5bnRheEtleXdvcmQiOiAiIzU2OWNkNiIsICJzeW50YXhGdW5jdGlvbiI6ICIjZGNkY2FhIiwgInN5bnRheFZhcmlhYmxlIjogIiM5Y2RjZmUiLAoJCSJzeW50YXhTdHJpbmciOiAiI2NlOTE3OCIsICJzeW50YXhOdW1iZXIiOiAiI2I1Y2VhOCIsICJzeW50YXhUeXBlIjogIiM0ZWM5YjAiLCAic3ludGF4T3BlcmF0b3IiOiAiI2Q0ZDRkNCIsCgkJInN5bnRheFB1bmN0dWF0aW9uIjogIiNkNGQ0ZDQiLCAidGhpbmtpbmdPZmYiOiAyNDAsICJ0aGlua2luZ01pbmltYWwiOiA2MCwgInRoaW5raW5nTG93IjogNjEsICJ0aGlua2luZ01lZGl1bSI6IDYyLAoJCSJ0aGlua2luZ0hpZ2giOiA2MywgInRoaW5raW5nWGhpZ2giOiAyMDEsICJiYXNoTW9kZSI6ICIjMDBmZjg4IgoJfSwKCSJleHBvcnQiOiB7ICJwYWdlQmciOiAiaHNsIiwgImNhcmRCZyI6IDIzNiwgImluZm9CZyI6ICJva2xjaCg1MCUgMC4wNSA5MCkiIH0KfQo=";
    private const string MyThemeSha256 = "47f46bb89391d93ba1fed49fb0aafb5b601ed234b0c583167d273c8654e62e7c";

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));
    private static Func<string, string?> NoEnvironment => _ => null;

    private static string WriteFixture(string directory)
    {
        var bytes = Convert.FromBase64String(FixtureBase64);
        Equal(FixtureSha256, Sha256(bytes), "fixture bytes");
        var path = Path.Combine(directory, "fixture.jsonl"); File.WriteAllBytes(path, bytes); return path;
    }

    private static PiThemeHost Themes(string? agentDirectory = null, Func<string, string?>? environment = null, TerminalColorReport? terminal = null) =>
        new() { Environment = environment ?? NoEnvironment, AgentDirectory = agentDirectory ?? Temp("agent"), TerminalColors = terminal ?? new() };

    private static string AgentWithMyTheme()
    {
        var agent = Temp("agent-mytheme"); Directory.CreateDirectory(Path.Combine(agent, "themes"));
        var bytes = Convert.FromBase64String(MyThemeBase64); Equal(MyThemeSha256, Sha256(bytes), "custom theme bytes");
        File.WriteAllBytes(Path.Combine(agent, "themes", "mytheme.json"), bytes); return agent;
    }

    private static string GitBlobSha1(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. bytes]));
    }

    private static string SessionDataJson(string html)
    {
        const string opening = "<script id=\"session-data\" type=\"application/json\">";
        var start = html.IndexOf(opening, StringComparison.Ordinal); Check(start >= 0, "session data script"); start += opening.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(html[start..end]));
    }

    // The upstream digest was taken with getCwd() returning the header cwd verbatim; SessionManager resolves it (path.resolve), which on
    // Windows adds the drive. Map the resolved cwd back so the digest compares every other byte.
    private static string UpstreamCwd(string text, SessionExportSource source) =>
        text.Replace("\"cwd\":" + Js.Stringify(source.Cwd) + "}", "\"cwd\":\"/work/proj\"}", StringComparison.Ordinal);

    private static string Link(string url) => $"\x1b]8;;{url}\x1b\\{url}\x1b]8;;\x1b\\";

    private static IEnumerable<(string, Func<Task>)> ExportShareCases() =>
    [
        Case("export-share.assets.byte-identical-to-upstream-blobs", () =>
        {
            var sha256 = new Dictionary<string, string>
            {
                ["template.html"] = "916782b1184a9597527605ad751e2b3af30fcea23ba2194002969cd217a06881",
                ["template.css"] = "8ee19851f8e583277ed396cbb76496687aa556707fdfc1f78d1118bff87c740a",
                ["template.js"] = "b5bbffdf5d9ec8bb519df45c7ff953ac1969af80e8aba33331b9f87983f91fa5",
                ["vendor/marked.min.js"] = "d5487edc7258b404bfa74c393d74a6393155f02517bd5e7e77cd64f8187f39a0",
                ["vendor/highlight.min.js"] = "837a6fa5b0c736b52bbde2b2b6190f305da3fc9ed41681db5321507057b5c846",
                ["dark.json"] = "c11a588b714d35300293079b425fb09a3693d4b2d453585d211b08163c648b75",
                ["light.json"] = "f590c51c2bc8b238891efd0e983473ed2e2a6487b6692cc1d4b2845165e703f7"
            };
            Equal(7, SessionHtmlExport.AssetBlobs.Count, "asset count");
            foreach (var (name, blob) in SessionHtmlExport.AssetBlobs)
            {
                var bytes = SessionHtmlExport.ReadAssetBytes(name);
                Equal(blob, GitBlobSha1(bytes), name + " git blob");
                Equal(sha256[name], Sha256(bytes), name + " sha256");
            }
        }),

        Case("export-share.html.export-from-file-system-theme-matches-upstream-bytes", async () =>
        {
            var root = Temp("html-file"); var fixture = WriteFixture(root); var output = Path.Combine(root, "out.html");
            Equal(output, await SessionHtmlExport.ExportFromFileAsync(fixture, new(OutputPath: output), Themes()), "returned path");
            var bytes = await File.ReadAllBytesAsync(output);
            Check(bytes.Length == 274158 && bytes[0] == (byte)'<', "page length and no BOM");
            Equal("685c70d9ee4f1e66d0d97bf0b61d2dd206fbee286b9e3122f39451d939318651", Sha256(bytes), "default (system) page");
            var html = Encoding.UTF8.GetString(bytes);
            Equal("f7a21bcebf3adf8f40c1487ee13801ad329ad48d911c1538f53e0745e9908a58", Sha256(SessionDataJson(html)), "decoded session data");
            var template = SessionHtmlExport.ReadAsset("template.html");
            Check(html.StartsWith(template[..template.IndexOf("{{CSS}}", StringComparison.Ordinal)], StringComparison.Ordinal), "template head");
            Check(html.EndsWith(template[(template.IndexOf("{{JS}}", StringComparison.Ordinal) + 6)..], StringComparison.Ordinal), "template tail");
            Check(html.Contains("--accent: #800080;\n      --borderAccent: #800080;", StringComparison.Ordinal) &&
                html.Contains("--exportPageBg: rgb(0, 0, 0);\n      --exportCardBg: rgb(0, 0, 0);\n      --exportInfoBg: rgb(20, 15, 0);", StringComparison.Ordinal),
                "system theme variables without a terminal");
            var data = SessionDataJson(html);
            Check(data.StartsWith("{\"header\":{\"type\":\"session\"", StringComparison.Ordinal) && data.EndsWith(",\"leafId\":\"f6\"}", StringComparison.Ordinal) &&
                data.Contains("\"arguments\":{\"1\":\"one\",\"2\":\"two\",\"b\":1,\"n\":1e+21,\"m\":0,\"s\":1e-7,\"big\":12345678901234567000,\"f\":0.1,\"g\":100.5,\"h\":1.5e+300}", StringComparison.Ordinal) &&
                data.Contains("\\u0001 lone \\udc00 x", StringComparison.Ordinal) && data.Contains("\"timestamp\":1000}", StringComparison.Ordinal) &&
                data.Contains("\"dup\":2", StringComparison.Ordinal) && !data.Contains("not json", StringComparison.Ordinal), "JSON.stringify of JSON.parse");
        }),

        Case("export-share.html.builtin-and-colorfgbg-themes-match-upstream-bytes", async () =>
        {
            var root = Temp("html-themes"); var fixture = WriteFixture(root);
            foreach (var (theme, expected) in new[] { ("dark", "7beae6d8b6dfd95a57d976aba3dcbbd903263b83502d504df18727b3e6931e1d"),
                ("light", "d1cf5b199fa87fd44d11fb162c1fb54c06baf0f559cd557404840a8ccc84abe0") })
            {
                var output = Path.Combine(root, theme + ".html");
                await SessionHtmlExport.ExportFromFileAsync(fixture, new(OutputPath: output, ThemeName: theme), Themes());
                Equal(expected, Sha256(await File.ReadAllBytesAsync(output)), theme + " page");
            }
            var light = Path.Combine(root, "colorfgbg.html");
            await SessionHtmlExport.ExportFromFileAsync(fixture, new(OutputPath: light), Themes(environment: name => name == "COLORFGBG" ? "0;15" : null));
            Equal("f9a1ec14522b6d7262430e81401f121bbdd6e4c6b5870b493943c7d48581137a", Sha256(await File.ReadAllBytesAsync(light)), "COLORFGBG light system page");
        }),

        Case("export-share.html.state-tools-and-pre-rendered-custom-tools-match-upstream", () =>
        {
            var root = Temp("html-state"); var source = SessionExportSource.Open(WriteFixture(root));
            var renderer = new FakeToolRenderer();
            var state = new SessionExportAgentState("sys $& prompt", [
                new("lookup", "Look up", Js.ParseJson("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}")),
                new("x", Js.Undefined, Js.Undefined)]);
            var data = SessionHtmlExport.BuildSessionData(source, state, renderer);
            data["leafId"] = "d4";
            Equal("call_1|lookup;result:call_1|lookup|False|[{\"type\":\"text\",\"text\":\"result\"}]|{\"k\":\"v\"}", string.Join(";", renderer.Calls), "renderer calls");
            Equal("8ee1043c2f8ea6d1b116f2a74cf9927940bac07bb44e6140e5ff9be63f01dabc", Sha256(Js.Stringify(data)), "session data with state");
            Check(Js.Stringify(data).EndsWith("\"renderedTools\":{\"call_1\":{\"callHtml\":\"<b>call</b>\",\"resultHtmlExpanded\":\"<i>e</i>\"}}}", StringComparison.Ordinal),
                "rendered tools shape");
            Equal("ef56e42ad614ea06328cfc432141decf2f5eb6f47044fe315ce8aaf310abc3cc", Sha256(SessionHtmlExport.GenerateHtml(data, Themes(), "light")), "light page with state");
            var none = SessionHtmlExport.BuildSessionData(source, state, new NoToolRenderer());
            Check(!none.Has("renderedTools") || Js.IsUndefined(none["renderedTools"]), "empty pre-render is omitted");
        }),

        Case("export-share.theme.variables-and-export-colors-match-upstream", () =>
        {
            var host = Themes(AgentWithMyTheme());
            foreach (var (theme, expected) in new (string?, string)[] {
                (null, "a8ac511b9c7f807e9a61f2a71dccbcd5f5f8dcfe44cc12317c1e673cc578a348"), ("dark", "8f1c4550c81a1b2781856d2bc19d037ed59e339c5077fee194877852a0fa3c9b"),
                ("light", "d33ae9a5b3835737c10d0c8e40e58c0ab3f417b87b6c159fecc4b209e6bb8bc6"), ("mytheme", "ad379df7853fa469d89d2f75f9d56e06211a3c1d3c2e4e99b76cc8bfe8d189c2") })
                Equal(expected, Sha256(SessionHtmlExport.GenerateThemeVars(host, theme)), "theme vars " + (theme ?? "(default)"));
            Equal(((string?)null, (string?)null, (string?)null), host.GetThemeExportColors(), "system export colors");
            Equal(("#21252c", "#282c34", "#4e2f1b"), host.GetThemeExportColors("dark"), "dark export colors");
            Equal(("#efeeee", "#f7f6f6", "#ede3dd"), host.GetThemeExportColors("light"), "light export colors");
            Equal(("#596436", "#303030", "oklch(50% 0.05 90)"), host.GetThemeExportColors("mytheme"), "custom export colors (var okhsl, index, oklch kept)");
            var vars = SessionHtmlExport.GenerateThemeVars(host, "mytheme");
            Check(vars.Contains("--accent: #aabbcc;", StringComparison.Ordinal) && vars.Contains("--border: #0087ff;", StringComparison.Ordinal) &&
                vars.Contains("--borderAccent: #40b1b7;", StringComparison.Ordinal) && vars.Contains("--borderMuted: #596436;", StringComparison.Ordinal) &&
                vars.Contains("--error: #ef0028;", StringComparison.Ordinal) && vars.Contains("--warning: #db9f22;", StringComparison.Ordinal) &&
                vars.Contains("--customMessageText: #ff5f00;", StringComparison.Ordinal) && vars.Contains("--dim: #e5e5e7;\n      --text: #e5e5e7;", StringComparison.Ordinal),
                "var refs, 256 index, oklch gamut mapping, okhsl and terminal defaults");
            Equal("94ab111fc9f472c7ae051ebaa1ea7bfe1a1fe1b1b76acbfd36d6e4a55a30c0a9",
                Sha256(SessionHtmlExport.GenerateThemeVars(Themes(environment: name => name == "COLORFGBG" ? "0;15" : null), null)), "COLORFGBG light vars");
            Equal((string?)null, SessionHtmlExport.SelectThemeName(host, "missing", "light/dark"), "invalid requested and auto settings are skipped");
            Equal("light", SessionHtmlExport.SelectThemeName(host, "missing", "light"), "settings theme");
            Equal("system", SessionHtmlExport.SelectThemeName(host, "system", "dark"), "requested system theme");
            var init = Themes(); init.InitTheme("missing"); Equal("system", init.CurrentThemeName, "initTheme falls back to system");
            Equal("dark", PiThemeHost.ResolveThemeSetting("light/dark", "dark"), "auto theme setting");
            Equal((string?)null, PiThemeHost.ResolveThemeSetting("a/b/c", "dark"), "invalid auto theme setting");
        }),

        Case("export-share.theme.system-theme-from-terminal-reports-matches-upstream", () =>
        {
            RgbChannels C(int r, int g, int b) => new(r, g, b);
            var palette = new[] { C(69, 71, 90), C(243, 139, 168), C(166, 227, 161), C(249, 226, 175), C(137, 180, 250), C(245, 194, 231), C(148, 226, 213),
                C(186, 194, 222), C(88, 91, 112), C(243, 139, 168), C(166, 227, 161), C(249, 226, 175), C(137, 180, 250), C(245, 194, 231), C(148, 226, 213),
                C(166, 173, 200) };
            string Resolved(TerminalColorReport report) =>
                "{" + string.Join(",", Themes(terminal: report).GetResolvedThemeColors("system").Select(pair => $"\"{pair.Key}\":\"{pair.Value}\"")) + "}";
            Equal("246b39af8acbb910903644a9e68ec30d5603d7564b68ac2d6f98f68a9b73f170", Sha256(Resolved(new(C(205, 214, 244), C(30, 30, 46), palette))), "palette dark");
            Equal("15dcb2af5cbd74fadfcc36cadb630ec3100d42d366f111d8d305feda01aac4fb", Sha256(Resolved(new(null, C(250, 250, 250)))), "background-only light");
            Equal("e0366d1dcb165549deac8b7407cdeb0c83e700260306769797e6e24665e90f58", Sha256(Resolved(new(C(0, 0, 0), C(128, 128, 128)))), "relaxed mid gray");
        }),

        Case("export-share.colors.derivation-parsing-and-js-semantics", () =>
        {
            Equal(("rgb(36, 37, 46)", "rgb(44, 45, 55)", "rgb(72, 68, 65)"), SessionHtmlExport.DeriveExportColors("#343541"), "dark base");
            Equal(("rgb(230, 230, 230)", "#f0f0f0", "rgb(250, 245, 220)"), SessionHtmlExport.DeriveExportColors("#f0f0f0"), "light base");
            Equal(("rgb(7, 14, 21)", "rgb(9, 17, 26)", "rgb(30, 35, 30)"), SessionHtmlExport.DeriveExportColors("rgb(10, 20, 30)"), "rgb() base, Math.round");
            Equal(("rgb(24, 24, 30)", "rgb(30, 30, 36)", "rgb(60, 55, 40)"), SessionHtmlExport.DeriveExportColors("oklch(50% 0.1 20)"), "unparseable base");
            Equal("#abc", SessionHtmlExport.AdjustBrightness("#abc", 0.5), "short hex is not adjusted");
            Equal("#aabbcc", PiColors.ToHex(PiColors.Parse("#ABC")), "short hex");
            Equal("#40b1b7", PiColors.ToHex(PiColors.Parse("oklch(70% 0.1 200deg)")), "oklch");
            Equal("#ef0028", PiColors.ToHex(PiColors.Parse("oklch(0.6 0.25 25)")), "oklch gamut mapping");
            Equal("#596436", PiColors.ToHex(PiColors.Parse("okhsl(120 50% 40%)")), "okhsl");
            Equal("#ff5f00", PiColors.ToHex(PiColors.Parse(202)), "256 cube");
            Equal("#303030", PiColors.ToHex(PiColors.Parse(236)), "256 gray");
            Throws<ArgumentException>(() => PiColors.Parse("#abcdef\n"), "JS $ does not match before a final newline");
            Throws<ArgumentException>(() => PiColors.Parse(256), "index range");
            Equal("1e+21|1e-7|0.000001|100|0.1|-1.5e-300|123456789012345680000", string.Join("|",
                new[] { 1e21, 1e-7, 1e-6, 100.0, 0.1, -1.5e-300, 123456789012345678901.0 }.Select(Js.NumberToString)), "Number::toString");
            Equal("{\"1\":2,\"10\":3,\"b\":1,\"01\":4}", Js.Stringify(Js.ParseJson("{\"b\":1,\"10\":3,\"1\":2,\"01\":4}")), "integer keys first");
            Equal("a$b|a{{X}}b|ab|ab|xa{{X}}by", string.Join("|", Js.ReplaceFirst("a{{X}}b", "{{X}}", "$$"), Js.ReplaceFirst("a{{X}}b", "{{X}}", "$&"),
                Js.ReplaceFirst("a{{X}}b", "{{X}}", ""), Js.ReplaceFirst("a{{X}}{{X}}b", "{{X}}{{X}}", ""), Js.ReplaceFirst("{{X}}", "{{X}}", "x$`a$&b$'y")), "GetSubstitution");
            Equal("<span style=\"color:#800000;font-weight:bold\">red</span> plain", AnsiToHtml.Convert("\x1b[1;31mred\x1b[0m plain"), "SGR");
            Equal("<span style=\"color:#ff0000;background-color:#585858\">x</span><span style=\"color:rgb(1,2,3);background-color:#585858\">y</span>",
                AnsiToHtml.Convert("\x1b[38;5;196;48;5;240mx\x1b[38;2;1;2;3my"), "256 and RGB colors");
            Equal("<div class=\"ansi-line\">&nbsp;</div><div class=\"ansi-line\">a&lt;b&amp;&#039;</div>", AnsiToHtml.LinesToHtml(["", "a<b&'"]), "lines");
        }),

        Case("export-share.html.default-output-name-and-errors", async () =>
        {
            Equal("Cannot export in-memory session to HTML", Throws<InvalidOperationException>(() => SessionHtmlExport.EnsureExportable(null), "in-memory").Message, "in-memory");
            Equal("Nothing to export yet - start a conversation first",
                Throws<InvalidOperationException>(() => SessionHtmlExport.EnsureExportable(Path.Combine(Temp("lazy"), "s.jsonl")), "lazy").Message, "lazy");
            Equal("pi-session-2026-10-01T00-00-00-000Z_0199.html", SessionHtmlExport.OutputPath(null, Path.Combine("a", "2026-10-01T00-00-00-000Z_0199.jsonl")), "default name");
            Equal("pi-session-s.html", SessionHtmlExport.OutputPath("", "/x/s.jsonl"), "empty output path");
            Equal(Path.Combine("rel", "out.html"), SessionHtmlExport.OutputPath(Path.Combine("rel", "out.html"), "/x/s.jsonl"), "relative path kept");
            var root = Temp("html-default"); var fixture = WriteFixture(root);
            Equal("pi-session-fixture.html", await SessionHtmlExport.ExportFromFileAsync(fixture, null, Themes(), root), "returned default");
            Check(File.Exists(Path.Combine(root, "pi-session-fixture.html")), "default output written in the working directory");
            var missing = Path.Combine(root, "missing.jsonl");
            Equal("File not found: " + missing, (await ThrowsAsync<FileNotFoundException>(() => SessionHtmlExport.ExportFromFileAsync(missing, null, Themes(), root), "missing")).Message, "missing file");
            var garbage = Path.Combine(root, "garbage.jsonl"); await File.WriteAllTextAsync(garbage, "not json\n");
            Equal("Session file is not a valid pi session: " + garbage,
                (await ThrowsAsync<InvalidOperationException>(() => SessionHtmlExport.ExportFromFileAsync(garbage, null, Themes(), root), "garbage")).Message, "invalid file");
            var empty = Path.Combine(root, "empty.jsonl"); await File.WriteAllBytesAsync(empty, []);
            var page = await File.ReadAllTextAsync(Path.Combine(root, await SessionHtmlExport.ExportFromFileAsync(empty, null, Themes(), root)));
            Check(SessionDataJson(page).Contains("\"entries\":[],\"leafId\":null}", StringComparison.Ordinal), "empty file exports a new header");
            // _setSessionFile: an empty file is initialized with the new header.
            var written = File.ReadAllText(empty);
            Check(written.StartsWith("{\"type\":\"session\",\"version\":3,\"id\":", StringComparison.Ordinal) && written.EndsWith("}\n", StringComparison.Ordinal) && written.Count(c => c == '\n') == 1, "empty file initialized: " + written);
        }),

        Case("export-share.html.legacy-session-is-migrated-and-rewritten", () =>
        {
            var root = Temp("legacy"); var path = Path.Combine(root, "v1.jsonl");
            File.WriteAllText(path, "{\"type\":\"session\",\"id\":\"old\",\"timestamp\":\"t\",\"cwd\":\"/w\"}\n{\"type\":\"message\",\"timestamp\":\"t\",\"message\":{\"role\":\"hookMessage\",\"content\":\"h\"}}\n" +
                "{\"type\":\"compaction\",\"timestamp\":\"t\",\"summary\":\"s\",\"firstKeptEntryIndex\":1}\n");
            var source = SessionExportSource.Open(path);
            var header = Js.Stringify(source.Header); var entries = source.Entries.Cast<JsObject>().ToArray();
            Equal("{\"type\":\"session\",\"id\":\"old\",\"timestamp\":\"t\",\"cwd\":\"/w\",\"version\":3}", header, "migrated header");
            Equal("custom", ((JsObject)entries[0]["message"]!)["role"], "hookMessage role");
            Equal(entries[0]["id"], entries[1]["parentId"], "v1 parent links");
            Equal(entries[0]["id"], entries[1]["firstKeptEntryId"], "compaction index to id");
            Check(!entries[1].Has("firstKeptEntryIndex") && (string)entries[1]["id"]! == (string)source.LeafId! && ((string)entries[0]["id"]!).Length == 8, "ids");
            // session-manager.ts _loadEntries: a migrated file is rewritten (_rewriteFile, JSON.stringify per line).
            Equal(string.Concat(new[] { header }.Concat(entries.Select(entry => Js.Stringify(entry))).Select(line => line + "\n")), File.ReadAllText(path), "rewritten in the current version");
        }),

        Case("export-share.jsonl.branch-export-matches-upstream-bytes", () =>
        {
            var root = Temp("jsonl"); var source = SessionExportSource.Open(WriteFixture(root));
            var clock = () => DateTimeOffset.Parse("2026-10-08T01:02:03.456Z", System.Globalization.CultureInfo.InvariantCulture);
            var share = new SessionShare { RandomUuid = () => "abcdef12-0000-4000-8000-000000000000", Clock = clock };
            var state = new SessionExportAgentState("sys", [new("lookup", "Look up", Js.ParseJson("{\"type\":\"object\"}"))]);
            var text = SessionJsonlExport.SerializeSessionBranch(source, (parent, time) => share.CreateShareTrailingEntries(state, parent, time), clock);
            Equal("cdc84c3323928247b51c292d67fbaf643697bb200f592795b19e655d92c70c94", Sha256(UpstreamCwd(text, source)), "branch JSONL");
            Check(text.StartsWith("{\"type\":\"session\",\"version\":3,\"id\":\"0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000\",\"timestamp\":\"2026-10-08T01:02:03.456Z\",\"cwd\":\"",
                StringComparison.Ordinal), "fresh header");
            var written = SessionJsonlExport.ExportSessionToJsonl(source, null, null, clock, root);
            Equal(Path.Combine(root, "session-2026-10-08T01-02-03-456Z.jsonl"), written, "default JSONL name");
            var nested = SessionJsonlExport.ExportSessionToJsonl(source, Path.Combine("a", "b", "c.jsonl"), null, clock, root);
            Check(nested == Path.Combine(root, "a", "b", "c.jsonl") && File.Exists(nested), "relative path with created directories");
        }),

        Case("export-share.share.radius-upload-request-and-errors", async () =>
        {
            var root = Temp("radius"); var fixture = WriteFixture(root);
            var clock = () => DateTimeOffset.Parse("2026-10-08T01:02:03.456Z", System.Globalization.CultureInfo.InvariantCulture);
            var session = new SessionShareSession(() => SessionExportSource.Open(fixture), () => new SessionExportAgentState("sys",
                [new("lookup", "Look up", Js.ParseJson("{\"type\":\"object\"}"))]), (_, _) => throw new InvalidOperationException("no html"),
                new FakeRadius(new ShareProviderAuth("tok")));
            var handler = new FakeHttp((HttpStatusCode.OK, "OK", "{\"artifact\":{\"canonical_url\":\"https://radius.pi.dev/a/1\"}}"));
            var ui = new RecordingUi(); var share = new SessionShare { Http = new HttpMessageInvoker(handler), Clock = clock, TempRoot = root,
                RandomUuid = () => "abcdef12-0000-4000-8000-000000000000", Processes = new FakeProcesses() };
            await share.ShareSessionAsync(session, ui.Ui);
            Equal("loader:Uploading to Radius...|restore|status:Share URL: " + Link("https://radius.pi.dev/a/1"), ui.Log, "radius status");
            var request = handler.Requests.Single();
            Equal("POST https://radius.pi.dev/v1/artifacts?visibility=organization&title=Pi+session", request.Line, "request line");
            Equal("Authorization: Bearer tok|Content-Type: application/x-ndjson|Content-Length: " + request.Body.Length, string.Join("|", request.Headers), "headers");
            Equal("cdc84c3323928247b51c292d67fbaf643697bb200f592795b19e655d92c70c94", Sha256(UpstreamCwd(Encoding.UTF8.GetString(request.Body), SessionExportSource.Open(fixture))), "body is the branch with the pi.share entry");
            Check(!Directory.Exists(share.LastTempDirectory) && Path.GetFileName(share.LastTempDirectory!).StartsWith("pi-share-", StringComparison.Ordinal), "temp dir removed");
            Equal(StoredMinimum, 300_000L, "minimum OAuth validity");
            foreach (var (response, expected) in new[] {
                ((HttpStatusCode.Unauthorized, "Unauthorized", "{\"error\":\"nope\"}"), "Failed to upload Radius artifact: nope"),
                ((HttpStatusCode.InternalServerError, "Internal Server Error", "<html>"), "Failed to upload Radius artifact: Internal Server Error"),
                ((HttpStatusCode.BadGateway, "", ""), "Failed to upload Radius artifact: 502"),
                ((HttpStatusCode.OK, "OK", "{\"artifact\":\"x\"}"), "status:Share URL: " + Link("undefined")) })
            {
                var failing = new RecordingUi();
                await new SessionShare { Http = new HttpMessageInvoker(new FakeHttp(response)), TempRoot = root, Processes = new FakeProcesses() }.ShareSessionAsync(session, failing.Ui);
                Equal(expected.StartsWith("status:", StringComparison.Ordinal) ? "loader:Uploading to Radius...|restore|" + expected
                    : "loader:Uploading to Radius...|restore|error:" + expected, failing.Log, "radius response " + response.Item1);
            }
            var thrown = new RecordingUi();
            await new SessionShare { Http = new HttpMessageInvoker(new FakeHttp(null)), TempRoot = root }.ShareSessionAsync(session, thrown.Ui);
            Equal("loader:Uploading to Radius...|restore|error:Failed to upload Radius artifact: boom", thrown.Log, "network failure");
            var cancelled = new RecordingUi(); using var abort = new CancellationTokenSource();
            var hanging = new FakeHttp((HttpStatusCode.OK, "OK", "{}")) { Hang = true };
            var pending = new SessionShare { Http = new HttpMessageInvoker(hanging), TempRoot = root }.ShareSessionAsync(session, cancelled.Ui, abort.Token);
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); abort.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Equal("loader:Uploading to Radius...|restore|status:Share cancelled", cancelled.Log, "abort");
            Equal("hdr", SessionShare.GetAuthCredential(new(null, new Dictionary<string, string?> { ["AUTHORIZATION"] = "bearer   hdr" })), "bearer header credential");
            Equal((string?)null, SessionShare.GetAuthCredential(new(null, new Dictionary<string, string?> { ["authorization"] = "Basic x" })), "non-bearer header");
        }),

        Case("export-share.session.export-to-html-and-jsonl-from-persistent-session", async () =>
        {
            var root = Temp("persistent"); var path = Path.Combine(root, "2026-10-01_s1.jsonl");
            var codec = new PiSharp.Sessions.Serialization.SessionEntryCodec();
            PiSharp.Sessions.Serialization.SessionEntry Entry(string json) => codec.Parse(json);
            await using (var store = await PiSharp.Sessions.Storage.SessionLogStore.CreateNewAsync(path,
                Entry("{\"type\":\"session\",\"version\":3,\"id\":\"s1\",\"timestamp\":\"2026-10-01T00:00:00.000Z\",\"cwd\":" + Js.Stringify(root) + "}")))
                await store.AppendAsync([
                    Entry("{\"type\":\"message\",\"id\":\"sys\",\"parentId\":null,\"timestamp\":\"2026-10-01T00:00:01.000Z\",\"message\":{\"role\":\"system\",\"content\":\"be brief\",\"timestamp\":1,\"toolsAdded\":[{\"name\":\"lookup\",\"description\":\"Look up\",\"parameters\":{\"type\":\"object\"}}]}}"),
                    Entry("{\"type\":\"message\",\"id\":\"u1\",\"parentId\":\"sys\",\"timestamp\":\"2026-10-01T00:00:02.000Z\",\"message\":{\"role\":\"user\",\"content\":\"kept\",\"timestamp\":2}}"),
                    Entry("{\"type\":\"message\",\"id\":\"u2\",\"parentId\":\"sys\",\"timestamp\":\"2026-10-01T00:00:03.000Z\",\"message\":{\"role\":\"user\",\"content\":\"other\",\"timestamp\":3}}")]);
            var model = new PiSharp.Contracts.ModelDescriptor("m", "openai-responses", "p");
            await using var session = await PiSharp.CodingAgent.PersistentAgentSession.OpenAsync(path, new(model, new IdleTransport(), []), () => 1, () => "unused",
                new(UseLatestLeaf: false, SelectedLeafId: "u1"));
            var host = new SessionHtmlExportHost(Themes(), "dark", root);
            Equal("pi-session-2026-10-01_s1.html", await AgentSessionExport.ExportToHtmlAsync(session, host), "default page name");
            var data = SessionDataJson(await File.ReadAllTextAsync(Path.Combine(root, "pi-session-2026-10-01_s1.html")));
            Check(data.Contains("\"leafId\":\"u1\",\"systemPrompt\":\"be brief\",\"tools\":[{\"name\":\"lookup\",\"description\":\"Look up\",\"parameters\":{\"type\":\"object\"}}]}",
                StringComparison.Ordinal) && data.Contains("\"content\":\"other\"", StringComparison.Ordinal), "state, selected leaf and the whole forest");
            Check((await File.ReadAllTextAsync(Path.Combine(root, "pi-session-2026-10-01_s1.html"))).Contains("--exportPageBg: #21252c;", StringComparison.Ordinal),
                "settings theme");
            var jsonl = AgentSessionExport.ExportToJsonl(session, "branch.jsonl", root, () => DateTimeOffset.UnixEpoch);
            Equal("{\"type\":\"session\",\"version\":3,\"id\":\"s1\",\"timestamp\":\"1970-01-01T00:00:00.000Z\",\"cwd\":" + Js.Stringify(ExportPaths.ResolvePath(root)) + "}\n" +
                "{\"type\":\"message\",\"id\":\"sys\",\"parentId\":null,\"timestamp\":\"2026-10-01T00:00:01.000Z\",\"message\":{\"role\":\"system\",\"content\":\"be brief\",\"timestamp\":1,\"toolsAdded\":[{\"name\":\"lookup\",\"description\":\"Look up\",\"parameters\":{\"type\":\"object\"}}]}}\n" +
                "{\"type\":\"message\",\"id\":\"u1\",\"parentId\":\"sys\",\"timestamp\":\"2026-10-01T00:00:02.000Z\",\"message\":{\"role\":\"user\",\"content\":\"kept\",\"timestamp\":2}}\n",
                await File.ReadAllTextAsync(jsonl), "branch JSONL");
        }),

        Case("export-share.share.cli-radius-auth-uses-stored-key-then-environment", async () =>
        {
            var root = Temp("radius-auth"); var store = new PiSharp.Cli.Authentication.AuthJsonCredentialStore(Path.Combine(root, "auth.json"));
            var none = PiSharp.Cli.Authentication.RadiusShareAuthentication.Create(store, NoEnvironment, () => new HttpClient());
            Check(none.HasProvider && await none.GetAuthAsync(SessionShare.RadiusMinimumOAuthValidityMilliseconds, CancellationToken.None) is null, "no radius auth");
            var ambient = PiSharp.Cli.Authentication.RadiusShareAuthentication.Create(store, name => name == "RADIUS_API_KEY" ? "env-key" : null, () => new HttpClient());
            Equal("env-key", SessionShare.GetAuthCredential(await ambient.GetAuthAsync(300_000, CancellationToken.None)), "RADIUS_API_KEY");
            await store.WriteApiKeyAsync("radius", "stored-key", null, CancellationToken.None);
            var stored = PiSharp.Cli.Authentication.RadiusShareAuthentication.Create(store, name => name == "RADIUS_API_KEY" ? "env-key" : null, () => new HttpClient());
            Equal("stored-key", SessionShare.GetAuthCredential(await stored.GetAuthAsync(300_000, CancellationToken.None)), "auth.json key owns the provider");
        }),

        Case("export-share.share.gist-fallback-auth-install-and-output", async () =>
        {
            var root = Temp("gist"); var fixture = WriteFixture(root); string? exported = null;
            var session = new SessionShareSession(() => SessionExportSource.Open(fixture), () => new SessionExportAgentState("sys", []),
                (path, _) => { exported = path; File.WriteAllText(path, "<html>"); return Task.CompletedTask; }, new FakeRadius(null));
            async Task<(string Log, FakeProcesses Processes)> Run(FakeProcesses processes, Func<string, string?>? environment = null)
            {
                var ui = new RecordingUi();
                await new SessionShare { Processes = processes, TempRoot = root, Environment = environment ?? NoEnvironment }.ShareSessionAsync(session, ui.Ui);
                return (ui.Log, processes);
            }
            Equal("error:GitHub CLI is not logged in. Run 'gh auth login' first.", (await Run(new FakeProcesses { Auth = new(1, "", "") })).Log, "not logged in");
            Equal("error:GitHub CLI is not logged in. Run 'gh auth login' first.", (await Run(new FakeProcesses { Auth = new(null, "", "") })).Log,
                "missing gh (spawnSync status null)");
            Equal("error:GitHub CLI (gh) is not installed. Install it from https://cli.github.com/", (await Run(new FakeProcesses { AuthThrows = true })).Log, "spawn threw");
            var (log, processes) = await Run(new FakeProcesses { Gist = new(0, "https://gist.github.com/user/abc123\n", "") });
            Equal("loader:Creating gist...|restore|status:Share URL: " + Link("https://pi.dev/session/#abc123") + "\nGist: " + Link("https://gist.github.com/user/abc123"), log, "gist share");
            Equal("gh auth status;gh gist create --public=false " + Path.Combine(Path.GetDirectoryName(exported!)!, "session.html"), string.Join(";", processes.Commands), "gh commands");
            Check(Path.GetFileName(exported) == "session.html" && Path.GetFileName(Path.GetDirectoryName(exported)!).StartsWith("pi-share-", StringComparison.Ordinal) &&
                !Directory.Exists(Path.GetDirectoryName(exported)), "html in the removed temp dir");
            Equal("loader:Creating gist...|restore|status:Share URL: " + Link("https://viewer.test/s/#abc123") + "\nGist: " + Link("https://gist.github.com/user/abc123"),
                (await Run(new FakeProcesses { Gist = new(0, "https://gist.github.com/user/abc123", "") }, name => name == "PI_SHARE_VIEWER_URL" ? "https://viewer.test/s/" : null)).Log,
                "PI_SHARE_VIEWER_URL");
            Equal("loader:Creating gist...|restore|error:Failed to create gist: bad", (await Run(new FakeProcesses { Gist = new(1, "", " bad \n") })).Log, "gist failure");
            Equal("loader:Creating gist...|restore|error:Failed to create gist: Unknown error", (await Run(new FakeProcesses { Gist = new(1, "", "") })).Log, "gist failure without stderr");
            Equal("loader:Creating gist...|restore|error:Failed to parse gist ID from gh output", (await Run(new FakeProcesses { Gist = new(0, "https://gist.github.com/user/", "") })).Log,
                "unparseable gist output");
            var failingExport = session with { ExportToHtmlAsync = (_, _) => throw new InvalidOperationException("disk full") };
            var ui = new RecordingUi();
            await new SessionShare { Processes = new FakeProcesses(), TempRoot = root }.ShareSessionAsync(failingExport, ui.Ui);
            Equal("error:Failed to export session: disk full", ui.Log, "html export failure");
            var cancelled = new RecordingUi(); using var abort = new CancellationTokenSource(); var hanging = new FakeProcesses { HangGist = true };
            var pending = new SessionShare { Processes = hanging, TempRoot = root }.ShareSessionAsync(session, cancelled.Ui, abort.Token);
            await hanging.GistEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); abort.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Equal("loader:Creating gist...|restore|status:Share cancelled", cancelled.Log, "abort kills gh");
            var broken = session with { Source = () => throw new InvalidOperationException("gone") }; var brokenUi = new RecordingUi();
            await new SessionShare { TempRoot = root }.ShareSessionAsync(broken, brokenUi.Ui);
            Equal("error:Failed to export session: gone", brokenUi.Log, "jsonl export failure");
        })
    ];

    private static long StoredMinimum;

    private sealed class FakeRadius(ShareProviderAuth? auth) : IRadiusShareAuthentication
    {
        public bool HasProvider => true;
        public ValueTask<ShareProviderAuth?> GetAuthAsync(long minimum, CancellationToken token) { StoredMinimum = minimum; return ValueTask.FromResult(auth); }
    }

    private sealed class RecordingUi
    {
        private readonly List<string> log = [];
        public string Log { get { lock (log) return string.Join("|", log); } }
        private void Add(string value) { lock (log) log.Add(value); }
        public SessionShareUi Ui => new(status => Add("status:" + status), error => Add("error:" + error), message => Add("loader:" + message), () => Add("restore"));
    }

    private sealed record CapturedRequest(string Line, List<string> Headers, byte[] Body);

    private sealed class FakeHttp((HttpStatusCode Status, string Reason, string Body)? response) : HttpMessageHandler
    {
        public readonly List<CapturedRequest> Requests = [];
        public bool Hang;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            var headers = request.Headers.Select(header => header.Key + ": " + string.Join(",", header.Value))
                .Concat(request.Content.Headers.Select(header => header.Key + ": " + string.Join(",", header.Value))).ToList();
            Requests.Add(new(request.Method + " " + request.RequestUri!.AbsoluteUri, headers, body));
            if (Hang) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (response is not { } value) throw new HttpRequestException("boom");
            return new(value.Status) { ReasonPhrase = value.Reason, Content = new StringContent(value.Body) };
        }
    }

    private sealed class FakeProcesses : IShareProcessRunner
    {
        public ShareProcessResult Auth { get; init; } = new(0, "", "");
        public bool AuthThrows { get; init; }
        public ShareProcessResult Gist { get; init; } = new(0, "https://gist.github.com/u/id", "");
        public bool HangGist { get; init; }
        public readonly TaskCompletionSource GistEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Commands = [];
        public async Task<ShareProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Commands.Add(fileName + " " + string.Join(" ", arguments));
            if (arguments[0] == "auth") { if (AuthThrows) throw new InvalidOperationException("spawn failed"); return Auth; }
            Check(File.Exists(arguments[^1]), "gist file exists while gh runs");
            if (HangGist) { GistEntered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { return new(null, "", ""); } }
            return Gist;
        }
    }

    private sealed class FakeToolRenderer : IToolHtmlRenderer
    {
        public readonly List<string> Calls = [];
        public string? RenderCall(string toolCallId, string? toolName, object? args) { Calls.Add(toolCallId + "|" + toolName); return "<b>call</b>"; }
        public ToolHtmlResult? RenderResult(string toolCallId, string toolName, object? result, object? details, bool isError)
        {
            Calls.Add("result:" + toolCallId + "|" + toolName + "|" + isError + "|" + Js.Stringify(result) + "|" + Js.Stringify(details));
            return new(null, "<i>e</i>");
        }
    }

    private sealed class IdleTransport : PiSharp.AI.IChatTransport
    {
        public async IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamAsync(PiSharp.AI.ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; if (request is not null) throw new InvalidOperationException("Export must not call the model."); yield break; }
    }

    private sealed class NoToolRenderer : IToolHtmlRenderer
    {
        public string? RenderCall(string toolCallId, string? toolName, object? args) => "";
        public ToolHtmlResult? RenderResult(string toolCallId, string toolName, object? result, object? details, bool isError) => null;
    }
}
