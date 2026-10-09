// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (setModel, cycleModel over
// modelRuntime.getAvailableSnapshot) and packages/coding-agent/src/core/model-runtime.ts (the available snapshot refreshed after
// credentials change). PiSharp binds each available model to the session runtime lazily: a model connects its live route on its
// first request.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Commands;

/// <summary>The models a live session can switch to: the run registry's available chat models (a usable credential and a live
/// route), published into the session runtime's model catalog and offered to RPC set_model/cycle_model/get_available_models.</summary>
internal sealed class LiveModelCatalog : IAsyncDisposable
{
    private readonly LiveSessionRuntime runtime;
    private readonly Func<ModelDescriptor, LiveSessionRuntime> runtimeFor;
    private readonly Func<SessionModelBinding, SessionModelBinding> bind;
    private readonly SessionRuntimeRegistry registry;
    private readonly ModelDescriptor primary;
    private readonly JsonData primaryWire;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly Dictionary<(string Provider, string Id), LazyModelTransport> transports = [];
    private ImmutableArray<RpcModelDefinition> available;
    private ModelRegistry? current;
    private bool disposed;

    internal LiveModelCatalog(LiveSessionRuntime runtime, Func<ModelDescriptor, LiveSessionRuntime> runtimeFor,
        Func<SessionModelBinding, SessionModelBinding> bind, SessionRuntimeRegistry registry, ModelDescriptor primary, JsonData primaryWire)
    {
        this.runtime = runtime; this.runtimeFor = runtimeFor; this.bind = bind; this.registry = registry;
        this.primary = primary; this.primaryWire = primaryWire;
        available = [new(primary, primaryWire)];
    }

    /// <summary>getAvailableSnapshot: the selectable models in the registry's order (the session's own model included).</summary>
    internal ImmutableArray<RpcModelDefinition> Available { get { lock (transports) return available; } }

    internal JsonData? Wire(ModelDescriptor model) => Available.FirstOrDefault(definition => definition.Model == model)?.WireBody;

    internal LazyModelTransport? Transport(ModelDescriptor model)
    { lock (transports) return transports.GetValueOrDefault((model.Provider, model.Id)) is { } found && found.Model == model ? found : null; }

