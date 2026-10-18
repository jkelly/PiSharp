using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RecoveryCases() =>
    [
        Case("recovery.a-killed-device-resumes-a-job-with-no-client-attached", async () =>
        {
            await using var child = new ChildServer(Temp("recovery-kill"));
            await child.StartAsync();
            var created = (await child.Client!.SendAsync(HttpMethod.Post, "/v1/sessions", new JsonObject
            {
                ["name"] = "Crash recovery",
                ["cwd"] = child.Work,
                ["model"] = new JsonObject { ["provider"] = "faux", ["modelId"] = "echo" },
            }))!;
            var main = $"/v1/sessions/{created["id"]!.GetValue<string>()}/agents/main";
            var task = (await child.Client.SendAsync(HttpMethod.Post, main + "/jobs", new JsonObject
            {
                ["job"] = "change-watch",
                ["input"] = new JsonObject { ["checks"] = 4, ["interval_seconds"] = 1 },
            }))!["taskId"]!.GetValue<long>();
            async Task<JsonObject?> Run() => (await child.Client!.GetAsync(main + "/docs/pisharp.job-runs"))!["value"]!["items"]!.AsArray().OfType<JsonObject>()
                .FirstOrDefault(run => run["id"]!.GetValue<long>() == task);
            await WaitUntilAsync(async () => (await Run())?["phase"]?.GetValue<string>() == "watch", "the watch phase started");
            await child.KillAsync();
            await child.StartAsync();
            await WaitUntilAsync(async () => (await Run())?["status"]?.GetValue<string>() == "completed", "completion after the kill", TimeSpan.FromSeconds(60));
            var done = (await Run())!;
            Equal("done,done,done", string.Join(",", done["phases"]!.AsArray().Select(phase => phase!["status"]!.GetValue<string>())), "every phase done");
            var all = new List<JsonObject>();
            string? cursor = null;
            do
            {
                var page = (await child.Client.GetAsync(main + "/entries?limit=500" + (cursor is null ? "" : "&cursor=" + cursor)))!;
                all.AddRange(page["items"]!.AsArray().OfType<JsonObject>());
                cursor = page["next"]?.GetValue<string>();
            } while (cursor is not null);
            Equal(1, all.Count(entry => entry["kind"]!.GetValue<string>() == "pisharp.job"), "exactly one job entry");
        }),
    ];
}
