using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

public sealed record LegacyHostOptions
{
    public string Name { get; init; } = "host";
    public required object? Self { get; init; }
    public Func<object?, string>? PrincipalKey { get; init; }
    public bool Dev { get; init; }
    public Action<Exception>? OnError { get; init; }
    public string Kind { get; init; } = "host";
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TaskStopDeadline { get; init; } = TimeSpan.FromSeconds(5);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public IReadOnlyDictionary<string, TimeSpan> RemoteLingerTypes { get; init; } = new Dictionary<string, TimeSpan>();
    public int RemoteLingerCap { get; init; } = 4;
    public Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<LegacyFacet>>>? Loader { get; init; }
}
public sealed record LegacyGetOptions { public object? Principal { get; init; } public bool HasPrincipal { get; init; } public bool State { get; init; } = true; }
public sealed record LegacyRootOptions { public object? Principal { get; init; } public bool HasPrincipal { get; init; } public int? CodeVersion { get; init; } public CancellationToken CancellationToken { get; init; } }
public sealed record LegacyLoadOptions { public bool AwaitHostObjects { get; init; } = true; }
public sealed record LegacyServeOptions
{
    public string Lifetime { get; init; } = "referenced";
    public TimeSpan? Linger { get; init; }
    public IReadOnlyDictionary<int, LegacyAdapter>? Accepts { get; init; }
}
public sealed record LegacyStateOptions { public int Version { get; init; } = 1; public Func<JsonNode?, int, JsonNode?>? Migrate { get; init; } }
public sealed record LegacyResourceDeclaration<T>
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<object> Dependencies { get; init; } = [];
    public bool Exclusive { get; init; }
    public required Func<CancellationToken, Task<T>> Open { get; init; }
    public Func<T, Task>? Close { get; init; }
}
public sealed record LegacyReloadOptions
{
    public IReadOnlyList<string> Remove { get; init; } = [];
    public TimeSpan? GateDeadline { get; init; }
    public TimeSpan? RetireDeadline { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Remote { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? RemoteRemove { get; init; }
    public TimeSpan? VoteDeadline { get; init; }
}
public sealed record LegacyReloadReport
{
    public int CodeVersion { get; init; }
    public int Previous { get; init; }
    public IReadOnlyList<string> Changed { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    public IReadOnlyList<string> Prepared { get; init; } = [];
    public IReadOnlyList<string> Gated { get; init; } = [];
    public IReadOnlyDictionary<string, LegacyReloadReport>? Children { get; init; }
}
public sealed record LegacyShape(string? Variant, IReadOnlyList<string> Members, string Contract);
public sealed record LegacyStateEvent(string Type, JsonNode? Value, IReadOnlyList<Delta.Op>? Ops = null);
public interface ILegacyReadable
{
    JsonNode? Value { get; }
    IDisposable Subscribe(Action<JsonNode?> listener);
    IDisposable Events(Action<LegacyStateEvent> listener);
}
/// <summary>A native event source used by a named legacy listener.</summary>
public interface ILegacySubscribable<T> { IDisposable Subscribe(Action<T> listener); }
public delegate Task<object?> LegacyMethod(JsonNode? args, LegacyContext context);
/// <summary>An in-process local method accepting native CLR values (including delegates). It cannot cross a link.</summary>
public delegate Task<object?> LegacyNativeMethod(object? args, LegacyContext context);
public delegate LegacyImplementation LegacyFactory(JsonNode? input, object? principal, LegacySelf self);
public sealed class LegacyImplementation
{
    public LegacyImplementation(IReadOnlyDictionary<string, object?> members, string? variant = null) { Members = new Dictionary<string, object?>(members, StringComparer.Ordinal); Variant = variant; }
    public IReadOnlyDictionary<string, object?> Members { get; }
    public string? Variant { get; }
}
public sealed record LegacyFacet(string Name, Action<LegacyFacetApi> Setup, IReadOnlyList<LegacyContract>? Uses = null);
public sealed class LegacyFacetApi
{
    private readonly Action<LegacyContract, LegacyFactory, LegacyServeOptions?> _serve;
    internal LegacyFacetApi(LegacyHost host, Action<LegacyContract, LegacyFactory, LegacyServeOptions?> serve, CancellationToken cancellationToken) { Host = host; _serve = serve; CancellationToken = cancellationToken; }
    internal LegacyHost Host { get; }
    public string Name => Host.Name;
    public CancellationToken CancellationToken { get; }
    public void Serve(LegacyContract contract, LegacyFactory factory, LegacyServeOptions? options = null) => _serve(contract, factory, options);
    public Task<T> RootAsync<T>(Func<LegacyContext, Task<T>> work, LegacyRootOptions? options = null) => Host.RootAsync(work, options);
    public LegacyRegistryView<T> Registry<T>(LegacyRegistry<T> registry) => Host.Registry(registry);
}
public sealed record LegacyRegistry<T>(string Name, string Conflict = "error")
{
    public static LegacyRegistry<T> Define(string name, string conflict = "error")
    { ArgumentException.ThrowIfNullOrEmpty(name); if (conflict is not ("error" or "last")) throw new ArgumentException("registry conflict must be error or last"); return new(name, conflict); }
}
public sealed class LegacyRegistryView<T>
{
    private readonly LegacyHost _host; private readonly LegacyRegistry<T> _registry;
    internal LegacyRegistryView(LegacyHost host, LegacyRegistry<T> registry) { _host = host; _registry = registry; }
    public IReadOnlyDictionary<string, T> Table(LegacyContext? context = null) => _host.ReadRegistry<T>(_registry.Name, context);
    public T? Get(string key, LegacyContext? context = null) => Table(context).TryGetValue(key, out var value) ? value : default;
    public IDisposable Subscribe(Action<IReadOnlyDictionary<string, T>, int> listener) => _host.SubscribeRegistry(_registry.Name, () => listener(Table(), _host.CodeVersion));
}
internal sealed class LegacySubscription(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
internal sealed record LegacyService(LegacyContract Contract, LegacyFactory Factory, LegacyServeOptions Options, string Facet);
internal sealed class LegacyGeneration(int id, Dictionary<string, LegacyService> services)
{
    internal int Id { get; } = id;
    internal Dictionary<string, LegacyService> Services { get; } = services;
    internal Dictionary<string, Dictionary<string, object?>> Tables { get; set; } = [];
    internal CancellationTokenSource Abort { get; } = new();
    internal HashSet<Task> Work { get; } = [];
    internal int Roots;
}
