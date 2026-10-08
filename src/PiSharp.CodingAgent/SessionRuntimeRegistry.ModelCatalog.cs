using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

/// <summary>Actual borrowed model/transport bindings, not advertised metadata or credentials.</summary>
public sealed record SessionModelCatalogSnapshot(long Revision, ImmutableArray<SessionModelBinding> Bindings);
public sealed record SessionModelCatalogPublication(long PreviousRevision, SessionModelCatalogSnapshot Current);

public sealed partial class SessionRuntimeRegistry
{
    private readonly ModelCatalogStore _modelCatalog;
    private sealed record ModelCatalog(long Revision, ImmutableArray<SessionModelBinding> Bindings,
        ImmutableDictionary<(string Provider, string Id), SessionModelBinding> Models,
        ImmutableDictionary<(string Provider, string Id), ImmutableArray<string>> Thinking);
    private sealed class ModelCatalogStore(ModelCatalog initial)
    {
        private ModelCatalog current = initial;
        internal ModelCatalog Read() => Volatile.Read(ref current);
        internal bool Publish(ModelCatalog expected, ModelCatalog replacement) =>
            ReferenceEquals(Interlocked.CompareExchange(ref current, replacement, expected), expected);
    }
    public SessionModelCatalogSnapshot CaptureModelCatalog()
    {
        var snapshot = _modelCatalog.Read(); return new(snapshot.Revision, snapshot.Bindings);
    }
    /// <summary>Explicit admitted host publication. All bindings and thinking capabilities validate
    /// before one atomic compare/exchange; concurrent or stale publishers leave the catalog untouched.
    /// Existing captured selections keep their exact borrowed transport objects.</summary>
    public SessionModelCatalogPublication PublishModelCatalog(long expectedRevision,
        ImmutableArray<SessionModelBinding> admittedBindings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = _modelCatalog.Read();
        if (previous.Revision != expectedRevision) throw new InvalidOperationException("Model catalog revision changed.");
        var next = BuildModelCatalog(checked(expectedRevision + 1), admittedBindings, cancellationToken);
        var receipt = new SessionModelCatalogPublication(expectedRevision, new(next.Revision, next.Bindings));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_modelCatalog.Publish(previous, next)) throw new InvalidOperationException("Model catalog changed during binding admission.");
        return receipt;
    }
    private ModelCatalog BuildModelCatalog(long revision, ImmutableArray<SessionModelBinding> bindings, CancellationToken token)
    {
        if (bindings.IsDefaultOrEmpty) throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
        if (bindings.Length > _options.MaximumModels) throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
        var models = ImmutableDictionary.CreateBuilder<(string, string), SessionModelBinding>();
        var thinking = ImmutableDictionary.CreateBuilder<(string, string), ImmutableArray<string>>();
        long characters = 0;
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            if (binding?.Model is { } model && (long)(model.Provider?.Length ?? 0) + (model.Id?.Length ?? 0) + (model.Api?.Length ?? 0) > _options.MaximumCharacters - characters)
                throw Error(SessionRuntimeRegistryFailure.ResourceLimit);
            if (binding is null || binding.Model is null || binding.Transport is null || !Identity(binding.Model.Id) ||
                !Identity(binding.Model.Provider) || !Identity(binding.Model.Api) ||
                binding.ExecutionMode is not (ToolExecutionMode.Parallel or ToolExecutionMode.Sequential) ||
                !models.TryAdd((binding.Model.Provider, binding.Model.Id), binding))
                throw Error(SessionRuntimeRegistryFailure.InvalidRegistration);
            characters += (long)binding.Model.Provider.Length + binding.Model.Id.Length + binding.Model.Api.Length;
        }
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            try { thinking.Add((binding.Model.Provider, binding.Model.Id), ThinkingLevels.GetSupported(binding.Transport, binding.Model)); }
            catch (ArgumentException) { throw Error(SessionRuntimeRegistryFailure.InvalidRegistration); }
        }
        token.ThrowIfCancellationRequested(); return new(revision, bindings, models.ToImmutable(), thinking.ToImmutable());
    }
}
