// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts (transport: settingsManager.getTransport()),
// core/settings-manager.ts (getTransport defaults to "auto") and modes/interactive/interactive-mode.ts (the transport setting also sets
// session.agent.transport, so the change applies to the next request).
namespace PiSharp.Cli.Pi;

/// <summary>The provider transport preference of the running session: the merged <c>transport</c> setting at startup, replaced when
/// /settings changes it. Providers read it for every request.</summary>
internal static class PiTransportSetting
{
    private static volatile string s_value = "auto";

    internal static void Start(string? setting) => s_value = string.IsNullOrEmpty(setting) ? "auto" : setting;
    internal static void Set(string transport) => s_value = transport;
    internal static string? Current() => s_value;
}
