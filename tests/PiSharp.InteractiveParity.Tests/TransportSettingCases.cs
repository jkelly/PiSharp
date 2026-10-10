using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Pi;
using static Expect;

/// <summary>interactive-mode.ts: the /settings transport choice is saved and also set on session.agent.transport, so the next provider
/// request (the Codex WebSocket or SSE choice) uses it; sdk.ts starts from settingsManager.getTransport() ("auto" by default).</summary>
internal static class TransportSettingCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("settings.transport-applies-to-the-running-session", Sync(() =>
        {
            PiTransportSetting.Start(null);
            Equal<string?>("auto", PiTransportSetting.Current(), "default transport");
            var settings = new InteractiveSettings(new JsonObject());
            settings.SetTransport("sse");
            Equal("sse", settings.Transport, "saved setting");
            Equal<string?>("sse", PiTransportSetting.Current(), "the running session's transport");
            PiTransportSetting.Start("websocket-cached");
            Equal<string?>("websocket-cached", PiTransportSetting.Current(), "startup setting");
            PiTransportSetting.Start(null);
        })),
    ];
}
