// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/37-harness-registry.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using PiSharp.Chord.Store;
using PiSharp.Durable.Store;
using static PiSharp.Chord.Contracts.Schemas;
using ChordRegistry = PiSharp.Chord.Registries.Registry;
using ChordJson = System.Text.Json.Nodes.JsonValue;
using DurableRegistry = PiSharp.Durable.Registry;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.Durable.Chord.Examples;

public sealed record RegistryExampleResult(string Greeting, string FailedState, string RecoveredState,
    IReadOnlyList<string> Kinds, int Conflicts, int Opens, int Closes);

public sealed class RegistryApplication
{
    public static readonly Registry<Extension, IRegistrySnapshot> Extensions = ChordRegistry.Define<Extension, IRegistrySnapshot>("example.extensions", new() { Version = 1 });
    public static readonly Registry<INative<IHarness>, IHarness?> Harnesses = ChordRegistry.Define<INative<IHarness>, IHarness?>("example.harnesses", new() { Version = 1 });
    public static readonly Contract Core = Contract.Local("example.harness", new()
    {
        Version = 1, Params = new(), Members = new() { ["greet"] = Method(null, Str()), ["close"] = Method() },
    });
    public static readonly Contract Transcript = Contract.Local("example.transcript", new()
    {
        Version = 1, Params = new(), Members = new() { ["kinds"] = Arr(Str()) },
    });
    public static readonly DurableTask Greet = Tasks.DefineTask(new()
    {
        Name = "example.greet", Version = 1, Initial = _ => (JsonObjectValue)DurableJson.Parse("{\"phase\":\"run\"}"),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (_, runtime, context) => await runtime.CommitAsync(async (tx, _) =>
            {
                await tx.AppendEntryAsync(ConversationId.Root, new EntryDraft { Kind = "example.greeted" });
                return NextTaskState.Completed(DurableJson.String("hello"));
            }, context),
        },
        Abort = (_, _, _) => Task.CompletedTask,
    });

    public int Opens { get; private set; }
    public int Closes { get; private set; }
    public int Conflicts { get; private set; }
    public IHarness? Harness { get; private set; }

    public Facet CoreFacet(ChordStore store) => Facet.Define((host, _) =>
    {
        host.Serve(Extensions, new() { Compose = (table, compose) =>
        {
            var registry = DurableRegistry.Create();
            foreach (var entry in table)
            {
                try { registry.Install(entry.Value); }
                catch (Exception error) { Conflicts++; compose.Report(new RegistryConflict(error.Message, [entry.Plugin])); }
            }
            return registry.Snapshot();
        } });
        host.Serve(Harnesses, new() { Compose = (table, _) => table.LastOrDefault()?.Value.Value });
        host.Serve(Core, (_, _, self) =>
        {
            var extensions = self.Registry(Extensions);
            var harness = self.Open(async ctx =>
            {
                var opened = await ChordExamples.OpenHarnessAsync(store, Core.Name, RegistryReader.Create(extensions, ctx), ctx);
                Harness = opened; Opens++;
                return opened;
            }, async (h, _, ctx) => { await h.CloseAsync(ViewBridge.Context(ctx)); Closes++; });
            ((IStoreSelf)self).Contribute(Harnesses, harness);
            self.Follow((observer, ctx) => ViewBridge.GraphAsync(harness.Value, observer, ctx));
            return new(new Dictionary<string, object?>
            {
                ["greet"] = (HostMethod)(async (_, ctx) =>
                {
                    if (extensions.Value.GetTask(Greet.Definition.Name) is null) return ChordJson.Create("no greeter installed");
                    var durable = ViewBridge.Context(ctx.Context);
                    var root = await harness.Value.ConversationAsync(ConversationId.Root, durable);
                    var id = await root!.CommitAsync(tx => tx.CreateTaskAsync(Greet, DurableJson.Null, new TaskOptions(TaskOwnership.Conversation)), durable);
                    harness.Value.Resume();
                    var done = await harness.Value.WaitForTaskAsync(id, durable);
                    return ChordJson.Create(((SettlingTaskState)done.State).Outcome.ToJson().ToString());
                }),
                ["close"] = (HostMethod)(async (_, ctx) => { await harness.Value.CloseAsync(ViewBridge.Context(ctx.Context)); return null; }),
            });
        });
        host.Serve(Transcript, (_, _, self) =>
        {
            var harness = self.Registry(Harnesses);
            self.RetryOn(harness);
            var view = self.Follow((observer, ctx) => harness.Value is { } value
                ? ViewBridge.ConversationAsync(value, observer, ctx)
                : Task.FromResult<FollowAttachment?>(null));
            return new(new Dictionary<string, object?> { ["kinds"] = view });
        });
    });

    public static PluginFacet Greeter(string plugin = "@example/greeter", string extension = "greeter") =>
        new(plugin, Facet.Define((_, self) => self.Contribute(Extensions, Define.Extension(new Extension { Name = extension, Tasks = [Greet] }))));
}

public static partial class ChordExamples
{
    public static async Task<RegistryExampleResult> RegistryAsync(TextWriter output)
    {
        await using var store = await OpenStoreAsync();
        var app = new RegistryApplication();
        await using var host = Hosts.Create(new() { Store = store, OnError = e => output.WriteLine($"onError: {e.Kind}") });
        await host.LoadAsync([new("@example/core", app.CoreFacet(store)), RegistryApplication.Greeter(),
            RegistryApplication.Greeter("@example/copycat", "copycat")], Context);
        using var core = await host.GetAsync(RegistryApplication.Core, new(), Context);
        using var transcript = await host.GetAsync(RegistryApplication.Transcript, new(), Context);
        var greeting = ((JsonNode)(await core.CallAsync("greet", null, Context))!).GetValue<string>();
        await EventuallyAsync(() => transcript.Member("kinds").Value!.AsArray().Any(n => n!.GetValue<string>() == "example.greeted"), "transcript sees task entry");
        await output.WriteLineAsync($"37 greet: {greeting}");
        try { await core.CallAsync("close", null, Context); }
        catch (Exception error) when (error is OperationCanceledException || error.Message.Contains("session closed", StringComparison.Ordinal))
        { await output.WriteLineAsync($"37 close: {error.Message}"); }
        await EventuallyAsync(() => transcript.State.Value.Kind == "failed", "follower fails with closed session");
        var failed = transcript.State.Value.Kind;
        core.Retry();
        await EventuallyAsync(() => core.State.Value.Kind == "live" && transcript.State.Value.Kind == "live" && app.Opens == 2, "retry opens persisted session and reattaches transcript");
        var recovered = transcript.State.Value.Kind;
        var kinds = transcript.Member("kinds").Value!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        await output.WriteLineAsync($"37 transcript after close/retry: {failed} -> {recovered}; [{string.Join(", ", kinds)}]");
        await host.CloseAsync(Context);
        return new(greeting, failed, recovered, kinds, app.Conflicts, app.Opens, app.Closes);
    }
}
