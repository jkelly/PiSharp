using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PublishedCliUiFixture;

/// <summary>Trusted authored consumer; marker IO exists only in this published test package.</summary>
public sealed class Entry : IPiSharpExtension
{
    private IExtensionUi? previous;
    public Entry() => Signals.Mark("constructor");
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Signals.Mark("initialize");
        registry.RegisterTool(new("ui", "fixture.cli.ui", "Explicit published CLI UI consumer",
            JsonData.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"}},\"required\":[\"action\"],\"additionalProperties\":false}"), Execute));
        return ValueTask.CompletedTask;
    }

    private async ValueTask<JsonData> Execute(JsonData arguments, IExtensionToolContext context, CancellationToken token)
    {
        var ui = ((IExtensionUiContext)context).Ui;
        var action = arguments.Value.GetProperty("action").GetString()!;
        Signals.Mark("tool:" + action);
        var observations = new List<object>(); var approved = false;
        try
        {
            if (action == "dialogs")
            {
                observations.Add(Observe("select", await ui.SelectAsync("choose \u6587", ["", "second"], new(0), token)));
                var confirmation = await ui.ConfirmAsync("confirm", "allow \0\U0001f642", cancellationToken: token);
                approved = ExtensionUiSourceDefaults.Confirmation(confirmation); observations.Add(Observe("confirm", confirmation));
                observations.Add(Observe("input", await ui.InputAsync("input", "", cancellationToken: token)));
                observations.Add(Observe("editor", await ui.EditorAsync("editor", "prefill\r\n", token)));
                foreach (var notification in new ExtensionUiNotification[]
                {
                    new ExtensionUiNotify("notice\0\u6587", ExtensionUiNotifyKind.Warning),
                    new ExtensionUiStatus("status", "ready"), new ExtensionUiStatus("status", null),
                    new ExtensionUiTextWidget("widget", ["first", "\u6587\0"], ExtensionUiWidgetPlacement.BelowEditor),
                    new ExtensionUiTextWidget("widget", null), new ExtensionUiTitle("title \U0001f642"),
                    new ExtensionUiEditorText("edit\n\0")
                }) observations.Add(Observe(notification.GetType().Name, await ui.PublishAsync(notification, token)));
            }
            else if (action is "confirm" or "repeat" or "timeout")
            {
                var result = await ui.ConfirmAsync("first", "approval", new(action == "timeout" ? 25 : 0), token);
                observations.Add(Observe("first", result)); approved = ExtensionUiSourceDefaults.Confirmation(result);
                if (action == "repeat")
                {
                    var second = await ui.ConfirmAsync("second", "approval", new(0), token);
                    observations.Add(Observe("second", second)); approved = ExtensionUiSourceDefaults.Confirmation(second);
                }
            }
            else if (action == "stale")
            {
                var stale = previous ?? throw new InvalidOperationException("Authored prior UI scope is absent.");
                var result = await stale.ConfirmAsync("stale", "must not publish", new(0), token);
                observations.Add(Observe("stale", result)); approved = ExtensionUiSourceDefaults.Confirmation(result);
            }
            else throw new InvalidOperationException("Unsupported authored UI action.");
            token.ThrowIfCancellationRequested();
            if (approved) Signals.Mark("approved");
            var details = new
            {
                action, approved, mode = ui.Capabilities.Mode.ToString(), connectionGeneration = ui.Capabilities.ConnectionGeneration,
                sessionGeneration = ui.Capabilities.SessionGeneration, features = ui.Capabilities.Features.Select(feature => feature.ToString()).ToArray(),
                observations
            };
            return JsonData.Parse(JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = approved ? "ui:approved" : "ui:denied" } }, details,
                structuredContent = details, future = (object?)null
            }));
        }
        finally { previous = ui; Signals.Mark("tool-closed"); }
    }
    private static object Observe<T>(string method, ExtensionUiOutcome<T> outcome) => new
    {
        method, kind = outcome.Kind.ToString(), reason = outcome.UnavailableReason?.ToString(),
        value = outcome.Kind == ExtensionUiOutcomeKind.Value ? (object?)outcome.Value : null
    };
    public ValueTask DisposeAsync() { Signals.Mark("dispose"); return ValueTask.CompletedTask; }
}

internal static class Signals
{
    [ModuleInitializer] internal static void Module() => Mark("module");
    internal static void Mark(string stage)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
        if (path is null) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(path) || root.Length > 4096 || !root.StartsWith(temporary, StringComparison.Ordinal) ||
            !Directory.Exists(root) || path.Any(char.IsControl)) throw new InvalidOperationException("Authored marker root is invalid.");
        File.AppendAllText(Path.Combine(root, "PublishedFixture.CliUi.markers"), stage + "\n");
    }
}
