using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>
/// settings-selector.ts, settings-submenu.ts, config-selector.ts, oauth-selector.ts, login-dialog.ts, radius-login-selector.ts,
/// footer.ts and core/footer-data-provider.ts. Ports settings-selector.test.ts, oauth-selector.test.ts, footer-width.test.ts and
/// footer-data-provider.test.ts, plus render snapshots authored from the sources.
/// </summary>
internal static class SettingsLoginFooterCases
{
    private const string Down = "\u001b[B";
    private const string Up = "\u001b[A";
    private const string Enter = "\r";
    private const string Escape = "\u001b";

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---- settings-selector.test.ts ----
        yield return ("set.settings.cycles-fullscreen-settings", Sync(() =>
        {
            var exitOutput = new List<string>(); var scrollbar = new List<string>(); var copyOnSelect = new List<bool>(); var wheel = new List<int?>();
            var config = new SettingsConfig
            {
                FullscreenExitOutput = "transcript", FullscreenScrollbar = "auto", FullscreenCopyOnSelect = true, FullscreenWheelScrollLines = 7,
            };
            var callbacks = new SettingsCallbacks
            {
                OnFullscreenExitOutputChange = exitOutput.Add, OnFullscreenScrollbarChange = scrollbar.Add,
                OnFullscreenCopyOnSelectChange = copyOnSelect.Add, OnFullscreenWheelScrollLinesChange = wheel.Add,
            };
            void Cycle(string label, int count)
            {
                var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();
                foreach (var character in label) list.HandleInput(character.ToString());
                for (var i = 0; i < count; i++) list.HandleInput(Enter);
            }
            Cycle("Fullscreen exit output", 2);
            Equal("resume-hint,transcript", string.Join(",", exitOutput), "exit output cycle");
            Cycle("Fullscreen scrollbar", 3);
            Equal("always,hidden,auto", string.Join(",", scrollbar), "scrollbar cycle");
            Cycle("Fullscreen copy on select", 2);
            Equal("False,True", string.Join(",", copyOnSelect), "copy on select cycle");
            // #9758: custom values from settings.json stay in the cycle.
            Cycle("Fullscreen wheel scrolling", 3);
            Equal("10,auto,1", string.Join(",", wheel.Select(lines => lines?.ToString() ?? "auto")), "wheel cycle");
        }));

        yield return ("set.settings.fixed-theme-marked-while-browsing", Sync(() =>
        {
            var config = new SettingsConfig { CurrentTheme = "dark", TerminalTheme = "dark", AvailableThemes = ["system", "dark", "light"] };
            var previews = new List<string>();
            var list = new SettingsSelectorComponent(config, new SettingsCallbacks { OnThemePreview = previews.Add }).GetSettingsList();
            list.SelectItem("theme");
            list.HandleInput(Enter);
            var lines = Strip(list.Render(120));
            var system = lines.FindIndex(line => line.StartsWith("    system ", StringComparison.Ordinal));
            Check(system >= 0 && lines[system].Contains("Theme created from your terminal's colors", StringComparison.Ordinal), "system row with description");
            Check(lines[system + 1].StartsWith("    automatic ", StringComparison.Ordinal) && lines[system + 1].Contains("Use separate themes", StringComparison.Ordinal), "automatic row follows");
            Contains(string.Join("\n", lines), "→ ✓ dark", "configured theme selected");

            list.HandleInput(Down);
            var output = string.Join("\n", Strip(list.Render(120)));
            Contains(output, "  ✓ dark", "still marked");
            Contains(output, "→   light", "cursor moved");
            Equal("light", previews[^1], "preview follows the cursor");
        }));

        yield return ("set.settings.automatic-theme-marked-while-browsing", Sync(() =>
        {
            var config = new SettingsConfig { CurrentTheme = "light/dark", TerminalTheme = "dark", AvailableThemes = ["dark", "light", "other"] };
            var list = new SettingsSelectorComponent(config, new SettingsCallbacks { OnThemePreview = _ => { } }).GetSettingsList();
            list.SelectItem("theme");
            list.HandleInput(Enter);
            list.HandleInput(Enter);
            Contains(string.Join("\n", Strip(list.Render(120))), "→ ✓ light", "configured light theme selected");
            list.HandleInput(Down);
            var output = string.Join("\n", Strip(list.Render(120)));
            Contains(output, "  ✓ light", "still marked");
            Contains(output, "→   other", "cursor moved");
        }));

        yield return ("set.settings.model-thinking-level-marked-while-browsing", Sync(() =>
        {
            var model = new JsonObject { ["provider"] = "faux", ["id"] = "thinking-model", ["reasoning"] = true };
            const string modelKey = "faux/thinking-model";
            var config = new SettingsConfig
            {
                DefaultModel = modelKey, AvailableDefaultModels = [model], ThinkingLevel = "high",
                ModelThinkingLevels = new Dictionary<string, string> { [modelKey] = "medium" },
            };
            var list = new SettingsSelectorComponent(config, new SettingsCallbacks()).GetSettingsList();
            list.SelectItem("model-thinking");
            list.HandleInput(Enter);
            list.HandleInput(Enter);
            var output = string.Join("\n", Strip(list.Render(120)));
            Contains(output, "→ ✓ medium", "override selected");
            Contains(output, "    (clear override)", "clear override row");
            list.HandleInput(Down);
            output = string.Join("\n", Strip(list.Render(120)));
            Contains(output, "  ✓ medium", "still marked");
            Contains(output, "→   high", "cursor moved");
        }));

        yield return ("set.settings.model-thinking-change-and-clear", Sync(() =>
        {
            var model = new JsonObject { ["provider"] = "faux", ["id"] = "m", ["reasoning"] = true, ["thinkingLevelMap"] = new JsonObject { ["minimal"] = null, ["xhigh"] = "x" } };
            var changes = new List<string>();
            var config = new SettingsConfig { DefaultModel = "faux/m", AvailableDefaultModels = [model], ThinkingLevel = "low" };
            var callbacks = new SettingsCallbacks
            {
                OnModelThinkingLevelChange = (provider, id, level) => changes.Add($"set {provider}/{id} {level}"),
                OnModelThinkingLevelRemove = (provider, id) => changes.Add($"clear {provider}/{id}"),
            };
            var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();
            list.SelectItem("model-thinking");
            list.HandleInput(Enter);
            list.HandleInput(Enter);
            // minimal is mapped to null (unsupported), xhigh is mapped, max is not: off, low, medium, high, xhigh.
            Lines(["Thinking Level for m [faux]", "", "Step 2/2 · Select default thinking level for this model", "",
                "→   off       No reasoning", "    low       Light reasoning (~2k tokens)", "    medium    Moderate reasoning (~8k tokens)",
                "    high      Deep reasoning (~16k tokens)", "    xhigh     Extra-high reasoning (~32k tokens)", "", "  Enter to select · Esc to go back"],
                list.Render(80), "level step");
            list.HandleInput(Down); list.HandleInput(Down); list.HandleInput(Enter);
            Equal("set faux/m medium", changes[^1], "override set");
            // Loop: back at the model step, whose description now shows the override.
            Contains(string.Join("\n", Strip(list.Render(80))), "medium", "model row shows override");
            list.HandleInput(Enter);
            var output = string.Join("\n", Strip(list.Render(80)));
            Contains(output, "(clear override)", "clear row present");
            Contains(output, "Revert to global default (low)", "clear description");
            for (var i = 0; i < 3; i++) list.HandleInput(Down); // medium (preselected) -> high, xhigh, (clear override)
            list.HandleInput(Enter);
            Equal("clear faux/m", changes[^1], "override cleared");
            list.HandleInput(Escape);
            var main = string.Join("\n", Strip(list.Render(80)));
            Contains(main, "Default thinking level per model  none", "summary after closing");
        }));

