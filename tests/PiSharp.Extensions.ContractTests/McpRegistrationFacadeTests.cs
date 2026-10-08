using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Registration;

// Authored source-only controls. Root owns Program registration and all compiler/runtime execution.
internal static class McpRegistrationFacadeTests
{
    internal const string Prefix = "mcp-registration-facade.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "same-owner-replacement-preserves-order-and-copied-snapshots", Replacement),
        (Prefix + "foreign-register-refuses-and-foreign-unregister-is-noop", ForeignOwner),
        (Prefix + "configured-server-wins-in-admitted-host-publication", ConfiguredWins),
        (Prefix + "invalid-configuration-has-no-publication-or-acquisition", Validation),
        (Prefix + "inactive-and-canceled-owner-refuse-before-publication", Inactive),
        (Prefix + "publication-reentry-refuses-before-effects", Reentry),
        (Prefix + "publisher-fault-fences-replay-and-retains-original", PublisherFault),
        (Prefix + "foreign-owner-or-revision-receipt-fences-uncertain-commit", ReceiptIdentity),
        (Prefix + "exact-owner-retirement-removes-only-owned-rows-after-close", Retirement)
    ];

    private static McpLoadedConfiguration Empty() => McpConfigurationReader.Load(null, null, true);
    private static JsonData Config(string extra = "") => JsonData.Parse("{\"command\":\"inert-command\"" + extra + "}");
    private static string PathFor(string owner) => Path.Combine(Path.GetTempPath(), "mcp-registration-authored", owner);
    private static McpRegistrationCatalog Catalog(Action<McpRegistrationPublication>? observed = null,
        Action<IExtensionRegistry>? assert = null) => new(Empty(), assert ?? (_ => { }), change =>
        { observed?.Invoke(change); return new(change.Owner, change.Revision, true); });
    private static void Require(bool ok) { if (!ok) throw new InvalidOperationException("MCP registration control failed."); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Replacement()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        var changes = new List<McpRegistrationPublication>();
        var catalog = Catalog(changes.Add); var facade = catalog.BindOwner(owner, PathFor("first"));
        facade.RegisterMcpServer("first", Config()); facade.RegisterMcpServer("second", Config());
        var before = facade.GetMcpServers(); facade.RegisterMcpServer("first", Config(",\"enabled\":false"));
        var after = facade.GetMcpServers();
        Require(string.Join(',', after.Select(row => row.Name)) == "first,second");
        Require(before[0].Config.Enabled && !after[0].Config.Enabled && !ReferenceEquals(before[0].Config, after[0].Config));
        Require(changes.Count == 3 && changes[2].Revision == 3 && ReferenceEquals(changes[2].Owner, owner));
        Throws<ArgumentException>(() => catalog.BindOwner(owner, PathFor("changed")));
    }

    private static async Task ForeignOwner()
    {
        await using var registry = new ExtensionRegistry();
        await using var first = await registry.ActivateAsync("first", new EmptyExtension());
        await using var second = await registry.ActivateAsync("second", new EmptyExtension());
        var calls = 0; var catalog = Catalog(_ => calls++);
        var a = catalog.BindOwner(first, PathFor("first")); var b = catalog.BindOwner(second, PathFor("second"));
        a.RegisterMcpServer("shared", Config());
        Throws<InvalidOperationException>(() => b.RegisterMcpServer("shared", Config()));
        b.UnregisterMcpServer("shared"); b.UnregisterMcpServer("absent");
        Require(calls == 1 && b.GetMcpServers().Length == 1);
        a.UnregisterMcpServer("shared"); Require(calls == 2 && b.GetMcpServers().IsEmpty);
    }

    private static async Task ConfiguredWins()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        var configured = McpConfigurationReader.Load(new("global.json", "{\"mcpServers\":{\"shared\":{\"command\":\"configured\"}}}"), null, true);
        McpRegistrationPublication? committed = null;
        var catalog = new McpRegistrationCatalog(configured, _ => { }, change =>
        { committed = change; return new(change.Owner, change.Revision, true); });
        catalog.BindOwner(owner, PathFor("first")).RegisterMcpServer("shared", Config());
        Require(committed is not null && committed.Catalog.Overridden.Length == 1 &&
            committed.Catalog.Servers[0].Config.Raw.Value.GetProperty("command").GetString() == "configured");
    }

    private static async Task Validation()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        var calls = 0; var facade = Catalog(_ => calls++).BindOwner(owner, PathFor("first"));
        Throws<ArgumentException>(() => facade.RegisterMcpServer("bad name", Config()));
        Throws<ArgumentException>(() => facade.RegisterMcpServer("valid", JsonData.Parse("{\"url\":\"file:///not-http\"}")));
        facade.RegisterMcpServer("inert", Config(",\"env\":{\"LITERAL\":\"${NO_EXPANSION}\"},\"unknown\":{\"x\":9007199254740993}"));
        Require(calls == 1 && facade.GetMcpServers()[0].Config.Raw.Value.GetProperty("unknown").GetProperty("x").GetRawText() == "9007199254740993");
    }

    private static async Task Inactive()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        var active = true; var calls = 0; var catalog = Catalog(_ => calls++, _ =>
        { if (!active) throw new InvalidOperationException("Owner inactive."); });
        var facade = catalog.BindOwner(owner, PathFor("first")); active = false;
        Throws<InvalidOperationException>(() => facade.RegisterMcpServer("first", Config()));
        Throws<InvalidOperationException>(() => facade.GetMcpServers()); active = true;
        await owner.DisposeAsync();
        Throws<OperationCanceledException>(() => facade.UnregisterMcpServer("absent")); Require(calls == 0);
    }

    private static async Task Reentry()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        IMcpServerRegistrationFacade? facade = null; var rejected = 0;
        var catalog = Catalog(_ => { Throws<InvalidOperationException>(() => facade!.GetMcpServers()); rejected++; });
        facade = catalog.BindOwner(owner, PathFor("first")); facade.RegisterMcpServer("first", Config());
        Require(rejected == 1 && facade.GetMcpServers().Length == 1);
    }

    private static async Task PublisherFault()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        var original = new IOException("Host publication original"); var calls = 0;
        var catalog = new McpRegistrationCatalog(Empty(), _ => { }, _ => { calls++; throw original; });
        var facade = catalog.BindOwner(owner, PathFor("first"));
        Require(ReferenceEquals(Throws<IOException>(() => facade.RegisterMcpServer("first", Config())), original));
        Require(ReferenceEquals(Throws<IOException>(() => facade.GetMcpServers()).InnerException, original));
        Throws<IOException>(() => facade.UnregisterMcpServer("first")); Require(calls == 1);
    }

    private static async Task ReceiptIdentity()
    {
        await using var registry = new ExtensionRegistry();
        await using var owner = await registry.ActivateAsync("first", new EmptyExtension());
        await using var foreign = await registry.ActivateAsync("second", new EmptyExtension());
        foreach (var wrongOwner in new[] { true, false })
        {
            var calls = 0;
            var catalog = new McpRegistrationCatalog(Empty(), _ => { }, change =>
            { calls++; return new(wrongOwner ? foreign : change.Owner, wrongOwner ? change.Revision : change.Revision + 1, true); });
            var facade = catalog.BindOwner(owner, PathFor("first"));
            Throws<InvalidOperationException>(() => facade.RegisterMcpServer("first", Config()));
            Throws<IOException>(() => facade.GetMcpServers()); Require(calls == 1);
        }
    }

    private static async Task Retirement()
    {
        await using var registry = new ExtensionRegistry();
        await using var first = await registry.ActivateAsync("first", new EmptyExtension());
        await using var second = await registry.ActivateAsync("second", new EmptyExtension());
        var calls = 0; var catalog = Catalog(_ => calls++);
        var a = catalog.BindOwner(first, PathFor("first")); var b = catalog.BindOwner(second, PathFor("second"));
        a.RegisterMcpServer("first", Config()); b.RegisterMcpServer("second", Config());
        await first.DisposeAsync(); catalog.RetireOwner(first); catalog.RetireOwner(first);
        Require(calls == 3 && b.GetMcpServers().Single().Name == "second");
        Throws<OperationCanceledException>(() => a.GetMcpServers());
    }
}