    /// <summary>Re-reads the registry (models.json, auth.json, the environment) and binds the models that became available.</summary>
    internal async Task RefreshAsync(CancellationToken token = default)
    {
        await refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var models = await runtime.CreateModelRegistryAsync(token).ConfigureAwait(false);
            Volatile.Write(ref current, models);
            var definitions = new List<RpcModelDefinition>();
            var entries = new List<(RegistryModel Entry, ModelDescriptor Model)>();
            foreach (var entry in models.GetAvailable())
            {
                if (VirtualModels.IsVirtual(entry) || entry.Type != CatalogModelType.Chat || !LiveSessionSelection.SupportedApi(entry.Provider, entry.Api)) continue;
                FrozenCatalogModel definition;
                try { definition = entry.ToDefinition(); }
                catch (Exception error) when (error is CatalogReadException or ArgumentException or InvalidOperationException) { continue; }
                var descriptor = new ModelDescriptor(definition.Id, definition.DeclaredApi, definition.Provider);
                var wire = descriptor == primary ? primaryWire : WireOf(definition);
                if (wire is null || definitions.Any(existing => existing.Model.Provider == descriptor.Provider && existing.Model.Id == descriptor.Id)) continue;
                definitions.Add(new(descriptor, wire));
                if (descriptor != primary) entries.Add((entry, descriptor));
            }
            if (!definitions.Any(definition => definition.Model == primary)) definitions.Insert(0, new(primary, primaryWire));
            var catalog = registry.CaptureModelCatalog();
            var bound = catalog.Bindings.Select(binding => (binding.Model.Provider, binding.Model.Id)).ToHashSet();
            var additions = new List<SessionModelBinding>();
            foreach (var (entry, descriptor) in entries)
            {
                if (bound.Contains((descriptor.Provider, descriptor.Id))) continue;
                var transport = new LazyModelTransport(entry, descriptor, () => Volatile.Read(ref current) ?? models, runtimeFor(descriptor));
                lock (transports) transports[(descriptor.Provider, descriptor.Id)] = transport;
                additions.Add(bind(new(descriptor, transport, ExecutionMode: ToolExecutionMode.Sequential)));
            }
            if (additions.Count > 0) registry.PublishModelCatalog(catalog.Revision, [.. catalog.Bindings, .. additions], token);
            // Only bound models are offered (a binding another publisher withdrew is no longer selectable).
            var selectable = registry.CaptureModelCatalog().Bindings.Select(binding => binding.Model).ToHashSet();
            lock (transports) available = [.. definitions.Where(definition => selectable.Contains(definition.Model))];
        }
        finally { refresh.Release(); }
    }

    /// <summary>The RPC model definition of a catalog row (its JSON without <c>type</c>), when it has the fields RPC models carry.</summary>
    private static JsonData? WireOf(FrozenCatalogModel definition)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in definition.Raw.Value.EnumerateObject())
                if (property.Name != "type") property.WriteTo(writer);
            writer.WriteEndObject();
        }
        var wire = JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
        var body = wire.Value;
        bool Text(string name) => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;
        static bool Number(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number;
        return Text("id") && Text("api") && Text("provider") && Text("name") && Text("baseUrl") &&
            body.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            body.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array && input.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) &&
            Number(body, "contextWindow") && Number(body, "maxTokens") && body.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
            Number(cost, "input") && Number(cost, "output") && Number(cost, "cacheRead") && Number(cost, "cacheWrite") ? wire : null;
    }

    public async ValueTask DisposeAsync()
    {
        LazyModelTransport[] owned;
        await refresh.WaitAsync().ConfigureAwait(false);
        try { disposed = true; lock (transports) { owned = [.. transports.Values]; transports.Clear(); } }
        finally { refresh.Release(); }
        var failures = new List<Exception>();
        foreach (var transport in owned)
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Live model connections failed to close.", failures);
    }
}

/// <summary>A switchable model's session transport: its live route (<see cref="LiveSessionSelection.FromEntry"/>, Anthropic through
/// its resolved authentication) connects on the first request; thinking levels come from the catalog row.</summary>
internal sealed class LazyModelTransport(RegistryModel entry, ModelDescriptor model, Func<ModelRegistry> registry, LiveSessionRuntime runtime)
    : IChatTransport, IThinkingLevelTransport, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private LiveSessionConnection? connection;
    private IChatTransport? main;
    private bool disposed;

    internal ModelDescriptor Model { get; } = model;

    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor descriptor) =>
        descriptor == Model ? VirtualModels.SupportedThinkingLevels(entry) : throw new ArgumentException("Model is outside this binding.", nameof(descriptor));

    private async Task<LiveSessionConnection> ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is not null) return connection;
            var selection = LiveSessionSelection.FromEntry(registry().Find(entry.Provider, entry.Id) ?? entry, registry(), null, useModelMaximum: true);
            if (selection.Model.Provider == "anthropic" && selection.Model.Api == "anthropic-messages")
            {
                var (authentication, handler, reresolve) = await selection.ResolveAnthropicAsync(runtime, token).ConfigureAwait(false);
                connection = await selection.ConnectResolvedAnthropicAsync(authentication, handler, token, reresolve).ConfigureAwait(false);
            }
            else connection = selection.Connect(runtime);
            return connection;
        }
        finally { gate.Release(); }
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var live = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var transport = main ??= live.CreateTransport();
        await foreach (var observation in transport.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return observation;
    }

    /// <summary>The summarization route of this model (compaction, branch and bug report summaries) at <paramref name="maximum"/> tokens.</summary>
    internal IChatTransport Summary(int maximum) => new SummaryRoute(this, maximum);

    private sealed class SummaryRoute(LazyModelTransport owner, int maximum) : IChatTransport, IThinkingLevelTransport
    {
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor descriptor) => ["off"];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var live = await owner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var observation in live.CreateTransport(Math.Min(maximum, live.MaximumOutputTokens), summary: true)
                .StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return observation;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
