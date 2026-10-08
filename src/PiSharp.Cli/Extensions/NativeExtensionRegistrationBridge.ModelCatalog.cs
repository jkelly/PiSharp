using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

public sealed partial class NativeExtensionRegistrationBridge
{
    private SessionRuntimeRegistry? configuredRuntime;
    private ImmutableArray<SessionModelBinding> configuredBaseline;
    private Func<SessionModelBinding, SessionModelBinding>? configuredPromptDecorator;
    /// <summary>Bind the actual application catalog once, before activation or at the application's
    /// existing runtime binding boundary. Already installed owners are reconciled immediately.
    /// This borrows a validated current baseline and never obtains executable bindings from disk.</summary>
    public void ConfigureModelCatalog(SessionRuntimeRegistry runtime, ImmutableArray<SessionModelBinding> admittedBaselineBindings,
        Func<SessionModelBinding, SessionModelBinding>? promptDecorator = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (promptDecorator?.GetInvocationList().Length > 1) throw new ArgumentException("One admitted prompt decorator required.");
        lock (gate)
        {
            Available();
            if (configuredRuntime is not null) throw new InvalidOperationException("Configure the model catalog once.");
            if (admittedBaselineBindings.IsDefaultOrEmpty || !runtime.CaptureModelCatalog().Bindings.SequenceEqual(admittedBaselineBindings))
                throw new ArgumentException("Exact current admitted runtime baseline required.");
            configuredBaseline = admittedBaselineBindings; configuredRuntime = runtime; configuredPromptDecorator = promptDecorator;
            try { if (installed.Count != 0) RefreshModelCatalog(); }
            catch { configuredRuntime = null; configuredBaseline = default; configuredPromptDecorator = null; throw; }
        }
    }
    public SessionModelCatalogPublication RefreshModelCatalog(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            Available();
            var runtime = configuredRuntime ?? throw new InvalidOperationException("Actual model catalog is not configured.");
            return PublishModelCatalog(runtime, runtime.CaptureModelCatalog().Revision, configuredBaseline, cancellationToken);
        }
    }
    private void RefreshConfiguredCatalog()
    { lock (gate) { if (configuredRuntime is not null) RefreshModelCatalog(); } }

    /// <summary>Loader retirement hook. Joins the exact native scope original, then reconciles the
    /// catalog even if cleanup faults. The original inventory and reconciliation fault are retained.</summary>
    public async Task RetireOwnerAsync(RegistrationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            Available();
            if (!installed.TryGetValue((scope.OwnerId, scope.OwnerGeneration), out var actual) || !ReferenceEquals(scope, actual))
                throw new InvalidOperationException("Exact installed native scope required.");
        }
        // Native DisposeAsync synchronously refuses same-owner callback retirement before effects.
        // Do not mistake that admission refusal for a completed close and withdraw live metadata.
        var admittedClose = scope.DisposeAsync();
        var original = admittedClose.AsTask(); Exception? primary = null;
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { primary = Preserve(original, error, CancellationToken.None, "native retirement"); }
        try { lock (gate) { installed.Remove((scope.OwnerId, scope.OwnerGeneration)); RefreshConfiguredCatalog(); } }
        catch (Exception error)
        {
            var refresh = Preserve(null, error, CancellationToken.None, "retirement catalog reconciliation");
            if (primary is not null) throw new AggregateException("Native retirement and model reconciliation failed.", primary, refresh);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(refresh).Throw(); throw;
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    /// <summary>Explicit admitted host installation/refresh. Called after genuine provider metadata
    /// commit and after replacement/unregister, with the application's retained original bindings.
    /// It preserves baseline providers which this host has not overridden and uses real leased transport.
    /// A stale publication refuses without changing the runtime; host reconciliation remains explicit.</summary>
    public SessionModelCatalogPublication PublishModelCatalog(SessionRuntimeRegistry runtime, long expectedRevision,
        ImmutableArray<SessionModelBinding> admittedBaselineBindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (admittedBaselineBindings.IsDefaultOrEmpty) throw new ArgumentException("Actual baseline model bindings required.");
        lock (gate)
        {
            Available(); cancellationToken.ThrowIfCancellationRequested();
            var rows = providers.CaptureModels();
            var names = rows.Select(row => row.Model.Provider).ToImmutableHashSet(StringComparer.Ordinal);
            var transport = new CatalogTransport(new(providers), rows);
            var models = admittedBaselineBindings.Where(binding => !names.Contains(binding.Model.Provider))
                .Concat(rows.Select(row => new SessionModelBinding(row.Model, transport))).ToImmutableArray();
            if (configuredPromptDecorator is { } decorate) models = models.Select(binding =>
            {
                var result = decorate(binding) ?? throw new InvalidOperationException("Prompt decorator returned no actual binding.");
                if (result.Model != binding.Model || !ReferenceEquals(result.Transport, binding.Transport) ||
                    result.ExecutionMode != binding.ExecutionMode || !ReferenceEquals(result.ToolHooks, binding.ToolHooks) ||
                    !ReferenceEquals(result.Hooks?.BeforePrompt, binding.Hooks?.BeforePrompt) ||
                    !ReferenceEquals(result.Hooks?.PrepareRequest, binding.Hooks?.PrepareRequest) ||
                    !ReferenceEquals(result.Hooks?.PrepareNextTurn, binding.Hooks?.PrepareNextTurn) ||
                    !ReferenceEquals(result.Hooks?.FinishTurn, binding.Hooks?.FinishTurn) ||
                    !ReferenceEquals(result.Hooks?.FinishTurnDecision, binding.Hooks?.FinishTurnDecision) ||
                    !ReferenceEquals(result.Hooks?.FinalTransformRequestMessages, binding.Hooks?.FinalTransformRequestMessages) ||
                    !ReferenceEquals(result.Hooks?.PrepareRequestBoundary, binding.Hooks?.PrepareRequestBoundary))
                    throw new InvalidOperationException("Prompt decorator changed admitted execution or provider hooks.");
                return result;
            }).ToImmutableArray();
            return runtime.PublishModelCatalog(expectedRevision, models, cancellationToken);
        }
    }
    /// <summary>Thinking admission belongs to the exact captured metadata table. Actual request
    /// dispatch still enters the existing current provider host's native owner/iterator lease.</summary>
    private sealed class CatalogTransport : IChatTransport, IThinkingLevelTransport
    {
        private readonly ExtensionProviderTransport actual;
        private readonly ImmutableDictionary<ModelDescriptor, ImmutableArray<string>> levels;
        internal CatalogTransport(ExtensionProviderTransport actual, ImmutableArray<ExtensionProviderModel> models)
        {
            this.actual = actual;
            levels = models.ToImmutableDictionary(row => row.Model, row => Supported(row.Metadata));
        }
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) =>
            levels.TryGetValue(model, out var admitted) ? admitted : throw new ArgumentException("Model is outside this captured provider catalog.");
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            if (!levels.ContainsKey(request.Model)) throw new ArgumentException("Model is outside this captured provider catalog.");
            return actual.StreamAsync(request, cancellationToken);
        }
        private static ImmutableArray<string> Supported(JsonData metadata)
        {
            var value = metadata.Value;
            if (!value.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind == JsonValueKind.False) return ["off"];
            if (reasoning.ValueKind != JsonValueKind.True) throw new ArgumentException("Invalid admitted reasoning metadata.");
            var map = value.TryGetProperty("thinkingLevelMap", out var supplied) ? supplied : default;
            if (map.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new ArgumentException("Invalid thinking map.");
            var supported = ThinkingLevels.Ordered.Where(level => {
                var suppliedLevel = map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out _);
                return (!suppliedLevel || map.GetProperty(level).ValueKind != JsonValueKind.Null) && (level is not ("xhigh" or "max") || suppliedLevel);
            }).ToImmutableArray();
            return supported.IsEmpty ? throw new ArgumentException("No admitted native thinking levels.") : supported;
        }
    }
}
