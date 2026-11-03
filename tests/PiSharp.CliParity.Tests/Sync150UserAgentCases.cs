using System.Net;
using PiSharp.AI;
using PiSharp.Cli.Pi;
using PiSharp.CodingAgent.Diagnostics;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/utils/pi-user-agent.ts (getPiUserAgent),
// utils/tools-manager.ts (the getPiUserAgent(VERSION) download header) and core/bug-report.ts (environment.userAgent).
// Pi 1.5.0 behaviour; the expectations are authored from the source, never captured from an upstream run. getPiUserAgent is pi-ai's
// detailed formatUserAgent, so pi's own requests send the User-Agent providers get; tools-manager.ts sent `pi-coding-agent` before.
#pragma warning disable PISHARP_IDENTITY001
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150UserAgentCases() =>
    [
        ("sync150.tools-download-sends-the-pi-user-agent", async () =>
        {
            using var sandbox = new Sandbox("tools-user-agent");
            var handler = new UserAgentRecorder();
            var manager = new PiToolsManager(Path.Combine(sandbox.AgentDir, "bin"), _ => null, () => handler, "https://gh.test")
            { Platform = "linux", Architecture = "x64" };
            Equal(null, await manager.EnsureToolAsync("rg", null, CancellationToken.None), "no release found");
            Check(handler.UserAgents.Count > 0, "the manager asked for the latest release");
            foreach (var agent in handler.UserAgents) Equal(ClientIdentities.FormatUserAgent(null), agent, "download User-Agent");
            Check(handler.UserAgents[0].StartsWith("pi/1.5.0 (", StringComparison.Ordinal), handler.UserAgents[0]);
        }),
        ("sync150.bug-report-user-agent-is-the-detailed-format", Sync(() =>
        {
            Equal(ClientIdentities.FormatUserAgent(null), BugReport.PiUserAgent(ClientIdentities.PiVersion), "tracked version");
            Equal(ClientIdentities.FormatUserAgent(new ClientIdentity { Version = "9.8.7" }), BugReport.PiUserAgent("9.8.7"), "given version");
            Equal($"pi/9.8.7 ({BugReport.Platform}; {BugReport.Runtime}; {BugReport.Arch})", BugReport.PiUserAgent("9.8.7"), "shape");
        })),
    ];

    private sealed class UserAgentRecorder : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (UserAgents) UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
#pragma warning restore PISHARP_IDENTITY001