        yield return ("set.settings.value-callbacks", Sync(() =>
        {
            var events = new List<string>();
            var config = new SettingsConfig { HttpIdleTimeoutMs = 45_000, QuietStartup = "header", DefaultProjectTrust = "never", OutputPad = 0 };
            var callbacks = new SettingsCallbacks
            {
                OnHttpIdleTimeoutMsChange = ms => events.Add("http " + ms),
                OnQuietStartupChange = quiet => events.Add("quiet " + quiet),
                OnDefaultProjectTrustChange = trust => events.Add("trust " + trust),
                OnOutputPadChange = pad => events.Add("pad " + pad),
                OnWarningsChange = warnings => events.Add("warn " + warnings.AnthropicExtraUsage),
            };
            var list = new SettingsSelectorComponent(config, callbacks).GetSettingsList();
            void Activate(string id) { list.SelectItem(id); list.HandleInput(Enter); }
            var main = string.Join("\n", Strip(list.Render(120)));
            Activate("http-idle-timeout");   // "45 sec" is not a choice: the cycle starts over at index 0
            Activate("quiet-startup");       // header -> false
            Activate("default-project-trust"); // Never trust -> Ask
            Activate("output-padding");      // 0 -> 1
            Activate("warnings");
            Lines(["→ Anthropic extra usage  true", "", "  Warn when Anthropic subscription auth may use paid extra usage", "",
                "  Enter/Space to change · Esc to cancel"], list.Render(80), "warnings submenu");
            list.HandleInput(Enter);
            list.HandleInput(Escape);
            Equal("http 30000|quiet False|trust ask|pad 1|warn False", string.Join("|", events), "callbacks");
            list.SelectItem("http-idle-timeout");
            Contains(string.Join("\n", Strip(list.Render(120))), "HTTP idle timeout                 30 sec", "updated value");
            _ = main;
        }));

        yield return ("set.settings.theme-apply-and-cancel", Sync(() =>
        {
            var changes = new List<string>(); var previews = new List<string>();
            var config = new SettingsConfig { CurrentTheme = "dark", TerminalTheme = "light", AvailableThemes = ["system", "dark", "light"] };
            var list = new SettingsSelectorComponent(config, new SettingsCallbacks { OnThemeChange = changes.Add, OnThemePreview = previews.Add }).GetSettingsList();
            list.SelectItem("theme");
            list.HandleInput(Enter);
            list.HandleInput(Escape);
            Equal("dark", previews[^1], "cancel restores the original preview");
            Equal(0, changes.Count, "no change on cancel");
            list.HandleInput(Enter);
            // system, automatic, dark (selected), light: up to automatic, then into the automatic menu.
            list.HandleInput(Up);
            Equal("dark/dark", previews[^1], "automatic preview uses the automatic setting (both sides default to the fixed theme)");
            list.HandleInput(Enter);
            Lines(["Automatic Theme", "", "Choose themes for terminal light and dark appearance.", "Light/dark detection requires terminal support.", "",
                "→ Light theme  dark", "  Dark theme   dark", "  Apply        save and go back", "  Change mode  switch to single theme", "",
                "  Theme to use in automatic mode when the terminal is light", "", "  Enter/Space to change · Esc to cancel"], list.Render(80), "automatic menu");
            list.HandleInput(Enter);            // light theme picker
            list.HandleInput(Down);             // light
            list.HandleInput(Enter);
            Equal("light/dark", previews[^1], "light theme chosen");
            list.HandleInput(Down); list.HandleInput(Down); list.HandleInput(Enter); // Apply
            Equal("light/dark", changes.Single(), "automatic setting applied");
            Contains(string.Join("\n", Strip(list.Render(120))), "Theme                             light/dark", "main list shows new value");
        }));

