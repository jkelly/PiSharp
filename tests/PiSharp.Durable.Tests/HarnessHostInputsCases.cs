// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-host-inputs.test.ts (7ec1f648e), ported in the Pi
// v1.5.0 sync (area harness-options). Verbatim apart from the adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - Upstream's `models` and `settings`, each a value or a function, are HarnessOptions.Models or CurrentModels, and Settings or
//   CurrentSettings.
// - A second Models with an extra "second-only" provider is AlsoServing, a decorator over the faux provider that also serves the faux models
//   of a second FauxModels. getProvider("second-only") is GetModel("second-only", "faux-1"): IDurableModels has no provider lookup.
// - "rejects a Models value and a function together" and the two cases after it are PiSharp-only: TypeScript's union type makes a value and
//   a function exclusive; two C# properties are checked at open.
// - `obj({})` is ToolsSchema150.EmptyObject (area tools-schema).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessHostInputsCases() => HostInputsTests.Cases();
}

/// <summary>The harness.host-inputs.* cases (upstream harness-host-inputs.test.ts).</summary>
internal static class HostInputsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Models whose only provider is a faux one answering every request with <paramref name="text"/>.</summary>
    private static FauxModels Answering(string text)
    {
        var faux = new FauxModels();
        faux.SetResponses([.. Enumerable.Range(0, 10).Select(_ => (FauxStep)FauxAssistantMessage(text))]);
        return faux;
    }

    private static async Task<(IHarness Harness, IConversation Root)> Open(IDurableModels? models = null, Func<IDurableModels>? currentModels = null,
        Func<HarnessSettings?>? currentSettings = null, IRegistry? registry = null)
    {
        var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
        {
            Models = models,
            CurrentModels = currentModels,
            CurrentSettings = currentSettings,
            Registry = registry ?? PiSharp.Durable.Registry.Create(),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        return (harness, root);
    }

    private static async Task<string?> Turn(IConversation root, string content)
    {
        var settled = await (await root.SubmitAsync(SubmissionDraft.Input(content), Context)).WaitAsync(Context);
        Equal(SubmissionStatus.Done, settled.Status, "status");
        var entries = await AllEntries(root);
        return TextOf(entries[^1].Model?[0]);
    }

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return Case("harness.host-inputs.generation-uses-the-current-return-value-of-a-models-function-at-each-request", async () =>
        {
            IDurableModels current = Answering("first");
            var (harness, root) = await Open(currentModels: () => current);
            Equal("first", await Turn(root, "one"), "first turn");
            current = Answering("second");
            Equal("second", await Turn(root, "two"), "second turn");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.host-inputs.still-accepts-a-plain-models-object", async () =>
        {
            var (harness, root) = await Open(models: Answering("plain"));
            Equal("plain", await Turn(root, "one"), "turn");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.host-inputs.tools-read-the-current-models-at-each-access-of-api-models", async () =>
        {
            var faux = new FauxModels();
            faux.SetResponses(
                FauxAssistantMessage([FauxToolCall("probe", JsonObjectValue.Empty, "call-1")], stopReason: "toolUse"),
                FauxAssistantMessage("done"));
            IDurableModels first = faux;
            // Marks the second Models.
            IDurableModels second = new AlsoServing(faux, new FauxModels(new FauxOptions { Provider = "second-only" }));
            var current = first;
            var seen = new SyncList<IDurableModels>();
            var registry = PiSharp.Durable.Registry.Create();
            registry.Install(Define.Extension(new Extension
            {
                Name = "probe",
                Tools =
                [
                    Define.Tool(new ToolRegistration
                    {
                        Name = "probe",
                        Description = "Records api.models before and after the host swaps it",
                        Parameters = ToolsSchema150.EmptyObject,
                        Execute = (_, api, _) =>
                        {
                            seen.Add(api.Models);
                            current = second;
                            seen.Add(api.Models);
                            return Task.FromResult(new ToolExecutionResult { Output = JsonArrayValue.Empty });
                        },
                    }),
                ],
            }));
            var (harness, root) = await Open(currentModels: () => current, registry: registry);
            Equal("done", await Turn(root, "go"), "turn");
            var models = seen.Snapshot();
            Equal(2, models.Count, "seen");
            Check(models[0].GetModel("second-only", "faux-1") is null, "the first access reads the first Models");
            Check(models[1].GetModel("second-only", "faux-1") is not null, "the second access reads the second Models");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.host-inputs.generation-reads-the-current-return-value-of-a-settings-function", async () =>
        {
            var timeouts = new SyncList<double?>();
            var faux = new FauxModels();
            var reply = FauxStep.From(request =>
            {
                timeouts.Add(request.Options?.TimeoutMs);
                return FauxAssistantMessage("ok");
            });
            faux.SetResponses(reply, reply);
            var settings = new HarnessSettings { Stream = new ConversationStreamOptions { TimeoutMs = 1000 } };
            var (harness, root) = await Open(models: faux, currentSettings: () => settings);
            await Turn(root, "one");
            settings = new HarnessSettings { Stream = new ConversationStreamOptions { TimeoutMs = 2000 } };
            await Turn(root, "two");
            Seq([1000.0, 2000.0], timeouts.Snapshot().Select(timeout => timeout ?? -1), "timeouts");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.host-inputs.rejects-a-models-value-and-a-function-together", async () =>
        {
            var faux = new FauxModels();
            await ThrowsAsync<ArgumentException>(() => Open(models: faux, currentModels: () => faux), "both");
            await ThrowsAsync<ArgumentException>(() => Open(), "neither");
        });

        yield return Case("harness.host-inputs.rejects-a-settings-value-and-a-function-together", async () =>
        {
            await ThrowsAsync<ArgumentException>(() => Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
            {
                Models = new FauxModels(),
                Registry = PiSharp.Durable.Registry.Create(),
                Settings = new HarnessSettings(),
                CurrentSettings = () => null,
            }, Context), "both");
        });

        yield return Case("harness.host-inputs.reads-no-settings-from-a-settings-function-that-returns-none", async () =>
        {
            var timeouts = new SyncList<double?>();
            var faux = new FauxModels();
            faux.SetResponses(FauxStep.From(request =>
            {
                timeouts.Add(request.Options?.TimeoutMs);
                return FauxAssistantMessage("ok");
            }));
            var (harness, root) = await Open(models: faux, currentSettings: () => null);
            Equal("ok", await Turn(root, "one"), "turn");
            Equal(null, timeouts.Snapshot().Single(), "no timeout");
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>Models serving <paramref name="inner"/>'s requests, whose lookup also finds <paramref name="extra"/>'s models (upstream's
    /// second <c>setProvider</c>).</summary>
    private sealed class AlsoServing(IDurableModels inner, IDurableModels extra) : IDurableModels
    {
        public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId) ?? extra.GetModel(provider, modelId);

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => inner.StreamSimple(model, messages, options, cancellationToken);

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, cancellationToken);
    }
}
