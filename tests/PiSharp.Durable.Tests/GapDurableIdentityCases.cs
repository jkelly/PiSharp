using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

#pragma warning disable PISHARP_IDENTITY001
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> GapDurableIdentityCases()
    {
        yield return Case("gap.durable.identity-wrapper-all-operations", async () =>
        {
            var recorder = new GapIdentityRecorder();
            var models = DurableModelIdentity.WithClientIdentity(
                DurableModelIdentity.WithClientIdentity(recorder, new() { Version = "inner", AttributionOff = true }),
                new() { Version = "outer" });
            var model = new DurableModel { Id = "m", Name = "m", Api = "fake", Provider = "fake" };
            var handle = new DurableDeferredHandle(JsonObjectValue.Empty);
            var options = new DurableRequestOptions { Identity = new() { Version = "request" } };
            foreach (var explicitOptions in new[] { options, null })
            {
                await foreach (var _ in models.StreamDeferred(model, handle, explicitOptions, CancellationToken.None)) { }
                await models.FetchDeferredAsync(model, handle, explicitOptions, CancellationToken.None);
                await models.CancelDeferredAsync(model, handle, explicitOptions, CancellationToken.None);
                await models.ClassifyAsync(JsonObjectValue.Empty, JsonObjectValue.Empty, explicitOptions, CancellationToken.None);
                await models.GenerateImagesAsync(JsonObjectValue.Empty, JsonObjectValue.Empty, explicitOptions, CancellationToken.None);
            }
            await foreach (var _ in models.StreamDeferred(model, handle, CancellationToken.None)) { }
            await models.FetchDeferredAsync(model, handle, CancellationToken.None);
            await models.CancelDeferredAsync(model, handle, CancellationToken.None);
            await models.ClassifyAsync(JsonObjectValue.Empty, JsonObjectValue.Empty, CancellationToken.None);
            await models.GenerateImagesAsync(JsonObjectValue.Empty, JsonObjectValue.Empty, CancellationToken.None);
            Equal(15, recorder.Identities.Count, "every operation reached the options overload");
            for (var i = 0; i < recorder.Identities.Count; i++)
            {
                Equal(i < 5 ? "request" : "outer", recorder.Identities[i]?.Version, "latest non-null field wins");
                Equal(true, recorder.Identities[i]?.AttributionOff, "earlier field survives merge");
            }
        });
    }

    private sealed class GapIdentityRecorder : IDurableModels
    {
        internal List<ClientIdentity?> Identities { get; } = [];
        private static AssistantMessage Answer => new("fake", "fake", "m", 0, [], TokenUsage.Zero, StopReason.Stop);
        private static async IAsyncEnumerable<StreamEvent> Empty() { await Task.CompletedTask; yield break; }
        public DurableModel? GetModel(string provider, string id) => null;
        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options, CancellationToken token) => Empty();
        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options, CancellationToken token) => Task.FromResult(Answer);
        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken token) => throw new InvalidOperationException("legacy overload dropped identity");
        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken token) => throw new InvalidOperationException("legacy overload dropped identity");
        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken token) => throw new InvalidOperationException("legacy overload dropped identity");
        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options, CancellationToken token)
        { Identities.Add(options?.Identity); return Empty(); }
        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options, CancellationToken token)
        { Identities.Add(options?.Identity); return Task.FromResult(Answer); }
        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, DurableRequestOptions? options, CancellationToken token)
        { Identities.Add(options?.Identity); return Task.CompletedTask; }
        public Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, DurableRequestOptions? options, CancellationToken token)
        { Identities.Add(options?.Identity); return Task.FromResult(JsonObjectValue.Empty); }
        public Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, DurableRequestOptions? options, CancellationToken token)
        { Identities.Add(options?.Identity); return Task.FromResult(JsonObjectValue.Empty); }
    }
}
