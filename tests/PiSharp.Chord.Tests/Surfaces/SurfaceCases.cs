// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/themes.ts, packages/chord/src/surfaces/messages.ts, packages/chord/src/surfaces/language.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using PiSharp.Chord.Surfaces;
using SurfaceMessages = PiSharp.Chord.Surfaces.Messages;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SurfacesPortableCases() =>
    [
        Case("surfaces.scripts.dto-roundtrip-and-native-register", () =>
        {
            var set = new ScriptSet("base", "/all.js", [new("a", "digest", "/a.js", "/a.css"), new("b", "other", "/b.js")]);
            var roundtrip = ScriptSet.FromJson(set.ToJson());
            Equal("base", roundtrip.Base, "base roundtrip"); Sequence(set.Plugins, roundtrip.Plugins, "plugins roundtrip");
            Equal("chord.web.scripts", Scripts.WebScripts.Name, "web contract"); Equal(1L, Scripts.TuiScripts.Version, "tui version");
            var facet = Facet.Define((_, _) => { });
            var script = new RegisteredScript("test", "one", (require, module) => { Equal(42, (int)require("api")!, "admitted module"); module.Exports = new Dictionary<string, object?> { ["default"] = facet }; });
            Check(ReferenceEquals(facet, Registered.Evaluate(script, new Dictionary<string, object?> { ["api"] = 42 })), "returned registered facet");
            Throws<InvalidOperationException>(() => Registered.Evaluate(script, new Dictionary<string, object?>()), "unadmitted module");
            Check(Registered.Source("a\"b", "x", "function(){} //# sourceMappingURL=a").EndsWith("\n);\n", StringComparison.Ordinal), "source map remains before closing line");
        }),
        Case("surfaces.loaded.identity-failure-retention-and-removal", async () =>
        {
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            var prepared = 0; var facet = Facet.Define((_, _) => { });
            var loaded = await LoadedFacets.LoadAsync(host, [], [new("test", "one", () => { prepared++; return new Prepared(facet, "one"); })], ChordContext.Background);
            await loaded.Loaded.ReloadAsync(host, [new("test", "one", () => throw new Exception("unchanged evaluation"))], ChordContext.Background);
            Equal(1, prepared, "unchanged digest preserves object identity and evaluation");
            var failed = await loaded.Loaded.ReloadAsync(host, [new("test", "two", () => new PreparationFailed(new Exception("new broken script")))], ChordContext.Background);
            Equal("test", failed.Failed.Single().Plugin, "failure forwarded to host"); Equal("one", loaded.Loaded.Digests["test"], "last good remains loaded");
            await loaded.Loaded.ReloadAsync(host, [new("test", "two", () => { prepared++; return new Prepared(Facet.Define((_, _) => { }), "two"); })], ChordContext.Background);
            Equal("two", loaded.Loaded.Digests["test"], "newest script retried"); Equal(2, prepared, "retry evaluated only once");
            await loaded.Loaded.ReloadAsync(host, [], ChordContext.Background); Equal(0, loaded.Loaded.Digests.Count, "complete set removes old plugin");
        }),
        Case("surfaces.page-state.reload-identity-and-close", async () =>
        {
            await using var host = Hosts.Create(new() { OnError = _ => { } });
            await host.LoadAsync([], ChordContext.Background);
            var first = PageStates.Get(host, "draft", "one"); var notifications = 0;
            var unsubscribe = first.Subscribe(() => notifications++);
            first.Set("one"); first.Set("two"); Equal(1, notifications, "only identity change notifies");
            await host.ReloadAsync([], ChordContext.Background);
            var second = PageStates.Get(host, "draft", "new"); Check(ReferenceEquals(first, second), "same host/key survives reload"); Equal("two", second.Read(), "draft survives reload");
            unsubscribe(); second.Set("three"); Equal(1, notifications, "unsubscribe");
            await host.CloseAsync(ChordContext.Background);
            Throws<ObjectDisposedException>(() => first.Set("late"), "host close clears owned state");
        }),
        Case("surfaces.themes.fallback-overrides-and-terminal-palette", () =>
        {
            var dark = Themes.FallbackThemes[ThemeMode.Dark]; var light = Themes.FallbackThemes[ThemeMode.Light];
            Check(ReferenceEquals(dark, Themes.Active([], new(), ThemeMode.Dark)), "fallback dark");
            var red = new Theme("red", "Red", ThemeMode.Dark, new Dictionary<string, string> { ["accent"] = "#ff0000" },
                new Dictionary<string, string> { ["radius"] = "4px" }, new Dictionary<string, string> { ["warning"] = "208" });
            Check(ReferenceEquals(red, Themes.Active([light, red, dark], new("red"), ThemeMode.Dark)), "pick chosen within mode");
            Equal("4px", Themes.Variables(red)["--radius"], "web variables");
            Equal("38;2;255;0;0", Themes.TerminalColor(red, "accent", ColorDepth.TrueColor), "truecolor");
            Equal("48;5;196", Themes.TerminalColor(red, "accent", ColorDepth.Indexed256, ColorLayer.Background), "nearest cube color");
            Equal("38;5;208", Themes.TerminalColor(red, "warning", ColorDepth.TrueColor), "terminal override index");
            Equal("39", Themes.TerminalColor(red, "absent", ColorDepth.TrueColor), "unknown role default");
        }),
        Case("surfaces.messages.native-signatures-data-and-locales", () =>
        {
            var calls = 0;
            var messages = SurfaceMessages.Define("notes", new Dictionary<string, Catalog>
            {
                ["en"] = new() { ["saved"] = "Saved", ["count"] = MessageValue.Function(new Func<int, string>(n => { calls++; return $"{n} notes"; })) },
                ["de"] = new() { ["saved"] = "Gespeichert", ["count"] = MessageValue.Function(new Func<int, string>(n => $"{n} Notizen")) },
            });
            var message = messages.Message("count", 3); Equal(0, calls, "data construction never executes function");
            var german = Languages.Create("de-CH", SurfaceTranslations.None);
            Equal("3 Notizen", messages.In(german).Render(MessageData.FromJson(message.ToJson())), "base-locale fallback and numeric wire args");
            Equal("Saved", messages.Text("saved"), "outside render first catalog");
            Equal("fr-CA", Languages.ResolveLocale(null, ["fr-FR"], ["de", "fr-CA"]), "related locale");
            Equal("pt-BR", SurfaceTranslations.CanonicalLocale("pt-br"), "canonical casing");
            Equal("rtl", Languages.Create("ar", SurfaceTranslations.None).Dir, "right-to-left language");
            Throws<ArgumentException>(() => SurfaceMessages.Define("bad", new Dictionary<string, Catalog> { ["en"] = new() { ["x"] = "x" }, ["de"] = new() { ["y"] = "y" } }), "catalog key mismatch");
            Throws<ArgumentException>(() => messages.Message("count", "wrong"), "runtime message signature");
        }),
        Case("surfaces.messages.translation-precedence-failure-and-report-once", () =>
        {
            var own = SurfaceMessages.Define("notes", new Dictionary<string, Catalog> { ["en"] = new() { ["saved"] = "Saved" }, ["de"] = new() { ["saved"] = "Gespeichert" } });
            var conflicts = new List<RegistryConflict>();
            var index = SurfaceTranslations.Compose([
                new() { Plugin = "b", Version = 1, Value = [new("notes", "de", new() { ["saved"] = "B" })] },
                new() { Plugin = "a", Version = 1, Value = [new("notes", "de", new() { ["saved"] = "A" })] },
                new() { Plugin = "z", Version = 1, Value = [new("notes", "de", new() { ["saved"] = "Override" }, true)] },
            ], new ComposeContext(conflicts.Add));
            var reports = new List<MessagesReport>(); var language = Languages.Create("de", index, reports.Add);
            Equal("Override", own.In(language).Text("saved"), "explicit override wins"); Equal(1, conflicts.Count, "same-tier duplicate reported");
            Equal("other", own.In(language).Render(new("foreign", "other", [])), "foreign message displays key");
            own.In(language).Render(new("foreign", "other", [])); Equal(1, reports.Count, "unknown key reported once");
            var bad = SurfaceTranslations.Compose([new() { Plugin = "bad", Version = 1, Value = [new("notes", "de", new() { ["saved"] = MessageValue.Function(new Func<string>(() => throw new Exception("bad translation"))) })] }], new(_ => { }));
            Equal("Gespeichert", own.In(Languages.Create("de", bad, reports.Add)).Text("saved"), "broken contribution falls back to own catalog");
        }),
        Case("surfaces.messages.render-scope-restores-and-does-not-flow", async () =>
        {
            var german = Languages.Create("de", SurfaceTranslations.None); var english = Languages.Create("en", SurfaceTranslations.None);
            var value = Languages.In(german, () =>
            {
                Equal("1.234,5", Format.Number(1234.5), "number uses render locale");
                Equal("1,234.5", Languages.In(english, () => Format.Number(1234.5)), "nested render locale");
                Equal("1.234,5", Format.Number(1234.5), "outer restored");
                return Task.Run(() => Languages.Current);
            });
            Check(await value is null, "synchronous rendering does not leak language to other threads"); Check(Languages.Current is null, "scope cleared");
            try { Languages.In<int>(german, () => throw new Exception("render")); } catch (Exception) { }
            Check(Languages.Current is null, "scope restored on failure");
        }),
    ];
}