        yield return ("set.settings.image-rows-when-supported", Sync(() =>
        {
            TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
            try
            {
                var list = new SettingsSelectorComponent(new SettingsConfig { ShowImages = true, ImageWidthCells = 80 }, new SettingsCallbacks()).GetSettingsList();
                var lines = Strip(list.Render(100));
                Equal("  Show images                       true", lines[3].TrimEnd(), "show images after auto-compact");
                Equal("  Image width                       80", lines[4].TrimEnd(), "image width");
                Equal("  Auto-resize images                false", lines[5].TrimEnd(), "auto-resize next");
            }
            finally { TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.None, true, true)); }
        }));

        yield return ("set.settings.render-snapshot", Sync(() =>
        {
            var config = new SettingsConfig
            {
                AutoCompact = true, AutoResizeImages = true, BlockImages = false, EnableSkillCommands = true, ShowHardwareCursor = false, EditorPaddingX = 1,
                OutputPad = 1, AutocompleteMaxVisible = 7, ClearOnShrink = false, ShowTerminalProgress = true,
            };
            var selector = new SettingsSelectorComponent(config, new SettingsCallbacks());
            static string Row(bool selected, string label, string value) => (selected ? "→ " : "  ") + label.PadRight(32) + "  " + value;
            Lines([
                new string('─', 100), ">", "",
                Row(true, "Auto-compact", "true"), Row(false, "Auto-resize images", "true"), Row(false, "Block images", "false"),
                Row(false, "Skill commands", "true"), Row(false, "Show hardware cursor", "false"), Row(false, "Editor padding", "1"),
                Row(false, "Output padding", "1"), Row(false, "Autocomplete max items", "7"), Row(false, "Clear on shrink", "false"),
                Row(false, "Terminal progress", "true"), "  (1/32)", "", "  Automatically compact context when it gets too large", "",
                "  Type to search · Enter/Space to change · Esc to cancel", new string('─', 100)], selector.Render(100), "settings selector");
            // Scrolled to the end: the last ten rows, theme selected.
            var list = selector.GetSettingsList();
            list.SelectItem("theme");
            var lines = Strip(list.Render(100));
            // start = min(31 - 10 / 2, 32 - 10) = 22: rows 22 (double-escape action) to 31 (theme).
            Equal(Row(false, "Double-escape action", "tree"), lines[2].TrimEnd(), "first visible row at the end");
            Equal(Row(true, "Theme", "dark"), lines[11].TrimEnd(), "theme row");
            Equal("  (32/32)", lines[12], "position");
        }));

        yield return ("set.settings.stepped-submenu-back-and-cancel", Sync(() =>
        {
            var completed = new List<string>(); var cancelled = 0;
            var steps = new List<SteppedSubmenuStep>
            {
                new() { Key = "a", Title = SteppedSubmenuStep.Fixed("First"), Description = SteppedSubmenuStep.Fixed("Pick a"),
                    Options = _ => [new("1", "one"), new("2", "two")] },
                new() { Key = "b", Title = ctx => "Second after " + ctx["a"], Description = ctx => "Pick b for " + ctx["a"],
                    Options = _ => [new("x", "ex")], Preselect = _ => "x" },
            };
            var menu = new SteppedSubmenu(steps, ctx => completed.Add(ctx["a"] + ctx["b"]), () => cancelled++);
            menu.HandleInput(Down); menu.HandleInput(Enter);
            Lines(["Second after 2", "", "Step 2/2 · Pick b for 2", "", "→ ex", "", "  Enter to select · Esc to go back"], menu.Render(60), "second step");
            menu.HandleInput(Escape);
            Contains(string.Join("\n", Strip(menu.Render(60))), "Step 1/2 · Pick a", "back to first step");
            menu.HandleInput(Enter); menu.HandleInput(Enter);
            Equal("1x", completed.Single(), "completed");
            Equal(1, cancelled, "closes after the last step without loop");
            var search = new SelectSubmenu("T", "", [new("a", "alpha"), new("b", "beta")], "", _ => { }, () => { }, null, new SelectSubmenuOptions(Searchable: true));
            search.HandleInput("b"); search.HandleInput("e");
            Lines(["T", "", "> be", "", "→ beta", "", "  Type to filter · Enter to select · Esc to go back"], search.Render(60), "searchable submenu filtered");
        }));

        // ---- footer-width.test.ts ----
        yield return ("set.footer.cwd-sibling-not-abbreviated", Sync(() =>
            Equal("/home/user2", FooterComponent.FormatCwdForFooter("/home/user2", "/home/user"), "sibling path")));

        yield return ("set.footer.cwd-home-abbreviated", Sync(() =>
        {
            Equal("~", FooterComponent.FormatCwdForFooter("/home/user", "/home/user"), "home");
            Equal("~" + Path.DirectorySeparatorChar + "project", FooterComponent.FormatCwdForFooter("/home/user/project", "/home/user"), "descendant");
        }));

        yield return ("set.footer.wide-session-name-within-width", Sync(() =>
        {
            const int width = 93;
            var footer = Footer(new FakeFooterSession { SessionName = string.Concat(Enumerable.Repeat("한글", 30)) }, 1);
            foreach (var line in footer.Render(width)) Check(TextUtils.VisibleWidth(line) <= width, "line within width: " + Strip(line));
        }));

        yield return ("set.footer.wide-model-and-provider-within-width", Sync(() =>
        {
            const int width = 60;
            var session = new FakeFooterSession
            {
                ModelId = new string('模', 30), Provider = "공급자", Reasoning = true, ThinkingLevel = "high",
            };
            session.AddAssistant(12_345, 6_789, 0, 0, 1.234);
            var footer = Footer(session, 2);
            foreach (var line in footer.Render(width)) Check(TextUtils.VisibleWidth(line) <= width, "line within width: " + Strip(line));
        }));

        yield return ("set.footer.routed-model", Sync(() =>
        {
            var session = new FakeFooterSession
            {
                ModelId = "auto", Reasoning = true, ThinkingLevel = "high",
                RoutedModel = new FooterRoutedModel(new JsonObject { ["id"] = "gpt-5.6-luna" }, "medium"),
            };
            Contains(Strip(Footer(session, 1).Render(120)[1]), "auto • high → gpt-5.6-luna • medium", "routed model");
        }));

        yield return ("set.footer.summary-and-tool-usage-in-cost", Sync(() =>
        {
            var session = new FakeFooterSession();
            session.AddAssistant(100, 10, 0, 0, 0.5);
            session.Entries.Add(new JsonObject { ["type"] = "branch_summary", ["usage"] = Usage(20, 5, 0, 0, 0.25) });
            session.Entries.Add(new JsonObject { ["type"] = "compaction", ["usage"] = Usage(5, 2, 0, 0, 0.125) });
            session.Entries.Add(new JsonObject { ["type"] = "message", ["message"] = new JsonObject { ["role"] = "toolResult", ["usage"] = Usage(15, 3, 0, 0, 0.375) } });
            Contains(Strip(Footer(session, 1).Render(120)[1]), "$1.250", "total cost");
        }));

        yield return ("set.footer.cached-totals-update-after-append", Sync(() =>
        {
            var session = new FakeFooterSession();
            session.AddAssistant(10, 1, 0, 0, 0.5);
            var footer = Footer(session, 1);
            Contains(Strip(footer.Render(120)[1]), "$0.500", "first render");
            session.AddAssistant(10, 1, 0, 0, 0.5);
            Contains(Strip(footer.Render(120)[1]), "$1.000", "after append");
        }));

        yield return ("set.footer.cache-hit-rate", Sync(() =>
        {
            var session = new FakeFooterSession();
            session.AddAssistant(100, 10, 50, 50, 0.001);
            Contains(Strip(Footer(session, 1).Render(120)[1]), "CH25.0%", "cache hit rate");
        }));

        yield return ("set.footer.kimi-coding-subscription", Sync(() =>
        {
            var session = new FakeFooterSession { Provider = "kimi-coding" };
            session.AddAssistant(100, 10, 0, 0, 1.234);
            Contains(Strip(Footer(session, 1).Render(120)[1]), "$1.234 (sub)", "kimi subscription");
        }));

        yield return ("set.footer.explicit-subscription", Sync(() =>
        {
            var session = new FakeFooterSession { Provider = "anthropic", UsingSubscription = true };
            Contains(Strip(Footer(session, 1).Render(120)[1]), "$0.000 (sub)", "subscription without cost");
        }));

        yield return ("set.footer.oauth-not-subscription", Sync(() =>
        {
            var session = new FakeFooterSession { Provider = "openrouter" };
            session.AddAssistant(100, 10, 0, 0, 1.234);
            var stats = Strip(Footer(session, 1).Render(120)[1]);
            Contains(stats, "$1.234", "cost");
            Check(!stats.Contains("(sub)", StringComparison.Ordinal), "no (sub)");
        }));

        yield return ("set.footer.render-snapshot", Sync(() =>
        {
            var session = new FakeFooterSession { SessionName = "my-session", Reasoning = true, ThinkingLevel = "high" };
            session.AddAssistant(1_234, 567, 0, 0, 0.123);
            var data = new FakeFooterData { Branch = "main", ProviderCount = 2 };
            data.Statuses["b"] = "second\tstatus";
            data.Statuses["a"] = "first\n  line";
            var footer = new FooterComponent(session, data) { Environment = name => name == "HOME" ? "/home/test" : null };
            const string left = "↑1.2k ↓567 $0.123 12.3%/200k (auto)";
            const string right = "(test) test-model • high";
            Lines(["/tmp/project (main) • my-session", left + new string(' ', 80 - left.Length - right.Length) + right, "first line second status"],
                footer.Render(80), "footer");
            footer.SetAutoCompactEnabled(false);
            session.Context = new FooterContextUsage(null, 200_000, null);
            session.Entries.Add(new JsonObject { ["type"] = "custom" });
            Contains(Strip(footer.Render(80)[1]), "$0.123 ?/200k  ", "unknown context percentage without auto");
            session.Context = new FooterContextUsage(190_000, 200_000, 95);
            session.Entries.Add(new JsonObject { ["type"] = "custom" });
            Contains(footer.Render(80)[1], Themes.Current.Fg("error", "95.0%/200k"), "error color above 90%");
            // Narrow: the provider prefix is dropped first, then the right side is truncated.
            // Narrow: the stats are truncated with "..." and leave no room for the model.
            Lines(["/tmp/project (main) • m...", "↑1.2k ↓567 $0.123 95.0%...", "first line second status"], footer.Render(26), "narrow footer");
        }));

        yield return ("set.footer.format-tokens", Sync(() =>
        {
            Equal("999", FooterComponent.FormatTokens(999), "below 1k");
            Equal("1.2k", FooterComponent.FormatTokens(1_234), "1k-10k");
            Equal("12k", FooterComponent.FormatTokens(12_345), "10k-1M");
            Equal("1.5M", FooterComponent.FormatTokens(1_500_000), "1M-10M");
            Equal("13M", FooterComponent.FormatTokens(12_500_000), "rounds half up");
        }));

        // ---- footer-data-provider.test.ts ----
        yield return ("set.footer-data.plain-repo-from-nested-dir", Sync(() =>
        {
            using var dir = new TempDir();
            var repo = CreatePlainRepo(dir.Path);
            var nested = Path.Combine(repo, "src", "nested");
            Directory.CreateDirectory(nested);
            var host = new FakeFooterHost();
            using var provider = new FooterDataProvider(nested, host);
            Equal("main", provider.GetGitBranch(), "branch");
            Equal(0, host.Spawns.Count, "git not spawned");
        }));

        yield return ("set.footer-data.reftable-invalid-head-uses-git", Sync(() =>
        {
            using var dir = new TempDir();
            var repo = CreatePlainReftableRepo(dir.Path);
            var host = new FakeFooterHost();
            using var provider = new FooterDataProvider(repo, host);
            Equal("main", provider.GetGitBranch(), "branch");
            var spawn = host.Spawns.Single();
            Equal("git", spawn.Command, "command");
            Equal("--no-optional-locks symbolic-ref --quiet --short HEAD", string.Join(" ", spawn.Args), "args");
            Check(spawn.Cwd.EndsWith("repo", StringComparison.Ordinal), "cwd is the repo: " + spawn.Cwd);
        }));

        yield return ("set.footer-data.reftable-worktree", Sync(() =>
        {
            using var dir = new TempDir();
            var (worktree, _) = CreateReftableWorktree(dir.Path);
            using var provider = new FooterDataProvider(worktree, new FakeFooterHost());
            Equal("main", provider.GetGitBranch(), "branch");
        }));

        yield return ("set.footer-data.unresolved-invalid-head-is-detached", Sync(() =>
        {
            using var dir = new TempDir();
            var repo = CreatePlainReftableRepo(dir.Path);
            using var provider = new FooterDataProvider(repo, new FakeFooterHost { ResolvedBranch = "" });
            Equal("detached", provider.GetGitBranch(), "detached");
        }));

        yield return ("set.footer-data.same-branch-does-not-notify", Sync(() =>
        {
            using var dir = new TempDir();
            var (worktree, _) = CreateReftableWorktree(dir.Path);
            var host = new FakeFooterHost();
            using var provider = new FooterDataProvider(worktree, host);
            Equal("main", provider.GetGitBranch(), "branch");
            host.Spawns.Clear();
            var notified = 0;
            provider.OnBranchChange(() => notified++);
            EmitReftableChange(provider);
            host.Advance(501);
            Equal(1, host.Execs.Count, "one async git call");
            Equal(0, host.Spawns.Count, "no sync git call");
            Equal("main", provider.GetGitBranch(), "branch unchanged");
            Equal(0, notified, "no notification");
        }));

        yield return ("set.footer-data.debounces-reftable-updates", Sync(() =>
        {
            using var dir = new TempDir();
            var (worktree, _) = CreateReftableWorktree(dir.Path);
            var host = new FakeFooterHost();
            using var provider = new FooterDataProvider(worktree, host);
            Equal("main", provider.GetGitBranch(), "branch");
            host.Execs.Clear();
            EmitReftableChange(provider); EmitReftableChange(provider); EmitReftableChange(provider);
            host.Advance(499);
            Equal(0, host.Execs.Count, "not yet");
            host.Advance(2);
            Equal(1, host.Execs.Count, "one refresh");
            host.Advance(650);
            Equal(1, host.Execs.Count, "still one refresh");
        }));

        yield return ("set.footer-data.real-watcher-updates-branch", async () =>
        {
            using var dir = new TempDir();
            var (worktree, reftable) = CreateReftableWorktree(dir.Path);
            var host = new FakeFooterHost { RealWatchersAndTimers = true };
            using var provider = new FooterDataProvider(worktree, host);
            Equal("main", provider.GetGitBranch(), "branch");
            host.ResolvedBranch = "foo";
            var notified = 0;
            provider.OnBranchChange(() => Interlocked.Increment(ref notified));
            await File.WriteAllTextAsync(Path.Combine(reftable, "tables.list"), "1\n");
            // Real watcher events can arrive late under load.
            await WaitFor(() => host.ExecCount == 1, 10_000);
            await WaitFor(() => provider.GetGitBranch() == "foo", 10_000);
            Equal(1, host.ExecCount, "one async git call");
            Equal("foo", provider.GetGitBranch(), "branch updated");
            Equal(1, Volatile.Read(ref notified), "notified once");
        });

        yield return ("set.footer-data.retries-watchers-after-error", Sync(() =>
        {
            using var dir = new TempDir();
            var repo = CreatePlainRepo(dir.Path);
            var host = new FakeFooterHost();
            using var provider = new FooterDataProvider(repo, host);
            var original = (FakeWatcher?)provider.HeadWatcher;
            Check(original is not null, "head watcher installed");
            original!.EmitError();
            Check(provider.HeadWatcher is null, "watcher cleared on error");
            Check(original.Disposed, "watcher closed");
            host.Advance(4999);
            Check(provider.HeadWatcher is null, "not retried before 5s");
            host.Advance(1);
            Check(provider.HeadWatcher is not null && !ReferenceEquals(provider.HeadWatcher, original), "new watcher after 5s");
        }));

        yield return ("set.footer-data.head-change-statuses-and-cwd", Sync(() =>
        {
            using var dir = new TempDir();
            var repo = CreatePlainRepo(dir.Path);
            var host = new FakeFooterHost();
            using var provider = new FooterDataProvider(repo, host);
            Equal("main", provider.GetGitBranch(), "branch");
            var notified = 0;
            var unsubscribe = provider.OnBranchChange(() => notified++);
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");
            ((FakeWatcher)provider.HeadWatcher!).Emit("change", "index");  // other files in .git are ignored
            host.Advance(600);
            Equal(0, host.Execs.Count + notified, "non-HEAD change ignored");
            ((FakeWatcher)provider.HeadWatcher!).Emit("rename", "HEAD");
            host.Advance(500);
            Equal("detached", provider.GetGitBranch(), "detached HEAD");
            Equal(1, notified, "notified");
            unsubscribe();

            provider.SetExtensionStatus("x", "one");
            provider.SetExtensionStatus("y", "two");
            provider.SetExtensionStatus("x", null);
            Equal("y=two", string.Join(",", provider.GetExtensionStatuses().Select(kv => kv.Key + "=" + kv.Value)), "statuses");
            provider.ClearExtensionStatuses();
            Equal(0, provider.GetExtensionStatuses().Count, "cleared");
            provider.SetAvailableProviderCount(3);
            Equal(3, provider.GetAvailableProviderCount(), "provider count");

            var outside = Path.Combine(dir.Path, "outside");
            Directory.CreateDirectory(outside);
            var cwdNotified = 0;
            provider.OnBranchChange(() => cwdNotified++);
            provider.SetCwd(outside);
            Equal(1, cwdNotified, "setCwd notifies");
            if (FooterDataProvider.FindGitPaths(outside) is null) Equal(null, provider.GetGitBranch(), "no repo");
        }));

        // ---- oauth-selector.test.ts ----
        yield return ("set.oauth.projects-provider-auth-options", Sync(() =>
        {
            var catalog = new FakeCatalog
            {
                Providers =
                [
                    new("anthropic", "Anthropic", OAuth: new("Anthropic (Claude Pro/Max)"), ApiKey: new("Anthropic API key")),
                    new("google-vertex", "Google Vertex AI", ApiKey: new("Google Cloud credentials", HasLogin: false)),
                ],
            };
            var apiKey = AuthSelectorFormatting.GetLoginProviderOptions(catalog, "api_key");
            Equal("anthropic:Anthropic:api_key:Anthropic API key|google-vertex:Google Vertex AI:api_key:Google Cloud credentials",
                string.Join("|", apiKey.Select(o => $"{o.Id}:{o.Name}:{o.AuthType}:{o.Method?.Name}")), "api key options");
            var oauth = AuthSelectorFormatting.GetLoginProviderOptions(catalog, "oauth");
            Equal("anthropic:Anthropic:oauth", string.Join("|", oauth.Select(o => $"{o.Id}:{o.Name}:{o.AuthType}")), "oauth options");
            Equal(3, AuthSelectorFormatting.GetLoginProviderOptions(catalog).Count, "all options");
        }));

        yield return ("set.oauth.without-status-is-not-configured", Sync(() =>
        {
            var output = SelectorOutput([new("google", "Google", "api_key")]);
            Contains(output, "not configured", "not configured");
            Check(!output.Contains("✓ configured", StringComparison.Ordinal), "not marked configured");
        }));

        yield return ("set.oauth.oauth-auth-in-api-key-selector", Sync(() =>
            Contains(SelectorOutput([new("anthropic", "Anthropic", "api_key", Status: new AuthCheck("oauth", "OAuth"))]), "subscription configured", "oauth shown distinctly")));

        yield return ("set.oauth.environment-api-key-configured", Sync(() =>
        {
            var output = SelectorOutput([new("openai", "OpenAI", "api_key", Status: new AuthCheck("api_key", "OPENAI_API_KEY"))]);
            Contains(output, "✓ env: OPENAI_API_KEY", "env source");
            Check(!output.Contains("not configured", StringComparison.Ordinal), "configured");
        }));

        yield return ("set.oauth.models-json-key-configured", Sync(() =>
            Contains(SelectorOutput([new("local-proxy", "local-proxy", "api_key", Status: new AuthCheck("api_key", "key in models.json"))]), "✓ key in models.json", "models.json key")));

        yield return ("set.oauth.models-json-command-configured", Sync(() =>
            Contains(SelectorOutput([new("op-proxy", "op-proxy", "api_key", Status: new AuthCheck("api_key", "command in models.json"))]), "✓ command in models.json", "models.json command")));

        yield return ("set.oauth.render-snapshot-filter-select", Sync(() =>
        {
            var selected = new List<string>(); var cancelled = 0;
            var providers = new List<AuthSelectorProvider>
            {
                new("anthropic", "Anthropic", "oauth", new AuthMethodInfo("Anthropic (Claude Pro/Max)"), new AuthCheck("oauth", "OAuth")),
                new("github-copilot", "GitHub Copilot", "oauth", new AuthMethodInfo("GitHub Copilot"), null, Subscription: false),
                new("openai", "OpenAI", "api_key", new AuthMethodInfo("OpenAI API key"), new AuthCheck("api_key", "OPENAI_API_KEY, OPENAI_ORG")),
            };
            var selector = new OAuthSelectorComponent("login", providers, (id, type) => selected.Add(id + "/" + type), () => cancelled++);
            Lines([new string('─', 70), "", " Select provider to configure:", "", ">", "",
                " → Anthropic [subscription] ✓ configured", "   GitHub Copilot [account] • not configured",
                "   OpenAI [API key] ✓ env: OPENAI_API_KEY, OPENAI_ORG", "", new string('─', 70)], selector.Render(70), "login selector");
            selector.HandleInput(Down);
            selector.HandleInput(Enter);
            Equal("github-copilot/oauth", selected[^1], "select second");
            foreach (var ch in "opena") selector.HandleInput(ch.ToString());
            Lines([" → OpenAI [API key] ✓ env: OPENAI_API_KEY, OPENAI_ORG"], selector.Render(70).Skip(6).Take(1), "filtered");
            selector.HandleInput(Enter);
            Equal("openai/api_key", selected[^1], "select filtered");
            foreach (var ch in "zzz") selector.HandleInput(ch.ToString());
            Contains(string.Join("\n", Strip(selector.Render(70))), "  No matching providers", "no matches");
            selector.HandleInput(Escape);
            Equal(1, cancelled, "cancel");
            Contains(string.Join("\n", Strip(new OAuthSelectorComponent("logout", [], (_, _) => { }, () => { }).Render(70))),
                "Select provider to logout:", "logout title");
            Contains(string.Join("\n", Strip(new OAuthSelectorComponent("logout", [], (_, _) => { }, () => { }).Render(70))),
                "  No providers logged in. Use /login first.", "logout empty");
            Contains(string.Join("\n", Strip(new OAuthSelectorComponent("login", [], (_, _) => { }, () => { }).Render(70))),
                "  No providers available", "login empty");
        }));

        yield return ("set.oauth.scroll-window", Sync(() =>
        {
            var providers = Enumerable.Range(1, 12).Select(i => new AuthSelectorProvider($"p{i}", $"Provider {i:00}", "api_key")).ToList();
            var selector = new OAuthSelectorComponent("login", providers, (_, _) => { }, () => { }, "Provider");
            for (var i = 0; i < 6; i++) selector.HandleInput(Down);
            var lines = Strip(selector.Render(60)).Select(line => line.TrimEnd()).ToList();
            Equal("> Provider", lines[4], "initial search input kept");
            Equal("   Provider 03 • not configured", lines[6], "window starts at selected - 4");
            Equal(" → Provider 07 • not configured", lines[10], "selected row");
            Equal("   (7/12)", lines[14], "scroll info");
        }));

        // ---- login-dialog.ts ----
        yield return ("set.login.auth-url-snapshot", Sync(() =>
        {
            var tui = new FakeTui(); var opened = new List<string>();
            var dialog = new LoginDialogComponent(tui, "anthropic", (_, _) => { }, "Anthropic") { OpenBrowser = opened.Add };
            dialog.ShowAuth("https://example.test/auth", "Complete login in your browser.");
            var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
            Lines([new string('─', 60), " Login to Anthropic", "", " https://example.test/auth", $" {clickHint} • ctrl+x to copy", "",
                " Complete login in your browser.", new string('─', 60)], dialog.Render(60), "auth url");
            Equal("https://example.test/auth", opened.Single(), "browser opened");
            Check(tui.RenderRequests > 0, "render requested");
        }));

        yield return ("set.login.prompt-unmasked-like-upstream", async () =>
        {
            var dialog = new LoginDialogComponent(new FakeTui(), "openai", (_, _) => { }, "OpenAI");
            var answer = dialog.ShowPrompt("Enter OpenAI API key", "sk-...");
            foreach (var ch in "sk-test") dialog.HandleInput(ch.ToString());
            Lines([new string('─', 50), " Login to OpenAI", "", " Enter OpenAI API key", " e.g., sk-...", "> sk-test",
                " (escape/ctrl+c to cancel, enter to submit)", new string('─', 50)], dialog.Render(50), "plain prompt");
            dialog.HandleInput(Enter);
            Equal("sk-test", await answer, "answer");
            Contains(string.Join("\n", Strip(dialog.Render(50))), "> sk-test", "submitted echo");
        });

        yield return ("set.login.secret-prompt-renders-masked", async () =>
        {
            var dialog = new LoginDialogComponent(new FakeTui(), "openai", (_, _) => { }, "OpenAI") { Focused = true };
            var answer = dialog.ShowPrompt("Enter OpenAI API key", null, secret: true);
            foreach (var ch in "sk-secret-123") dialog.HandleInput(ch.ToString());
            dialog.HandleInput("\u007f"); // backspace edits the hidden value
            var rendered = dialog.Render(50);
            Check(!string.Join("\n", rendered).Contains("sk-secret", StringComparison.Ordinal), "secret never rendered");
            Lines([new string('─', 50), " Login to OpenAI", "", " Enter OpenAI API key", "> ************",
                " (escape/ctrl+c to cancel, enter to submit)", new string('─', 50)], rendered, "masked prompt");
            Contains(string.Join("\n", rendered), TuiBase.CursorMarker, "focused cursor marker");
            dialog.HandleInput(Enter);
            Equal("sk-secret-12", await answer, "answer is the real value");
            var after = string.Join("\n", Strip(dialog.Render(50)));
            Contains(after, "> ************", "submitted echo masked");
            Check(!after.Contains("sk-secret", StringComparison.Ordinal), "echo hides the value");
        });

        yield return ("set.login.cancel-rejects-and-completes", async () =>
        {
            var completions = new List<string>();
            var dialog = new LoginDialogComponent(new FakeTui(), "x", (success, message) => completions.Add($"{success}:{message}"));
            var answer = dialog.ShowManualInput("Paste the redirect URL:");
            Lines([new string('─', 40), " Login to x", "", " Paste the redirect URL:", ">", " (escape/ctrl+c to cancel)", new string('─', 40)],
                dialog.Render(40), "manual input");
            dialog.HandleInput(Escape);
            var error = await Task.Run(async () => { try { await answer; return null; } catch (Exception e) { return e; } });
            Equal("Login cancelled", error?.Message, "rejected");
            Equal("False:Login cancelled", completions.Single(), "completed");
            Check(dialog.Signal.IsCancellationRequested, "signal aborted");
        });

        yield return ("set.login.device-code-info-details", Sync(() =>
        {
            var dialog = new LoginDialogComponent(new FakeTui(), "github-copilot", (_, _) => { }, "GitHub Copilot", "Copilot setup");
            dialog.ShowDeviceCode("ABCD-1234", "https://github.test/device");
            dialog.ShowWaiting("Waiting for authentication...");
            var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
            Lines([new string('─', 50), " Copilot setup", "", " https://github.test/device", $" {clickHint}", "", " Enter code: ABCD-1234", "",
                " Waiting for authentication...", " (escape/ctrl+c to cancel)", new string('─', 50)], dialog.Render(50), "device code");
            dialog.ShowDetails(["line one"]);
            dialog.ShowInfo("Configured outside pi.", [new PiSharp.AI.Authentication.OAuth.AuthInfoLink("https://docs.test", "Docs")], showCloseHint: true);
            dialog.ShowProgress("Working...");
            Lines([new string('─', 50), " Copilot setup", "", " line one", "", " Configured outside pi.", " Docs: https://docs.test", "",
                " (escape/ctrl+c to close)", " Working...", new string('─', 50)], dialog.Render(50), "details and info");
        }));

        // ---- radius-login-selector.ts ----
        yield return ("set.radius.shimmers-selected-option", Sync(() =>
        {
            var tui = new FakeTui(); var selected = new List<string>();
            const string text = "Sign in with Radius";
            const string label = text + " • not configured";
            var now = 1000.0;
            using var menu = new RadiusLoginMenuComponent(tui, "Select authentication method:",
                ["Sign in with an account", "Sign in with an API key", label], new RadiusOption(label, text), selected.Add, () => { }, () => now);
            var plain = menu.Render(60);
            Check(!menu.Animating, "not animating while another option is selected");
            menu.HandleInput(Down); menu.HandleInput(Down);
            now = 1250;
            var lines = menu.Render(60);
            Check(menu.Animating, "animating when selected");
            var index = Strip(lines).FindIndex(line => line.StartsWith(" → Sign in with Radius", StringComparison.Ordinal));
            Equal(" → " + label, Strip(lines[index]).TrimEnd(), "same text");
            var theme = Themes.Current;
            var expected = new Text(theme.Fg("accent", "→ ") + RadiusLoginMenuComponent.RadiusShimmer(text, 250) + label[text.Length..], 1, 0).Render(60)[0];
            Equal(expected, lines[index], "animated line");
            Equal(plain.Count, lines.Count, "same line count");
            menu.HandleInput(Enter);
            Equal(label, selected.Single(), "selected");
            Check(!menu.Animating, "animation stopped");
        }));

        yield return ("set.radius.shimmer-colors", Sync(() =>
        {
            var shimmer = RadiusLoginMenuComponent.RadiusShimmer("ab", 0);
            // Position 0 is band 0 at t = 0: exactly the first logo color; position 1 is a quarter into band 0.
            var first = Colors.ForegroundAnsi(Colors.ParseColor("#4d9abf"), TerminalColorMode.TrueColor);
            Check(shimmer.StartsWith(first + "a", StringComparison.Ordinal), "first character in the first logo color");
            Check(shimmer.EndsWith("b\u001b[39m", StringComparison.Ordinal), "foreground reset");
            var later = RadiusLoginMenuComponent.RadiusShimmer("a", 400); // offset 4: band 3 (#f09082) at t = 0
            Check(later.StartsWith(Colors.ForegroundAnsi(Colors.ParseColor("#f09082"), TerminalColorMode.TrueColor) + "a", StringComparison.Ordinal), "colors stream");
        }));

        // ---- config-selector.ts ----
        yield return ("set.config.render-snapshot-and-toggle", Sync(() =>
        {
            using var dir = new TempDir();
            var (agentDir, cwd, settings) = ConfigFixture(dir.Path);
            var resolved = ConfigResolved(agentDir, cwd);
            var closed = 0; var renders = 0;
            var selector = new ConfigSelectorComponent(resolved, settings, cwd, agentDir, () => closed++, () => { }, () => renders++, 30, homeDir: dir.Path);
            Lines(["", new string('─', 80), "",
                "Global Resources" + new string(' ', 80 - 16 - 42) + "tab switch mode · space toggle · esc close", "~/.pi/agent/settings.json", "",
                ">", "",
                "  npm:tools (user)", "    Extensions", ">     [x] tools/index.ts", "    Skills", "      [ ] review",
                "  User (~/agent/)", "    Extensions", "      [x] local.ts", "    Prompts", "      [x] fix.md",
                "  Project (.pi/)", "    Extensions", "      [x] proj.ts",
                "", new string('─', 80)], selector.Render(80), "global config");
            var list = (IInputHandler)selector.GetResourceList();
            list.HandleInput(" ");
            Equal("-" + Path.Combine("extensions", "tools", "index.ts"), settings.Global["packages"]![0]!["extensions"]![0]!.GetValue<string>(), "package pattern written");
            Equal(1, renders, "render requested");
            list.HandleInput(Down); list.HandleInput(Down);
            list.HandleInput(Enter);
            Equal("-" + Path.Combine("extensions", "local.ts"), string.Join(",", settings.GlobalPaths["extensions"]), "top-level pattern written");
            list.HandleInput(Down); list.HandleInput(Down);
            list.HandleInput(" ");  // project item in global mode is not toggled
            Equal(2, renders, "project item ignored in global mode");
            list.HandleInput(Up);  list.HandleInput(Up); list.HandleInput(Up); list.HandleInput(Up);
            list.HandleInput(" "); // package again: re-enable, filter object collapses to the source string
            Equal("+" + Path.Combine("extensions", "tools", "index.ts"), settings.Global["packages"]![0]!["extensions"]![0]!.GetValue<string>(), "re-enable pattern");
            foreach (var ch in "fix") list.HandleInput(ch.ToString());
            Lines(["> fix", "", "  User (~/agent/)", "    Prompts", ">     [x] fix.md"], selector.Render(80).Skip(6).Take(5), "filtered");
            list.HandleInput(Escape);
            Equal(1, closed, "closed");
        }));

        yield return ("set.config.project-mode-cycles-overrides", Sync(() =>
        {
            using var dir = new TempDir();
            var (agentDir, cwd, settings) = ConfigFixture(dir.Path);
            var resolved = ConfigResolved(agentDir, cwd);
            var selector = new ConfigSelectorComponent(resolved, settings, cwd, agentDir, () => { }, () => { }, () => { }, 30, homeDir: dir.Path);
            var list = (IInputHandler)selector.GetResourceList();
            list.HandleInput("\t");
            var lines = Strip(selector.Render(80));
            Equal("Project Local Resources" + new string(' ', 80 - 23 - 53) + "tab switch mode · space cycle inherit/+/- · esc close", lines[3].TrimEnd(), "project header");
            Equal(".pi/settings.json · inherited global resources are dimmed", lines[4].TrimEnd(), "project scope hint");
            Equal("  npm:tools (user) · inherited global", lines[8].TrimEnd(), "inherited group");
            Equal(">     [x] tools/index.ts  inherited global", lines[10].TrimEnd(), "inherited item");
            // inherit (enabled) -> unload -> load -> inherit
            list.HandleInput(" ");
            // node:path relative: platform separators, as upstream writes them.
            var unload = new JsonArray(new JsonObject { ["source"] = "npm:tools", ["autoload"] = false, ["extensions"] = new JsonArray("-" + Path.Combine("extensions", "tools", "index.ts")) });
            Equal(unload.ToJsonString(), settings.Project["packages"]!.ToJsonString(), "unload override");
            Equal(">     [-] tools/index.ts  project unload", Strip(selector.Render(80))[10].TrimEnd(), "unload row");
            list.HandleInput(" ");
            Equal(">     [+] tools/index.ts  project load", Strip(selector.Render(80))[10].TrimEnd(), "load row");
            list.HandleInput(" ");
            Equal("[]", settings.Project["packages"]!.ToJsonString(), "back to inherit removes the autoload:false override");
            // Top-level inherited global file: unload names the file then marks it.
            list.HandleInput(Down); list.HandleInput(Down); // review (skills), then local.ts
            list.HandleInput(" ");
            var local = Path.Combine(agentDir, "extensions", "local.ts");
            Equal(local + ",-" + local, string.Join(",", settings.ProjectPaths["extensions"]), "top-level project override");
        }));
    }

    // ---- helpers ----

    private static FooterComponent Footer(FakeFooterSession session, int providerCount) =>
        new(session, new FakeFooterData { Branch = "main", ProviderCount = providerCount }) { Environment = name => name == "HOME" ? "/home/test" : null };

    private static JsonObject Usage(double input, double output, double cacheRead, double cacheWrite, double cost) =>
        new() { ["input"] = input, ["output"] = output, ["cacheRead"] = cacheRead, ["cacheWrite"] = cacheWrite, ["cost"] = new JsonObject { ["total"] = cost } };

    private sealed class FakeFooterSession : IFooterSession
    {
        public string ModelId { get; init; } = "test-model";
        public string Provider { get; init; } = "test";
        public bool Reasoning { get; init; }
        public string ThinkingLevel { get; init; } = "off";
        public string SessionName { get; init; } = "";
        public bool UsingSubscription { get; init; }
        public FooterContextUsage? Context { get; set; } = new(null, 200_000, 12.3);
        public List<JsonObject> Entries { get; } = [];
        private JsonObject? model;

        public void AddAssistant(double input, double output, double cacheRead, double cacheWrite, double cost) =>
            Entries.Add(new JsonObject { ["type"] = "message", ["message"] = new JsonObject { ["role"] = "assistant", ["usage"] = Usage(input, output, cacheRead, cacheWrite, cost) } });

        public JsonObject? StateModel => model ??= new JsonObject { ["id"] = ModelId, ["provider"] = Provider, ["contextWindow"] = 200_000, ["reasoning"] = Reasoning };
        public string? StateThinkingLevel => ThinkingLevel;
        public JsonObject? Model => StateModel;
        public FooterRoutedModel? RoutedModel { get; init; }
        public FooterContextUsage? GetContextUsage() => Context;
        public bool IsUsingSubscription(string provider) => UsingSubscription;
        public int GetEntryCount() => Entries.Count;
        public string GetSessionId() => "test-session";
        public string? GetLeafId() => null;
        public IReadOnlyList<JsonObject> GetEntries() => Entries;
        public string GetCwd() => "/tmp/project";
        public string? GetSessionName() => SessionName;
    }

    private sealed class FakeFooterData : IReadonlyFooterDataProvider
    {
        public string? Branch { get; init; }
        public int ProviderCount { get; init; }
        public Dictionary<string, string> Statuses { get; } = [];
        public string? GetGitBranch() => Branch;
        public IReadOnlyDictionary<string, string> GetExtensionStatuses() => Statuses;
        public int GetAvailableProviderCount() => ProviderCount;
        public Action OnBranchChange(Action callback) => () => { };
    }

    private sealed class FakeWatcher(string path, Action<string, string?> listener, Action onError) : IDisposable
    {
        public string Path { get; } = path;
        public bool Disposed { get; private set; }
        public void Emit(string eventType, string? filename) => listener(eventType, filename);
        public void EmitError() => onError();
        public void Dispose() => Disposed = true;
    }

    /// <summary>The source test's mocked child_process plus vitest fake timers and inspectable watchers.</summary>
    private sealed class FakeFooterHost : FooterDataProviderHost
    {
        public string ResolvedBranch { get; set; } = "main";
        public bool RealWatchersAndTimers { get; init; }
        public List<(string Command, IReadOnlyList<string> Args, string Cwd)> Spawns { get; } = [];
        public List<(string Command, IReadOnlyList<string> Args, string Cwd)> Execs { get; } = [];
        public int ExecCount { get { lock (Execs) return Execs.Count; } }
        private readonly List<(double Due, Action Callback, Box Handle)> timers = [];
        private double now;

        private sealed class Box : IDisposable { public bool Cancelled; public void Dispose() => Cancelled = true; }

        public override IDisposable? Watch(string path, Action<string, string?> listener, Action onError) =>
            RealWatchersAndTimers ? base.Watch(path, listener, onError) : new FakeWatcher(path, listener, onError);

        public override IDisposable WatchFile(string path, int intervalMs, Action<FooterFileStat, FooterFileStat> listener) =>
            RealWatchersAndTimers ? base.WatchFile(path, intervalMs, listener) : new Box();

        public override FooterSpawnResult SpawnSync(string command, IReadOnlyList<string> args, string cwd)
        {
            Spawns.Add((command, args, cwd));
            if (args.Count > 1 && args[1] == "symbolic-ref")
                return ResolvedBranch.Length > 0 ? new(0, ResolvedBranch + "\n") : new(1, "");
            return new(1, "");
        }

        public override Task<FooterExecResult> ExecFile(string command, IReadOnlyList<string> args, string cwd)
        {
            lock (Execs) Execs.Add((command, args, cwd));
            if (args.Count > 1 && args[1] == "symbolic-ref")
                return Task.FromResult(new FooterExecResult(ResolvedBranch.Length == 0, ResolvedBranch.Length > 0 ? ResolvedBranch + "\n" : ""));
            return Task.FromResult(new FooterExecResult(true, ""));
        }

        public override IDisposable SetTimeout(Action callback, int milliseconds)
        {
            if (RealWatchersAndTimers) return base.SetTimeout(callback, milliseconds);
            var handle = new Box();
            timers.Add((now + milliseconds, callback, handle));
            return handle;
        }

        /// <summary>vi.advanceTimersByTimeAsync: fires due timers in order, including ones scheduled while advancing.</summary>
        public void Advance(double milliseconds)
        {
            var target = now + milliseconds;
            while (true)
            {
                var next = timers.Where(timer => !timer.Handle.Cancelled && timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next.Callback is null) break;
                timers.Remove(next);
                now = next.Due;
                next.Handle.Cancelled = true;
                next.Callback();
            }
            now = target;
        }
    }

    private static void EmitReftableChange(FooterDataProvider provider)
    {
        var watcher = provider.ReftableWatcher as FakeWatcher;
        Check(watcher is not null, "reftable watcher installed");
        watcher!.Emit("change", "tables.list");
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs) throw new TimeoutException("Timed out waiting for condition");
            await Task.Delay(10);
        }
    }

    private static string CreatePlainReftableRepo(string tempDir)
    {
        var repoDir = Path.Combine(tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git", "reftable"));
        File.WriteAllText(Path.Combine(repoDir, ".git", "HEAD"), "ref: refs/heads/.invalid\n");
        return repoDir;
    }

    private static string CreatePlainRepo(string tempDir)
    {
        var repoDir = Path.Combine(tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, ".git", "HEAD"), "ref: refs/heads/main\n");
        return repoDir;
    }

    private static (string WorktreeDir, string ReftableDir) CreateReftableWorktree(string tempDir)
    {
        var repoDir = Path.Combine(tempDir, "repo");
        var commonGitDir = Path.Combine(repoDir, ".git");
        var gitDir = Path.Combine(commonGitDir, "worktrees", "src");
        var worktreeDir = Path.Combine(tempDir, "worktree");
        var reftableDir = Path.Combine(commonGitDir, "reftable");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(reftableDir);
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(Path.Combine(worktreeDir, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/.invalid\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(reftableDir, "tables.list"), "0\n");
        return (worktreeDir, reftableDir);
    }

    private sealed class FakeCatalog : ILoginProviderCatalog
    {
        public List<LoginProviderInfo> Providers { get; init; } = [];
        public IReadOnlyList<LoginProviderInfo> GetProviders() => Providers;
        public ProviderAuthStatus GetProviderAuthStatus(string providerId) => new(false);
        public bool IsUsingOAuth(string providerId) => false;
    }

    private static string SelectorOutput(List<AuthSelectorProvider> providers) =>
        string.Join("\n", Strip(new OAuthSelectorComponent("login", providers, (_, _) => { }, () => { }).Render(120)));

    private sealed class FakeConfigSettings : IConfigSelectorSettings
    {
        public JsonObject Global { get; } = [];
        public JsonObject Project { get; } = [];
        public Dictionary<string, List<string>> GlobalPaths { get; } = [];
        public Dictionary<string, List<string>> ProjectPaths { get; } = [];
        public JsonObject GetGlobalSettings() => (JsonObject)Global.DeepClone();
        public JsonObject GetProjectSettings() => (JsonObject)Project.DeepClone();
        private void SetGlobal(string key, IReadOnlyList<string> paths) { GlobalPaths[key] = [.. paths]; Global[key] = new JsonArray([.. paths.Select(p => (JsonNode)p)]); }
        private void SetProject(string key, IReadOnlyList<string> paths) { ProjectPaths[key] = [.. paths]; Project[key] = new JsonArray([.. paths.Select(p => (JsonNode)p)]); }
        public void SetExtensionPaths(IReadOnlyList<string> paths) => SetGlobal("extensions", paths);
        public void SetSkillPaths(IReadOnlyList<string> paths) => SetGlobal("skills", paths);
        public void SetPromptTemplatePaths(IReadOnlyList<string> paths) => SetGlobal("prompts", paths);
        public void SetThemePaths(IReadOnlyList<string> paths) => SetGlobal("themes", paths);
        public void SetProjectExtensionPaths(IReadOnlyList<string> paths) => SetProject("extensions", paths);
        public void SetProjectSkillPaths(IReadOnlyList<string> paths) => SetProject("skills", paths);
        public void SetProjectPromptTemplatePaths(IReadOnlyList<string> paths) => SetProject("prompts", paths);
        public void SetProjectThemePaths(IReadOnlyList<string> paths) => SetProject("themes", paths);
        public void SetPackages(JsonArray packages) => Global["packages"] = packages.DeepClone();
        public void SetProjectPackages(JsonArray packages) => Project["packages"] = packages.DeepClone();
    }

    private static (string AgentDir, string Cwd, FakeConfigSettings Settings) ConfigFixture(string root)
    {
        var agentDir = Path.Combine(root, "agent");
        var cwd = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(agentDir, "extensions"));
        Directory.CreateDirectory(Path.Combine(cwd, ".pi", "extensions"));
        var settings = new FakeConfigSettings();
        settings.Global["packages"] = new JsonArray("npm:tools");
        return (agentDir, cwd, settings);
    }

    private static ScopedResolvedPaths ConfigResolved(string agentDir, string cwd)
    {
        var packageRoot = Path.Combine(agentDir, "npm", "tools");
        var package = new PathMetadata("npm:tools", "user", "package", packageRoot, packageRoot);
        var user = new PathMetadata("auto", "user", "top-level");
        var project = new PathMetadata("auto", "project", "top-level");
        var global = new ResolvedPaths(
            [new(Path.Combine(packageRoot, "extensions", "tools", "index.ts"), true, package), new(Path.Combine(agentDir, "extensions", "local.ts"), true, user),
             new(Path.Combine(cwd, ".pi", "extensions", "proj.ts"), true, project)],
            [new(Path.Combine(packageRoot, "skills", "review", "SKILL.md"), false, package)],
            [new(Path.Combine(agentDir, "prompts", "fix.md"), true, user)],
            []);
        return new ScopedResolvedPaths(global, global);
    }

    private sealed class FakeTui : ITui
    {
        public int RenderRequests { get; private set; }
        public UiLoop Loop { get; } = UiLoop.CreateManual();
        public void RequestRender(bool force = false) => RenderRequests++;
        public TuiMode Mode => TuiMode.Regular;
        public ITerminal Terminal => throw new NotSupportedException();
        public int FullRedraws => 0;
        public List<IComponent> Children { get; } = [];
        public void AddChild(IComponent component) => Children.Add(component);
        public void RemoveChild(IComponent component) => Children.Remove(component);
        public void Clear() => Children.Clear();
        public bool ShowHardwareCursor { get; set; }
        public bool ClearOnShrink { get; set; }
        public IComponent? FocusedComponent { get; private set; }
        public void SetFocus(IComponent? component) => FocusedComponent = component;
        public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) => throw new NotSupportedException();
        public void HideOverlay() { }
        public bool HasOverlay() => false;
        public void Start() { }
        public void Stop(bool preserveScreen = false) { }
        public void RenderNow(bool force = false) { }
        public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) => () => { };
        public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) => () => { };
        public void SetTerminalColorSchemeNotifications(bool enabled) { }
        public Task<TerminalColors> QueryTerminalColors(int timeoutMs, Action<TerminalColors>? onLateReply = null) => Task.FromResult(new TerminalColors(null, null, null));
        public Action? OnDebug { get; set; }
        public void Invalidate() { }
    }
}
