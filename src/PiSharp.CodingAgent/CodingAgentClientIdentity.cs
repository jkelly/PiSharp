// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/client-identity.ts.
using PiSharp.AI;

namespace PiSharp.CodingAgent;

#pragma warning disable PISHARP_IDENTITY001
/// <summary>
/// client-identity.ts <c>getClientIdentity</c>: how the coding agent's requests identify it to providers, a detailed
/// <c>User-Agent</c> with the Pi version PiSharp tracks (<see cref="ClientIdentities.FormatUserAgent"/>, decision 0013 Identity).
/// Upstream attributes requests to Pi (<c>PI_ATTRIBUTION</c>) while install telemetry is enabled. PiSharp sends no attribution,
/// whatever <c>enableInstallTelemetry</c> says (owner decision 2026-11-05, "Use safe defaults", core.client-identity-attribution):
/// PiSharp must not report as Pi, so attribution is <c>null</c>, which also turns off attribution set by an identity below it.
/// </summary>
internal static class CodingAgentClientIdentity
{
    private static readonly ClientIdentity Identity = new() { UserAgent = ClientUserAgent.Detailed, Version = ClientIdentities.PiVersion, AttributionOff = true };

    /// <summary>The identity of every coding agent request; sdk.ts sets it on each request, over the agent's default.</summary>
    public static ClientIdentity Get() => Identity;
}
#pragma warning restore PISHARP_IDENTITY001
