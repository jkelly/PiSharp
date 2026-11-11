// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/app.ts.
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Surfaces;
using PiSharp.Chord.Store;
using PiSharp.Chord.TelemetryIngest;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Hosting.App;

public sealed record AppListen(IPAddress Address, int Port = 0);
public sealed record AppTimeouts(TimeSpan? Close = null, TimeSpan? Ready = null, TimeSpan? ReloadDrain = null);
public sealed record AppLimits(int EphemeralSessions = 1000, int PendingPairings = 100, int PairingsPerAddress = 10,
    int Uploads = 64, long ImportBytes = 4L * 1024 * 1024 * 1024, int ImportEntries = 100000);
public sealed record AppPrincipalsOptions(JsonObject Server, JsonObject Owner, Func<Address, JsonObject>? Worker = null);
public sealed record AppError(string Kind, Exception? Error = null, string? Base = null, object? Details = null);

/// <summary>A native worker entry, frozen with its deployment dependencies before its first process starts.</summary>
public sealed class AppWorkerKind
{
    public required HostKind Kind { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? ApplicationDirectory { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public string Facet { get; init; } = "worker";
    public required Func<JsonObject, Address, bool> Authorize { get; init; }
    public Func<Address, ChordContext, Task<JsonNode?>> Start { get; init; } = (_, _) => Task.FromResult<JsonNode?>(null);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Services available while the application's explicitly selected core facets are assembled.</summary>
public sealed record AppServices(ChordHost Host, IStore Store, AppLayout Layout, Sessions Sessions);

public sealed class AppOptions
{
    public required string Name { get; init; }
    public string? DataDir { get; init; }
    public string? Root { get; init; }
    public required AppApi Api { get; init; }
    public IReadOnlyDictionary<string, PluginKindDescriptor> Kinds { get; init; } = new Dictionary<string, PluginKindDescriptor>
    { ["server"] = new([]) };
    public required AppPrincipalsOptions Principal { get; init; }
    public AppListen Listen { get; init; } = new(IPAddress.Loopback);
    public IReadOnlyList<AppWorkerKind> Workers { get; init; } = [];
    public FilesCaps Files { get; init; } = new();
    public AppLimits Limits { get; init; } = new();
    public AppTimeouts Timeouts { get; init; } = new();
    public string DotnetPath { get; init; } = "dotnet";
    public bool RestorePlugins { get; init; }
    public string? PackageDirectory { get; init; }
    public TimeSpan PluginBuildTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public IReadOnlyList<string> Base { get; init; } = [];
    public IReadOnlyList<SurfaceBuild> Surfaces { get; init; } = [];
    /// <summary>Selected optional facets, including users, invites and ephemeral login. None are enabled implicitly.</summary>
    public Func<AppServices, IReadOnlyList<PluginFacet>>? CoreFacets { get; init; }
    public ITelemetryContext? Telemetry { get; init; }
    /// <summary>Optional separate ingest target; defaults to Telemetry when it also implements the ingest interface.</summary>
    public ITelemetryIngestTarget? TelemetryTarget { get; init; }
    public Func<JsonObject, SpanAttributes>? PrincipalAttributes { get; init; }
    public Action<WebApplicationBuilder>? ConfigureHttp { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public required Action<AppError> OnError { get; init; }
}

public sealed class WorkerAppOptions
{
    public required HostKind Kind { get; init; }
    public string Facet { get; init; } = "worker";
    public required AppApi Api { get; init; }
    public IReadOnlyDictionary<string, PluginKindDescriptor> Kinds { get; init; } = new Dictionary<string, PluginKindDescriptor>();
    public IReadOnlyList<Assembly> SharedAssemblies { get; init; } = [];
    public Func<JsonObject, SpanAttributes>? PrincipalAttributes { get; init; }
    public Func<WorkerAppServices, IReadOnlyList<PluginFacet>>? CoreFacets { get; init; }
    public required Action<Exception> OnError { get; init; }
}
public sealed record WorkerAppServices(ChordHost Host, IStore Store, Address Address, string Directory, JsonNode? Start);
