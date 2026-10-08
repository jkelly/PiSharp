using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Sessions.Serialization;

internal static class ProfileRuntimeViewMcpBindingTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [("profile view actual MCP profile acquisition binds the same attachment and disposes once", ActualProfile)];
    private static void Check(bool value) { if (!value) throw new IOException("MCP profile view binding failed."); }
    private sealed class Resource : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
    private static async Task ActualProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.jsonl");
        var native = new Resource(); var discovery = new Resource();
        OfflineSessionProfile? profile = null; PersistentAgentSession? session = null;
        AgentSessionAttachment? bound = null; var admissions = 0; var bindings = 0;
        try
        {
            profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default,
                mcpAdmission: (cwd, generation, registry, policy, token) =>
                {
                    admissions++; Check(cwd == root && generation == 1);
                    return ValueTask.FromResult(new McpSessionRuntimeAdmission(registry, native, discovery, policy,
                        new McpServerCatalog([], []), [], false, (_, current) => new(current, []))
                    { BindProfileView = (owner, attachment) =>
                        { bindings++; Check(ReferenceEquals(owner.Current, attachment)); bound = attachment; } });
                });
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "mcp-view", timestamp = "2026-10-05T00:00:00.000Z", cwd = root }));
            var lifecycle = profile.CreateLifecycle(() => 1, () => Guid.NewGuid().ToString("N"));
            session = await lifecycle.CreateAsync(path, header, profile.SelectedModel);
            await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
            Check(admissions == 1 && bindings == 1 && ReferenceEquals(bound, profile.Sessions!.Current));
            Check(ReferenceEquals(profile.Registry, bound!.Session.CaptureToolCatalogRegistry()) == false);
            // The profile view retains the unbound native catalog; execution uses its owner-bound catalog.
            Check(profile.Registry.InvocationOwnerGeneration is null && bound.Session.CaptureToolCatalogRegistry().InvocationOwnerGeneration == 1);
            Check(native.Releases == 0 && discovery.Releases == 0);
            await profile.DisposeAsync(); await profile.DisposeAsync();
            Check(native.Releases == 1 && discovery.Releases == 1);
        }
        finally
        {
            if (profile is not null) await profile.DisposeAsync();
            if (session is not null) await session.DisposeAsync();
            var actual = Path.GetFullPath(root);
            Check(actual.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Path.GetFileName(actual).StartsWith("pisharp-mcp-view-", StringComparison.Ordinal));
            Directory.Delete(actual, true);
        }
    }
}
