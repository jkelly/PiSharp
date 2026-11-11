// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts.
// Authored native regression; not an upstream execution capture.
#pragma warning disable PISHARP_EXTUI001
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Extensions;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>Authored native regression evidence for Pi 1.5.2 interactive flow contracts.</summary>
internal static class Sync152UiFlowCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("sync152.ui.invalid-changelog-marker-repair", InvalidChangelog);
        yield return ("sync152.ui.cache-warm-adjacent-notices", CacheNotices);
        yield return ("sync152.ui.pending-bash-expansion-queue-settle", PendingBash);
        yield return ("sync152.ui.overlay-blocked-hidden-reset", Overlays);
        yield return ("sync152.ui.changelog-transform-factory", ChangelogTransform);
    }

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Start(InteractiveHarness pi) => pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");

    private static async Task InvalidChangelog()
    {
        foreach (var text in new[] { " v1.5.2-preview.17+build.4 ", "1.5.2.3-preview.1" })
            Check(Changelog.TryParseVersion(text, out _), "valid leading version " + text);
        Check(!Changelog.TryParseVersion("latest", out _), "invalid marker distinguished");
        await using var pi = new InteractiveHarness("sync152-invalid-marker");
        pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{\"lastChangelogVersion\":\"broken\"}");
        var telemetry = 0;
        pi.Configure = context => context with
        {
            ParseChangelogEntries = () => [new(1, 5, 2, "## [1.5.2]\nshould not appear")],
            ReportInstallTelemetry = (_, _) => telemetry++
        };
        Start(pi); await pi.WaitFor("escape interrupt");
        Equal(Field<string>(pi.Mode!, "version"), Field<InteractiveSettings>(pi.Mode!, "settings").LastChangelogVersion, "invalid app marker replaced");
        Equal(0, telemetry, "repair is not an installation event");
        Check(!pi.Terminal.Text.Contains("should not appear", StringComparison.Ordinal), "no stale notes");

        var package = pi.Write("extension/package.json", "{\"name\":\"test\",\"version\":\"1.5.2\"}");
        var changelog = pi.Write("extension/CHANGELOG.md", "## [1.5.2]\nnotes\n");
        var source = "npm:ui-marker-test";
        var key = PiSharp.Cli.Extensions.Pi.PiSourceInfo.PackageKey(source, "user");
        var settings = new InteractiveSettings(new JsonObject());
        settings.SetExtensionChangelogVersion(key, "broken");
        Equal(0, ExtensionChangelog.GetUpdates(0, [new JsonObject { ["origin"] = "package", ["baseDir"] = Path.GetDirectoryName(package),
            ["changelogPath"] = changelog, ["source"] = source, ["scope"] = "user" }], settings).Count, "invalid extension marker shows no notes");
        Equal("1.5.2", settings.GetExtensionChangelogVersion(key), "extension marker repaired");
        Equal(0, await pi.Quit(), "exit");
    }

    private static async Task CacheNotices()
    {
        await using var pi = new InteractiveHarness("sync152-warm-notice");
        UiLoop? loop = null;
        pi.Configure = context => { loop = context.Loop; return context with { FormatCacheWarmingUsage = entry =>
            "warm " + (double)entry["usage"]!["cost"]!["total"]! + " " + (string?)entry["note"] }; };
        Start(pi); await pi.WaitFor("escape interrupt");
        await loop!.InvokeAsync(() =>
        {
            var mode = pi.Mode!;
            var chat = Field<Container>(mode, "chatContainer");
            Field<InteractiveSettings>(mode, "settings").SetShowCacheMissNotices(true);
            chat.Clear();
            JsonObject Warm(double cost, string note) => new() { ["note"] = note, ["usage"] = new JsonObject { ["cost"] = new JsonObject { ["total"] = cost } } };
            Call(mode, "AddCacheWarmingUsage", Warm(1, "a"));
            var component = chat.Children.Last(); component.Render(100);
            Call(mode, "AddCacheWarmingUsage", Warm(2, "a"));
            Equal(2, chat.Children.Count, "adjacent notices share spacer and component");
            Contains(string.Join("\n", component.Render(100)), "warm 3 a", "cumulative total invalidates rendered cache");
            Call(mode, "AddCacheWarmingUsage", Warm(4, "b"));
            Equal(4, chat.Children.Count, "different note stays separate");
            chat.AddChild(new Text("intervening content"));
            Call(mode, "AddCacheWarmingUsage", Warm(5, "b"));
            Equal(7, chat.Children.Count, "intervening content prevents merge");
            Field<InteractiveSettings>(mode, "settings").SetShowCacheMissNotices(false);
            Call(mode, "AddCacheWarmingUsage", Warm(6, "b"));
            Equal(7, chat.Children.Count, "disabled notices ignored");
        });
        Equal(0, await pi.Quit(), "exit");
    }

    private static async Task PendingBash()
    {
        await using var pi = new InteractiveHarness("sync152-pending-bash");
        UiLoop? loop = null;
        pi.Configure = context => { loop = context.Loop; return context; };
        Start(pi); await pi.WaitFor("escape interrupt");
        var mode = pi.Mode!;
        await loop!.InvokeAsync(() => { Call(mode, "UpdateStateFromEvent", new JsonObject { ["type"] = "agent_start" }); Call(mode, "SetToolsExpanded", true); });
        foreach (var exclude in new[] { false, true })
            await loop.InvokeAsync(() => (Task)Call(mode, "HandleBashCommandAsync", "echo pending-bash", exclude)!);
        await loop.InvokeAsync(() =>
        {
            var pending = Field<List<BashExecutionComponent>>(mode, "pendingBashComponents");
            Equal(2, pending.Count, "both ! and !! paths defer while streaming");
            Check(pending.All(component => Field<bool>(component, "expanded")), "new components inherit expansion");
            Call(mode, "UpdatePendingMessagesDisplay");
            Check(pending.All(component => Field<Container>(mode, "pendingMessagesContainer").Children.Contains(component)), "queue refresh retains bash");
            Call(mode, "SetToolsExpanded", false);
            Check(pending.All(component => !Field<bool>(component, "expanded")), "toggle updates pending output");
        });
        await loop.InvokeAsync(() => (Task)Call(mode, "HandleEventAsync", new JsonObject { ["type"] = "agent_settled" })!);
        await loop.InvokeAsync(() =>
        {
            Equal(0, Field<List<BashExecutionComponent>>(mode, "pendingBashComponents").Count, "settle drains pending once");
            Equal(2, Field<Container>(mode, "chatContainer").Children.OfType<BashExecutionComponent>().Count(), "settle moves both to chat");
            Call(mode, "FlushPendingBashComponents");
            Equal(2, Field<Container>(mode, "chatContainer").Children.OfType<BashExecutionComponent>().Count(), "second flush does not duplicate");
            Call(mode, "UpdateStateFromEvent", new JsonObject { ["type"] = "agent_start" });
        });
        await loop.InvokeAsync(() => (Task)Call(mode, "HandleBashCommandAsync", "echo before-rebuild", false)!);
        await loop.InvokeAsync(() => { Call(mode, "RenderCurrentSessionState"); Equal(0, Field<List<BashExecutionComponent>>(mode, "pendingBashComponents").Count, "rebuild clears references"); Call(mode, "UpdateStateFromEvent", new JsonObject { ["type"] = "agent_settled" }); });
        Equal(0, await pi.Quit(), "exit");
    }

    private static async Task Overlays()
    {
        await using var pi = new InteractiveHarness("sync152-overlay-status");
        UiLoop? loop = null;
        pi.Configure = context => { loop = context.Loop; return context; };
        pi.Vars["PI_PROGRAM_STATUS"] = "1";
        Start(pi); await pi.WaitFor("escape interrupt");
        var disposed = new int[2];
        var handles = new ExtensionCustomComponentOverlayHandle?[2];
        ExtensionCustomComponentCallbacks Panel(int i) => new(new("sync152", 1, 1, "p" + i),
            (_, _, _) => Task.FromResult(new ExtensionCustomComponentRows(["overlay " + i], [9])), (_, _, _) => Task.CompletedTask,
            _ => { Interlocked.Increment(ref disposed[i]); return Task.CompletedTask; })
        { Blocked = new("permission", "approval " + i), Overlay = new(null) { OnHandle = (handle, _) => { handles[i] = handle; return Task.CompletedTask; } } };
        string Status() { var output = pi.Terminal.RawOutput; var start = output.LastIndexOf("]7501;state=", StringComparison.Ordinal); return start < 0 ? "" : output[start..].Split("\u001b\\")[0]; }
        string Approval(int i) => Convert.ToBase64String(Encoding.UTF8.GetBytes("approval " + i));
        var first = Panel(0); var second = Panel(1);
        var task1 = pi.Mode!.ShowExtensionCustomComponentAsync(first, null!, CancellationToken.None);
        await pi.WaitUntil(_ => Status().Contains(Approval(0), StringComparison.Ordinal), "first blocked overlay");
        var task2 = pi.Mode.ShowExtensionCustomComponentAsync(second, null!, CancellationToken.None);
        await pi.WaitUntil(_ => handles[1] is not null && Status().Contains(Approval(1), StringComparison.Ordinal), "second blocked overlay");
        handles[1]!.Send("setHidden", true);
        await loop!.InvokeAsync(() => { });
        Check(Status().Contains(Approval(1), StringComparison.Ordinal), "hidden overlay remains blocking");
        pi.Mode.SignalExtensionCustomComponent(second.Identity, true);
        await task2.WaitAsync(TimeSpan.FromSeconds(10));
        await pi.WaitUntil(_ => Status().Contains(Approval(0), StringComparison.Ordinal), "closing one retains other source");
        await loop.InvokeAsync(() => Call(pi.Mode, "ResetExtensionUI"));
        await task1.WaitAsync(TimeSpan.FromSeconds(10));
        await pi.WaitUntil(_ => !Status().Contains("state=blocked", StringComparison.Ordinal), "reset clears blocked overlays");
        Check(disposed.All(count => count == 1), "each custom component disposed once");
        Equal(0, await pi.Quit(), "exit");
    }

    private static async Task ChangelogTransform()
    {
        await using var pi = new InteractiveHarness("sync152-changelog-transform");
        var seen = new List<(string Text, MarkdownTransformContext Context)>();
        pi.Configure = context => context with { ParseChangelogEntries = () => [new(1, 5, 2, "## [1.5.2]\nrelease payload")],
            MarkdownTransformers = () => [(text, ctx) => { seen.Add((text, ctx)); return text.Replace("release payload", "transformed release", StringComparison.Ordinal); }] };
        Start(pi); await pi.WaitFor("escape interrupt");
        await pi.Submit("/changelog"); await pi.WaitFor("transformed release");
        Check(seen.Any(item => item.Context.MessageType == MarkdownMessageType.Changelog && item.Context.AvailableWidth > 0), "changelog type and width supplied");
        Check(seen.All(item => !item.Text.Contains("What's New", StringComparison.Ordinal)), "app heading excluded from payload");
        Equal(0, await pi.Quit(), "exit");
    }
}
