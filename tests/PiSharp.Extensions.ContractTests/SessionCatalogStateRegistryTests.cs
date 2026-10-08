using System.Collections.Immutable;
using System.Runtime.InteropServices;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class SessionCatalogStateRegistryTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("session-catalog-sdk.owned-list-does-not-alias-host-array", OwnedList),
        ("session-catalog-sdk.query-and-result-bounds-before-return", Bounds),
        ("session-catalog-sdk.cancellation-before-and-after-host-read", Cancellation),
        ("session-catalog-sdk.legacy-resume-and-switch-fail-before-effects", Legacy),
        ("session-state.selected-owner-kind-latest-and-opaque-data", SelectedState),
        ("session-state.explicit-version-error-and-canceled-context", StateErrors)
    ];
    private static ExtensionSessionCatalogItem Item(char key = 'a') => new(new string(key, 64), "store", "same.jsonl", "/store/same.jsonl",
        "same", "2026-10-02T00:00:00Z", "/cwd", "/cwd", null, 10, "2026-10-02T00:00:00Z", false);
    private static async Task OwnedList()
    {
        var array = new[] { Item() }; var provider = new Provider(new(ImmutableCollectionsMarshal.AsImmutableArray(array), "cursor", 2, 1));
        await using var registry = new ExtensionRegistry(null, null, provider);
        await Activate(registry, async context =>
        {
            var page = await context.ListSessionsAsync(new()); array[0] = Item('b');
            Check(page.Items[0].Key == new string('a', 64) && page.NextCursor == "cursor" && page.SkippedFiles == 2 && page.UnavailableStores == 1,
                "Captured SDK listing aliases the provider's mutable backing array or changed diagnostics.");
        });
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "catalog", JsonData.Null);
        Check(provider.ListCalls == 1 && provider.Closed == 1, "Catalog scope did not close after the command callback.");
    }
    private static async Task Bounds()
    {
        var valid = Item(); var invalid = new List<ExtensionSessionCatalogPage>
        {
            new(default, null, 0, 0), new([valid, valid], null, 0, 0), new([valid with { Key = "bad" }], null, 0, 0),
            new([valid with { Path = null! }], null, 0, 0), new([valid with { FileBytes = -1 }], null, 0, 0),
            new([valid], "\ud800", 0, 0), new([valid], null, -1, 0), new([valid with { FileName = new string('a', 8193) }], null, 0, 0),
            new(Enumerable.Range(0, 128).Select(index => valid with { Key = index.ToString("x64"), WorkingDirectory = new string('\u6587', 8192) }).ToImmutableArray(), null, 0, 0)
        };
        foreach (var page in invalid)
        {
            var provider = new Provider(page); await using var registry = new ExtensionRegistry(null, null, provider);
            await Activate(registry, async context => { await Reject<InvalidOperationException>(async () => { await context.ListSessionsAsync(new(128)); }); });
            await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "catalog", JsonData.Null);
            Check(provider.ListCalls == 1 && provider.Closed == 1, "Rejected host view escaped callback cleanup.");
        }
        var source = new Provider(new([valid], null, 0, 0)); await using var queryRegistry = new ExtensionRegistry(null, null, source);
        await Activate(queryRegistry, async context =>
        {
            foreach (var query in new[] { new ExtensionSessionCatalogQuery(0), new(129), new(1, new string('a', 32769)), new(1, null, "\ud800") })
                await Reject<ArgumentException>(async () => { await context.ListSessionsAsync(query); });
        });
        await queryRegistry.InvokeCommandAsync(queryRegistry.CaptureSnapshot(), "catalog", JsonData.Null);
        Check(source.ListCalls == 0, "Invalid SDK query reached host discovery.");
    }
    private static async Task Cancellation()
    {
        using var token = new CancellationTokenSource(); var source = new Provider(new([Item()], null, 0, 0));
        await using var registry = new ExtensionRegistry(null, null, source);
        await Activate(registry, async context =>
        {
            token.Cancel(); await Reject<OperationCanceledException>(async () => { await context.ListSessionsAsync(new(), token.Token); });
            Check(source.ListCalls == 0, "Canceled query entered provider.");
            using var during = new CancellationTokenSource(); source.BeforeReturn = during.Cancel;
            await Reject<OperationCanceledException>(async () => { await context.ListSessionsAsync(new(), during.Token); });
        });
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "catalog", JsonData.Null);
        Check(source.ListCalls == 1 && source.Closed == 1, "Post-read cancellation returned a listing or escaped cleanup.");
    }
    private static async Task Legacy()
    {
        var source = new Provider(new([], null, 0, 0)); await using var registry = new ExtensionRegistry(null, null, source);
        await Activate(registry, async context =>
        {
            await Reject<InvalidOperationException>(async () => { await context.ResumeSessionAsync(new string('a', 64)); });
            await Reject<InvalidOperationException>(async () => { await context.SwitchSessionAsync("/absolute/same.jsonl"); });
            await context.AppendSessionEntryAsync("still-source", 1, JsonData.Null);
        });
        await registry.InvokeCommandAsync(registry.CaptureSnapshot(), "catalog", JsonData.Null);
        Check(source.LegacyResume == 0 && source.LegacySwitch == 0 && source.Writes == 1, "Default preflight overload called an effectful legacy method or lost source context.");
    }
    private static Task SelectedState()
    {
        var records = ImmutableArray.Create(State("own", "state", 1, "old", "{\"value\":1}"), State("other", "state", 99, "foreign", "{}"),
            State("own", "other-kind", 99, "other", "{}"), JsonData.Parse("{\"type\":\"custom\",\"customType\":\"unknown\",\"data\":{\"delegate\":\"inert\"}}"),
            State("own", "state", 1, "latest", "{\"opaque\":1.00e400,\"future\":{\"x\":true}}"));
        var context = new Context(new("same", 2, "latest", records)); var result = context.ReadLatest("state")!;
        Check(result.EntryId == "latest" && result.SchemaVersion == 1 && result.Data.Value.GetProperty("opaque").GetRawText() == "1.00e400",
            "State restoration lost namespace, last-entry ordering or opaque number bytes.");
        Check(context.ReadLatest("missing") is null && context.SessionSnapshot!.BranchEntries == records, "Missing state rewrote the branch.");
        return Task.CompletedTask;
    }
    private static async Task StateErrors()
    {
        var future = new Context(new("same", 1, "future", [State("own", "state", 2, "future", "{}")]));
        var error = await Reject<ExtensionSessionStateException>(() => { future.ReadLatest("state"); return Task.CompletedTask; });
        Check(error.Failure == ExtensionSessionStateFailure.IncompatibleVersion, "Future schema was silently migrated.");
        Check(future.ReadLatest("state", 2)!.SchemaVersion == 2, "Explicit compatible version was rejected.");
        var malformed = new Context(new("same", 1, "bad", [JsonData.Parse("{\"type\":\"custom\",\"id\":\"bad\",\"customType\":\"pisharp.extension-state\",\"data\":{\"extensionId\":\"own\",\"entryKind\":\"state\",\"schemaVersion\":\"1\",\"data\":{}}}")]));
        error = await Reject<ExtensionSessionStateException>(() => { malformed.ReadLatest("state"); return Task.CompletedTask; });
        Check(error.Failure == ExtensionSessionStateFailure.InvalidEnvelope, "Malformed matching envelope was silently ignored.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var old = future with { SessionCancellationToken = canceled.Token };
        await Reject<OperationCanceledException>(() => { old.ReadLatest("state", 2); return Task.CompletedTask; });
        await Reject<ArgumentException>(() => { future.ReadLatest("invalid kind"); return Task.CompletedTask; });
    }
    private static JsonData State(string owner, string kind, int version, string id, string data) => JsonData.Parse(
        "{\"type\":\"custom\",\"id\":\"" + id + "\",\"customType\":\"pisharp.extension-state\",\"data\":{\"extensionId\":\"" + owner +
        "\",\"entryKind\":\"" + kind + "\",\"schemaVersion\":" + version + ",\"data\":" + data + "}}");
    private sealed record Context(ExtensionSessionSnapshot? SessionSnapshot, CancellationToken SessionCancellationToken = default) : IExtensionSessionContext
    {
        public string OwnerId => "own"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => default; public CancellationToken ExtensionLifetimeCancellationToken => default;
    }
    private static Task Activate(ExtensionRegistry registry, Func<IExtensionSessionCatalogCommandContext, Task> action) => registry.ActivateAsync("own", new Plugin(action));
    private sealed class Plugin(Func<IExtensionSessionCatalogCommandContext, Task> action) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { registry.RegisterCommand(new("catalog", "catalog", "", async (_, context, _) => await action((IExtensionSessionCatalogCommandContext)context))); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Provider(ExtensionSessionCatalogPage page) : IExtensionSessionCatalogProvider
    {
        private ExtensionSessionCatalogPage Page { get; } = page;
        internal int ListCalls, LegacyResume, LegacySwitch, Writes, Closed; internal Action? BeforeReturn;
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => new("source", 1, null, []);
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) => new Scope(this, snapshot);
        private sealed class Scope(Provider provider, ExtensionSessionSnapshot snapshot) : IExtensionSessionCatalogScope
        {
            public ExtensionSessionSnapshot Snapshot => snapshot; public CancellationToken SessionCancellationToken => default;
            public ValueTask<ExtensionSessionCatalogPage> ListAsync(ExtensionSessionCatalogQuery query, CancellationToken token)
            { provider.ListCalls++; provider.BeforeReturn?.Invoke(); return ValueTask.FromResult(provider.Page); }
            public ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string key, bool latest, string? leaf, CancellationToken token)
            { provider.LegacyResume++; return ValueTask.FromResult<IExtensionSessionCatalogScope?>(null); }
            public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token)
            { provider.LegacySwitch++; return ValueTask.FromResult<IExtensionSessionActionScope?>(null); }
            public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request, CancellationToken token) => throw new NotSupportedException();
            public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int version, JsonData data, CancellationToken token)
            { provider.Writes++; return ValueTask.FromResult(new ExtensionSessionEntryAcknowledgment("source", 1, data, 1, 0, 1, null)); }
            public ValueTask DisposeAsync() { provider.Closed++; return ValueTask.CompletedTask; }
        }
    }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
