// Decision 0012: the models the research team runs on: scripted (FauxAgentModels and ScriptedResearch) without --provider, else a real
// provider through NativeDurableModels, counting the requests this process sends.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.CustomAgents.Testing;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Research;

/// <summary>The models, counting the requests this process sent.</summary>
internal sealed class ResearchModels(IDurableModels inner, ModelRef model, bool scripted) : IDurableModels, IDisposable
{
    private int requests;

    public ModelRef Model { get; } = model;
    public bool Scripted { get; } = scripted;
    public int Requests => Volatile.Read(ref requests);

    public static async Task<(ResearchModels? Models, int Exit)> CreateAsync(ResearchOptions options, TextWriter output, Func<string, string?> environment)
    {
        if (options.Provider is null)
        {
            var faux = new FauxAgentModels("scripted");
            ScriptedResearch.Configure(faux);
            return (new ResearchModels(faux, faux.Model(), scripted: true), 0);
        }
        var variables = NativeDurableModels.ApiKeyVariables(options.Provider);
        if (variables.Count > 0 && variables.All(name => string.IsNullOrEmpty(environment(name))))
        {
            await output.WriteLineAsync($"{string.Join(" or ", variables)} is not set.");
            return (null, 1);
        }
        var access = new NativeDurableModels(new NativeDurableModelsOptions { Environment = environment });
        var model = access.GetModel(options.Provider, options.Model!);
        if (model is null || !NativeDurableModels.Supports(model))
        {
            await output.WriteLineAsync(model is null
                ? $"Unknown model {options.Provider}/{options.Model}."
                : $"{options.Provider}/{options.Model} ({model.Api}) has no durable route yet.");
            access.Dispose();
            return (null, 2);
        }
        return (new ResearchModels(access, new ModelRef(options.Provider, options.Model!), scripted: false), 0);
    }

    public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        await foreach (var item in inner.StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false)) yield return item;
    }

    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        return inner.CompleteSimpleAsync(model, messages, options, cancellationToken);
    }

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.StreamDeferred(model, handle, cancellationToken);

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.FetchDeferredAsync(model, handle, cancellationToken);

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.CancelDeferredAsync(model, handle, cancellationToken);

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
