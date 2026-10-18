// PiSharp-only (decision 0008, PLAN.md Appendix B). The three event streams of a device, after durable-pi's streams
// (apps/dpi-backend/src/streams.ts, MIT). Every stream opens with `hello`. Device: `tree` (deduplicated, at most every 250 ms), `activity`
// (agents that started or finished, a finished one with a preview of its answer) and requested `doc` frames. Session: `session` once, then
// `tasks`, `agents` and `usage` when they change. Agent: `snapshot` with the inbox, then one `batch` of pi-durable events per commit
// (verbatim), `inbox` after a queue change, and requested `doc` frames. A stream is a viewer only: closing it never stops work.
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;

namespace PiSharp.Durable.Server;

internal static class DeviceStreams
{
    public static async Task<IResult> DeviceAsync(DeviceService service, HttpContext http)
    {
        var docs = ParseDocs(http.Request.Query["docs"].ToString(), DeviceService.DeviceDocKinds);
        var sse = await SseStream.OpenAsync(http, new JsonObject { ["kind"] = "device" }, service.Options).ConfigureAwait(false);
        await using (sse.ConfigureAwait(false))
        {
            DeviceTree? previous = null;
            var push = new Coalescer();
            async Task Push()
            {
                var tree = await service.TreeAsync(sse.Closed).ConfigureAwait(false);
                var items = Activity(previous, tree);
                previous = tree;
                sse.SendIfChanged("tree", new JsonObject { ["type"] = "tree", ["tree"] = DurableWire.ToNode(tree with { At = 0 }) });
                if (items.Count > 0)
                {
                    foreach (var item in items.Where(item => item["type"]?.GetValue<string>() == "agent.finished"))
                        if (await PreviewAsync(service, item["sessionId"]!.GetValue<string>(), item["agentId"]!.GetValue<long>(), sse.Closed).ConfigureAwait(false) is { } preview)
                            item["preview"] = preview;
                    sse.Send(new JsonObject { ["type"] = "activity", ["items"] = new JsonArray([.. items]) });
                }
                foreach (var kind in docs)
                {
                    var doc = service.AppState();
                    doc["type"] = "doc";
                    sse.SendIfChanged("doc:" + kind, doc);
                }
            }
            using var subscription = service.Host.SubscribeChanges(_ => push.Trigger());
            await Push().ConfigureAwait(false);
            await push.RunAsync(Push, service.Options.Coalesce, Report(service), sse.Closed).ConfigureAwait(false);
        }
        return Results.Empty;
    }

    public static async Task<IResult> SessionAsync(DeviceService service, HttpContext http, HostedSession session)
    {
        session.GetHarness();
        await using var feed = await session.WatchTaskGraphAsync().ConfigureAwait(false);
        var sse = await SseStream.OpenAsync(http, new JsonObject { ["kind"] = "session", ["sessionId"] = session.Id }, service.Options).ConfigureAwait(false);
        await using (sse.ConfigureAwait(false))
        {
            var tasks = await service.LiveTasksAsync(session, feed.Initial, sse.Closed).ConfigureAwait(false);
            var agents = (await session.AgentsAsync(sse.Closed).ConfigureAwait(false)).Select(DeviceService.Agent).ToList();
            var usage = await DeviceService.UsageAsync(session, sse.Closed).ConfigureAwait(false);
            sse.Send(new JsonObject
            {
                ["type"] = "session",
                ["agents"] = DurableWire.ToNode(agents),
                ["tasks"] = DurableWire.ToNode(tasks),
                ["usage"] = usage,
            });
            sse.SendIfChanged("agents", new JsonObject { ["type"] = "agents", ["agents"] = DurableWire.ToNode(agents) });
            sse.SendIfChanged("tasks", new JsonObject { ["type"] = "tasks", ["tasks"] = DurableWire.ToNode(tasks) });
            sse.SendIfChanged("usage", new JsonObject { ["type"] = "usage", ["usage"] = usage?.DeepClone() });
            var push = new Coalescer();
            async Task Push()
            {
                if (!session.IsAvailable) return;
                sse.SendIfChanged("agents", new JsonObject
                {
                    ["type"] = "agents",
                    ["agents"] = DurableWire.ToNode((await session.AgentsAsync(sse.Closed).ConfigureAwait(false)).Select(DeviceService.Agent).ToList()),
                });
                sse.SendIfChanged("usage", new JsonObject { ["type"] = "usage", ["usage"] = await DeviceService.UsageAsync(session, sse.Closed).ConfigureAwait(false) });
            }
            using var subscription = service.Host.SubscribeChanges(change =>
            {
                if (change.SessionId is null || change.SessionId == session.Id) push.Trigger();
            });
            var graphs = Task.Run(async () =>
            {
                try
                {
                    await foreach (var graph in feed.ReadAllAsync(sse.Closed).ConfigureAwait(false))
                    {
                        var labelled = await service.LiveTasksAsync(session, graph, sse.Closed).ConfigureAwait(false);
                        sse.SendIfChanged("tasks", new JsonObject { ["type"] = "tasks", ["tasks"] = DurableWire.ToNode(labelled) });
                        push.Trigger();
                    }
                }
                catch (OperationCanceledException) { /* Closed. */ }
                catch (Exception failure)
                {
                    Report(service)(failure);
                }
                sse.Close();
            });
            await push.RunAsync(Push, service.Options.Coalesce, Report(service), sse.Closed).ConfigureAwait(false);
            await graphs.ConfigureAwait(false);
        }
        return Results.Empty;
    }

