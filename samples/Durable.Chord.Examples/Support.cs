// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/35-37.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Store;
using PiSharp.Durable.Store;
using ChordContext = PiSharp.Chord.ChordContext;
using DurableJson = PiSharp.Durable.Chord.JsonValue;
using ChordJson = System.Text.Json.Nodes.JsonValue;
using DurableWatchEndKind = PiSharp.Durable.Chord.WatchEndKind;

namespace PiSharp.Durable.Chord.Examples;

/// <summary>The C# boundary between Durable's typed views and Chord's JSON members.</summary>
public static class ViewBridge
{
    public static DurableContext Context(ChordContext context) =>
        DurableContext.Background.WithTelemetry(context.Telemetry).WithCancellation(context.AbortSignal ?? CancellationToken.None);

    public static async Task<FollowAttachment?> ConversationAsync(IHarness harness, IFollowObserver observer, ChordContext context,
        Action? detached = null)
    {
        var subscription = await harness.ObserveConversationAsync(ConversationId.Root,
            Observer<ConversationView>(observer, Kinds, context), Context(context), new ConversationWindow { Min = 50 });
        return new(Kinds(subscription.Value), () => { subscription.Detach(); detached?.Invoke(); });
    }

    public static async Task<FollowAttachment?> GraphAsync(IHarness harness, IFollowObserver observer, ChordContext context,
        Action? detached = null)
    {
        var subscription = await harness.ObserveTaskGraphAsync(Observer<TaskGraph>(observer, Count, context), Context(context));
        return new(Count(subscription.Value), () => { subscription.Detach(); detached?.Invoke(); });
    }

    private static Observer<T> Observer<T>(IFollowObserver observer, Func<T, JsonNode> project, ChordContext context) => new(
        (call, _) =>
        {
            if (call is PiSharp.Durable.Chord.WatchValueCall<T> changed)
            {
                var value = project(changed.Value);
                observer.Advance(new PiSharp.Chord.WatchValueCall<JsonNode?>(value,
                    [new PiSharp.Chord.Delta.ReplaceOp(value)], changed.Retired), context);
            }
        },
        end => observer.End(end.Kind switch
        {
            DurableWatchEndKind.Failed => PiSharp.Chord.WatchEnd.Failed(end.Error),
            DurableWatchEndKind.ListenerError => PiSharp.Chord.WatchEnd.ListenerError((Exception)end.Error!),
            DurableWatchEndKind.Aborted => PiSharp.Chord.WatchEnd.Aborted,
            DurableWatchEndKind.Stopped => PiSharp.Chord.WatchEnd.Stopped,
            _ => PiSharp.Chord.WatchEnd.Ended,
        }));

    private static JsonNode Kinds(ConversationView view) => new JsonArray(view.Entries.Select(entry => (JsonNode?)ChordJson.Create(entry.Kind)).ToArray());
    private static JsonNode Count(TaskGraph graph) => ChordJson.Create(graph.Tasks.Count)!;
}

public static partial class ChordExamples
{
    public static readonly ChordContext Context = ChordContext.Background;
    public static readonly DurableContext DurableContext = global::PiSharp.Durable.Chord.DurableContext.Background;

    public static async Task<ChordStore> OpenStoreAsync(string path = ":memory:") =>
        (ChordStore)await Tables.OpenStoreAsync(await StoreBackends.OpenSqliteAsync(path), Context);

    public static async Task<IHarness> OpenHarnessAsync(IStore store, string owner, IRegistryReader registry, ChordContext context)
    {
        var durable = ViewBridge.Context(context);
        var storage = await StoreStorage.OpenAsync(store, owner, durable);
        IHarness? harness = null;
        try
        {
            harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, durable);
            await harness.RootAsync(durable);
            return harness;
        }
        catch
        {
            if (harness is null) await storage.CloseAsync(DurableContext);
            else await harness.CloseAsync(DurableContext);
            throw;
        }
    }

    public static INative<IHarness> OpenHarness(ISelf self, IStore store, string owner, IRegistryReader registry) =>
        self.Open(context => OpenHarnessAsync(store, owner, registry, context),
            (harness, _, context) => harness.CloseAsync(ViewBridge.Context(context)));

    public static async Task WriteAsync(IHarness harness, string kind, DurableContext context, string? text = null)
    {
        var root = await harness.ConversationAsync(ConversationId.Root, context);
        await root!.SubmitAsync(SubmissionDraft.Write(new EntryDraft
        {
            Kind = kind,
            Data = text is null ? null : DurableJson.Parse(new JsonObject { ["text"] = text }.ToJsonString()),
        }), context);
    }

    public static async Task EventuallyAsync(Func<bool> condition, string description)
    {
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= stop) throw new TimeoutException(description);
            await Task.Delay(10);
        }
    }
}
