using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.CodingAgent.Configuration;
using PiSharp.CodingAgent.Export;

// Authored from Pi v1.2.0 (06a466935536f8c9bcacb2d5efc0fc021be1bdff) packages/coding-agent/schemas/*.schema.json,
// scripts/generate-schemas.ts, src/core/{keybindings,key-id-schema,keybindings-schema,settings-schema,model-config}.ts,
// src/modes/interactive/theme/theme-schema.ts and src/main.ts, following test/config-schemas.test.ts and
// test/resource-loader-theme.test.ts. Messages were checked against TypeBox 1.3.27; nothing here is an upstream capture.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ConfigSchemaCases() =>
    [
        ("schemas.published-files-embedded-byte-for-byte", Sync(SchemasEmbedded)),
        ("schemas.representative-documents-and-limits", Sync(SchemasRepresentative)),
        ("schemas.settings-values-runtime-accessors-reject", Sync(SchemasSettingsRuntime)),
        ("settings.dollar-schema-accepted-and-unknown-settings-kept", SettingsDollarSchema),
        ("keybindings.dollar-schema-skipped-and-invalid-key-ids-dropped", KeybindingsDollarSchema),
        ("theme.strict-schema-validation-and-messages", ThemeStrictValidation),
    ];

    private static string GitBlob(byte[] bytes) =>
        Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0"), .. bytes]));

    private static void SchemasEmbedded()
    {
        // git ls-tree v1.2.0 packages/coding-agent/schemas
        var blobs = new Dictionary<string, string>
        {
            [PiConfigurationSchemas.ModelsFileName] = "40e27fa3ebee59118b0db6373435479d5fc1baeb",
            [PiConfigurationSchemas.SettingsFileName] = "81cfc5fb198d0e1bb7dd3fd782dd9553c023f97d",
            [PiConfigurationSchemas.KeybindingsFileName] = "aa3b3e9be501475ce465855ee676abe32b8b9c2e",
            [PiConfigurationSchemas.ThemeFileName] = "95350a58db931d116d982d3212dbfd2c5c946760",
        };
        Names(blobs.Keys, PiConfigurationSchemas.FileNames, "the four published schemas");
        foreach (var (name, blob) in blobs)
        {
            Equal(blob, GitBlob(PiConfigurationSchemas.ReadBytes(name)), name);
            Equal(PiConfigurationSchemas.Url(name), JsonNode.Parse(PiConfigurationSchemas.Read(name))!["$id"]!.GetValue<string>(), name + " $id");
        }
        // The built-in themes point at the published theme schema.
        foreach (var theme in new[] { "dark.json", "light.json" })
            Equal("https://pi.dev/schemas/theme.schema.json", JsonNode.Parse(Encoding.UTF8.GetString(SessionHtmlExport.ReadAssetBytes(theme)))!["$schema"]!.GetValue<string>(), theme);
    }

    private static void SchemasRepresentative()
    {
        static JsonNode? J(string json) => JsonNode.Parse(json);
        Check(PiConfigurationSchemas.Models.Check(J("""
            {"providers":{"local":{"compat":{"zaiToolStream":true,"thinkingTokenBudgetField":"thinking_budget","chatTemplateKwargs":{"budget":{"$var":"thinking.budget"}},
              "supportsMidConvoSystemMessages":true},"models":[{"id":"model","inputLimits":{"maxRequestBytes":1024,"images":{"maxPerRequest":2}},"promptCache":{"short":300}}]}}}
            """)), "valid models.json");
        Check(!PiConfigurationSchemas.Models.Check(J("""{"providers":{"local":{"models":[{"id":""}]}}}""")), "invalid models.json");
        Check(PiConfigurationSchemas.Settings.Check(J("""
            {"theme":"dark","cacheWarming":"idle","extensionSetting":{"enabled":true},"queueMode":"all","websockets":true,
             "skills":{"enableSkillCommands":true,"customDirectories":["./skills"]},"retry":{"maxDelayMs":1000}}
            """)), "valid settings.json");
        Check(!PiConfigurationSchemas.Settings.Check(J("""{"cacheWarming":"always"}""")), "invalid settings.json");
        Check(PiConfigurationSchemas.Keybindings.Check(J("""{"app.session.new":"ctrl+n","extension.action":["alt+x"]}""")), "valid keybindings.json");
        Check(!PiConfigurationSchemas.Keybindings.Check(J("""{"app.session.new":42}""")), "invalid keybindings.json");
        foreach (var field in new[] { "contextWindow", "maxTokens" })
            foreach (var value in new[] { 0, -1 })
            {
                Check(!PiConfigurationSchemas.Models.Check(J("{\"providers\":{\"local\":{\"models\":[{\"id\":\"model\",\"" + field + "\":" + value + "}]}}}")), $"models[].{field}={value}");
                Check(!PiConfigurationSchemas.Models.Check(J("{\"providers\":{\"local\":{\"modelOverrides\":{\"model\":{\"" + field + "\":" + value + "}}}}}")), $"modelOverrides.{field}={value}");
            }
        Check(PiConfigurationSchemas.Models.Check(J("""{"providers":{"local":{"models":[{"id":"model","contextWindow":1,"maxTokens":1}],"modelOverrides":{"model":{"contextWindow":1,"maxTokens":1}}}}}""")),
            "positive limits");
        // key-id-schema.ts: modifiers once each, at most four, before a lower-case key or a named key.
        foreach (var binding in new[] { "a", "9", "pageUp", "+", "ctrl+shift+x", "alt+ctrl+?", "ctrl+shift+alt+super+f12" })
            Check(PiConfigurationSchemas.Keybindings.Check(new JsonObject { ["extension.action"] = binding }) && PiConfigurationSchemas.KeybindingValue.Check(binding), binding);
        foreach (var binding in new[] { "", "Ctrl+x", "control+x", "ctrl+not-a-key", "ctrl+ctrl+x", "ctrl+shift+alt+super+ctrl+x" })
            Check(!PiConfigurationSchemas.Keybindings.Check(new JsonObject { ["extension.action"] = binding }) && !PiConfigurationSchemas.KeybindingValue.Check(binding), binding);
        Check(PiConfigurationSchemas.KeybindingValue.Check(new JsonArray("ctrl+a", "escape")) && !PiConfigurationSchemas.KeybindingValue.Check(new JsonArray("ctrl+a", "bogus+b")), "arrays");
        var errors = PiConfigurationSchemas.Keybindings.Errors(J("""{"app.session.new":42}"""));
        Names(["/app.session.new: must be string", "/app.session.new: must be array", "/app.session.new: must match a schema in anyOf"],
            errors.Select(error => error.InstancePath + ": " + error.Message), "keybinding errors");
    }

    private static void SchemasSettingsRuntime()
    {
        foreach (var invalid in new[]
        {
            """{"compaction":{"reserveTokens":-1}}""", """{"compaction":{"keepRecentTokens":1.5}}""",
            """{"compaction":{"modelOverrides":{"provider/model":{"reserveTokens":9007199254740992}}}}""",
            """{"httpIdleTimeoutMs":-1}""", """{"websocketConnectTimeoutMs":-1}""", """{"httpIdleTimeoutMs":"bogus"}""",
            """{"websocketConnectTimeoutMs":"30000"}""", """{"codemode":{"inlineBudget":-1}}""",
        })
            Check(!PiConfigurationSchemas.Settings.CheckJson(invalid), invalid);
        Check(PiConfigurationSchemas.Settings.CheckJson("""
            {"compaction":{"reserveTokens":0,"keepRecentTokens":9007199254740991,"modelOverrides":{"provider/model":{"reserveTokens":0}}},
             "httpIdleTimeoutMs":0,"websocketConnectTimeoutMs":"disabled","codemode":{"inlineBudget":0}}
            """), "accepted runtime values");
    }

    private static Task SettingsDollarSchema() => WithTemp("settings-schema", async root =>
    {
        var agent = Path.Combine(root, "agent"); Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            """{"$schema":"https://pi.dev/schemas/settings.schema.json","theme":"light","extensionSetting":{"enabled":true}}""");
        var settings = new InteractiveSettings(root, agent, projectTrusted: false);
        Equal("light", settings.Theme, "theme");
        Equal("""{"$schema":"https://pi.dev/schemas/settings.schema.json","theme":"light","extensionSetting":{"enabled":true}}""",
            settings.Snapshot.ToJsonString(), "$schema and unknown settings kept");
    });

    private static Task KeybindingsDollarSchema() => WithTemp("keybindings-schema", async root =>
    {
        const string json = """{"$schema":"https://pi.dev/schemas/keybindings.schema.json","app.session.new":"ctrl+n","app.session.tree":"ctrl+not-a-key","extension.invalid":"Ctrl+x","extension.mixed":["alt+x","bogus+b"],"extension.list":["alt+x"]}""";
        await File.WriteAllTextAsync(Path.Combine(root, "keybindings.json"), json);
        var user = AppKeybindings.LoadFromFile(Path.Combine(root, "keybindings.json"));
        Names(["app.session.new", "extension.list"], user.Keys.Order(StringComparer.Ordinal), "only valid actions");
        Names(["ctrl+n"], user["app.session.new"]!, "binding");
        Names(["ctrl+n"], AppKeybindings.Create(root).GetEffectiveConfig()["app.session.new"], "effective binding");
        // The terminal keybinding loader reads keybindings.json the same way: $schema skipped, invalid key ids keep the defaults.
        var defaults = TerminalKeybindingConfigurationLoader.Load(root, "linux", readText: _ => "{}").CreateBindings();
        var terminal = TerminalKeybindingConfigurationLoader.Load(root, "linux", readText: _ => json);
        Equal("loaded", terminal.LoadStatus, "terminal loader status");
        var bindings = terminal.CreateBindings();
        Names(["ctrl+n"], bindings.GetKeys("app.session.new"), "terminal binding");
        Names(defaults.GetKeys("app.session.tree"), bindings.GetKeys("app.session.tree"), "invalid key id ignored");
    });

    private static Task ThemeStrictValidation() => WithTemp("theme-schema", async root =>
    {
        var themes = Path.Combine(root, "themes"); Directory.CreateDirectory(themes);
        var dark = JsonNode.Parse(Encoding.UTF8.GetString(SessionHtmlExport.ReadAssetBytes("dark.json")))!.AsObject();
        // pi installs the validator before anything loads a theme (main.ts), so every host validates, the default export host included.
        var host = new PiThemeHost { AgentDirectory = root };
        async Task<string?> Load(string name, Action<JsonObject> change)
        {
            var theme = (JsonObject)dark.DeepClone(); change(theme);
            await File.WriteAllTextAsync(Path.Combine(themes, name + ".json"), theme.ToJsonString());
            try { _ = host.LoadTheme(name); return null; } catch (ArgumentException error) { return error.Message; }
        }
        Equal<string?>(null, await Load("custom", theme => { theme["$schema"] = "https://pi.dev/schemas/theme.schema.json"; theme["name"] = "custom"; }), "$schema accepted");
        Check((await Load("named", theme => theme["name"] = "invalid/name"))!.Contains("theme names cannot contain", StringComparison.Ordinal), "name with /");
        Check((await Load("empty", theme => theme["colors"] = new JsonObject()))!.Contains("Missing required color tokens", StringComparison.Ordinal), "missing colors");
        const string head = "Invalid theme \"{0}\":\n\n\nOther errors:\n";
        Equal(string.Format(head, "unexpected") + "  - /unexpected: schema is false\n  - /: must not have additional properties",
            await Load("unexpected", theme => theme["unexpected"] = true), "unsupported top-level metadata");
        Equal(string.Format(head, "token") + "  - /colors/scrollbarThmb: schema is false\n  - /colors: must not have additional properties",
            await Load("token", theme => theme["colors"]!["scrollbarThmb"] = ""), "unknown color token");
        Equal(string.Format(head, "export") + "  - /export/unexpected: schema is false\n  - /export: must not have additional properties",
            await Load("export", theme => theme["export"] = new JsonObject { ["unexpected"] = "" }), "unknown export color");
        Equal(string.Format(head, "blank") + "  - /name: must match pattern \"^[^/]+$\"", await Load("blank", theme => theme["name"] = ""), "empty name");
        Equal(string.Format(head, "accent") + "  - /colors/accent: must be string\n  - /colors/accent: must be integer\n  - /colors/accent: must match a schema in anyOf",
            await Load("accent", theme => theme["colors"]!["accent"] = true), "color value branches");
        Equal(string.Format(head, "values") + "  - /appearance: must be equal to constant\n  - /appearance: must be equal to constant\n  - /appearance: must match a schema in anyOf\n" +
            "  - /vars/x: must be string\n  - /vars/x: must be >= 0\n  - /vars/x: must match a schema in anyOf",
            await Load("values", theme => { theme["appearance"] = "dim"; theme["vars"]!["x"] = -1; }), "appearance and vars");
    });
}