    public static async Task<IResult> AgentAsync(DeviceService service, HttpContext http, HostedSession session, long agentId)
    {
        var docs = ParseDocs(http.Request.Query["docs"].ToString(), DeviceService.AgentDocKinds);
        await using var feed = await session.WatchAgentAsync(agentId, http.RequestAborted).ConfigureAwait(false);
        var sse = await SseStream.OpenAsync(http, new JsonObject { ["kind"] = "agent", ["sessionId"] = session.Id, ["agentId"] = agentId },
            service.Options).ConfigureAwait(false);
        await using (sse.ConfigureAwait(false))
        {
            sse.Send(new JsonObject
            {
                ["type"] = "snapshot",
                ["snapshot"] = DeviceService.ToNode(feed.Snapshot.ToJson()),
                ["inbox"] = DurableWire.ToNode(await DeviceService.InboxAsync(session, agentId, sse.Closed).ConfigureAwait(false)),
            });
            var pushDocs = new Coalescer();
            async Task PushDocs()
            {
                foreach (var kind in docs)
                    sse.SendIfChanged("doc:" + kind, new JsonObject
                    {
                        ["type"] = "doc",
                        ["kind"] = kind,
                        ["value"] = await DeviceService.AgentDocAsync(session, agentId, kind, sse.Closed).ConfigureAwait(false),
                    });
            }
            await PushDocs().ConfigureAwait(false);
            using var subscription = docs.Count == 0 ? null : service.Host.SubscribeChanges(change =>
            {
                if (change.SessionId == session.Id) pushDocs.Trigger();
            });
            var documents = pushDocs.RunAsync(PushDocs, service.Options.DocumentCoalesce, Report(service), sse.Closed);
            try
            {
                await foreach (var batch in feed.ReadAllAsync(sse.Closed).ConfigureAwait(false))
                {
                    sse.Send(new JsonObject
                    {
                        ["type"] = "batch",
                        ["events"] = new JsonArray([.. batch.Select(item => DeviceService.ToNode(item.ToJson()))]),
                    });
                    if (batch.Any(item => item.Type is "inbox_update" or "snapshot"))
                        sse.Send(new JsonObject
                        {
                            ["type"] = "inbox",
                            ["items"] = DurableWire.ToNode(await DeviceService.InboxAsync(session, agentId, sse.Closed).ConfigureAwait(false)),
                        });
                }
            }
            catch (OperationCanceledException) { /* Closed. */ }
            sse.Close();
            await documents.ConfigureAwait(false);
        }
        return Results.Empty;
    }

    private static List<JsonObject> Activity(DeviceTree? previous, DeviceTree next)
    {
        if (previous is null) return [];
        var before = previous.Sessions.SelectMany(session => session.Agents.Select(agent => ($"{session.Id}/{agent.Id}", agent.Busy)))
            .ToDictionary(pair => pair.Item1, pair => pair.Busy);
        var items = new List<JsonObject>();
        foreach (var session in next.Sessions)
            foreach (var agent in session.Agents)
            {
                var was = before.TryGetValue($"{session.Id}/{agent.Id}", out var busy) && busy;
                if (was == agent.Busy) continue;
                items.Add(new JsonObject
                {
                    ["type"] = agent.Busy ? "agent.started" : "agent.finished",
                    ["sessionId"] = session.Id,
                    ["agentId"] = agent.Id,
                    ["at"] = next.At,
                });
            }
        return items;
    }

    private static async Task<string?> PreviewAsync(DeviceService service, string sessionId, long agentId, CancellationToken cancellationToken)
    {
        try
        {
            var session = service.Host.ResolveSession(sessionId);
            var text = Transcript.LastAssistantText(await DeviceService.ActiveEntriesAsync(session, agentId, cancellationToken).ConfigureAwait(false));
            return text is null ? null : text.Length > 200 ? text[..200] : text;
        }
        catch (Exception failure) when (failure is DurableHostException or InvalidOperationException)
        {
            return null;
        }
    }

    private static List<string> ParseDocs(string raw, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var kinds = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        foreach (var kind in kinds)
            if (!allowed.Contains(kind)) throw new HostInvalidException($"Unknown document {kind}. Allowed: {string.Join(", ", allowed)}.");
        return kinds;
    }

    private static Action<Exception> Report(DeviceService service) => failure => service.Options.OnError?.Invoke(failure);
}
