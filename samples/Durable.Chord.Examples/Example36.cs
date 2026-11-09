// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/36-durable-views.ts
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;
using ChordJson = System.Text.Json.Nodes.JsonValue;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.Durable.Chord.Examples;

public sealed record ViewsExampleResult(JsonNode Summary, int Frames, int Computes, int Detached);

/// <summary>An actual host-owned Durable session with two followed views and one derived public member.</summary>
public sealed class ViewsApplication
{
    public static readonly Contract Chat = Contract.Local("example.views", new()
    {
        Version = 1, Params = new(), Members = new()
        {
            ["summary"] = Obj(("kinds", Arr(Str())), ("tasks", Int())), ["write"] = Method(Str()),
            ["writeAndTask"] = Method(Str()),
        },
    });
    public int Computes { get; private set; }
    public int Detached { get; private set; }
    public IHarness? Harness { get; private set; }
    public Facet Facet(ChordStore store) => PiSharp.Chord.Host.Facet.Define((host, _) => host.Serve(Chat, (_, _, self) =>
    {
        var harness = self.Open(async ctx => Harness = await ChordExamples.OpenHarnessAsync(store, Chat.Name, Registry.Create(), ctx),
            (h, _, ctx) => h.CloseAsync(ViewBridge.Context(ctx)));
        var view = self.Follow((observer, ctx) => ViewBridge.ConversationAsync(harness.Value, observer, ctx, () => Detached++));
        var graph = self.Follow((observer, ctx) => ViewBridge.GraphAsync(harness.Value, observer, ctx, () => Detached++));
        var summary = self.Derive([view, graph], values =>
        {
            Computes++;
            return new JsonObject { ["kinds"] = ((JsonNode)values[0]!).DeepClone(), ["tasks"] = ((JsonNode)values[1]!).DeepClone() };
        });
        return new(new Dictionary<string, object?>
        {
            ["summary"] = summary,
            ["write"] = (HostMethod)(async (args, ctx) =>
            {
                await ChordExamples.WriteAsync(harness.Value, ((JsonNode)args!).GetValue<string>(), ViewBridge.Context(ctx.Context));
                return null;
            }),
            // Exercises a single commit which changes both followed views, without scheduling the deliberately blocked task.
            ["writeAndTask"] = (HostMethod)(async (args, ctx) =>
            {
                var durable = ViewBridge.Context(ctx.Context);
                var root = await harness.Value.ConversationAsync(ConversationId.Root, durable);
                await root!.CommitAsync(async tx =>
                {
                    await tx.AppendEntryAsync(ConversationId.Root, new EntryDraft { Kind = ((JsonNode)args!).GetValue<string>() });
                    await tx.CreateTaskAsync(RegistryApplication.Greet, DurableJson.Null, new TaskOptions(TaskOwnership.Conversation));
                }, durable);
                return null;
            }),
        });
    }));

}

public static partial class ChordExamples
{
    public static async Task<ViewsExampleResult> ViewsAsync(TextWriter output)
    {
        await using var store = await OpenStoreAsync();
        var app = new ViewsApplication();
        await using var host = Hosts.Create(new() { Store = store, OnError = e => output.WriteLine($"onError: {e.Kind}") });
        await host.LoadAsync([new("@example/core", app.Facet(store))], Context);
        using var chat = await host.GetAsync(ViewsApplication.Chat, new(), Context);
        var frames = 0;
        var watch = await chat.Member("summary").WatchAsync(Context);
        watch.Start((call, _) =>
        {
            if (call is PiSharp.Chord.WatchValueCall<JsonNode?> value)
            {
                Interlocked.Increment(ref frames);
                output.WriteLine($"36 frame: {value.Value!.ToJsonString()}");
            }
            return Task.CompletedTask;
        });
        var computes = app.Computes;
        await chat.CallAsync("write", ChordJson.Create("example.first"), Context);
        await EventuallyAsync(() => Volatile.Read(ref frames) >= 1, "first view frame");
        await chat.CallAsync("write", ChordJson.Create("example.second"), Context);
        await EventuallyAsync(() => Volatile.Read(ref frames) >= 2, "second view frame");
        var value = chat.Member("summary").Value!.DeepClone();
        await watch.StopAsync();
        await host.CloseAsync(Context);
        await output.WriteLineAsync($"36 member value: {value.ToJsonString()}; detached: {app.Detached}");
        return new(value, frames, app.Computes - computes, app.Detached);
    }
}
